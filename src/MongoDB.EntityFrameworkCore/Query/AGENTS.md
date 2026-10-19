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

```
QMTEV (Visitors/MongoQueryableMethodTranslatingExpressionVisitor)
   ├─ populates the native IR (MongoSelectDefinition) via NativeSlotPopulator / NativeProjectionBinder / other Native*Binders
   └─ always also captures the raw chain (CapturedExpression) for the fallback
MongoShapedQueryCompilingExpressionVisitor = THE GATE (compile time; ClassifyNativeDisposition)
   native:   MongoSelectLowerer (BSON-free) → MongoPipelineFactory (template + placeholders; renderers produce BSON)
   fallback: MongoEFToLinqTranslatingExpressionVisitor rewrites CapturedExpression
```

The template is built once at compile time; only `factory.Build(parameterValues)` runs per execution. `Select.Route`
is the authoritative is-native signal. `MongoExpression.VisitChildren` is a no-op; every consumer hand-rolls a
`switch`. `MongoQueryTranslationPreprocessor` lifts `VectorSearch` out before nav-expansion (which crashes on it) and
re-inserts it after; re-check if you reorder the preprocessor.

## Invariants

Breaking these usually yields **silently wrong rows**, not a failure.

Gate and fallback:

- **The gate must call the fix's predicate, not restate it.** A gate admitting more than the fix handles yields wrong
  rows; share one predicate.
- **A recognizer must not mutate then decline.** Stage into locals, commit only after every gate passes
  (`NativeProjectionBinder`'s commit block).
- **Post-terminal gating.** After a terminal operator (`GroupBy`, `Distinct`, set op, `SelectMany` unwind —
  `HasTerminalOperator`) every later operator must decide explicitly if it can stay native. A `Translate*`
  override bypasses the shared gates and must replicate the check (incl. `SelectMany` after a terminal).
- **Some shapes have no driver-LINQ oracle** (`SelectMany` over a reference collection, `Intersect`/`Except`,
  correlated-reducer projection leaf, `Reverse`): they hard-fail (`null`) in every mode; don't mark them
  non-native.
- **Structural classification beats metadata/depth/CLR-type shortcuts** (query filters, join-hop depth,
  self-referencing types defeat them). `DriverLinq` is no escape hatch for a misclassification.
- **Guards that decline often become routers** for a later feature; check before loosening one.
- **Unsupported shapes call `MarkNotNativelyRepresentable()`** so `Route` becomes `Fallback` instead of emitting a
  pipeline that drops the operator.
- **Bulk `ExecuteUpdate`/`ExecuteDelete` (EF9+) also uses the driver-LINQ bridge** (`MongoEFToLinqTranslatingExpressionVisitor`).
  Retiring the query fallback does not retire it.
- **New native join shapes must be tested under explicit `DriverLinq`** too; the native path can mask a broken
  fallback (and the reverse).
- **A recognizer that turns a correlated `DbSet` subquery into a navigation** (`db.Orders.Where(o => o.CustomerId ==
  c.X)` → `c.Orders`) must prove `c.X` is that navigation's FK principal key via `NativeCorrelationMatcher` (single-
  property FK; an alternate key via `ForeignKey.PrincipalKey`; a composite key declines). Binding emits
  `$lookup {localField: principalKey}`, so any other outer member silently reads the key-correlated rows.

Scope, joins, grouping:

- **Scope resolves by parameter identity (`ReferenceEquals`), never member name** (shared property names across
  types is the regression). Primitive: `MongoExpressionTranslator.TryBeginOwnedHopWalk`.
- **Multi-scope join projections**: a trailing `Select` over `Joins.Count >= 2` is native only for a whole-entity
  leaf or a leaf resolving to exactly one chain scope.
- **Collection Include over a join scope** (`ti => Include(ti.Outer…, nav)`) is native only on the root scope. A join
  holding the Include's `_lookup_<Nav>` alias would be collapsed into it by `AddLookup`: at depth 1 the join is
  renamed (so an Inner-side op, `JoinInnerAccessConfirmed`, declines); over a chain no join is renamed and the Include
  renames itself (`existingIncompatibleLookup`), keeping later joins' `localField`s and Inner filters valid. Renames use
  `MongoQueryExpression.GetUnusedLookupAlias`. A join level the binding would take as the Include's intermediate
  (a lookup satisfying `LookupExpression.IsCollectionIncludeIntermediateFor(root type)`) declines: the binding nests by type, not alias.
