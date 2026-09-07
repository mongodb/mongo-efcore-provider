---
area: Query / LINQ translation
scope: ["src/MongoDB.EntityFrameworkCore/Query/**"]
reviewer-agent: query-reviewer
adjacent-areas: [Storage, Metadata, Serializers, "C# driver LINQ v3"]
---

# Query — AGENTS.md

## Scope

Translates `IQueryable<T>` against MongoDB-backed DbSets into aggregation pipelines. **Two paths**, chosen at
compile time by the `MongoQueryMode` option:

1. **Native (default).** The provider builds the `BsonDocument[]` pipeline itself from a structured query tree
   and runs it via `IMongoCollection<>.Aggregate`. The driver's LINQ provider is not involved.
2. **Driver-LINQ (fallback).** Everything outside the native slice is handed to the driver's LINQ v3 provider.

Per the top-level `AGENTS.md` rubric: **which path a query takes, the exact MQL it emits, and the exception
type for an unsupported shape are all implementation details, not contract.** A fallback → native transition
with unchanged results is never a breaking change.

> **Per-feature history is deliberately not in this file.** Which ticket widened what, measurement tables and
> spec-suite deltas live in git history and in the tests named below. This file holds only what you need before
> touching the area. Keep it that way — it was allowed to grow ~10x once, and the result was a guard documented
> here in detail that did not exist in the code.

## Pipeline at a glance

```
IQueryable<T>  (EF Core)
   │
   ▼  MongoQueryCompilationContext           (preserves the original LINQ tree; carries the MongoQueryMode)
   ▼  MongoQueryTranslationPreprocessor      (hoist final predicates; lift VectorSearch out before nav expansion)
   ▼  MongoQueryableMethodTranslatingExpressionVisitor (QMTEV)
   │      ├─ accepts only Queryable / MongoQueryableExtensions / MongoDB.Driver.Linq.MongoQueryable
   │      ├─ DELEGATES slot population to NativeSlotPopulator (the ordered PipelineOps list) and projection
   │      │     binding to NativeProjectionBinder; either marks the select non-native for anything it can't lower
   │      └─ ALWAYS also captures the raw method chain (CapturedExpression) for the fallback
   ▼  MongoQueryTranslationPostprocessor     (apply final ProjectionMapping)
   ▼  MongoShapedQueryCompilingExpressionVisitor  ── THE GATE (compile time, honors MongoQueryMode) ──
   │   if Native & Select.Route != Fallback & lower/render succeed → NATIVE:
   │      MongoSelectLowerer  : PipelineOps → MongoPipelineStage[] (verbatim arrival order; then $lookup/terminal)
   │      MongoQueryLanguageRenderer + PlaceholderTable : predicate → $match BSON; params → sentinels
   │      MongoPipelineFactory.Create : render ONCE into a template + placeholder table
   │      streaming shaper (MongoStreamingEntityMaterializerRewriter) if eligible, else DOM
   │   else → DRIVER-LINQ FALLBACK: MongoEFToLinqTranslatingExpressionVisitor rewrites CapturedExpression; DOM shaper
   ▼  MongoExecutableQuery + QueryingEnumerable → MongoClientWrapper.Execute → IAsyncCursor → shaper
```

The template is built **once at compile time**; only `factory.Build(parameterValues)` runs per execution.
Native-vs-driver and streaming-vs-DOM are therefore **compile-time-deterministic** — one shaper per query, no
runtime dispatch.

## Key entry points

