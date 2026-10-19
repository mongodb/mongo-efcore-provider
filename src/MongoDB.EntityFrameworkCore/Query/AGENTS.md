---
area: Query / LINQ translation
scope: ["src/MongoDB.EntityFrameworkCore/Query/**"]
reviewer-agent: query-reviewer
adjacent-areas: [Storage, Metadata, Serializers, "C# driver LINQ v3"]
---

# Query — AGENTS.md

## Scope

Translates `IQueryable<T>` into aggregation pipelines. **Two paths**, chosen at compile time by
`MongoQueryMode`:

1. **Native (default).** Provider builds the `BsonDocument[]` pipeline itself from a structured query tree,
   runs it via `IMongoCollection<>.Aggregate`.
2. **Driver-LINQ (fallback).** Everything outside the native slice goes to the driver's LINQ v3 provider.

Per the top-level `AGENTS.md` rubric: **which path a query takes, the exact MQL emitted, and the exception type
for an unsupported shape are implementation details, not contract.**

> Per-feature history (which ticket widened what, measurement tables) belongs in git history and tests, not
> here. This file holds only what you need before touching the area.

## Pipeline at a glance

```
IQueryable<T>  (EF Core)
   │
   ▼  MongoQueryCompilationContext           (preserves original LINQ tree; carries MongoQueryMode)
   ▼  MongoQueryTranslationPreprocessor      (hoist final predicates; lift VectorSearch out before nav expansion)
   ▼  MongoQueryableMethodTranslatingExpressionVisitor (QMTEV)
   │      ├─ accepts only Queryable / MongoQueryableExtensions / MongoDB.Driver.Linq.MongoQueryable
   │      ├─ delegates slot population to NativeSlotPopulator, projection binding to NativeProjectionBinder
   │      └─ always also captures the raw method chain (CapturedExpression) for the fallback
   ▼  MongoQueryTranslationPostprocessor     (apply final ProjectionMapping)
   ▼  MongoShapedQueryCompilingExpressionVisitor  ── THE GATE (compile time, honors MongoQueryMode) ──
   │   if Native & Select.Route != Fallback & lower/render succeed → NATIVE:
   │      MongoSelectLowerer  : PipelineOps → MongoPipelineStage[]
   │      MongoQueryLanguageRenderer + PlaceholderTable : predicate → $match BSON; params → sentinels
   │      MongoPipelineFactory.Create : render once into a template + placeholder table
   │      streaming shaper if eligible, else DOM
   │   else → DRIVER-LINQ FALLBACK: MongoEFToLinqTranslatingExpressionVisitor rewrites CapturedExpression
   ▼  MongoExecutableQuery + QueryingEnumerable → MongoClientWrapper.Execute → IAsyncCursor → shaper
```

Template is built once at compile time; only `factory.Build(parameterValues)` runs per execution.
Native-vs-driver and streaming-vs-DOM are compile-time-deterministic.

## Key entry points

