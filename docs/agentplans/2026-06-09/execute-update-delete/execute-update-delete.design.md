# ExecuteUpdate / ExecuteDelete for the MongoDB EF Core Provider — Design

**Date:** 2026-06-09
**Status:** Approved (design); pending implementation plan
**JIRA:** [EF-107](https://jira.mongodb.org/browse/EF-107)

## Summary

Implement EF Core's bulk-operation APIs — `ExecuteDelete()` / `ExecuteDeleteAsync()` and
`ExecuteUpdate(...)` / `ExecuteUpdateAsync(...)` — in the MongoDB EF Core provider. These
execute a single server-side `deleteMany` / `updateMany` against one collection, bypassing
the change tracker, instead of loading entities and saving them back. This is the highest-ROI
independent feature gap identified in the provider's gap analysis: MongoDB has native
multi-document update/delete, so the impedance is low and the performance upside (no
load-into-change-tracker round trip) is high.

## Background

`ExecuteDelete`/`ExecuteUpdate` were **relational-only in EF8** and were promoted into EF Core
*core* in EF9. The EF8 public extension methods live in `EFCore.Relational`
(`RelationalQueryableExtensions`), which this provider deliberately does not reference, and the
core base class `QueryableMethodTranslatingExpressionVisitor` has **no** `TranslateExecuteDelete`/
`TranslateExecuteUpdate` virtuals in EF8. Therefore the feature is supportable only on **EF9 and
EF10** and must be gated behind `#if !EF8`.

The relational reference behavior (confirmed against the EF Core source):

- `NonQueryResult` / `NonQueryResultAsync` in `RelationalShapedQueryCompilingExpressionVisitor`
  issues exactly **one** SQL statement — no `BeginTransaction`, no `IDbContextTransaction`.
  Atomicity of a standalone bulk op comes from the single statement being inherently atomic at
  the database level.
- The command **enlists in the context's `CurrentTransaction`** if one is already open.
- The `IExecutionStrategy.Execute` wrapper around it is about **retry on transient failures**,
  not atomicity.

We mirror this contract for MongoDB.

## Decisions

| Decision | Choice |
|---|---|
| EF version support | EF9 + EF10 only (`#if !EF8`). EF8 cannot reach the core hooks. |
| ExecuteUpdate value scope | Constants/parameters via `$set`; self-referencing expressions (e.g. `e => e.Count + 1`) via aggregation pipeline-form updates. |
| Transaction semantics | Match relational: thread `CurrentTransaction.Session` if open; otherwise a single un-wrapped `updateMany`/`deleteMany`. No implicit transaction of our own. |
| Concurrency | Bypasses the change tracker; optimistic-concurrency tokens are **not** checked (standard EF behavior for these APIs). |
| Public API | None added — the EF9/EF10 extension methods already exist in `EntityFrameworkQueryableExtensions`. |

## Scope & boundaries

**In scope**

- `ExecuteDelete()` / `ExecuteDeleteAsync(CancellationToken)`.
- `ExecuteUpdate(...)` / `ExecuteUpdateAsync(...)` on a single `DbSet`.
- Source query scoped by `Where(...)` predicate(s) that the existing EF→driver-LINQ translator
  can render.
- TPH inheritance: the discriminator filter the query pipeline already injects **must** be
  preserved in the bulk filter, so a bulk op on a derived type only affects documents of that
  type.

**Out of scope (v1) — must throw the canonical EF "could not be translated" message**

- Any source-chain operator beyond filtering: `OrderBy`/`ThenBy`, `Skip`/`Take`, `Select`/
  projection, `Distinct`, set operations, joins, cross-collection / cross-`DbSet` predicates.
  `deleteMany`/`updateMany` take only a filter, so ordering/paging have no server-side meaning.
- Owned / nested-navigation setters. v1 setters target **root scalar properties only**.

**Return value**

- `int` affected-count, cast from the driver's `long` `ModifiedCount` (update) /
  `DeletedCount` (delete), matching EF's `int`-returning API surface.

## Architecture

### Dispatch path (EF9/EF10)

```
query.ExecuteDelete()/.ExecuteUpdate(...)            (EntityFrameworkQueryableExtensions)
   │  Provider.Execute<int>(Expression.Call(ExecuteDeleteMethodInfo/..., source))
   ▼
QueryCompiler.Execute<int> → IDatabase.CompileQuery<int>
   ▼
MongoQueryableMethodTranslatingExpressionVisitor
   │  VisitMethodCall lets the EntityFrameworkQueryableExtensions markers through to base
   │  dispatch (the AllowedQueryableExtensions gate must NOT short-circuit them)
   │  base dispatch → TranslateExecuteDelete(source) / TranslateExecuteUpdate(source, setters)
   │  → returns a MongoNonQueryExpression
   ▼
MongoShapedQueryCompilingExpressionVisitor.VisitExtension
   │  recognizes MongoNonQueryExpression
   │  → emits Expression.Call(NonQueryResult / NonQueryResultAsync, queryContext, ...)
   ▼
static NonQueryResult(queryContext, ...) : int        (NonQueryResultAsync : Task<int>)
       ├─ resolve IMongoCollection by collection name
       ├─ build FilterDefinition from captured predicate (+ discriminator)
       ├─ build UpdateDefinition (update only)
       ├─ thread CurrentTransaction.Session if present
       └─ collection.UpdateMany / DeleteMany → return (int) count
```

### New components

- **`Query/Expressions/MongoNonQueryExpression.cs`** — marker node analogous to relational's
  `NonQueryExpression`. Carries:
  - the source `MongoQueryExpression` (collection name + captured predicate chain),
  - a kind enum (`Delete` / `Update`),
  - for update: an ordered list of setters `(IProperty target, Expression valueExpr,
    bool isSelfReferencing)`.

- **SetProperty-chain extraction** (version-split):
  - **EF9** — `TranslateExecuteUpdate(ShapedQueryExpression source, LambdaExpression
    setPropertyCalls)`. We walk the chained `.SetProperty(...)` calls ourselves.
  - **EF10** — `TranslateExecuteUpdate(ShapedQueryExpression source,
    IReadOnlyList<ExecuteUpdateSetter> setters)`. EF hands us parsed
    `(PropertySelector, ValueExpression)` pairs (a `Convert`-to-`object` wrapper on
    value-type values is stripped by EF).
  - Guarded with `#if EF10 / #elif EF9`.

- **Update-definition builder:**
  - Constant / parameter setters → `Builders<BsonDocument>.Update.Set(elementName,
    serializedValue)`, value serialized through `BsonSerializerFactory` (same path the write
    pipeline uses in `MongoUpdate`).
  - Self-referencing setters → aggregation **pipeline-form** update
    `[{ $set: { <element>: <renderedExpr> } }]`.

- **Bulk executor** — static `NonQueryResult` / `NonQueryResultAsync` (mirroring relational's
  shape): resolve `IMongoCollection<…>` by collection name, build the filter, call
  `UpdateMany`/`DeleteMany` (threading `CurrentTransaction.Session` if present), return the
  count. Wrapped in the execution strategy for retry parity with relational.

### Modified components

- **`Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`**
  - `#if !EF8` overrides of `TranslateExecuteDelete` / `TranslateExecuteUpdate` that return a
    `MongoNonQueryExpression`.
  - The `AllowedQueryableExtensions` gate (currently `:82`) must allow the
    `EntityFrameworkQueryableExtensions` `ExecuteDelete`/`ExecuteUpdate` marker methods to reach
    base dispatch instead of returning `NotTranslatedExpression`.

- **`Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`**
  - Add a `VisitExtension` override recognizing `MongoNonQueryExpression` and emitting the
    `Expression.Call` to the static executor. (Today it overrides only `VisitShapedQuery`.)

## Filter & value translation (reuse)

- **Filter.** Reuse the EF→driver-LINQ predicate translation — the same mechanism the
  vector-search pre-filter uses to produce an `ExpressionFilterDefinition` from a translated
  lambda over the entity type (see `MongoEFToLinqTranslatingExpressionVisitor`, the VectorSearch
  pre-filter path). Turn the captured `Where` chain (plus any discriminator filter) into a
  `FilterDefinition`. The supported source query reduces to a filter-only chain; anything else
  throws.

- **Self-referencing values — primary technical risk.** Rendering `e => e.Count + 1` to the
  aggregation expression `{$add: ["$count", 1]}`. The implementation plan will **spike** the
  exact rendering path (reusing `MongoEFToLinqTranslatingExpressionVisitor`'s expression
  translation vs. driver aggregation-expression rendering) before committing. If the spike shows
  the cost is disproportionate, the fallback is to ship constants-only in v1 and pipeline-form
  self-referencing updates as a fast-follow — flagged explicitly at the spike, not decided
  silently.

## Testing

- **Unit tests** (`tests/MongoDB.EntityFrameworkCore.UnitTests/`) — translator tests asserting
  the produced filter + update BSON for: delete, constant-`$set`, and self-referencing-`$set`
  cases; and that unsupported source shapes (`OrderBy`/`Take`/`Select`/projection) throw the
  canonical EF translation-failure message. Run across EF9 and EF10.

- **Functional tests** (`tests/MongoDB.EntityFrameworkCore.FunctionalTests/`, real DB) —
  end-to-end delete and update with MQL assertions; transaction-enlistment behavior (op runs in
  an open `MongoTransaction`'s session); and the no-implicit-transaction path on a standalone
  server.

- **Specification conformance**
  (`tests/MongoDB.EntityFrameworkCore.SpecificationTests/`) — wire up EF Core's `BulkUpdates`
  specification test base (Northwind) with MongoDB overrides, following the existing
  override-and-assert pattern; annotate any still-unsupported shapes with `// Fails:` ticket tags
  per `docs/failing-spec-tests.md` conventions.

- **EF8 boundary** — a test asserting the methods are unavailable / surface a clear
  "not supported on EF8" path, so the version boundary is explicit and intentional.

## Risks & open questions

1. **Self-referencing value rendering** (see above) — the one real unknown; resolved by a spike
   in the implementation plan with a documented fallback.
2. **Filter typing** — exact collection/serializer typing for the `FilterDefinition`
   (`IMongoCollection<BsonDocument>` vs entity-typed) is an implementation detail to settle in
   the plan; the vector-search pre-filter proves the `ExpressionFilterDefinition` pattern works.
3. **`int` overflow** — counts exceeding `int.MaxValue` are truncated to match EF's `int` API.
   Same behavior as relational; documented, not guarded.