- **Navigation-less joins** are native iff `RebindInnerShaperToOuterQuery`'s raw-key branch resolved both keys
  (`JoinInfo.Lookup != null`); a navigation resolved to the wrong target is rebuilt by it, a non-simple key
  declines (`JoinLookupImplementsKeySelectors`). Composite-PK components live at `_id.<Name>`
  (`LookupExpression.GetFieldPath`). "Simple" includes an anonymous key of scalar properties (same anonymous type
  both sides, one hop, `StoredSerialization.StoredAlike` per pair), rendered as `let` + `$and`.
- **Paging vs. joins.** EF hoists `Skip`/`Take`/`Where`/`OrderBy` ahead of a join's result selector; recorded ops
  are deferred until after the join unless the join is in the left-outer-reference-navigation "safe to page
  before `$lookup`" set (reducers there decline). Paging ahead of a row-multiplying join stays ahead; paging both
  before and after declines; paging *between* joins declines (`HasPagingRecordedBetweenJoins`). Deferring paging
  past a non-1:1 join in a GroupBy silently changes group counts, so that declines.
- **GroupBy over a join scope** (`GroupByJoinScope`) needs a `TransparentIdentifier` key parameter and is
  confirmed only at the binder's commit point. Key parts / accumulators / conditions that may read an unmatched
  left-outer side decline unless every operator propagates missing as null
  (`MongoGroupElementTranslator.PropagatesMissingAsNull`). A finalized grouping or Distinct takes precedence over a
  confirmed join's inner access.
- **Post-group operators resolve by the Select's output alias** (`MongoProjectedAliasScope`), never key-part name
  or entity property; only `Where` is native. A `$push` list (`g.Select(e => e.X).ToList()`) is null-safe for
  nullable/reference elements; entity elements decline.
- **Set ops form a tree; each `Union`'s dedup belongs to its own link**, never hoisted or flattened
  (`A.Concat(B.Union(C))` differs from a left chain). Ops recorded between links go to `TrailingOps`, then move to
  the new link's `PrecedingOps` (`AppendSetOperation`). `Intersect`/`Except` never nest.
- **Projected set-op operands are read through source1's shaper.** Bare scalars with different aliases re-alias
  source2 (`CanAlignBareScalarAliases`). A constant/parameter on source1, including inside a projected Distinct's
  key parts, declines unless `CanRebindConstantLeafToDocument` rebinds it (bare, ungrouped, int/long/double/bool/
  string). source1 may be a single-level confirmed join scope (`IsPreCombineJoinScope`); source2 has no lookups.
  Projected operands must store each alias the same way (`OperandSerializationsMatch`: same property, both default,
  or `StoredSerialization.StoredAlike`).
- **One "stored alike" rule** (`StoredSerialization.StoredAlike`) for two properties whose stored values are compared
  or read through one serializer: equal `BsonRepresentation`; converters absent, the same instance, or the same type
  and provider type with structurally equal to/from-provider expressions (closures compare by reference: separately
  created closures decline). Don't add a per-caller variant.
- **A composite `$group` `_id` omits a missing sub-key**; `MongoPipelineFactory.RenderCompositeKeyPart`
  `$ifNull`-normalizes every possibly-null part once. Key-only accumulator conditions use `NullSafeKeyRead`.

Rendering (null/missing/dialect semantics):

- **Every string constant/parameter in the aggregation dialect is `$literal`-wrapped** (else `"$Field"` user input
  is read as a field path). New operand positions must go through `RenderBranch` or `RenderOperand`.
