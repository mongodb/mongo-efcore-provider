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

Scope, joins, grouping:

- **Scope resolves by parameter identity (`ReferenceEquals`), never member name** (shared property names across
  types is the regression). Primitive: `MongoExpressionTranslator.TryBeginOwnedHopWalk`.
- **Multi-scope join projections**: a trailing `Select` over `Joins.Count >= 2` is native only for a whole-entity
  leaf or a leaf resolving to exactly one chain scope.
- **Navigation-less joins** are native iff `RebindInnerShaperToOuterQuery`'s raw-key branch resolved both keys
  (`JoinInfo.Lookup != null`); a navigation resolved to the wrong target is rebuilt by it, a non-simple key
  declines (`JoinLookupImplementsKeySelectors`). Composite-PK components live at `_id.<Name>`
  (`LookupExpression.GetFieldPath`).
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

Shapers and projections:

- **The mixed reader reads a computed scalar leaf whole** (`IsClientComputedLeaf`, `TryBindClientComputedLeaf`,
  admitted nodes in `ClientLeafChecker`); operand-by-operand binding makes every operand read as the last one
  bound. Ambiguous member-less construction arguments are recorded in `AliasedConstructionMembers` and the mixed
  reader declines them loudly.
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

- **MQL shape cannot prove a query went native**; use `MongoQueryMode.NativeOnly` (`MONGODB_EF_NATIVE_ONLY=1`
  flips every spec context).
- For result-affecting native shapes, write a `[Theory]` comparing native results to an in-memory LINQ oracle
  over ragged/missing/null/empty data (`NativeOwnedCollectionAllTests`, `NativeModeAssert`).
- MQL baselines are regenerated (see SpecificationTests `AGENTS.md`).
- Tests: `tests/…UnitTests/Query/` (native in `NativeTranslation/`), `tests/…FunctionalTests/Query/`; filter
  `FullyQualifiedName~Query`.
