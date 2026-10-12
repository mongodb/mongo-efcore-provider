---
area: Query / LINQ translation
scope: ["src/MongoDB.EntityFrameworkCore/Query/**"]
reviewer-agent: query-reviewer
adjacent-areas: [Storage, Metadata, Serializers, "C# driver LINQ v3"]
---

# Query — AGENTS.md

Translates `IQueryable<T>` into aggregation pipelines via two paths, chosen at compile time by `MongoQueryMode`:

- **Native (default):** the provider builds the `BsonDocument[]` pipeline from its own query tree.
- **Driver-LINQ (fallback):** anything not representable natively goes to the driver's LINQ v3 provider.
  `MongoQueryMode.NativeOnly` throws at compile time instead of falling back.

Which path a query takes, the exact MQL, and the exception type for an unsupported shape are not contract.

## Pipeline

The QMTEV populates the native IR (`MongoSelectDefinition`) via `Native*Binder`s and always also captures the raw chain
(`CapturedExpression`) for the fallback. `MongoShapedQueryCompilingExpressionVisitor` is the gate
(`ClassifyNativeDisposition`): native goes `MongoSelectLowerer` (BSON-free) -> `MongoPipelineFactory` (template +
placeholders); fallback rewrites `CapturedExpression` via `MongoEFToLinqTranslatingExpressionVisitor`.

The template is built once at compile time; only `factory.Build(parameterValues)` runs per execution. `Select.Route` is
the authoritative is-native signal. `MongoExpression.VisitChildren` is a no-op; every consumer hand-rolls a `switch`.
`MongoQueryTranslationPreprocessor` lifts `VectorSearch` out before nav-expansion (which crashes on it) and re-inserts
it after; re-check if you reorder the preprocessor.

## Invariants

Breaking these usually yields **silently wrong rows**, not a failure.

Gate and fallback:

- **The gate must call the fix's predicate, not restate it.** A gate admitting more than the fix handles yields wrong rows.
- **A recognizer must not mutate then decline.** Stage into locals, commit only after every gate passes.
- **Post-terminal gating.** After a terminal operator (`GroupBy`, `Distinct`, set op, `SelectMany` unwind;
  `HasTerminalOperator`) every later operator must decide explicitly if it can stay native. A `Translate*` override
  bypasses the shared gates and must replicate the check.
- **Structural classification beats metadata/depth/CLR-type shortcuts** (query filters, join-hop depth,
  self-referencing types defeat them). `DriverLinq` is no escape hatch for a misclassification.
- **Unsupported shapes call `MarkNotNativelyRepresentable()`** so `Route` becomes `Fallback` instead of emitting a
  pipeline that drops the operator.
- **A recognizer turning a correlated `DbSet` subquery into a navigation** (`db.Orders.Where(o => o.CustomerId == c.X)`
  -> `c.Orders`) must prove `c.X` is that navigation's FK principal key via `NativeCorrelationMatcher` (single-property FK;
  alternate key via `ForeignKey.PrincipalKey`; composite declines). Binding emits `$lookup {localField: principalKey}`,
  so any other outer member silently reads key-correlated rows.

Scope, joins, grouping:

- **Scope resolves by parameter identity (`ReferenceEquals`), never member name** (shared property names across types is
  the regression). Primitive: `MongoExpressionTranslator.TryBeginOwnedHopWalk`.
- **Collection Include over a join scope** is native only on the root scope. A join holding the Include's
  `_lookup_<Nav>` alias would be collapsed into it by `AddLookup`; renames use `MongoQueryExpression.GetUnusedLookupAlias`.
  A join level the binding would take as the Include's intermediate
  (`LookupExpression.IsCollectionIncludeIntermediateFor`) declines: the binding nests by type, not alias.