- **Negation is exact complement or decline.** `$eq`/`$ne` may be inverted; `$lt/$lte/$gt/$gte` must be
  `$not`-wrapped (they don't partition missing/null). A negated `&&`/`||` is De Morgan'd by
  `MongoExpressionNegator` or declines — never an aggregation `$not`.
- **The aggregation dialect orders null/missing below every value**, so bare `$lt`/`$lte` (or `$gt`/`$gte` vs a null
  right side) answer true where C# answers false. The renderer conjoins `$gt: [<lower side>, null]` when
  `MayBeNull`. Non-nullable-typed values that may be *missing* (unmatched left-join side) get no guard; conditions
  over them decline.
- **Top-level aggregation `$eq`/`$ne` against null is `$ifNull`-wrapped**, but not inside a `$filter`/`$map` scope.
- **`$expr` inside `$elemMatch` is a hard server error**; reject at `IsQueryDialectRenderable`. `$size` on a
  missing/null array also errors: `$ifNull` around `$size`/`$filter` is mandatory.
- **Relational comparisons, sort keys and aggregates run on the stored form**: over a converted or non-default-
  represented property they decline natively and the driver-LINQ bridge throws (EF-337). Shared predicates:
  `StoredOrdering.PreservesClrOrdering` (comparison vs a value) and `PreservesClrOrderingForAggregateAndSort` (adds
  integral narrowing converters); equality is never gated. Native aggregates stay on `HasDefaultKeySerialization`. The
  bridge walks the whole key/operand (`??`, `?:`, projected members, `g.Key`, set-op sources); an unclassifiable
  construct is refused if the query reads such a property.
- **Non-default-serialized bools are truthiness-tested wrongly** (`HasConversion<string>()` stores `"True"`/
  `"False"`, both truthy): gate `$not`/`$and`/`$or` operands and bare boolean roots via
  `MongoExpressionTranslator.IsUnsafeTruthinessRoot`.
- **Negator, dialect classifier and both renderers must agree** (`MongoExpressionNodeCoverageTests` checks every
  `MongoExpression` subtype x dispatcher). It is blind to shape-conditional dispatch (e.g. `MongoRegexExpression`
  constant vs field-to-field); those need dedicated tests.
- **A node needing different handling at 3+ sites is a sealed sibling type, not a bool flag**
  (`MongoFilteredSizeExpression` beside `MongoSizeExpression`).
- **Element-scoped nodes bind their own variable** (`MongoFilteredSizeExpression`, `MongoElemMatchExpression`,
  `MongoQuantifierExpression`); `MongoFieldPrefixRewriter` prefixes only the array path and declines on unknown
  nodes.
- **String operators that don't propagate null are null-guarded at render** (`NullPropagating`; `$concat`
  coalesces to `""`). A non-nullable-typed projection leaf over a guarded `Length`/`IndexOf` is flagged
  `MongoProjection.ThrowsOnNull` and read as `T?`, throwing EF's "Nullable object must have a value." on null; one
  whose null an operator may absorb (`$max`/`$min`, `Sign`) declines (`ClassifyNonNullableValueRead`). Group keys,
  accumulators and `Min`/`Max`/`Average` over such values decline rather than read null as `0`, including over an
  upstream alias: a reference to a flagged Distinct key / bare Select / set-op operand carries
  `MongoElementRefExpression.ThrowsOnNull` (from `MongoGroupingKeyPart.ThrowsOnNull` or `FindThrowOnNullProjection`).
- **`$toLower`/`$toUpper` are never emitted natively** (ASCII-only): `Where` becomes an anchored case-insensitive
  regex; `Select` projects the raw string and re-applies the mapping client-side. After such a leaf
  (`HasClientCaseMappingProjectionLeaf`) value-reading operators decline. Grouped results stage the receiver and
  re-apply (`NativeGroupByBinder.PeelResultMemberCaseMapping`). A GroupBy query that would run on driver-LINQ with a
  case mapping anywhere in its chain throws (`ThrowIfDriverLinqCaseMapsGroupedResult`); it does not search
  `GroupBy` inside lambdas, and ungrouped driver-LINQ queries still emit `$toUpper`/`$toLower`.
- **DateTime read-back must honour the property's `DateTimeKind`** (`NativeDateTimeKindReadBack`): trace to the
  backing `IProperty`; untraceable values, whole-document class-map reads with kind-sensitive members, and set ops
  whose operands differ in kind shape decline. Don't fold this into `HasDefaultKeySerialization`. Server-side
  date parts over Local-kind properties compute in UTC on both paths (EF-459).
- **Clock members (`DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`) are never baked into the template.**
  A maximal closed subtree holding one (bool/DateTime typed) becomes a `MongoParameterExpression` with a
  `RuntimeEvaluator`, evaluated once per `Build` and serialized like a parameter (Local → UTC instant, as the
  driver does); one predicate (`RuntimeClock.IsRuntimeEvaluable`) gates `IsSimpleValue` and the factory.
  `TryEvaluateClosedSubtree` declines any clock; its `SpecifyKind` relabel is for literals only.