| File | Responsibility |
|---|---|
| `MongoQueryTranslationPreprocessor` | Extracts `VectorSearch(...)` before nav-expansion, re-inserts after (nav expansion crashes on it). |
| `Visitors/MongoQueryableMethodTranslatingExpressionVisitor` | LINQ-method dispatcher; allowed-method-source set; delegates slot/projection work; captures final chain; carries bulk `ExecuteUpdate`/`ExecuteDelete`. |
| `Visitors/MongoShapedQueryCompilingExpressionVisitor` | The gate. `ClassifyNativeDisposition` → `{Native, Fallback, HardDecline}` from `Select.Route`, `IsGroupByFallbackUnsafe`, unbound vector search. Streamability is a separate axis. |
| `Expressions/MongoSelectDefinition` | Native logical IR: ordered `PipelineOps` (+`TrailingOps` once a set op attaches), `Projection`, `Cardinality`, `Grouping`, `VectorSearch`. `Route` is the authoritative is-native signal. Dialect-neutral. |
| `Expressions/MongoQueryExpression` (+`.Lookup.cs`) | Has-a `MongoSelectDefinition`, cross-collection `$lookup` state, retained `CapturedExpression`/`ProjectionMapping` for fallback. |
| `Expressions/MongoExpression` + subtypes | Dialect-agnostic node hierarchy; `VisitChildren` is a no-op, every consumer hand-rolls a `switch`. |
| `NativeTranslation/MongoExpressionTranslator` | EF predicate/key/value body → `MongoExpression`; returns `false` outside its acceptance set. |
| `NativeTranslation/MongoSelectLowerer` | Native IR → `MongoPipelineStage[]` (BSON-free); owns lookup-eligibility guards. |
| `NativeTranslation/MongoQueryLanguageRenderer` | Renders **query dialect** (`$match`); keeps `&&`/`\|\|` at query level, wraps only non-expressible subtrees in `$expr` for indexability. |
| `NativeTranslation/MongoAggregationExpressionRenderer` | Renders **aggregation-expression dialect** (`$expr`/`$project`) — field-to-field comparisons, arithmetic. |
| `NativeTranslation/MongoPipelineFactory` | Stages → `BsonDocument[]` template + placeholder table; validates `$limit>0`/`$skip≥0`. |
| `NativeTranslation/Native{SlotPopulator,ProjectionBinder,CardinalityBinder,GroupByBinder,SelectManyBinder,JoinScope*}` | Per-shape recognizers populating the IR. |
| `NativeTranslation/MongoStreamingEntityMaterializerRewriter` + `StreamingEligibility` | One-pass forward-only `IBsonReader` → POCO; deserialization *is* materialization. |
| `Visitors/MongoProjectionBindingRemovingExpressionVisitor` (+`Mixed…`) | DOM read side; `Mixed` sibling reads whole un-projected documents (fallback/mixed leg). |
| `Query/MongoIncludeFixups` | `Include` navigation fix-up shared by both read paths. |
| `ExpressionExtensionMethods` | Shared expression-shape helpers. |

## Durable invariants

Rules that cost real bugs to learn. Breaking one usually produces **silently wrong rows**, not a failure.

- **Post-terminal gating.** After a terminal operator (`GroupBy`, `Distinct`, a set op, `SelectMany` unwind —
  `HasTerminalOperator`), every later operator must decide explicitly whether it can still go native. Any
  operator with its own `Translate*` override bypasses the shared gates and must replicate the check itself.
- **No driver-LINQ oracle for some shapes**: `SelectMany` over a reference collection, `Intersect`/`Except`,
  the correlated-reducer projection leaf, `Reverse`. These hard-fail (return `null`) in every `MongoQueryMode`
  rather than falling back — don't mark them non-native.
- **Scope resolves by parameter identity, never member name.** Route by `ReferenceEquals` against the scope's
  parameter (shared property names across types is the regression test). Shared primitive:
  `MongoExpressionTranslator.TryBeginOwnedHopWalk`.
- **Multi-scope join projections.** A trailing `Select` over a `Joins.Count >= 2` chain can go native for a
  whole-entity leaf, or a scalar/computed leaf resolving to exactly one chain scope. A leaf spanning multiple
  scopes, or a nested wrapped leaf, still declines. `Skip`/`Take`/`Where`/`OrderBy` written after such a
  `Select` are usually hoisted ahead of the join's result selector by EF Core — see the paging bullet below.
- **Navigation-less joins are native-eligible** exactly like navigation-backed ones, as long as
  `RebindInnerShaperToOuterQuery`'s raw-key branch resolved both key properties (`JoinInfo.Lookup != null`).
  Don't confuse with a navigation resolving to the *wrong* target (still declines, guarded by
  `JoinLookupImplementsKeySelectors`). See `NativeJoinTests.cs`'s
  `Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly` vs.
  `Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly`.
- **`Skip`/`Take` (and hoisted `Where`/`OrderBy`) ahead of a join's confirming `Select`** now goes native by
  deferring the recorded `PipelineOps` to run after the join, unless the join is already in the
  left-outer-reference-navigation "safe to page before `$lookup`" set. A reducer in the same position still
  declines. Exception: a genuine 1:N collection-navigation join with paging recorded before any join existed
  declines instead of deferring (deferring would page the joined/multiplied result, not the outer sequence).