| File | Responsibility |
|---|---|
| `MongoQueryTranslationPreprocessor` | First phase. **Extracts `VectorSearch(...)` before nav-expansion and re-inserts after** — nav expansion crashes on it. |
| `Visitors/MongoQueryableMethodTranslatingExpressionVisitor` | LINQ-method dispatcher; enforces the allowed-method-source set; delegates slot/projection work; captures the final chain. Also carries bulk `ExecuteUpdate`/`ExecuteDelete`. |
| `Visitors/MongoShapedQueryCompilingExpressionVisitor` | **The gate.** `ClassifyNativeDisposition` → `{Native, Fallback, HardDecline}` from three signals: `Select.Route`, `IsGroupByFallbackUnsafe`, and an unbound vector search. Streamability (`AllPendingLookupsAreStreamable`) is a separate streaming-vs-DOM axis, *not* an is-native signal. |
| `Expressions/MongoSelectDefinition` | The native logical IR: ordered `PipelineOps` (+ `TrailingOps` once a set op attaches; `ActiveOps` flips), `Projection`, `Cardinality`, `Grouping`, `VectorSearch`. `Route` (`NativeRoute`) is the authoritative is-native signal. Dialect-neutral — no BSON. |
| `Expressions/MongoQueryExpression` (+ `.Lookup.cs`) | Has-a `MongoSelectDefinition`, plus cross-collection `$lookup` state and the retained `CapturedExpression`/`ProjectionMapping` for the fallback. |
| `Expressions/MongoExpression` + 21 subtypes | The dialect-agnostic node hierarchy (`SqlExpression` analog). `VisitChildren` is a no-op — every consumer hand-rolls a `switch`. |
| `NativeTranslation/MongoExpressionTranslator` (4 partials) | EF predicate/key/value body → `MongoExpression`. Returns `false` for anything outside its acceptance set. |
| `NativeTranslation/MongoSelectLowerer` | Native IR → `MongoPipelineStage[]` (typed, **BSON-free**); owns lookup-eligibility guards. |
| `NativeTranslation/MongoQueryLanguageRenderer` | Renders the **query dialect** (`$match` bodies). Keeps `&&`/`\|\|` at the query level and wraps only non-query-expressible subtrees in `$expr`, so indexable clauses stay indexable. |
| `NativeTranslation/MongoAggregationExpressionRenderer` | Renders the **aggregation-expression dialect** (inside `$expr`/`$project`) — the only one that can express field-to-field comparisons and arithmetic. |
| `NativeTranslation/MongoPipelineFactory` | Stages → `BsonDocument[]` template + placeholder table; `Build` clones and substitutes. Validates `$limit>0`/`$skip≥0`. |
| `NativeTranslation/Native{SlotPopulator,ProjectionBinder,CardinalityBinder,GroupByBinder,SelectManyBinder,JoinScope*}` | The per-shape recognizers that populate the IR. |
| `NativeTranslation/MongoStreamingEntityMaterializerRewriter` + `StreamingEligibility` | One-pass forward-only `IBsonReader` → POCO materialization; deserialization *is* materialization, no DOM. |
| `Visitors/MongoProjectionBindingRemovingExpressionVisitor` (+ `Mixed…` subclass) | DOM read side: `ProjectionBindingExpression` → concrete `BsonDocument` reads. The `Mixed` sibling reads whole *un-projected* documents (fallback/mixed leg). |
| `Query/MongoIncludeFixups` | `Include` navigation fix-up shared by **both** read paths (DOM and streaming). |
| `ExpressionExtensionMethods` (provider root) | Shared expression-shape helpers: `TryGetProjectionMembers`/`RebuildProjectionMembers`, `TryGetMemberOrEFProperty`, `ReferencesParameter`, `RemoveConvert`. |

## Durable invariants

These are the rules that cost real bugs to learn. Breaking one usually produces **silently wrong rows**, not a
failure.

- **Post-terminal gating.** Once a select has a terminal operator (`GroupBy`, `Distinct`, a set op, a
  `SelectMany` unwind — `HasTerminalOperator`), every later operator must decide explicitly whether it can
  still go native. Two catch-alls gate broadly (`NativeSlotPopulator`, `NativeCardinalityBinder`); any operator
  with its **own** `Translate*` override bypasses them and must replicate the check. A gate must call the same
  structural predicate its companion rewrite relies on — a gate *wider* than the rewrite is a wrong-data trap
  that has recurred here more than once.