- **Local-collection folding never folds a non-deterministic call or a clock member**
  (`LocalCollectionFilterFoldingVisitor`: `NonDeterministicCalls.ContainsNonDeterministicCall`, `RuntimeClock.ContainsClock`).
  The fold runs in the preprocessor for every mode, so a folded `Guid.NewGuid()`/`DateTime.UtcNow` would be evaluated
  once and baked into the cached plan.
- **`DateTime.TimeOfDay` is a projection leaf only** (`TryTranslateTimeOfDayLeaf`, never `TranslateOperand`): it is
  milliseconds, read by alias through `BsonSerializerFactory.TimeOfDayMillisecondsSerializer`
  (`MongoSelectDefinition.IsTimeOfDayProjection`). `AllFieldsDefaultSerialized` answers false for it, a projected
  `Distinct` declines it, and a set op declines unless both operands' alias is TimeOfDay (`OperandSerializationsMatch`),
  so later operators never read it as a default `TimeSpan`; Local-kind receivers decline.
- **Read modes: how a non-nullable value read back from the server treats null and MISSING.** Main (driver-LINQ)
  is the oracle: a malformed document must answer what it answered there, never a silent `0`. Emit side, one call per
  value leaf, both gate and flag: `MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead`
  (`NonNullableValueRead`); a projected Distinct over a bare field uses `ClassifyMalformedFieldRead`. The stack:
  - `Plain`: a bare field leaf, read through its property (D-F10 below). Non-nullable fields are never flagged on the
    emit side (that would decline `Min`/`Max`/`Average`/group keys over every `x.A + x.B`).
  - `ThrowOnNull` (`MongoProjection.ThrowsOnNull`): every operator between a null and the leaf propagates it
    (`Length`/`IndexOf`, a date operator over a nullable date, an operator over a nullable field: `x.Score.Value + 1`,
    `Math.Abs(...)`, `c ? x.Score.Value : 0`): read as `T?`, throwing EF's "Nullable object must have a value."; an
    operator that may absorb the null declines (`Decline`). Group keys/accumulators over a date operator still read
    `0` (EF-461).
  - `ThrowOnMalformedNull`: a computed leaf over a non-nullable field (`x.Rank + 1`), null only when a document omits
    the field: read strictly on the `Projection` route; read side only (every emit-side caller treats it as `Plain`); a
    Distinct flatten carries it as `MongoProjection.ThrowsOnMalformedNull`.
  - `DefaultOnMalformedMissing`: the leaf selects the field itself through `$cond`/`$ifNull` value positions
    (`x.Rank > 1 ? -1 : x.Rank`, `x.Score ?? x.Rank`), so the server answers MISSING as driver-LINQ did: MISSING reads
    `default`, null throws. Only where native answers MISSING exactly where the driver does (`MayAnswerMissing`): a field
    under a widening the translator dropped (the driver's `$toLong` answers null) makes the leaf strict
    (`ReclassifyMalformedReadAs`).
  - bool null-as-false: the driver's `BooleanSerializer` reads null as `false`, so a malformed `bool` never throws
    (`DriverReadsNullAsDefault`, honoured by the strict alias read, `ClassifyMalformedFieldRead`, the wrapped read and
    `BsonBinding.GetScalarProjectionValueAtElement`).
  - Identity and enum relabel casts (`TryCreateRequiredScalarCastRead`): an identity cast staged as the field reads like
    `x.Rank` (also over a nullable source: missing → `default`, null throws); a `$toX` cast reads strictly
    (`(long)x.Rank`, `(long)x.Score`); an enum's cast to its underlying type (`MongoExpressionTranslator.IsEnumUnderlyingRelabel`,
    int- and long-backed; byte-backed declines) reads the stored integer, missing → `default`.
  - Distinct markers: a projected Distinct key that would turn MISSING into null (a lone `$group` key does) carries a
    `$type` missing marker (`MongoGroupingKeyPart.MarksMissing`), and its flatten restores MISSING
    (`MongoProjection.DefaultsOnMalformedMissing`), so a missing and a null value are two groups (read `default` /
    throw). A value-converted key read as its own type is marked too and read through its property, so the converter
    applies (`ReadsConvertedDistinctKeyThroughProperty`); the set-op gate's `StoredField` sees through a marked flatten
    to the key's field, so `OperandSerializationsMatch` still compares stored forms.
  - Aggregate `{_v: ...}` reduction: a terminal non-nullable `Min`/`Max` reduces `{_v: operand}` documents as
    driver-LINQ does (`NativeAggregateReadBack.ReducesWrappedValue`, shared by lowerer and reader; MISSING winner →
    `default`, null → throws; an unfaithful MISSING is `$ifNull`'d to null); a non-nullable `Average`'s null throws.
- **D-F10: on the `Projection` route a MISSING bare stored scalar reads `default(T)`, as driver-LINQ's `$project`
  push-down did; an explicit BSON null throws** (a `bool` reads false). This covers root leaves
  (`BsonBinding.GetScalarProjectionValueAtElement`) and join-scope leaves (`o.Customer!.Rank` over an unmatched
  optional reference: `ReadsJoinScopeBareScalarThroughProperty`, read by `TryCreateJoinScopeBareScalarRead`).
  Whole-entity reads stay strict ("Document element ... is missing"). The exception TYPE for malformed stored data
  (native `InvalidOperationException` vs the driver's `FormatException`/converter exceptions) is not contract; the
  outcome (value vs throw) is.
- **`TranslateOperand` may return an enum-typed `MongoFieldExpression` for `(int)x.E`** over a default-serialized
  enum field (`IsEnumUnderlyingRelabel`: the stored value is already the integer), so an operand's `Type` may be the
  enum, not the cast target. Callers comparing or reading by type must allow for it.
- **A null parameter for a non-nullable property serializes as BSON null** (`BsonValueSerializer.SerializeNullAware`),
  never through the property's non-nullable serializer, so `x.Rank == p` with `int? p = null` matches the null/missing
  rows, as driver-LINQ did.

Shapers and projections:

- **The mixed reader reads a computed scalar leaf whole** (`IsClientComputedLeaf`, `TryBindClientComputedLeaf`,
  admitted nodes in `ClientLeafChecker`); operand-by-operand binding makes every operand read as the last one
  bound. Ambiguous member-less construction arguments are recorded in `AliasedConstructionMembers` and the mixed
  reader declines them loudly.
- **Row-independent leaves are evaluated by the shaper, never projected** (`NativeProjectionBinder.IsRowIndependentLeaf`,
  admitted only after the `$literal` path declines; an all-client projection stages the constant sentinel `_c`).
  `HasClientEvaluatedProjectionLeaf` makes set ops (`IsPlainProjectedSelect`/`IsPlainDistinctSelect`) and every
  value-reading later operator except `Distinct` decline.
- **A ternary over constructions is read whole only if its type's class map reads each member by name**
  (`IsMisreadWholeValueLeaf`, called by every binder staging a ternary/coalesce leaf: projection, join-scope,
  GroupBy; `Id` maps to `_id`, so `new P { Id = ... }` would throw FormatException). Otherwise
  `NativeProjectionBinder` stages only the test, as the member's bool leaf, and the shaper evaluates the ternary
  (`TryCollectClientConditionalBranches`; read side admits it only via `IsClientConditionalLeaf`). Branch row reads
  are staged as `_cr<N>` and the read side takes each alias from the staged read (`TryGetClientConditionalReadAlias`,
  registered as an explicit alias for `ApplyProjection`), never from the member path. `HasClientConditionalProjectionLeaf`
  declines set ops, value-reading operators and `Distinct`.
- **Client-only bodies over the whole entity** (a client method on it, a combinator around one, or a construction
  with a whole-entity operand: `new object[] { x }`, `new Wrapper(x) { City = x.City }`) fetch whole documents
  (`HasClientWrappedWholeEntityShaper`, so set ops decline). Binder and shaper gate both call
  `NativeClientWholeEntityShape`; at least one whole-entity operand is required (`new[] { x.Id }` must not match).
- **Positional containers of scalars** (`new[] { x.A, x.B }`, `new object[] { ... }`) are `_ctorArg<N>` leaves read
  by index (`HasPositionalCtorProjectionShaper`); both sides peel the boxing `Convert` with
  `NativeProjectionBinder.UnwrapContainerElementBoxing` so each element keeps its runtime type. A whole-entity
  argument/element never takes the scalar positional path (`IsScalarPositionalConstruction`: its `$$ROOT` read is a
  class-map deserialization that throws). Containers are an opt-in of `TryGetProjectionMembers`
  (`allowContainerElements`, passed only by the plain-`Select` callers: `NativeProjectionBinder` and
  `NativeJoinScopeProjectionBinder`); never widen `allowPositionalConstructorArguments`, which the
  `GroupBy`/`SelectMany` result selectors share. `new List<T> { ... }` is not a container: the driver can't push a list
  initializer down, and the index-based shaper can't be read off whole documents.
- **A `NativeComputedLeafExpression` is read whole only by the native alias reader**; every other shaper visitor
  must use its `ClientExpression`.
- **Mixed-projection alias agreement.** An array/owned-nav projection leaf's alias must equal the navigation's
  document path, or a fallback silently returns an empty collection (`NativeArrayProjectionTests`).
- **`ProjectionMapping` keys must match exactly** what the shaper expects; `CapturedExpression` is set once at the
  tail of `VisitMethodCall` (mid-chain truncates the query).

## Conventions and traps

- **Layering:** Query never calls `IMongoCollection<>.Aggregate` (Storage executes via
  `MongoClientWrapper.Execute`), never instantiates serializers (`BsonSerializerFactory`), and only reads
  metadata. The lowerer is BSON-free. Bulk writes cross to Storage as behavior (`MongoBulkPlan` delegates), not data.
- **Allowed method sources** are `Queryable`, `MongoQueryableExtensions`, `MongoDB.Driver.Linq.MongoQueryable`, plus
  EF8/EF9's internal `LeftJoin` shim (admitted by `MethodInfo` identity).
- **Adding a native operator:** slot-style needs `NativeSlotPopulator.PopulateNativeSlots` and
  `IsNativeRepresentableSlotOperator`; a `Translate*`-override operator needs the override and
  `IsNativeRepresentableSlotOperator`. QMTEV's `TranslateWhere/OrderBy/ThenBy/Skip/Take` are deliberately inert
  (`=> null`); routing them through the switch double-populates slots.
- **`MethodInfo` reference equality** needs canonical constants (`QueryableMethods`, `EnumerableMethods`).
- **Paging:** MongoDB rejects `{$limit: 0}`, so `Build` rewrites it to an always-false `$match`; negative values
  throw. Normalization covers top level and `$unionWith`, not `$lookup` sub-pipelines.
- **TPH `$lookup` to a derived type must narrow by discriminator** (done in `LookupExpression`'s constructor).
- **EF query cache** keys on tree shape; changing output for a previously translatable tree invalidates user
  caches.
- Visitor signatures differ across EF8/EF9/EF10; guard with `#if`.

## Testing

- **MQL shape cannot prove a query went native**; use `MongoQueryMode.NativeOnly` (`MONGODB_EF_QUERY_MODE=NativeOnly`
  flips every spec and functional context; `MONGODB_EF_NATIVE_ONLY=1` is an alias).
- For result-affecting native shapes, write a `[Theory]` comparing native results to an in-memory LINQ oracle
  over ragged/missing/null/empty data (`NativeOwnedCollectionAllTests`, `NativeModeAssert`).
- MQL baselines are regenerated (see SpecificationTests `AGENTS.md`).
- Tests: `tests/…UnitTests/Query/` (native in `NativeTranslation/`), `tests/…FunctionalTests/Query/`; filter
  `FullyQualifiedName~Query`.
- **Three-mode rule.** A parity test runs one query under `NativeOnly` (proves it goes native), `Native` (same
  results) and `DriverLinq` (the oracle).
- **Ragged-seed rule.** Seed missing / explicit-null / empty-array / populated rows with raw `BsonDocument`
  inserts, never via the context: materialization normalizes missing owned collections to empty, so a
  context-seeded fixture can't express the ragged states.
- **Which helper** (`NativeModeAssert`): `NativeAndParity` when driver-LINQ is trustworthy;
  `NativeAndExpected(..., driverKnownWrong: true)` with a hand-written expected list when driver-LINQ is wrong
  (name the bug ID; the test breaks when the driver is fixed); `TwiceWithDifferentValues` for parameterized
  queries, called once per mode (three-mode rule), using one lambda shape so the second run hits the compiled-query cache. Both runs must share one `DbContext` instance:
  `SingleEntityDbContext`/`IgnoreCacheKeyFactory` gives each instance its own model, so separate instances never
  share compiled queries.
