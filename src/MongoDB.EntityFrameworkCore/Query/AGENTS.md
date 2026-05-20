---
area: Query / LINQ translation
scope: ["src/MongoDB.EntityFrameworkCore/Query/**"]
reviewer-agent: query-reviewer
adjacent-areas: [Storage, Metadata, Serializers, "C# driver LINQ v3"]
---

# Query — AGENTS.md

## Scope

Translates `IQueryable<T>` against MongoDB-backed DbSets into MongoDB aggregation pipelines. The provider sits **on top of** the MongoDB C# driver's LINQ v3 provider — it does not generate aggregation BSON itself. The provider's job is to take an EF Core expression tree, normalize it, decide what to push to the driver vs. handle client-side (entity materialization, change tracking), and hand a driver-LINQ expression to the driver for compilation.

In: top-level method-call translators, projection binding, EF-to-driver-LINQ bridge, shaper compilation, vector-search pre-extraction.
Out: aggregation pipeline generation (driver), BSON encoding/decoding (Serializers area), change-tracking snapshots (ChangeTracking area), index/collection schema (Storage area), `IQueryable<T>` materialization in the runtime sense (driver's `IMongoQueryProvider`).

## Pipeline at a glance

```
IQueryable<T>  (EF Core)
   │
   ▼  MongoQueryCompilationContext           (preserves the original LINQ tree alongside EF's)
   ▼  MongoQueryTranslationPreprocessor      (hoist final predicates; lift VectorSearch out before nav expansion)
   ▼  MongoQueryableMethodTranslatingExpressionVisitor
   │      ├─ accepts only Queryable / MongoQueryableExtensions / MongoDB.Driver.Linq.MongoQueryable
   │      ├─ builds a MongoQueryExpression with a ProjectionMapping
   │      └─ rejects unsupported shapes (Join / GroupBy / set ops) early
   ▼  MongoQueryTranslationPostprocessor     (apply final ProjectionMapping)
   ▼  MongoShapedQueryCompilingExpressionVisitor
   │      ├─ ProjectionAnalyzer decides what can push down to driver-LINQ
   │      ├─ BsonDocumentInjectingExpressionVisitor parameterizes shapers on BsonDocument
   │      └─ MongoEFToLinqTranslatingExpressionVisitor rewrites the residual EF tree as driver-LINQ
   ▼  MongoExecutableQuery + QueryingEnumerable
          ├─ MongoClientWrapper.Execute hands the driver-LINQ expression to IMongoQueryProvider
          └─ driver returns IAsyncCursor<BsonDocument>; shaper turns each doc into the requested CLR shape
```

## Key entry points

- `MongoQueryCompilationContext` — EF Core hook to preserve the original captured `IQueryable` so post-translation we can recover the user's intent (e.g. for diagnostics).
- `MongoQueryTranslationPreprocessor` — first phase. Notably, **`VectorSearch(...)` is extracted before EF's nav-expansion and re-inserted after** — nav expansion doesn't know about it.
- `Visitors/MongoQueryableMethodTranslatingExpressionVisitor` — the central LINQ-method dispatcher. Enforces the allowed-method-source set; captures the final method-chain expression on `MongoQueryExpression`.
- `Visitors/MongoShapedQueryCompilingExpressionVisitor` — bridges to the C# driver. Splits scalar vs. entity result paths and compiles the shaper.
- `Visitors/MongoEFToLinqTranslatingExpressionVisitor` — converts the residual EF expression into a tree the driver's LINQ v3 provider understands (`Mql.Field`, parameter resolution, `As<T>(serializer)`).
- `Visitors/MongoProjectionBindingRemovingExpressionVisitor` — replaces `ProjectionBindingExpression` nodes with concrete index-based reads from a `BsonDocument`.
- `Visitors/MongoMixedProjectionBindingRemovingExpressionVisitor` — sibling of the above used on the mixed path (projection contains entity references LINQ v3 can't handle); `MongoShapedQueryCompilingExpressionVisitor` strips the trailing `Select` and dispatches to this visitor so the shaper runs client-side over full `BsonDocument`s.
- `Expressions/MongoQueryExpression` — root MongoDB query node; holds `_projectionMapping` and the captured method chain.
- `Expressions/MongoCollectionExpression`, `EntityProjectionExpression`, `RootReferenceExpression`, `ObjectAccessExpression`, `ObjectArrayProjectionExpression` — provider-specific expression nodes for collection roots, nested-document access, and entity shape.
- `MongoExecutableQuery` + `QueryingEnumerable` — the compiled-query handoff. `QueryingEnumerable` calls `MongoClientWrapper.Execute(MongoExecutableQuery)` and applies the shaper.
- `Factories/*` — EF Core DI factories registered in `MongoServiceCollectionExtensions.AddEntityFrameworkMongoDB()`.

## Boundaries with adjacent areas

- **vs Storage.** Query stops at the executable query. `MongoClientWrapper.Execute(...)` is in Storage; query execution (cursor lifecycle, transaction binding, retries) is owned by the driver and by Storage's wrapper. Never call `IMongoCollection<>.Aggregate(...)` directly from Query.
- **vs Serializers.** Query needs an `IBsonSerializer` for every materialized projection result — it asks `BsonSerializerFactory` (Serializers area). Query should never instantiate serializers directly.
- **vs Metadata.** `IEntityType`, `IProperty`, navigation info come from Metadata. Query reads `GetElementName()`, `GetBsonRepresentation()`, `GetDiscriminator*()` etc. — it doesn't write them.
- **vs the driver's LINQ v3 provider.** Query produces a driver-LINQ expression (using types from `MongoDB.Driver.Linq`) and hands it off. Pipeline-stage construction, server-side `$expr` rendering, and the actual `BsonDocument[]` pipeline are the driver's job.

## Common pitfalls

- **Allowed method sources are strict.** Only `Queryable`, `MongoQueryableExtensions`, and `MongoDB.Driver.Linq.MongoQueryable` methods are accepted. Anything else throws `NotTranslatedExpression` early.
- **VectorSearch extraction.** `VectorSearch(...)` must be pulled out of the tree before EF's nav-expansion runs (it crashes nav-expansion otherwise) and stitched back in after. If you change preprocessor ordering, re-check this dance.
- **ProjectionMapping discipline.** The `_projectionMapping` keys (`ProjectionMember`s) must match exactly what the shaper expects. A mismatch between the post-processor mapping and the shaper compilation produces silent wrong-results, not crashes.
- **`MongoQueryExpression.CapturedExpression`** must be a *complete* method chain — set once at the tail of `MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall`. Setting it mid-chain truncates the query.
- **Reference-equality on `MethodInfo`.** Translators that match by `MethodInfo` must use canonical constants — `QueryableMethods` for the top-level dispatch in `MongoQueryableMethodTranslatingExpressionVisitor`, `EnumerableMethods` inside the projection-binding visitors, and the driver's `*Method` reflection classes where the bridge to driver-LINQ needs them; open vs. constructed generic methods compare unequal.
- **Unsupported shapes are detected, not silently translated.** Joins, `GroupBy`, set operations (`Intersect` / `Except`) throw early — see the early-fail branches in `MongoQueryableMethodTranslatingExpressionVisitor`.
- **EF Core query cache.** Compiled queries are cached by EF Core by expression-tree shape; if you change a translator's output for a previously-translatable tree, you've quietly invalidated user caches.
- **Multi-EF guards.** Some visitor signatures changed between EF8/EF9/EF10. For representative guard shapes elsewhere in the tree see `Storage/MongoTypeMappingSource.cs` (`#if EF8 || EF9`), the `ChangeTracking/StringDictionaryComparer*.cs` pair (legacy vs. EF10 split), and the `ChangeTracking/ListOf*Comparer.cs` files (`#if EF8`); in Query itself, `QueryingEnumerable.cs` has a `#if !EF8` block.

## How to test

Most query tests assert the **rendered MQL pipeline** rather than executing against a server. The functional and specification tests both have helpers (`AssertMql(...)`) that capture the MQL via `TestMqlLoggerFactory`.

```bash
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~Query"
```

- Unit tests: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/`
- Functional tests (real DB): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/` — golden-path translations and end-to-end materialization.
- Specification tests (EF Core conformance, Northwind suite): `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/` — pattern is to override the upstream test and assert the produced MQL.
