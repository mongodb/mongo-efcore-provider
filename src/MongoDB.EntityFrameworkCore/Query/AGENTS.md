---
area: Query / LINQ translation
scope: ["src/MongoDB.EntityFrameworkCore/Query/**"]
reviewer-agent: query-reviewer
adjacent-areas: [Storage, Metadata, Serializers, "C# driver LINQ v3"]
---

# Query — AGENTS.md

## Scope

Translates `IQueryable<T>` against MongoDB-backed DbSets into MongoDB aggregation pipelines. There are **two translation paths**, chosen at compile time by the `MongoQueryMode` option (see *the gate* below):

1. **Native (default).** For the supported slice — single-collection **filter / sort / paging** over whole-entity results — the provider **generates the aggregation `BsonDocument[]` pipeline itself**, from a structured query expression tree, and runs it via `IMongoCollection<…>.Aggregate`. It does **not** go through the driver's LINQ provider for these.
2. **Driver-LINQ (gated fallback).** For everything outside the native slice (projections, predicate breadth, cardinality, `GroupBy`/`SelectMany`/set ops/`Distinct`/`OfType`/`VectorSearch`, and — for now — **reference `Include`**, which is deferred), the provider still hands a driver-LINQ expression to the MongoDB C# driver's LINQ v3 provider, exactly as it always did.

> **As-built scope (EF-323 foundation).** Native = filter/sort/paging only. **Reference `Include` is deferred** — it nav-expands to a `LeftJoin` that the gate treats as non-native, so it falls back to driver-LINQ (correct results; throws under `NativeOnly`). The native lookup machinery (lowerer `$lookup`/`$unwind`, `GetStreamingReferenceLookups`, streaming-Include eligibility) is **built but dormant**, awaiting the Collection Includes sub-project. Predicate breadth, projection pushdown, scalar cardinality, etc. are later sub-projects.