- **Set ops form a tree; each `Union`'s dedup belongs to its own link, never hoisted.**
  `MongoSelectDefinition.SetOperations` is an ordered list where an operand may itself carry a link, so
  whole-entity `Concat`/`Union` nests both directions. Right-nesting (`A.Concat(B.Union(C))`) cannot be
  flattened into a left chain without changing results. Ops recorded between two links land in `TrailingOps`
  (routed there once a set op attaches) but belong to the *new* link as `PrecedingOps` — `AppendSetOperation`
  moves them. `Intersect`/`Except` are excluded from nesting entirely. See `NativeSetOpsTests`.
- **Structural classification beats metadata/depth/CLR-type shortcuts.** Query filters, join-hop depth, and
  self-referencing entity types can all defeat shortcuts — walk the actual tree. Mode-independent;
  `DriverLinq` is not an escape hatch for a misclassification.
- **The negator's contract is exact complement or decline, never approximation.** `$eq`/`$ne` may be inverted
  (they partition every value including missing/null); the four relational operators must be `$not`-wrapped,
  never inverted (they don't partition missing/null). Ask whether the *rendered* pair partitions the value
  space.
- **Negator ↔ dialect classifier ↔ both renderers must agree**, enforced by
  `MongoExpressionNodeCoverageTests` (reflection-discovers every `MongoExpression` subtype, checks all seven
  dispatchers). The matrix is keyed by node type, so it's blind to shape-conditional dispatch (e.g.
  `MongoRegexExpression` differs for constant vs. field-to-field `Term`) — those need a dedicated test.
- **`$expr` inside `$elemMatch` is a hard server error.** Anything nesting inside `$elemMatch` must be rejected
  at `IsQueryDialectRenderable`, not fall through to the aggregation renderer.
- **`$ifNull` around `$size`/`$filter` is mandatory** — `$size` against a missing/null array is a hard server
  error, not a wrong answer.
- **A non-default-serialized bool is truthiness-tested and answers the wrong boolean.**
  `HasConversion<string>()` bools store `"True"`/`"False"` — both truthy. Gate `$not`, `$and`/`$or` operands,
  and bare boolean predicate roots via `MongoExpressionTranslator.IsUnsafeTruthinessRoot`.
- **A node needing different handling at 3+ call sites should be a sealed sibling type, not a bool flag** (e.g.
  `MongoFilteredSizeExpression` beside `MongoSizeExpression`) — a flag leaves sites wrong by default.
- **Element-scoped children must not be prefixed.** `MongoFilteredSizeExpression`/`MongoElemMatchExpression`/
  `MongoQuantifierExpression` bind their own `$filter`/`$elemMatch` variable; `MongoFieldPrefixRewriter`
  prefixes only the array path and declines (never throws) on an unhandled node kind.
- **Mixed-projection alias agreement.** The native shaper is alias-addressed at translation time, but a
  fallback reads whole documents. An array/owned-nav-entity projection leaf's alias must equal the
  navigation's own document path, or a fallback silently returns an empty collection. See
  `NativeArrayProjectionTests`.
- **Guards that decline a shape often become routers, not walls** — a rejection check can later become the
  signal a feature uses to pick between two strategies. Check before loosening one.
