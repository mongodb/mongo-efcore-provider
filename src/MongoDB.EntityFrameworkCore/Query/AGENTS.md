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
    filter; `MongoEFToLinqTranslatingExpressionVisitor`'s constructor refuses any pending lookup with `IsJoinWithNativeOnlyInnerFilter`
    (or `HasElementStagesWithoutLeaf`). Call those predicates; don't restate them.
  - **A TPH-derived entity set is narrowed in the preprocessor** (`DerivedEntityRootNarrower`:
    `Set<Dog>()` -> `Set<Animal>().OfType<Dog>()` before nav-expansion), so one discriminator predicate applies in every
    mode, on any join side or subquery. Not applied with a vector search.
  - **Paging written before a join stays ahead of its `$lookup` whenever the join can drop rows**
    (`JoinInfo.PagingBeforeItMustStayAhead`): collection/navigation-less joins, inner joins onto optional or
    principal-side references, any lookup with an `InnerPredicate`. Only a REQUIRED dependent-side reference defers it
    (dangling data is the accepted divergence). Paging written AFTER the join applies to joined rows.
- **Aggregates and quantifiers over a referenced (cross-document) collection** (`c.Orders.Any/All/Count(pred)`,
  `.Sum/Min/Max/Average(o => o.Prop)`, GroupJoin aggregates) are projection leaves over the not-unwound `$lookup` array
  `_lookup_<Nav>`. `MongoExpressionTranslator.ReferencedCollectionResolver` (set only on the projection binder's root
  translator) maps the strict FK correlation (`NativeCorrelationMatcher`, never a target-type guess) to that array.
  Value-position `Any(pred)` is `Count(pred) >= 1` and `All(pred)` is `Count(not pred) == 0` (`$elemMatch` is query
  dialect only). Empty group: `$sum` is 0; `$min/$max/$avg` are null, read strictly for a non-nullable result (throws
  like LINQ). The read side binds the whole call only once the native binder accepted the projection
  (`Route == Projection`). Owned collections keep their existing behavior (no `Sum/Min/Max/Average`, no value-position
  `Any/All`). Element leaves and the driver-LINQ bridge are below.
  - **A computed selector** (`Sum(o => o.Price * o.Qty)`) is `MongoMappedArrayReduceExpression`: the reduction of a per-element `$map`
    (`TranslateReferencedComputedAggregate`), where `MongoArrayReduceExpression` reduces the path through the array to one property.
    Only arithmetic over the element's own default-stored numeric properties, constants and query parameters, and numeric casts of them
    (`IsNumericArithmeticOverElement`); anything else (a string `Length`, a conditional, a date part) declines, because a null or missing
    operand would be read as 0 where LINQ throws. Integer division follows the result type, so it truncates per element as C# does; the
    driver-LINQ bridge cannot answer that one (the driver turns `int / int` into a fractional `$divide` and fails on truncation), so it
    fails loudly there.
  - **A correlation with no collection navigation** (`o => c.Key != null && c.Key == o.Fk`) is a keyed array lookup
    (`IsCorrelatedArraySource`, `NativeCorrelationMatcher.TryMatchRawKeyCorrelation`; exactly one such `Where`, the rest
    are element filters). `$lookup` matches a null/missing local key to null/missing foreign values (C# `==` for an
    unguarded subquery); a guarded outer key means null matches NOTHING, which only the `let`/`pipeline` form
    (`ExcludesNullLocalKey`) expresses. Each distinct element-filter set (structural comparer) gets its own alias suffix
    `_fN`; a filter referencing the outer entity is a second correlation and declines.
  - **A correlation on several properties** (an anonymous-type `GroupJoin` key, or `r.A == s.A && r.B == s.B`) is a composite-key
    keyed array: `NativeCorrelationMatcher.TryMatchCorrelationKeys` returns `CorrelationKeyPart`s (the single-pair match is its one-part
    case). The predicate must be a pure conjunction of equalities, each compared property used once per side, plus at most one null guard
    per compared OUTER property (or per outer condition, below); a guard on a property not compared, a conjunct that is not an equality
    or an `||` declines. A guarded part excludes a null outer value (`LookupKeyPair.ExcludesNullLocal`), an unguarded part equates null
    with null, as in a `Join`.
    - **An equality that is not outer-vs-inner is a condition on one side** (`TryMatchCorrelation`; `TryMatchCorrelationKeys` is the
      strict form that admits none). On the ELEMENT alone against a constant or query parameter (`r.Zone == 1`, `1996 == r.OrderDate.Year`,
      `r.Zone + 1 == 2`, `== p`) it becomes an element filter (`CorrelationConditions.Element`, a lambda into `InnerPredicate`). On the OUTER
      row alone (`c.City == "London"`, an outer property against a non-null constant or parameter) it gates the group
      (`LookupExpression.OuterConditions`): `RenderLookup` adds a `let` of the outer field (`$ifNull`-normalized) and a `$match $expr`, so the
      group is empty for an outer row that fails it, a parameter read per execution. At least one outer/inner key pair must remain, and
      only equalities are admitted: `r.Price > 100` is still never dropped. EF's GroupJoin rewrite writes the parts as
      `object.Equals((object)x, (object)y)` and guards a constant (`!(1 == null)`, ignored); both are normalized by the matcher. The
      driver-LINQ bridge applies element conditions as `Enumerable.Where` over its plain array and leaves an outer gate UNRESOLVED (it
      fails at translation, never empty): a gated lookup is `IsNativeOnlyArray`/`IsBridgeIgnorable`, and an element leaf's gated lookup is
      rendered natively like any other (`RenderElementLeafLookup`). Each distinct gate gets its own `_fN` alias.
    EF10's GroupJoin rewrite guards every outer part, so the group is EMPTY for an outer row with a null part. EF8/EF9 keep the key whole
    (`Equals(new { s.A, s.B }, new { r.A, r.B })` behind a guard on a constructed object that is never false): member-wise, null equals
    null; `TryExpandAnonymousKeys` expands it to unguarded pairs. Each version's own rewrite is answered, so the two differ for a null
    part. A composite lookup renders `let` + `$match $expr` and can carry an element filter, ordering and paging in the same
    sub-pipeline (`LookupExpression.CompositeKeyLetAndMatch`).
  - **A keyed lookup has no TPH discriminator narrowing**, so `TryStageKeyedArrayLookup` declines a derived target.
  - **Element leaves, `ToList`/`FirstOrDefault` of the group** (`c.Orders.Where(..).OrderBy(..).Skip(..).Take(..).ToList()`,
    `.FirstOrDefault(pred)`, off a GroupJoin group, `Select(o => o.Total).ToList()`) match `NativeReferencedElementMatcher` (structural,
    shared by both sides) and stage ONE keyed array lookup, whether or not a navigation exists
    (`TryTranslateReferencedElementLeaf`, correlation via `TryMatchCorrelationKeys`, never the navigation). Filters ride in
    `InnerPredicate`; ordering, `Skip` and the limit are the typed `ElementSort`/`ElementSkip`/`ElementLimit`, rendered by `RenderLookup`
    after the filter (not `PipelineStages`: `HasPipeline` would make the lookup non-native). A filter moved ahead of the ordering is
    equivalent, but `Skip`/`Take` must be the OUTERMOST operators: a `Where` after them filters the page, so it declines. `Take` on a
    `FirstOrDefault`, a non-positive CONSTANT `Take`, a second `OrderBy` and a sort key that is not a default-stored scalar in CLR order
    also decline. `Skip`/`Take` may be a constant or a query parameter (`LookupExpression.ElementSkip`/`ElementLimit` are
    `MongoExpression`s rendered through the placeholder table, so the value is read per execution); a parameter's value is checked at
    `Build` (zero is an empty array, a negative count throws), see Paging below. Each distinct (filters, sort, skip, limit) gets its own
    `_fN` alias; parameters are told apart by name (`LookupExpression.SameElementCount`), never by value.
    - The read side finds the lookup by the member's alias in `LookupExpression.ElementLeafAliases` (written at the binder's commit),
      never by re-deriving it (`TryBindReferencedElementLeaf`, native route only). The shaper is a `CollectionShaperExpression` with NO
      navigation over an `ArrayAliasProjectionExpression`: the list is assigned plainly and never fixed up into the principal's own
      collection (it would hold only the filtered subset). A scalar `Select` is re-applied to the materialized entities. The leaf is
      not an `isArrayLeaf` (siblings such as `Count` stay readable) but sets `HasArrayProjectionLeaf`.
    - Two element leaves of one element type in a projection need distinct `Identity` (the projection member) on their
      `ArrayAliasProjectionExpression` and element `RootReferenceExpression`: otherwise they are structurally equal and the shaper
      pipeline merges them into one variable declared only in the first array's lambda.
  - **`Where` and `OrderBy`/`ThenBy` keys** use the same resolver via `NativeReferencedCollectionBinder`. Accept on "the
    resolver resolved a source", never on "it staged a new lookup" (an earlier op may have registered it). Stage nothing
    until the whole predicate/key translated, then relocate a trailing sort
    (`DeferTrailingSortPastConfirmedJoin`: one ordering must stay one `$sort`) and flip to `PostJoinOps`. Ops recorded
    earlier stay ahead (`Take(n).Where(Any(..))` pages first). A join scope or terminal operator declines.
  - **The driver-LINQ bridge answers the aggregate, quantifier and count leaves itself**
    (`MongoEFToLinqTranslatingExpressionVisitor.ReferencedCollection.cs`): the terminal operator and the `Where`/`Select`/`OrderBy`
    chain under it become the `Enumerable` operators over `Mql.Field(outerDoc, <array alias>, serializer)`. It reads only PLAIN arrays
    (`IsBridgeReadableArray`) and stages its own for the query root's correlations (`StageBridgeOwnedLookups`), applying the filter as
    `Enumerable.Where`, because the binder folds the filter into the lookup it stages; that filtered array is `IsBridgeIgnorable`.
    - The correlation `Where` may sit anywhere in the chain (`TryFindCorrelationBase`); any other `Where` is an element filter and two
      outer-reading predicates are ambiguous. A query parameter is not the outer entity.
    - Never resolve a subquery correlated with a non-root document (an aggregate inside another aggregate's lambda reads the wrong
      rows) nor a set operation's other operands (`$unionWith` cannot see an array staged after the root). A set operation with any
      referenced terminal fails at translation (`MultiBranchNavigationCountFinder`), not at the server.
    - A relational comparison of a nullable rewritten aggregate is null-guarded (`VisitBinary`): the aggregation dialect orders null
      below every number, so `Max(..) < 650` would match the empty group.
    - **Element leaves are answered by the native lookup, not by driver LINQ.** The emit side stages their keyed lookups in every mode and
      the shaper reads them from the member's alias in a whole document, so the bridge renders each one as the native pipeline would
      (`RenderElementLeafLookup`: a one-stage `MongoPipelineFactory`, built with this execution's parameters, so a filter parameter is
      read per execution) and copies its array to every alias in `ElementLeafAliases` with `$set`. The bridge's constructor refuses a
      lookup that has element stages but no leaf, a join-inner predicate, and an alias that is a stored field of the root or dotted
      (the `$set` would overwrite what another leaf reads). A computed sibling leaf (`N = c.Orders.Count`) beside an element leaf cannot
      be read from whole documents and fails at compile time under `DriverLinq`; the native modes answer it.
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
  - **A function computed from such a property is refused too** (EF-465): a string method, `Length`/date part, arithmetic
    or `Math` over it, in a filter, projection or group key. Predicate: `StoredOrdering.PreservesClrValueForComputation`
    (default storage, or a number the server holds exactly). The bridge runs it per node
    (`ThrowIfComputedFromStoredValue`, the walker with `StoredOrderingUse.Computation`); native computed leaves already
    require `AllFieldsDefaultSerialized`, and `IsRegexSearchableString` (Contains/StartsWith/EndsWith/Like/case-folded
    equality) declines any converter. Reading the property and equality stay exact.
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
- **A non-deterministic call must stay in the tree for `NonDeterministicCalls` to refuse it.** EF's default filter only keeps
  `Random.Next` and `Guid.NewGuid`; `MongoEvaluatableExpressionFilter` keeps every call `IsNonDeterministic` recognizes
  (`NextDouble`, `NextSingle`, `NextInt64`, `Guid.CreateVersion7`), or EF parameterizes it and every row shares one value (EF-464).
- **Read modes: how a non-nullable value read back treats null and MISSING.** Driver-LINQ is the oracle: a malformed
  document must answer what it answered there, never a silent `0`. Emit side decides once per value leaf
  (`MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead`; projected Distinct over a bare field uses
  `ClassifyMalformedFieldRead`):
  - `Plain`: a bare field leaf, read through its property (D-F10). Non-nullable fields are never flagged on the emit side
    (it would decline `Min`/`Max`/`Average`/group keys over every `x.A + x.B`).
  - `ThrowOnNull`: every operator between a null and the leaf propagates it (`Length`/`IndexOf`, date operator over a
    nullable date, `x.Score.Value + 1`): read as `T?`; an operator that may absorb the null declines. Group
    keys/accumulators over a date operator over a nullable date decline (EF-461); `x.Score.Value + 1` there still reads `0`.
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
  Normalization covers top level, `$unionWith` and `$lookup` sub-pipelines (an element leaf's parameterized `Take`).
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