- **Bare join-scope leaves.** A bare scalar leaf over a chain re-roots onto the ONE scope its hop chain names
  (`TryTranslateSingleScope`), never by CLR type; it declines when paging, ordering or a deferred paging snapshot exists
  (the lowerer places a chain's paging after every `$lookup`). A bare INNER entity leaf (`HasBareJoinInnerEntityLeaf`)
  never streams: the streaming rewriter is typed by the root entity. A construction whose members are ALL whole-entity
  inner leaves stages each level whole; `BindResultMember` rebinds each shaper with `alias: null` so a sibling
  `o1.o2` is the same instance.
- **Filter joins before a reference Include** (`TryAdmitFilterJoins`): extra joins are admitted only when each is a
  resolved, flattened, eligible reference join and the Where that created them left its predicate (and later
  paging/Distinct) in `PostJoinOps`. A REQUIRED navigation's inner join can drop rows the page counted and stays
  declined. Confirmations must equal `Joins.Count`.
- **A filtered join inner is carried on the join's own `$lookup`** (`LookupExpression.InnerPredicate`), set by
  `TryCarryInnerFilterOnLookup` only for `IsFilterOnlyCollectionScan` on an otherwise-eligible, single-pair, un-stamped
  lookup. It renders in `MongoPipelineFactory.RenderLookup` through the placeholder table so parameters and clocks are
  read per execution; never bake it into `PipelineStages`. The predicate is relative to the joined document: nothing is
  re-rooted (a translated predicate cannot be rebased).
  - **No non-native consumer may see such a lookup.** The driver-LINQ bridge drops a join's inner source and with it the
    filter; `MongoEFToLinqTranslatingExpressionVisitor`'s constructor refuses any pending lookup with `HasInnerPredicate`.
    Call that one predicate; don't restate it.
  - **A TPH-derived entity set is narrowed in the preprocessor** (`DerivedEntityRootNarrower`:
    `Set<Dog>()` -> `Set<Animal>().OfType<Dog>()` before nav-expansion), so one discriminator predicate applies in every
    mode, on any join side or subquery. Not applied with a vector search.
  - **Paging written before a join stays ahead of its `$lookup` whenever the join can drop rows**
    (`JoinInfo.PagingBeforeItMustStayAhead`): a collection or navigation-less join, an inner join onto an optional or
    principal-side reference (an explicit `Join` over a nullable FK), or any join whose lookup carries an `InnerPredicate`.
    Only a REQUIRED dependent-side reference defers it (dangling data is the sole way it drops a row: the accepted
    divergence). Deferring paging past a dropping join pages the joined rows, not the outer rows
    (`NativeJoinFilteredInnerTests.Take_before_*`). Paging written AFTER the join is correctly applied to the joined rows.
- **Aggregates and quantifiers over a referenced (cross-document) collection** (`c.Orders.Any(..)`, `.All(..)`, `.Count(pred)`,
  `.Sum/Min/Max/Average(o => o.Prop)`, also what EF makes of a `GroupJoin` with an aggregate) are projection leaves over the not-unwound
  `$lookup` array `_lookup_<Nav>`. `MongoExpressionTranslator.ReferencedCollectionResolver` (set only on the projection binder's
  root translator, `NativeProjectionBinder.ResolveReferencedCollection`) maps the strict FK correlation
  (`NativeCorrelationMatcher`, never a target-type guess) to that array and stages the lookup; `TryResolveOwnedCollectionPath` then
  serves the existing `Count`/filtered-count arms unchanged. Any/All have no value-position translation (the predicate arm renders
  `$elemMatch`, query dialect only), so a projected `Any(pred)` is `Count(pred) >= 1` and `All(pred)` is `Count(not pred) == 0` (the
  aggregation dialect's `$not` is the exact complement; true for the empty group). `Sum/Min/Max/Average` of one numeric element
  property use `MongoArrayReduceExpression` over the path through the array (`{ $sum: "$_lookup_Orders.Total" }`). An empty group:
  `$sum` is 0; `$min/$max/$avg` are null, which `WalkNullBehindNonNullableType` reads strictly for a non-nullable result (throws like
  LINQ). The read side (`MongoProjectionBindingExpressionVisitor`) only binds the whole call as one member once the native binder accepted
  the projection (`Route == Projection`); the native binder is the single gatekeeper. Owned collections keep their existing behavior
  (no `Sum/Min/Max/Average`, no value-position `Any/All`). `FirstOrDefault`/`ToList` elements and driver-LINQ bridge parity are later
  slices of the design.
  - **A correlation with no collection navigation** (`db.Orders.Where(o => c.Key != null && c.Key == o.Fk)`, what EF makes of a
    `GroupJoin` + aggregate on arbitrary keys) is a keyed array lookup (`LookupExpression.IsCorrelatedArraySource`, not unwound), built
    from exactly the two properties the predicate compares (`NativeCorrelationMatcher.TryMatchRawKeyCorrelation`; exactly one such
    `Where` in the chain, the rest are element filters). Null semantics: `$lookup` matches a null/missing local key to null/missing
    foreign values, which is C# `==` for an unguarded subquery, so that stays the plain form; a guarded outer key (`c.Key != null &&`)
    means a null key matches NOTHING, which only the `let`/`pipeline` form (`ExcludesNullLocalKey`) expresses.
    Element filters ride in the sub-pipeline (`InnerPredicate`, placeholder-rendered like a filtered join inner); each distinct filter
    set (structural `ExpressionEqualityComparer`) gets its own alias suffix `_fN`, so different filters or parameters never share an
    array. A filter that references the outer entity is a second correlation and declines.
  - **A keyed lookup has no TPH discriminator narrowing** (the navigation constructor does it), so `TryStageKeyedArrayLookup` declines a
    derived target (`BaseType is not null`); without it the count included sibling types' documents.
  - **The driver-LINQ bridge answers the aggregate, quantifier and count leaves over these arrays itself**
    (`MongoEFToLinqTranslatingExpressionVisitor.ReferencedCollection.cs`, slice 5), by rewriting the terminal operator and the chain
    under it (`Where`/`Select`/`OrderBy` between the correlation and the terminal) to the `Enumerable` operators over
    `Mql.Field(outerDoc, <array alias>, serializer)`, which the driver renders server-side. Only the exact correlation the binder staged
    for may read an array: a navigation correlation re-verified through `TryMatchCorrelatedCollection`, a navigation-less one through
    `TryMatchRawKeyCorrelation` and the lookup alias built from it. The bridge reads only PLAIN arrays (`IsBridgeReadableArray`): the
    binder folds an element filter into the lookup it stages, which the bridge cannot use, so the bridge stages its OWN plain array
    for every correlation of the query root (`StageBridgeOwnedLookups`) and applies the filter as an `Enumerable.Where`; the
    filtered native array is then `IsBridgeIgnorable` and not emitted. Emitted right after the root, like a navigation count.
    - **Never resolve a subquery that correlates with a non-root document.** The pre-scan skips every lambda of a correlated
      subquery (`c.Orders.Sum(o => o.Details.Sum(..))` correlates the inner one with `o`, so a root-level array would read the wrong
      rows: it did, silently), a set operation's other operands (`$unionWith` cannot see an array staged after the root), and
      `_referencedElementLambdaDepth > 0` refuses the rewrite there. A set operation with ANY referenced terminal fails at translation
      (`MultiBranchNavigationCountFinder`), not at the server.
    - **A relational comparison of a nullable rewritten aggregate is null-guarded** (`VisitBinary`, `_nullableReferencedAggregates`):
      the aggregation dialect orders null below every number, so `Max(..) < 650` would match the empty group where C# answers false.
    - **Element leaves (`ToList`/`FirstOrDefault`) are still native-only**: the bridge refuses them (`ElementLeafAliases`/
      `HasElementStages`), as does a join-inner predicate (`HasInnerPredicate` without `IsCorrelatedArraySource`).
  - **Element leaves: `ToList` and `FirstOrDefault` of the group** (`c.Orders.Where(..).OrderBy(..).ToList()`, `.FirstOrDefault(pred)`,
    the same off a `GroupJoin` group, `Select(o => o.Total).ToList()`) match `NativeReferencedElementMatcher` (purely structural, called by
    both sides) and stage ONE keyed array lookup, with or without a collection navigation (`TryTranslateReferencedElementLeaf`; the
    correlation is `TryMatchRawKeyCorrelation`, never the navigation). Element filters ride in `InnerPredicate` (placeholder-rendered,
    so parameters are per execution); the ordering and the `FirstOrDefault` limit are `LookupExpression.ElementSort`/`ElementLimit`,
    typed fields rendered by `RenderLookup` after the filter, NOT `PipelineStages` (`HasPipeline` would make the lookup non-native). A
    filter moved ahead of the ordering is equivalent; `Skip`/`Take`, a second `OrderBy`, and a sort key that is not a plain default-stored
    scalar whose stored order is the CLR order decline. A distinct (filters, sort, limit) gets its own `_fN` alias.
    - **The read side finds the lookup by the member's alias, never by re-deriving it**: the binder records the alias on the lookup
      (`ElementLeafAliases`) only at its commit point, and `TryBindReferencedElementLeaf` binds only a leaf that finds itself there
      (native route only). The shaper is a `CollectionShaperExpression` with NO navigation over an `ArrayAliasProjectionExpression`, so
      materializing assigns a plain list and does not fix the elements up into the principal's own collection (which would leave it holding
      only the filtered subset); `FirstOrDefault` is `Enumerable.FirstOrDefault` over that list, and a scalar `Select` is re-applied to the
      materialized entities (`IsSelfContainedLambda`). The leaf is not an `isArrayLeaf` (siblings such as `Count` stay readable) but sets
      `HasArrayProjectionLeaf`, so set operations decline it.
    - **`IsNativeOnlyArray` is the one predicate the bridge refuses on.** The emit side stages these lookups in every mode, so without
      it explicit `DriverLinq` rendered a plain `$lookup` nothing reads and silently returned empty lists.
  - **`Where` and `OrderBy`/`ThenBy` keys** use the same resolver through `NativeReferencedCollectionBinder` (tried after
    `NativeReferenceCollectionCountPredicateBinder`, and after the sort arms, in `NativeSlotPopulator`). A `Where` translates in
    predicate mode, so `Any(pred)`/`All(pred)` are `$elemMatch` over `_lookup_<Nav>` (query dialect) and a count/sum comparison is
    `$expr`; a sort key translates in value mode and is a computed `__sort` key. Accept on "the resolver resolved a source", never on "it
    staged a new lookup": an earlier `Where`/`OrderBy` may already have registered the same lookup, so nothing new is staged. The commit
    stages nothing until the whole predicate/key translated, then `AddLookup`s, relocates a trailing sort
    (`DeferTrailingSortPastConfirmedJoin`: one logical ordering must stay one `$sort`, the post-lookup key must not become primary) and
    flips to `PostJoinOps` (`MarkReferenceCollectionCountPredicateConfirmed`). Ops recorded earlier stay ahead of the lookup, so
    `Take(n).Where(Any(..))` pages first. A join scope or terminal operator declines (the flip has no place in that lowering).
- **Navigation-less joins** are native iff `RebindInnerShaperToOuterQuery`'s raw-key branch resolved both keys
  (`JoinInfo.Lookup != null`); a non-simple key declines. Composite-PK components live at `_id.<Name>`
  (`LookupExpression.GetFieldPath`). "Simple" includes an anonymous key of scalar properties (same anonymous type both
  sides, `StoredAlike` per pair), rendered as `let` + `$and`.
- **Paging vs. joins.** EF hoists `Skip`/`Take`/`Where`/`OrderBy` ahead of a join's result selector; recorded ops are
  deferred until after the join unless the join is a left-outer reference navigation (reducers there decline). Paging
  both before and after a join declines; paging *between* joins declines (`HasPagingRecordedBetweenJoins`). Deferring
  paging past a non-1:1 join in a GroupBy silently changes group counts, so that declines.
- **GroupBy over a join scope** needs a `TransparentIdentifier` key parameter and is confirmed only at the binder's
  commit point. Key parts/accumulators/conditions that may read an unmatched left-outer side decline unless every
  operator propagates missing as null (`MongoGroupElementTranslator.PropagatesMissingAsNull`).
- **Post-group operators resolve by the Select's output alias** (`MongoProjectedAliasScope`), never key-part name or
  entity property. `Where`, `OrderBy`/`ThenBy` (bare alias key only) and `Skip`/`Take` are native, in `PostGroupOps`
  after the flatten `$project`; over a join they decline. An accumulator whose operand is the key itself
  (`g.Max(e => g.Key)`) reads the key part's raw per-document expression, never `_id`. A `$push` list is null-safe for
  nullable/reference elements; entity elements decline.
- **Set ops form a tree; each `Union`'s dedup belongs to its own link**, never hoisted or flattened
  (`A.Concat(B.Union(C))` differs from a left chain). Ops recorded between links go to `TrailingOps`, then move to the new
  link's `PrecedingOps` (`AppendSetOperation`). `Intersect`/`Except` never nest.
- **Projected set-op operands are read through source1's shaper.** Bare scalars with different aliases re-alias source2
  (`CanAlignBareScalarAliases`). A constant/parameter on source1 declines unless `CanRebindConstantLeafToDocument`.
  source1 may be a single-level confirmed join scope; source2 has no lookups. Operands must store each alias the same way
  (`OperandSerializationsMatch`).
- **A composite `$group` `_id` omits a missing sub-key**; `RenderCompositeKeyPart` `$ifNull`-normalizes every possibly-null
  part once. Key-only accumulator conditions use `NullSafeKeyRead`.

Rendering (null/missing/dialect semantics):

- **Every string constant/parameter in the aggregation dialect is `$literal`-wrapped** (else `"$Field"` user input is read
  as a field path). New operand positions must go through `RenderBranch` or `RenderOperand`.
- **Negation is exact complement or decline.** `$eq`/`$ne` may be inverted; `$lt/$lte/$gt/$gte` must be `$not`-wrapped
  (they don't partition missing/null). A negated `&&`/`||` is De Morgan'd by `MongoExpressionNegator` or declines, never
  an aggregation `$not`.
- **The aggregation dialect orders null/missing below every value**, so bare `$lt`/`$lte` (or `$gt`/`$gte` vs null) answer
  true where C# answers false. The renderer conjoins `$gt: [<lower side>, null]` when `MayBeNull`. Non-nullable values
  that may be *missing* (unmatched left-join side) get no guard; conditions over them decline.
- **`$expr` inside `$elemMatch` is a hard server error**; reject at `IsQueryDialectRenderable`. `$size` on a missing/null
  array also errors: `$ifNull` around `$size`/`$filter` is mandatory.
- **Relational comparisons, sort keys and aggregates run on the stored form**: over a converted or non-default-represented
  property they decline natively and the driver-LINQ bridge throws (EF-337). Shared predicates:
  `StoredOrdering.PreservesClrOrdering` and `PreservesClrOrderingForAggregateAndSort`; equality is never gated. Native
  aggregates stay on `HasDefaultKeySerialization`. The bridge walks the whole key/operand; an unclassifiable construct is
  refused if the query reads such a property.
- **Non-default-serialized bools are truthiness-tested wrongly** (`HasConversion<string>()` stores `"True"`/`"False"`, both
  truthy): gate `$not`/`$and`/`$or` operands and bare boolean roots via `MongoExpressionTranslator.IsUnsafeTruthinessRoot`.
- **Negator, dialect classifier and both renderers must agree** (`MongoExpressionNodeCoverageTests` checks every
  `MongoExpression` subtype x dispatcher). It is blind to shape-conditional dispatch (e.g. `MongoRegexExpression`
  constant vs field-to-field); those need dedicated tests.
- **String operators that don't propagate null are null-guarded at render** (`NullPropagating`; `$concat` coalesces to
  `""`). A non-nullable projection leaf over a guarded `Length`/`IndexOf` is flagged `MongoProjection.ThrowsOnNull` and
  read as `T?` (throws "Nullable object must have a value."); one whose null an operator may absorb (`$max`/`$min`,
  `Sign`) declines (`ClassifyNonNullableValueRead`). Group keys, accumulators and `Min`/`Max`/`Average` over such values
  decline rather than read null as `0`, including via an upstream alias (`MongoElementRefExpression.ThrowsOnNull`).
- **`$toLower`/`$toUpper` are never emitted natively** (ASCII-only): `Where` becomes an anchored case-insensitive regex;
  `Select` projects the raw string and re-applies the mapping client-side. After such a leaf
  (`HasClientCaseMappingProjectionLeaf`) value-reading operators decline. A GroupBy query that would run on driver-LINQ
  with a case mapping anywhere in its chain throws (`ThrowIfDriverLinqCaseMapsGroupedResult`); ungrouped driver-LINQ
  queries still emit `$toUpper`/`$toLower`.
- **DateTime read-back must honour the property's `DateTimeKind`** (`NativeDateTimeKindReadBack`): untraceable values,
  whole-document class-map reads with kind-sensitive members, and set ops whose operands differ in kind decline. Don't fold
  this into `HasDefaultKeySerialization`. Date parts over Local-kind properties compute in UTC on both paths (EF-459).
- **Clock members (`DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`) are never baked into the template.** A maximal
  closed subtree holding one becomes a `MongoParameterExpression` with a `RuntimeEvaluator`, evaluated per `Build`; one
  predicate (`RuntimeClock.IsRuntimeEvaluable`) gates `IsSimpleValue` and the factory. `TryEvaluateClosedSubtree` declines
  any clock.
- **Local-collection folding never folds a non-deterministic call or a clock member**
  (`LocalCollectionFilterFoldingVisitor`). The fold runs in the preprocessor for every mode, so a folded
  `Guid.NewGuid()`/`DateTime.UtcNow` would be baked into the cached plan.
- **Read modes: how a non-nullable value read back treats null and MISSING.** Driver-LINQ is the oracle: a malformed
  document must answer what it answered there, never a silent `0`. Emit side decides once per value leaf
  (`MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead`; projected Distinct over a bare field uses
  `ClassifyMalformedFieldRead`):
  - `Plain`: a bare field leaf, read through its property (D-F10). Non-nullable fields are never flagged on the emit side
    (it would decline `Min`/`Max`/`Average`/group keys over every `x.A + x.B`).
  - `ThrowOnNull`: every operator between a null and the leaf propagates it (`Length`/`IndexOf`, date operator over a
    nullable date, `x.Score.Value + 1`): read as `T?`; an operator that may absorb the null declines. Group
    keys/accumulators over a date operator still read `0` (EF-461).
  - `ThrowOnMalformedNull`: computed leaf over a non-nullable field (`x.Rank + 1`), null only when a document omits the
    field: strict on the `Projection` route; read side only (emit-side callers treat it as `Plain`).
  - `DefaultOnMalformedMissing`: the leaf selects the field itself through `$cond`/`$ifNull` (`x.Rank > 1 ? -1 : x.Rank`):
    MISSING reads `default`, null throws, only where native answers MISSING exactly as the driver (`MayAnswerMissing`); a
    widening the translator dropped (the driver's `$toLong` answers null) makes it strict (`ReclassifyMalformedReadAs`).
- **D-F10: on the `Projection` route a MISSING bare stored scalar reads `default(T)`, as driver-LINQ's `$project` push-down
  did; an explicit BSON null throws** (a `bool` reads false). Covers root leaves
  (`BsonBinding.GetScalarProjectionValueAtElement`) and join-scope leaves (`ReadsJoinScopeBareScalarThroughProperty`).
  Whole-entity reads stay strict. The exception TYPE for malformed stored data is not contract; value-vs-throw is.
- **A null parameter for a non-nullable property serializes as BSON null** (`BsonValueSerializer.SerializeNullAware`), so
  `x.Rank == p` with `int? p = null` matches null/missing rows, as driver-LINQ did.

Shapers and projections:

- **The mixed reader reads a computed scalar leaf whole** (`IsClientComputedLeaf`, `TryBindClientComputedLeaf`);
  operand-by-operand binding makes every operand read as the last one bound. Ambiguous member-less construction arguments
  are recorded in `AliasedConstructionMembers` and the mixed reader declines them loudly.
- **A ternary over constructions is read whole only if its type's class map reads each member by name**
  (`IsMisreadWholeValueLeaf`, called by every binder staging a ternary/coalesce leaf; `Id` maps to `_id`, so
  `new P { Id = ... }` would throw FormatException). Otherwise only the test is staged and the shaper evaluates the
  ternary (`TryCollectClientConditionalBranches`). Branch reads are staged as `_cr<N>` and the read side takes each alias
  from the staged read (`TryGetClientConditionalReadAlias`), never the member path. `HasClientConditionalProjectionLeaf`
  declines set ops, value-reading operators and `Distinct`.
- **Client-only bodies over the whole entity** (client method on it, a combinator around one, or a construction with a
  whole-entity operand) fetch whole documents (`HasClientWrappedWholeEntityShaper`, so set ops decline). Binder and shaper
  gate both call `NativeClientWholeEntityShape`; at least one whole-entity operand is required (`new[] { x.Id }` must not
  match).
- **Positional containers of scalars** (`new[] { x.A, x.B }`) are `_ctorArg<N>` leaves read by index
  (`HasPositionalCtorProjectionShaper`); both sides peel the boxing `Convert` with
  `NativeProjectionBinder.UnwrapContainerElementBoxing`. A whole-entity element never takes this path
  (`IsScalarPositionalConstruction`). Containers are an opt-in of `TryGetProjectionMembers` (`allowContainerElements`,
  plain-`Select` callers only); never widen `allowPositionalConstructorArguments`, which `GroupBy`/`SelectMany` result
  selectors share. `new List<T> { ... }` is not a container.
- **An element `Select` over a root-declared owned collection** (`e.Kids.Select(k => k.W).ToList()`) is staged as the
  collection's whole array (`TryMatchOwnedArraySelectLeaf`, the one predicate both sides call); the shaper re-applies the
  `Select` over materialized owned entities. The lambda must be self-contained (`IsSelfContainedLambda`). A lambda reading
  the outer entity crashes shaper build in every mode (pre-existing).
- **`Mql.Field(p, "element", serializer)` resolves like a mapped field** (`TryResolveMqlField`, shared by Where, OrderBy,
  Select, aggregate selectors) by STORED element name. The serializer argument is ignored (can't be inspected at compile
  time), so a converter/non-default representation or a CLR-type mismatch declines.
- **A nested construction that is not all plain top-level fields is flattened** into the enclosing projection
  (`FlattenNestedConstructions`): each member becomes a leaf aliased by its LAST name; an all-plain nest stays a
  sub-document leaf. Two members sharing a last name collide in `seenAliases` and decline.
- **Mixed-projection alias agreement.** An array/owned-nav projection leaf's alias must equal the navigation's document
  path, or a fallback silently returns an empty collection. A member NAMED differently, or an element renamed with
  `HasElementName`, takes the path as alias plus a `DocumentPath` alias override. The same rule applies to a dotted scalar
  beside an array leaf; a path that equals/prefixes/extends another alias declines (`ProjectionPathsCollide`).
- **Owned-entity equality** (`p.Home == x`, `Visits.Contains(x)`) is a per-property conjunction over every CLR-mapped
  non-shadow property (driver-LINQ's rewrite), not a sub-document match; `ElementParam` (not `SelfParam`, which enables
  root-entity arms wrong inside `$elemMatch`) marks the element operand. Parameter comparands over all-nullable
  properties, owned == owned, TPH-owned and outer-scoped operands decline.

## Conventions and traps

- **Layering:** Query never calls `IMongoCollection<>.Aggregate` (Storage executes via `MongoClientWrapper.Execute`),
  never instantiates serializers (`BsonSerializerFactory`), and only reads metadata. The lowerer is BSON-free. Bulk writes
  cross to Storage as behavior (`MongoBulkPlan` delegates), not data.
- **Adding a native operator:** slot-style needs `NativeSlotPopulator.PopulateNativeSlots` and
  `IsNativeRepresentableSlotOperator`; a `Translate*`-override operator needs the override and
  `IsNativeRepresentableSlotOperator`. QMTEV's `TranslateWhere/OrderBy/ThenBy/Skip/Take` are deliberately inert
  (`=> null`); routing them through the switch double-populates slots.
- **Paging:** MongoDB rejects `{$limit: 0}`, so `Build` rewrites it to an always-false `$match`; negative values throw.
  Normalization covers top level and `$unionWith`, not `$lookup` sub-pipelines.
- **One discriminator test for `OfType<T>`, `x is T` and `x.GetType() ==/!= typeof(T)`**
  (`MongoDiscriminatorPredicate.TryBuild`): values go through the discriminator property's serializer, and an abstract type
  contributes no value (its `null` in `$in` would match a document with no discriminator). A `GetType()` test against a
  type outside the hierarchy folds to false (EF-202: driver-LINQ answers every row). Root-level, one-scope only. A member
  declared only on `TDerived` resolves by the lambda parameter's own type after `OfType<TDerived>`;
  `((Derived)e).Member` is not narrowed and declines.
- **TPH `$lookup` to a derived type must narrow by discriminator** (in `LookupExpression`'s constructor). The kind
  `LookupPipelineKind.DiscriminatorNarrowed` is write-once; native for a top-level collection Include only. Reference
  navigations, renamed-to-avoid-collision lookups and correlated reducers still decline.
- **Primitive collections, dictionaries and composite-key `Contains` are query-dialect only.**
  - `Any()`/`Count()`/`Length` over a primitive-collection PROPERTY reuse the owned size forms
    (`TryResolvePrimitiveCollectionPath`); a missing/null array counts as empty. `Any(pred)`/`All(pred)` stay declined.
  - A string-keyed dictionary entry is the sub-field `<dict>.<key>` (`MongoDictionaryEntryExpression`, serialized by the
    dictionary's VALUE serializer, hence `MongoParameterExpression.ExplicitSerializer`); `ContainsKey` is `$exists`. Only a
    constant, dotted-path-addressable key (not empty, no `.`, no leading `$`, no NUL). The aggregation dialect
    deliberately has no arm: a bare `$lt` over a possibly-missing key answers true there.
  - `entities.Contains(e)` over a composite key is a per-entity `$or` of `_id.<Key>` conjunctions
    (`MongoCompositeKeyInExpression`), never an order-sensitive `_id: {$in: [docs]}`; a parameter list is built per
    execution (`CompositeKeyClauses`). An empty list emits one always-false clause (`$or: []`/`$nor: []` are server errors).
- **Spec scalar functions.**
  - Integer `&`/`|`/`^`/`~` over int/long are `$bitAnd`/`$bitOr`/`$bitXor`/`$bitNot` inside `$expr` (server 6.3+).
    `MapBitwiseOperator` is separate from `MapArithmeticOperator` (shared with `SelectMany`). A bitwise Select/GroupBy LEAF
    is deliberately not admitted; boolean `^` declines.

## Testing

- **MQL shape cannot prove a query went native**; use `MongoQueryMode.NativeOnly` (`MONGODB_EF_QUERY_MODE=NativeOnly`
  flips every spec and functional context; `MONGODB_EF_NATIVE_ONLY=1` is an alias).
- For result-affecting native shapes, write a `[Theory]` comparing native results to an in-memory LINQ oracle over
  ragged/missing/null/empty data (`NativeOwnedCollectionAllTests`, `NativeModeAssert`).
- MQL baselines are regenerated (see SpecificationTests `AGENTS.md`). Tests: `tests/…UnitTests/Query/` (native in
  `NativeTranslation/`), `tests/…FunctionalTests/Query/`; filter `FullyQualifiedName~Query`.