- **MQL shape cannot prove a query went native** — see Common pitfalls.
- **A recognizer must not mutate then decline.** Stage into locals, commit only once every gate passes
  (`NativeProjectionBinder`'s commit block is the pattern).

## Boundaries with adjacent areas

- **vs Storage.** Query produces data (a pipeline or driver-LINQ expression); `MongoClientWrapper.Execute(...)`
  owns execution, cursors, sessions, retries. Never call `IMongoCollection<>.Aggregate(...)` from Query.
- **vs Storage — bulk `ExecuteUpdate`/`ExecuteDelete` crosses as behavior, not data** (`MongoBulkPlan` carries
  translation delegates invoked per execution, since bulk translation is parameter-value-dependent).
- **vs Serializers.** Ask `BsonSerializerFactory` for an `IBsonSerializer`; never instantiate one here.
- **vs Metadata.** Query reads `GetElementName()`/`GetBsonRepresentation()`/`GetDiscriminator*()`, never writes.
- **vs driver LINQ v3.** Only the fallback uses it; we do not build on the driver's internal AST.

## Common pitfalls

- **MQL shape cannot prove a query went native.** Native and fallback pipelines are often structurally
  identical for filter/sort/paging. The only reliable signal is `NativeOnly` mode.
- **Allowed method sources are strict** — `Queryable`, `MongoQueryableExtensions`,
  `MongoDB.Driver.Linq.MongoQueryable`, plus EF8/EF9's internal `LeftJoin` shim (admitted unconditionally by
  `MethodInfo` identity — no per-call signal survives nav-expansion to distinguish its two source shapes).
- **VectorSearch extraction ordering** — pulled out before nav-expansion, stitched back after; re-check if you
  change preprocessor ordering.
- **`CapturedExpression` must be complete** — set once at the tail of `VisitMethodCall`; setting mid-chain
  truncates the query.
- **`ProjectionMapping` keys must match exactly** what the shaper expects — a mismatch is silent wrong results.
- **`MethodInfo` reference-equality** needs canonical constants (`QueryableMethods`, `EnumerableMethods`); open
  vs. constructed generics compare unequal.
- **Unsupported shapes call `MarkNotNativelyRepresentable()`**, driving `Route` to `Fallback` rather than
  silently emitting a pipeline that drops the operator.
- **Adding a native operator touches two places**: a slot-style operator needs both
  `NativeSlotPopulator.PopulateNativeSlots` and `IsNativeRepresentableSlotOperator`; a `Translate*`-override
  operator needs both the override and `IsNativeRepresentableSlotOperator`.
- **QMTEV's `Translate{Where,OrderBy,ThenBy,Skip,Take}` overrides are inert (`=> null`)** — slot population is
  delegated at the `VisitMethodCall` fall-through instead. Don't route these through the switch without first
  removing their `NativeSlotPopulator` handling, or slots double-populate.
- **The lowerer is BSON-free**; keep `BsonDocument` construction in the renderer/factory only.
- **Non-positive paging** — `Build` validates `$limit > 0`/`$skip ≥ 0` (throws `ArgumentOutOfRangeException`);
  never emit `{$limit: 0}`. Normalization covers top level and `$unionWith` inner pipelines, not `$lookup`
  sub-pipelines.
- **TPH `$lookup` joins must narrow by discriminator** when the target is a derived type, or sibling-subtype
  rows leak in (`LookupExpression`'s constructor does this).
- **EF Core query cache** — compiled queries are cached by expression-tree shape; changing a translator's
  output for a previously-translatable tree quietly invalidates user caches.
- **Multi-EF guards** — visitor signatures differ across EF8/EF9/EF10, e.g.
  `Storage/MongoTypeMappingSource.cs` (`#if EF8 || EF9`), `QueryingEnumerable.cs` (`#if !EF8`).

## How to test

- **Proving native.** Run under `MongoQueryMode.NativeOnly`, assert success (or throws for a shape that should
  fall back). `MONGODB_EF_NATIVE_ONLY=1` flips every spec context to `NativeOnly`.
- **Differential correctness.** For a native shape that can change results, prefer a `[Theory]` asserting the
  native result equals an in-memory LINQ oracle over ragged/missing/null/empty fixtures — see
  `NativeOwnedCollectionAllTests`, `NativeOwnedCollectionCountTests`, `NativeModeAssert`.
- **MQL baselines are generated, not hand-written** — see SpecificationTests `AGENTS.md` for
  `EF_TEST_REWRITE_BASELINES`.
- Unit: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/` (native under `NativeTranslation/`).
- Functional: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~Query"
```