- **Some families have no driver-LINQ oracle at all**: `SelectMany` over a reference collection,
  `Intersect`/`Except`, the correlated-reducer projection leaf, and `Reverse`. For these, an out-of-scope shape
  returns `null` (EF's own translation-failure path) rather than marking non-native — there is no working
  fallback to land on, so they hard-fail in **every** `MongoQueryMode`. Confusing the two dispositions either
  crashes on a shaper rebuild the fallback can't perform or pins a shape to the wrong exception family.
- **Scope is resolved by parameter IDENTITY, never by member name.** Anywhere an outer scope must be told from
  an inner/element scope, route by `ReferenceEquals` against the scope's own parameter. Two types sharing a
  property name (`Item.Name` vs `Owner.Name`) is the standing regression test. The shared primitive is
  `MongoExpressionTranslator.TryBeginOwnedHopWalk`; don't reintroduce a per-resolver copy.
- **Structural classification beats metadata/depth/CLR-type shortcuts.** A TPH-inherited or EF10-named query
  filter isn't visible through `GetQueryFilter()`; join-hop depth doesn't distinguish a root hop from a
  transitive one; a self-referencing entity type defeats a CLR-type check. Walk the actual tree. These
  misclassifications are **mode-independent** — `UseQueryMode(DriverLinq)` is not an escape hatch.
- **The negator's contract is exact complement or decline, never an approximation.** `$eq`/`$ne` may be
  *inverted* because they partition every BSON value including missing/null; the four relational operators must
  be `$not`-**wrapped**, never inverted, because `$gt`/`$lte` match neither a missing nor an explicitly-null
  field. An array-count comparison is the documented exception in the other direction (its rendered `$exists`
  form *does* partition). The general rule: ask whether the **rendered** pair partitions the value space.
- **Negator ↔ query-dialect classifier ↔ both renderers must agree.** A node the negator can produce that a
  renderer refuses is a hard failure at execution time, not a decline. This is now **enforced**, not
  conventional: `MongoExpressionNodeCoverageTests` reflection-discovers every `MongoExpression` subtype and
  characterizes it against all seven dispatchers, so adding a node type or changing any dispatcher's answer
  reddens with the exact cell.
- **`$expr` inside `$elemMatch` is a hard server error, not a slow path.** Anything that can nest inside an
  `$elemMatch` (a quantifier's element predicate, a negated one) must be rejected at `IsQueryDialectRenderable`
  rather than falling through to the aggregation renderer.
- **`$ifNull` around `$size`/`$filter` is mandatory, not defensive.** `$size` against a missing or explicitly
  null array is a hard server error — not a wrong answer. Wrap every aggregation-dialect array-size path.
- **A bare non-default-serialized bool is truthiness-tested, and answers the wrong boolean.** A
  `HasConversion<string>()` bool stores `"True"`/`"False"` — both truthy. `$not`, an `$and`/`$or` operand and a
  bare boolean predicate root must all be gated by `MongoExpressionTranslator.IsUnsafeTruthinessRoot` (one
  predicate; it used to be six restatements, and a sweep had to fix all of them at once).
- **A node kind needing different handling at 3+ call sites must be a sealed sibling type, not a bool flag**
  (`MongoFilteredSizeExpression` beside `MongoSizeExpression`). A pattern naming the base type doesn't match a
  sibling, so sites that must *not* treat it alike fail closed. A flag leaves every site wrong by default —
  `MongoElementRefExpression.NullSafe` was dropped by a rewrite arm for exactly this reason.
- **Element-scoped children must not be prefixed.** `MongoFilteredSizeExpression`/`MongoElemMatchExpression`/
  `MongoQuantifierExpression` bind their own `$filter`/`$elemMatch` variable, so their element predicates are
  element-relative. `MongoFieldPrefixRewriter` prefixes the array path only, and `MongoOuterFieldExpression` is
  root-anchored and passes through untouched. It **declines** (never throws) for an unhandled node kind, so a
  miss is a fallback rather than a hard failure in every mode.
- **Alias-agreement and sibling-readability for mixed projections.** The native shaper is built
  alias-addressed at translation time, but native-vs-fallback is decided later; on a fallback the shaper reads a
  *whole document* instead. So an array/owned-nav-entity projection leaf's alias must equal the navigation's own
  document path (a renamed alias silently returns an empty collection), and once such a leaf is admitted every
  sibling leaf is swept through `IsWholeDocumentReadableLeaf`. See `NativeArrayProjectionTests`.
- **`Route == Projection` (and similar scoped state) is usually load-bearing for one of two reasons:** confining
  a new native case away from a mixed shape, or letting a re-entrant visitor recognize its own prior output.
  Both recur in `MongoProjectionBindingExpressionVisitor`.
- **Guards that decline a shape often become routers, not walls.** More than once a check that rejected a
  shape (e.g. "this predicate reaches outside its scope") later became the *signal* a feature uses to pick
  between two strategies. Look for that before loosening one.
- **MQL shape cannot prove a query went native** — see Common pitfalls.
- **A recognizer must not mutate then decline.** Stage into locals and commit once every gate has passed;
  otherwise a later decline leaves the query expression half-populated. `NativeProjectionBinder`'s commit block
  is the pattern to copy.

## Boundaries with adjacent areas

- **vs Storage.** Query stops at the executable query — it produces *data*: a `BsonDocument[]` pipeline or a
  driver-LINQ expression. `MongoClientWrapper.Execute(...)` owns execution, cursors, sessions, retries. Never
  call `IMongoCollection<>.Aggregate(...)` from Query.
- **vs Storage — bulk `ExecuteUpdate`/`ExecuteDelete` uses a deliberately *different* seam.** Reads cross as
  data; bulk crosses as **behavior** (`MongoBulkPlan` carrying translation delegates, invoked per execution by
  `MongoBulkOperationExecutor`), because bulk filter/update translation is parameter-value-dependent. Don't
  "fix" bulk to look like the read seam.
- **vs Serializers.** Ask `BsonSerializerFactory` for an `IBsonSerializer`; never instantiate one here.
- **vs Metadata.** Query *reads* `GetElementName()`, `GetBsonRepresentation()`, `GetDiscriminator*()`; it never
  writes them.
- **vs the driver's LINQ v3 provider.** Only the fallback uses it. The stable boundary is **our query tree →
  `BsonDocument[]` → `Aggregate`**; we do not build on the driver's internal AST.

## Common pitfalls

- **MQL shape cannot prove a query went native.** For filter/sort/paging the native and fallback pipelines are
  structurally identical, so asserting those substrings under `Native` passes even on fallback. The only
  reliable signal is `NativeOnly` mode. This bites hardest for reference `Include` and set operations, where
  the fallback was deliberately made to emit the *same* shape.
- **Allowed method sources are strict** — only `Queryable`, `MongoQueryableExtensions`, and
  `MongoDB.Driver.Linq.MongoQueryable`. EF8/EF9's internal `LeftJoin` shim
  (`Microsoft.EntityFrameworkCore.Internal.QueryableExtensions.LeftJoin`) is additionally admitted by
  `MethodInfo` identity, unconditionally — nav-expansion flattens both a user `GroupJoin`/`SelectMany` pair and
  EF's own optional-reference lowering onto it, and no per-call signal survives expansion to tell them apart.
- **VectorSearch extraction ordering.** It must be pulled out before nav-expansion and stitched back after. If
  you change preprocessor ordering, re-check this dance.
- **`CapturedExpression` must be a *complete* method chain** — set once at the tail of `VisitMethodCall`.
  Setting it mid-chain truncates the query.
- **ProjectionMapping discipline.** `_projectionMapping` keys must match exactly what the shaper expects; a
  mismatch is silent wrong results, not a crash.
- **Reference-equality on `MethodInfo`** requires canonical constants (`QueryableMethods` for top-level
  dispatch, `EnumerableMethods` inside projection binding). Open vs. constructed generics compare unequal.
- **Unsupported shapes are detected, not silently translated.** Anything `NativeSlotPopulator` doesn't lower
  calls `MarkNotNativelyRepresentable()`, driving `Route` to `Fallback` so the gate falls back (or throws under
  `NativeOnly`) rather than emitting a pipeline that drops the operator.
- **Adding a native operator touches two places.** A slot-style operator: `NativeSlotPopulator
  .PopulateNativeSlots` **and** `IsNativeRepresentableSlotOperator` (which now *composes*
  `IsSevenSlotOperator` rather than restating it, so the seven can't drift). A `Translate*`-override operator:
  the override **and** `IsNativeRepresentableSlotOperator`. Miss a half and the operator is silently dropped or
  double-decided.
- **The QMTEV's `Translate{Where,OrderBy,ThenBy,Skip,Take}` overrides are inert (`=> null`)** — they implement
  abstract EF members but are unreachable, because slot population is delegated at the `VisitMethodCall`
  fall-through (routing them through `base` rebuilds a fresh `MongoQueryExpression` per operator, so slots
  don't accumulate). **Do not add these to the `VisitMethodCall` switch** without first removing their
  `NativeSlotPopulator` handling, or slots double-populate.
- **The lowerer is BSON-free; the renderer/factory own BSON.** Keep `BsonDocument` construction out of the
  lowerer and out of the recognizers.
- **Non-positive paging matches EF's contract.** `Build` validates `$limit > 0` / `$skip ≥ 0` and throws
  `ArgumentOutOfRangeException`; never emit `{$limit: 0}` (the server rejects it). Note this normalization walks
  the top level and `$unionWith` inner pipelines, **not** `$lookup` sub-pipelines.
- **TPH `$lookup` joins must narrow by discriminator when the target is a derived type**, or sibling-subtype
  rows satisfying the same FK equality are admitted. `LookupExpression`'s constructor does this; a new
  `$lookup` call site that skips it reintroduces the gap.
- **EF Core query cache.** Compiled queries are cached by expression-tree shape; changing a translator's output
  for a previously-translatable tree quietly invalidates user caches.
- **Multi-EF guards.** Some visitor signatures differ across EF8/EF9/EF10. Representative shapes:
  `Storage/MongoTypeMappingSource.cs` (`#if EF8 || EF9`), `QueryingEnumerable.cs` (`#if !EF8`).

## How to test

- **Proving native.** Run under `MongoQueryMode.NativeOnly` and assert **success**; assert **throws** for a
  shape that should fall back. `MONGODB_EF_NATIVE_ONLY=1` flips every spec context to `NativeOnly`, making the
  pass/fail set a "what actually goes native" report.
- **Differential correctness.** For a native shape that can *change* results (not merely fall back), prefer a
  `[Theory]` asserting the native result equals an in-memory LINQ oracle over the *same* `Expression`, across a
  fixture covering ragged/missing/null/empty states — see `NativeOwnedCollectionAllTests`,
  `NativeOwnedCollectionCountTests`, and the shared `NativeModeAssert` helpers.
- **MQL baselines** are generated, not hand-written — see the SpecificationTests `AGENTS.md` for
  `EF_TEST_REWRITE_BASELINES`.
- Unit tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` (native ones under `NativeTranslation/`).
- Functional tests (real DB): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```