In: top-level method-call translators **and native slot population**, the **query expression tree → stage IR → `BsonDocument[]` pipeline generator** (lowerer / renderer / pipeline factory), the compile-time **gate**, projection binding, the EF-to-driver-LINQ bridge (fallback), shaper compilation (native streaming + DOM, and driver-LINQ DOM), vector-search pre-extraction.
Out: BSON encoding/decoding of *values* (Serializers area — the renderer asks `BsonSerializerFactory` for serializers), change-tracking snapshots (ChangeTracking area), index/collection schema (Storage area), cursor execution itself (`MongoClientWrapper.Execute` in Storage), and aggregation-pipeline generation **for the fallback path** (still the driver's job).

## Pipeline at a glance

```
IQueryable<T>  (EF Core)
   │
   ▼  MongoQueryCompilationContext           (preserves the original LINQ tree; carries the MongoQueryMode)
   ▼  MongoQueryTranslationPreprocessor      (hoist final predicates; lift VectorSearch out before nav expansion)
   ▼  MongoQueryableMethodTranslatingExpressionVisitor (QMTEV)
   │      ├─ accepts only Queryable / MongoQueryableExtensions / MongoDB.Driver.Linq.MongoQueryable
   │      ├─ POPULATES native slots on MongoQueryExpression (Predicate / Orderings / Offset / Limit / Lookups)
   │      │     via PopulateNativeSlots + MongoExpressionTranslator; sets IsNativeRepresentable=false for
   │      │     any operator it does not lower into a slot (catch-all) — see pitfalls
   │      └─ ALWAYS also captures the raw method chain (CapturedExpression) for the driver-LINQ fallback
   ▼  MongoQueryTranslationPostprocessor     (apply final ProjectionMapping)
   ▼  MongoShapedQueryCompilingExpressionVisitor  ── THE GATE (compile time, honors MongoQueryMode) ──
   │   if Native & IsNativeRepresentable & lower/render succeed → NATIVE PATH:
   │      MongoSelectLowerer  : slots → MongoPipelineStage[]  (canonical $match→$sort→$skip→$limit→$lookup/$unwind)
   │      MongoQueryLanguageRenderer + PlaceholderTable : predicate → $match BSON; params → sentinels
   │      MongoPipelineFactory.Create : render ONCE into a template + placeholder table  (B2)
   │      compile streaming shaper (MongoStreamingEntityMaterializerRewriter) if StreamingEligibility, else DOM
   │   else (DriverLinq, or not representable, or NativeOnly→throws) → DRIVER-LINQ FALLBACK:
   │      MongoEFToLinqTranslatingExpressionVisitor rewrites CapturedExpression as driver-LINQ; DOM shaper
   ▼  MongoExecutableQuery + QueryingEnumerable
          ├─ NATIVE: factory.Build(parameterValues) → BsonDocument[] set as NativePipeline;
          │          MongoClientWrapper.Execute runs collection.Aggregate(pipeline) (RawBsonDocument if streaming, else BsonDocument)
          └─ FALLBACK: MongoClientWrapper.Execute hands the driver-LINQ expression to IMongoQueryProvider
          → IAsyncCursor; shaper turns each row into the requested CLR shape
```

B2 = the pipeline template is built **once at compile time** (lower + render → `MongoPipelineFactory`); only `factory.Build(parameterValues)` (clone-and-substitute the placeholder sentinels) runs per execution. The native-vs-driver and streaming-vs-DOM decisions are therefore **compile-time-deterministic** — there is exactly **one** shaper per query and **no** runtime dual-shaper dispatch.

## Key entry points

- `MongoQueryCompilationContext` — EF Core hook to preserve the original captured `IQueryable` so post-translation we can recover the user's intent (e.g. for diagnostics).
- `MongoQueryTranslationPreprocessor` — first phase. Notably, **`VectorSearch(...)` is extracted before EF's nav-expansion and re-inserted after** — nav expansion doesn't know about it.
- `Visitors/MongoQueryableMethodTranslatingExpressionVisitor` — the central LINQ-method dispatcher. Enforces the allowed-method-source set; **populates the native slots** (`PopulateNativeSlots`, the `Translate*` overrides are inert — see pitfalls); captures the final method-chain expression on `MongoQueryExpression`.
- `Visitors/MongoShapedQueryCompilingExpressionVisitor` — **the compile-time gate**. Reads `MongoQueryMode`; builds the `MongoPipelineFactory` and compiles the native (streaming/DOM) shaper when representable, else compiles the driver-LINQ fallback shaper. Splits scalar vs. entity result paths.
- **Native translation (`NativeTranslation/` + `Expressions/`):**
  - `Expressions/MongoExpression` + subtypes (`MongoFieldExpression`/`MongoConstantExpression`/`MongoParameterExpression`/`MongoBinaryExpression`/`MongoUnaryExpression`) + `MongoOrdering` — the dialect-agnostic `SqlExpression`-analog node hierarchy.
  - `Expressions/MongoQueryExpression` (+ `.NativeSlots.cs`, `.Lookup.cs`) — the structured query tree: the native slots (`Predicate`/`Orderings`/`Offset`/`Limit`/`Lookups`/`IsNativeRepresentable`) **plus** the retained `CapturedExpression` + `ProjectionMapping` for the fallback. (We extended this type in place rather than adding a separate `MongoSelectExpression`.)
  - `NativeTranslation/MongoExpressionTranslator` — EF predicate / key-selector body → `MongoExpression` (`TryTranslate` / `TryTranslateField`); returns false for anything outside the spike's basic acceptance set.
  - `NativeTranslation/MongoSelectLowerer` — slots → `MongoPipelineStage[]` (typed IR under `NativeTranslation/Stages/`), BSON-free; owns canonical order + the lookup eligibility guards.
  - `NativeTranslation/MongoQueryLanguageRenderer` — `MongoExpression` → `$match`-dialect BSON; `PlaceholderTable` records parameter placeholders. There is only the one (`$match`/query) dialect at the foundation; dialect selection and an `$expr` (aggregation-expression) renderer are **deferred to SP2 (predicate breadth)** — neither is present or wired at the foundation.
  - `NativeTranslation/MongoPipelineFactory` — the B2 template: `Create(stages, renderer)` walks the stages into a `BsonDocument[]` template (+ placeholder table); `Build(parameterValues)` deep-clones and substitutes per execution. Validates `$limit>0`/`$skip≥0` (throws `ArgumentOutOfRangeException`, matching EF).
  - `NativeTranslation/MongoStreamingEntityMaterializerRewriter` + `BsonRowReader` + `StreamingEligibility` — forward-only `RawBsonDocument` → POCO materialization for eligible entities (no DOM build).
  - `Infrastructure/MongoQueryMode` (`Native`/`DriverLinq`/`NativeOnly`) — the user-facing gate option (`UseQueryMode`).
- `Visitors/MongoEFToLinqTranslatingExpressionVisitor` — converts the residual EF expression into a tree the driver's LINQ v3 provider understands (`Mql.Field`, parameter resolution, `As<T>(serializer)`).
- `Visitors/MongoProjectionBindingRemovingExpressionVisitor` — replaces `ProjectionBindingExpression` nodes with concrete index-based reads from a `BsonDocument`.
- `Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor` — sibling of the above used on the mixed path (projection contains entity references LINQ v3 can't handle); `MongoShapedQueryCompilingExpressionVisitor` strips the trailing `Select` and dispatches to this visitor so the shaper runs client-side over full `BsonDocument`s.
- `Expressions/MongoCollectionExpression`, `EntityProjectionExpression`, `RootReferenceExpression`, `ObjectAccessExpression`, `ObjectArrayProjectionExpression` — provider-specific expression nodes for collection roots, nested-document access, and entity shape.
- `MongoExecutableQuery` + `QueryingEnumerable` — the compiled-query handoff. `QueryingEnumerable` calls `MongoClientWrapper.Execute(MongoExecutableQuery)` and applies the shaper. For the native path `MongoExecutableQuery` carries `NativePipeline` (the built `BsonDocument[]`), `Streaming`, and `Session` (internal); for the fallback it carries the driver-LINQ `Query` expression.
- `Factories/*` — EF Core DI factories registered in `MongoServiceCollectionExtensions.AddEntityFrameworkMongoDB()`.

## Boundaries with adjacent areas

- **vs Storage.** Query stops at the executable query — it produces *data*: either a native `BsonDocument[]` pipeline (`MongoExecutableQuery.NativePipeline`) or a driver-LINQ expression. `MongoClientWrapper.Execute(...)` (Storage) owns execution: it runs `collection.Aggregate(nativePipeline)` for the native path and hands the expression to `IMongoQueryProvider` for the fallback. Cursor lifecycle, transaction/session binding, retries are Storage + driver. Never call `IMongoCollection<>.Aggregate(...)` directly from Query — build the `BsonDocument[]` and let the wrapper run it.
- **vs Storage — bulk `ExecuteUpdate`/`ExecuteDelete` (EF9+) uses a *second*, deliberately different seam.** Reads cross to Storage as **data** (a `MongoExecutableQuery`); bulk crosses as **behavior** — `MongoShapedQueryCompilingExpressionVisitor.CreateBulkPlan<TSource>` builds a `MongoBulkPlan` (Storage) carrying `Func<QueryContext, …>` translation delegates, and `MongoBulkOperationExecutor` (Storage) invokes them at runtime. This is intentional: bulk filter/update translation is parameter-value-dependent, so it must be deferred and re-run per execution. The translation helpers (`BuildFilter`/`BuildUpdate`/`BuildIdDocumentQuery`) stay here in Query; only their rendered results cross the boundary. Don't "fix" the bulk seam to look like the read seam — they differ for this reason.
- **vs Serializers.** Query needs an `IBsonSerializer` for every materialized projection result — it asks `BsonSerializerFactory` (Serializers area). Query should never instantiate serializers directly.
- **vs Metadata.** `IEntityType`, `IProperty`, navigation info come from Metadata. Query reads `GetElementName()`, `GetBsonRepresentation()`, `GetDiscriminator*()` etc. — it doesn't write them.
- **vs the driver's LINQ v3 provider.** Only the **fallback** path uses it: Query produces a driver-LINQ expression (types from `MongoDB.Driver.Linq`) and hands it off, and the driver builds that pipeline. The **native** path bypasses the driver's LINQ provider entirely — the provider builds the `BsonDocument[]` itself (`MongoSelectLowerer` + `MongoQueryLanguageRenderer` + `MongoPipelineFactory`) and only uses the driver as a BSON/cursor/protocol library via `Aggregate`. The deliberate, stable boundary is **our query tree → `BsonDocument[]` → `Aggregate`**; we do **not** build on the driver's internal AST.

## Common pitfalls

- **Allowed method sources are strict.** Only `Queryable`, `MongoQueryableExtensions`, and `MongoDB.Driver.Linq.MongoQueryable` methods are accepted. Anything else throws `NotTranslatedExpression` early.
- **VectorSearch extraction.** `VectorSearch(...)` must be pulled out of the tree before EF's nav-expansion runs (it crashes nav-expansion otherwise) and stitched back in after. If you change preprocessor ordering, re-check this dance.
- **ProjectionMapping discipline.** The `_projectionMapping` keys (`ProjectionMember`s) must match exactly what the shaper expects. A mismatch between the post-processor mapping and the shaper compilation produces silent wrong-results, not crashes.
- **`MongoQueryExpression.CapturedExpression`** must be a *complete* method chain — set once at the tail of `MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall`. Setting it mid-chain truncates the query.
- **Reference-equality on `MethodInfo`.** Translators that match by `MethodInfo` must use canonical constants — `QueryableMethods` for the top-level dispatch in `MongoQueryableMethodTranslatingExpressionVisitor`, `EnumerableMethods` inside the projection-binding visitors, and the driver's `*Method` reflection classes where the bridge to driver-LINQ needs them; open vs. constructed generic methods compare unequal.
- **Unsupported shapes are detected, not silently translated.** Two layers: the base visitor early-fails truly untranslatable shapes; and for the *native* path, anything the QMTEV does not lower into a slot sets `IsNativeRepresentable = false` so the gate falls back (or throws under `NativeOnly`) rather than emitting a pipeline that silently drops the operator.
- **The native catch-all whitelist must stay in sync.** `PopulateNativeSlots` lowers exactly seven operators (`Where`/`OrderBy`/`OrderByDescending`/`ThenBy`/`ThenByDescending`/`Skip`/`Take`); `IsNativeRepresentableSlotOperator` lists those same seven, and the `else` catch-all marks **everything else** non-native (correctness-safe: worst case is a missed optimization + fallback, never a wrong result). **Adding a new native operator means updating BOTH** `PopulateNativeSlots` *and* `IsNativeRepresentableSlotOperator` — miss the latter and the operator is silently dropped on the native pipeline.
- **The QMTEV `Translate{Where,OrderBy,ThenBy,Skip,Take}` overrides are inert (`=> null`).** Native slot population is done inline by `PopulateNativeSlots` at the `VisitMethodCall` fall-through, because routing these through `base.VisitMethodCall` rebuilds a fresh `MongoQueryExpression` per operator (slots don't accumulate). **Do not add these operators to the `VisitMethodCall` switch** without first removing their `PopulateNativeSlots` handling — otherwise slots double-populate.
- **The lowerer is BSON-free; the renderer/factory own BSON.** `MongoSelectLowerer` produces only typed `MongoPipelineStage`s; all `BsonDocument` construction lives in `MongoQueryLanguageRenderer` (the `$match` body) and `MongoPipelineFactory.Create` (the stage-walk: `{$match}`/`$sort`/`$skip`/`$limit`/`$lookup`/`$unwind`).
- **Composite-PK key access is NOT native** (strict spike parity) — `MongoExpressionTranslator` rejects multi-property-PK member access, so such predicates/sort fall back. `LookupExpression.GetFieldPath` (`_id.<prop>`) exists for the deferred Include `$lookup` only.
- **Non-positive paging matches EF's contract.** `MongoPipelineFactory.Build` validates `$limit > 0` / `$skip ≥ 0` and throws `ArgumentOutOfRangeException` (client-side, like the driver) — do not emit `{$limit: 0}` (MongoDB rejects it server-side). Property-less primitive values (Skip/Take counts have `forSerialization == null`) serialize via `BsonValue.Create`, not a property serializer.
- **MQL shape cannot prove a query went native.** For filter/sort/paging the native and driver-LINQ pipelines are structurally identical (`$match`/`$sort`/`$skip`/`$limit`), so asserting those substrings under `Native` mode passes even on fallback. The **only reliable "goes native" signal is `NativeOnly` mode**: native-capable ⇒ succeeds; otherwise ⇒ throws `NativeTranslationNotSupportedException`. (Several early gate tests were false positives masked by structurally-identical fallback MQL.)
- **EF Core query cache.** Compiled queries are cached by EF Core by expression-tree shape; if you change a translator's output for a previously-translatable tree, you've quietly invalidated user caches.
- **Multi-EF guards.** Some visitor signatures changed between EF8/EF9/EF10. For representative guard shapes elsewhere in the tree see `Storage/MongoTypeMappingSource.cs` (`#if EF8 || EF9`), the `ChangeTracking/StringDictionaryComparer*.cs` pair (legacy vs. EF10 split), and the `ChangeTracking/ListOf*Comparer.cs` files (`#if EF8`); in Query itself, `QueryingEnumerable.cs` has a `#if !EF8` block.

## How to test

Most query tests assert the **rendered MQL pipeline** rather than executing against a server. The functional and specification tests both have helpers (`AssertMql(...)`) that capture the MQL via `TestMqlLoggerFactory`. Unit tests use **plain xUnit `Assert.*`** (FluentAssertions is not referenced anywhere in the test projects).

**Proving the native path.** `AssertMql` shape does **not** distinguish native from driver-LINQ for filter/sort/paging (identical pipelines) — to assert a query genuinely goes native, run it under `MongoQueryMode.NativeOnly` and assert it **succeeds** (a fallback shape would throw `NativeTranslationNotSupportedException`); to assert a shape falls back, assert it **throws** under `NativeOnly` (or, for `Include`, assert the driver `_outer`/`_inner` shape under `Native`). The spec suite has a **test-only** coverage instrument: `MONGODB_EF_NATIVE_ONLY=1` flips all spec contexts to `NativeOnly` (via `MongoTestStore.AddProviderOptions`), so the pass/fail set is the "what runs native" report. Native unit tests live under `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/NativeTranslation/`; the `QueryModeGate*` functional tests exercise the gate end-to-end.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Query"
```

- Unit tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/`
- Functional tests (real DB): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — golden-path translations and end-to-end materialization.
- Specification tests (EF Core conformance, Northwind suite): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/` — pattern is to override the upstream test and assert the produced MQL.
