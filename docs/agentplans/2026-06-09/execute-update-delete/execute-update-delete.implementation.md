# ExecuteUpdate / ExecuteDelete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement EF Core's `ExecuteDelete`/`ExecuteUpdate` bulk-operation APIs (EF9 + EF10) so they run a single server-side `deleteMany`/`updateMany` against one collection, scoped by the query's `Where` predicate, bypassing the change tracker.

**Architecture:** New `TranslateExecuteDelete`/`TranslateExecuteUpdate` overrides on `MongoQueryableMethodTranslatingExpressionVisitor` produce a new `MongoNonQueryExpression` marker node. `MongoShapedQueryCompilingExpressionVisitor.VisitExtension` recognizes that node and emits a call to a static executor that builds a `FilterDefinition<BsonDocument>` (and, for update, an `UpdateDefinition<BsonDocument>`) from the captured predicate/setters and runs `DeleteMany`/`UpdateMany`, returning the affected count. The predicate and self-referencing-value translation reuse `MongoEFToLinqTranslatingExpressionVisitor` (the same machinery the vector-search pre-filter uses).

**Tech Stack:** C# / .NET 8 & 10, EF Core 9/10 (`#if !EF8`), MongoDB C# driver 3.9.0, xUnit + FluentAssertions.

**Key constraints (from the design doc `docs/agentplans/2026-06-09/execute-update-delete/execute-update-delete.design.md`):**
- EF9 + EF10 only. All new code gated `#if !EF8`. `TranslateExecuteUpdate` signature differs EF9 vs EF10.
- Source query may only contain `Where` filtering (+ the implicit discriminator filter for TPH). `OrderBy`/`Skip`/`Take`/`Select`/`Distinct`/joins/set-ops/cross-collection ⇒ throw the canonical EF translation-failure message.
- Update setters target **root scalar properties only**. Constants/parameters ⇒ `$set`; self-referencing (`e => e.Count + 1`) ⇒ aggregation pipeline-form update.
- Transactions: thread `CurrentTransaction.Session` if open; otherwise a single un-wrapped command. No implicit transaction.
- Bypasses change tracker; concurrency tokens not checked. Returns `int` (cast from driver `long`).

**Conventions to follow:**
- Preserve file BOMs on edits. `<Nullable>enable</Nullable>` — annotate new types.
- Build a single version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` (or `EF10`).
- Run a filtered test: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~ExecuteUpdateDelete"`.
- Functional/spec tests need MongoDB (env `MONGODB_URI`/`ATLAS_URI`, else Docker auto-spins). Tests run serially.
- First commit message + branch are JIRA-tagged (`EF-NNN`). Branch `EF-execute-update-delete` already exists with the design doc committed. Replace `EF-NNN` with the real ticket once filed.

---

## Phase 0 — Spikes (resolve the two rendering unknowns first)

These two spikes empirically determine driver-3.9.0 APIs that the production tasks depend on. Each produces a throwaway test that prints rendered BSON and a written decision appended to this plan. **Do not skip** — the production code in Phases 2 and 4 is written against the *expected* API and must be reconciled with the spike output.

### Task 0A: Spike — render an EF predicate to a `FilterDefinition<BsonDocument>`

**Goal:** Determine exactly how to turn a translated entity predicate lambda into a `BsonDocument` filter whose element names honor EF configuration (camel-case, `[Column]`/`HasElementName`, discriminators), using the EF entity serializer — because `IMongoCollection<TEntity>` does NOT use EF's serializer (it's applied per-query via `.As(serializer)`).

**Files:**
- Test (throwaway): `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs`

- [ ] **Step 1: Write a spike test that renders a filter and prints it**

Use an existing functional-test entity that has a renamed/camel-cased element so a wrong serializer is observable. Pick one from `tests/MongoDB.EntityFrameworkCore.FunctionalTests` (e.g. an entity mapped with `HasElementName` or camel-case convention — search the test models). Replace `TEntity`/`SomeEntity` and the property below with that concrete type.

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Xunit;
using Xunit.Abstractions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public sealed class ExecuteUpdateDeleteSpikeTests(ITestOutputHelper output)
{
    [Fact]
    public void Render_predicate_to_bson_filter()
    {
        // Arrange: build the translated driver-LINQ predicate the way the production
        // executor will. For the spike, hand-build a simple predicate over TEntity.
        // Goal: render it to a BsonDocument with EF element names.
        // Candidate API (confirm against driver 3.9.0):
        //   var filter = new ExpressionFilterDefinition<TEntity>(predicateLambda);
        //   var serializer = (IBsonSerializer<TEntity>)bsonSerializerFactory.GetEntitySerializer(entityType);
        //   var bson = filter.Render(new RenderArgs<TEntity>(serializer, BsonSerializer.SerializerRegistry));
        // Print `bson` and assert the element name matches the EF-configured name, NOT the CLR name.
        output.WriteLine("TODO: print rendered filter");
    }
}
```

- [ ] **Step 2: Build and run; iterate on the real API**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` then
`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~ExecuteUpdateDeleteSpikeTests"`
Inspect compiler errors / runtime output to discover the exact `FilterDefinition<T>.Render(...)` overload in driver 3.9.0 (likely `Render(RenderArgs<TDocument>)`; older shape is `Render(IBsonSerializer<TDocument>, IBsonSerializerRegistry)`). Confirm the rendered element name is the EF-configured one when the EF serializer is passed, and is wrong (CLR name) when the default serializer is passed — proving the serializer must be the EF one.

- [ ] **Step 3: Determine how to source the predicate lambda from the captured chain**

The source `MongoQueryExpression.CapturedExpression` is the post-EF method chain (`root.Where(...).Where(...)`, possibly with a discriminator filter). Decide the extraction approach and verify it: run each `Where` lambda body through a `MongoEFToLinqTranslatingExpressionVisitor` and combine with `AndAlso` under a single shared `ParameterExpression` of the entity type. Confirm the vector-search pre-filter path (`MongoEFToLinqTranslatingExpressionVisitor` `ProcessVectorSearch`, which does `new ExpressionFilterDefinition<TEntity>(Visit(preFilterExpression))`) renders correctly when later combined with `.As(serializer)` — and whether `Render` with the EF serializer reproduces that.

- [ ] **Step 4: Write the decision into this plan**

Append a "Spike 0A result" subsection below recording: the exact `Render` call for driver 3.9.0, the predicate-extraction approach, and how a multi-`Where` chain and the discriminator filter are combined. Phase 2 consumes this.

- [ ] **Step 5: Commit the spike test and decision**

```bash
git add docs/superpowers/plans/2026-06-09-execute-update-delete.md tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs
git commit -m "EF-NNN: Spike filter rendering for ExecuteDelete"
```

#### Spike 0A result (verified against driver 3.9.0, EF9)

**Status: PROVEN.** The `Render`-with-EF-serializer approach produces correct (EF-configured) element names; the default driver serializer produces the wrong (CLR) name, confirming the serializer is load-bearing. End-to-end `DeleteMany` on a `BsonDocument` collection with the rendered filter deletes exactly the EF-stored documents. Spike test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs` (3 tests, all pass).

Observed renders (entity `Person` with `Property(e => e.FirstName).HasElementName("forename")`, `LastName` left as CLR name):

| Render path | Predicate | Rendered BSON |
|---|---|---|
| **EF serializer** | `p => p.FirstName == "Ada"` | `{ "forename" : "Ada" }` ✅ |
| Default driver serializer | `p => p.FirstName == "Ada"` | `{ "FirstName" : "Ada" }` ❌ (CLR name) |
| EF serializer (combined) | `p => p.FirstName == "Ada" && p.LastName == "Lovelace"` | `{ "forename" : "Ada", "LastName" : "Lovelace" }` ✅ |

**1. Exact `Render` call (driver 3.9.0).** `FilterDefinition<TDocument>` exposes a *single* `Render` overload: `BsonDocument Render(RenderArgs<TDocument> args)`. The older two-arg `Render(IBsonSerializer<TDocument>, IBsonSerializerRegistry)` shape does **not** exist in 3.9.0. `RenderArgs<TDocument>` has one public ctor — `RenderArgs(IBsonSerializer<TDocument> documentSerializer, IBsonSerializerRegistry serializerRegistry, PathRenderArgs = default, bool renderForFind = default, bool renderForElemMatch = default, bool renderDollarForm = default, ExpressionTranslationOptions = default)` — so the trailing args default and the call is two-arg in practice:

```csharp
var efSerializer = (IBsonSerializer<TEntity>)bsonSerializerFactory.GetEntitySerializer(entityType);
var filter       = new ExpressionFilterDefinition<TEntity>(predicateLambda);   // Expression<Func<TEntity,bool>>
BsonDocument bson = filter.Render(new RenderArgs<TEntity>(efSerializer, BsonSerializer.SerializerRegistry));
```

`GetEntitySerializer(IReadOnlyEntityType)` is `internal` on `BsonSerializerFactory`; the spike added `MongoDB.EntityFrameworkCore.FunctionalTests` to `<InternalsVisibleTo>` in `src/.../MongoDB.EntityFrameworkCore.csproj` (the production executor lives in `src` so it needs no IVT; this IVT is for the test layer and is reused by the production tests in Phase 2).

The production executor builds an `IMongoCollection<BsonDocument>` (matching the write pipeline), so the rendered `BsonDocument` is passed straight to `DeleteMany(rendered)`. Convert to a typed filter where the driver API wants one via `new BsonDocumentFilterDefinition<BsonDocument>(bson)`, or keep the `BsonDocument` directly for `DeleteMany`/`UpdateMany`.

**2. Predicate-extraction approach.** The source is `MongoQueryExpression.CapturedExpression` — the post-EF method chain (`root.Where(λ₁).Where(λ₂)…`, optionally carrying a TPH discriminator `Where`). Extraction:
   - Walk the chain collecting each `Where`'s predicate lambda body.
   - Run each body through a `MongoEFToLinqTranslatingExpressionVisitor` (constructed exactly as in `MongoShapedQueryCompilingExpressionVisitor.TranslateQuery`, `:188-214` — it rewrites `EF.Property(p,"x")` → `Mql.Field(p,"elementName",serializer)` and resolves query-parameter bindings to constants).
   - Combine the translated bodies under a **single shared `ParameterExpression`** of the entity CLR type with `Expression.AndAlso`, then `Expression.Lambda<Func<TEntity,bool>>(combined, param)`.
   - Wrap in `ExpressionFilterDefinition<TEntity>` and `Render` as above.

   The spike proved the combine-with-`AndAlso`-under-one-parameter step renders correctly (`{ "forename": ..., "LastName": ... }`). For predicates the EF visitor lowers to `Mql.Field(...)`, the element name is baked into the expression itself, so it is already correct; the EF serializer is still required so that *plain CLR-member* references (and nested/owned shapes) resolve to EF element names rather than driver defaults — which is exactly the EF-vs-default contrast the spike measured.

**3. Multi-`Where` and discriminator filter.** Multiple `Where`s combine via `AndAlso` under the shared parameter (proven). The TPH discriminator filter rides along as just another `Where` in the captured chain, so it is picked up by the same walk and `AndAlso`-combined — no special-casing needed. (Discriminator-specific end-to-end coverage is deferred to Task 5; the mechanism is identical to any other `Where`.)

**4. Vector-search pre-filter parity.** The vector-search path (`MongoEFToLinqTranslatingExpressionVisitor`, ~`:366-374`) builds `ExpressionFilterDefinition<TEntity>(Visit(preFilterExpression))` and assigns it to `VectorSearchOptions.Filter`, where the driver renders it **inside** the aggregation pipeline after `.As(efSerializer)` has already been applied — so it never calls `Render` explicitly. The `DeleteMany`-on-`BsonDocument` path has **no** `.As()`, which is precisely why this executor must `Render` with the EF serializer itself. Both paths reach the same rendered shape; the only difference is *where* the serializer is supplied (implicitly via `.As()` for vector search vs. explicitly via `RenderArgs` for the bulk executor). Confirmed: the same `ExpressionFilterDefinition<TEntity>` rendered with the EF serializer reproduces the EF element names.

### Task 0B: Spike — render a self-referencing setter value to a pipeline `$set`

**Goal:** Determine how to render `e => e.Count + 1` to the aggregation expression `{ $add: ["$count", 1] }` and assemble a pipeline-form `UpdateDefinition<BsonDocument>` (`[{ $set: { count: { $add: ["$count", 1] } } }]`) using the EF serializer, in driver 3.9.0.

**Files:**
- Test (throwaway): extend `ExecuteUpdateDeleteSpikeTests.cs`.

- [ ] **Step 1: Write a spike test that builds and prints a pipeline update**

```csharp
[Fact]
public void Render_self_referencing_setter_to_pipeline_update()
{
    // Candidate approaches to evaluate (pick whichever driver 3.9.0 supports cleanly):
    //  A) Builders<BsonDocument>.Update.Pipeline(PipelineDefinition<BsonDocument,BsonDocument>)
    //     where the $set stage is a BsonDocument built from a rendered aggregation expression.
    //  B) Render the value via the driver's aggregation-expression translation
    //     (the same translation MongoEFToLinqTranslatingExpressionVisitor feeds the LINQ provider).
    // Print the resulting update document and assert it equals
    //   [{ "$set": { "<element>": { "$add": ["$<element>", 1] } } }]
    output.WriteLine("TODO: print rendered pipeline update");
}
```

- [ ] **Step 2: Build, run, iterate to find the real rendering path**

Run the same build/test commands as 0A (filter to this test). The key unknown is rendering an arbitrary value expression to an aggregation-expression `BsonValue` with EF element names. Investigate whether `MongoEFToLinqTranslatingExpressionVisitor` output can be rendered via a `$project`/`$set` probe (e.g. translate `source.Select(e => new { v = e.Count + 1 })` and capture the rendered stage), or whether the driver exposes a direct expression-to-`BsonValue` renderer.

- [ ] **Step 3: Decide scope per the design's fallback**

If a robust rendering path exists, record it. If it proves disproportionate for driver 3.9.0, invoke the design's documented fallback: **ship constants-only `$set` in v1, defer self-referencing to a fast-follow.** Record the decision explicitly.

- [ ] **Step 4: Write "Spike 0B result" into this plan and commit**

```bash
git add docs/superpowers/plans/2026-06-09-execute-update-delete.md tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs
git commit -m "EF-NNN: Spike self-referencing pipeline-update rendering"
```

#### Spike 0B result (verified against driver 3.9.0, EF9 + EF10)

**Status: PROVEN — a robust rendering path exists. The constants-only fallback is NOT needed.** Self-referencing setters (`o => o.Quantity + 1`) render to an aggregation-expression `BsonValue` with EF element names via a *direct* driver renderer — no `$project`/`$set` query probe, no `LoggedStages` capture, no query execution required. Spike test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs` (3 new tests added under "SPIKE 0B"; all 6 tests in the file pass under both `Debug EF9` and `Debug EF10`).

Observed renders (entity `Order` with `Property(e => e.Quantity).HasElementName("qty")`):

| Render path | Value expression | Rendered `BsonValue` |
|---|---|---|
| **EF serializer** | `o => o.Quantity + 1` | `{ "$add" : ["$qty", 1] }` ✅ (EF element name) |
| Default driver serializer | `o => o.Quantity + 1` | `{ "$add" : ["$Quantity", 1] }` ❌ (CLR name) |
| EF serializer, visitor-shaped lambda | `o => Mql.Field(o, "qty", <int ser>) + 1` | `{ "$add" : ["$qty", 1] }` ✅ |

End-to-end (`UpdateMany` on a `BsonDocument` collection with the assembled pipeline update): documents seeded via EF (`qty` 10/20 with `Status="open"`, 99 with `"closed"`), filtered to `Status == "open"`, incremented → `ModifiedCount == 2`, stored `qty` became `11`/`21`, the `"closed"` doc stayed `99`. ✅

**1. The renderer (driver 3.9.0).** Driver exposes `ExpressionAggregateExpressionDefinition<TSource, TResult>` with a **public** ctor `ExpressionAggregateExpressionDefinition(Expression<Func<TSource, TResult>> expression)` and `BsonValue Render(RenderArgs<TSource> args)`. This is the aggregation-expression analogue of `ExpressionFilterDefinition<T>` from Spike 0A, and it uses the *same* `RenderArgs<TSource>` two-arg form:

```csharp
var efSerializer = (IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType);
var valueExpr    = (Expression<Func<TSource, TResult>>)translatedValueLambda;   // driver-LINQ form
BsonValue rendered = new ExpressionAggregateExpressionDefinition<TSource, TResult>(valueExpr)
    .Render(new RenderArgs<TSource>(efSerializer, BsonSerializer.SerializerRegistry));
// rendered == { "$add" : ["$qty", 1] }
```

As with 0A, the EF serializer is load-bearing: the default driver serializer renders the CLR name (`$Quantity`), the EF serializer renders the configured element name (`$qty`).

**2. Value-expression extraction.** The user's value body (`o => o.Quantity + 1`, or the EF9 `SetProperty(sel, val)` value, or the EF10 `ExecuteUpdateSetter.ValueExpression`) is translated through a `MongoEFToLinqTranslatingExpressionVisitor` constructed exactly as in `MongoShapedQueryCompilingExpressionVisitor.TranslateQuery` (`:188-214`) — the same machinery Phase 2 uses for the predicate. The visitor lowers `EF.Property(o,"Quantity")` / CLR member access to `Mql.Field(o, "qty", <typeSerializer>)`, so the element name is baked into the expression even before `Render`. The spike proved both shapes render identically: a plain-CLR lambda (`o => o.Quantity + 1`) **and** a hand-built visitor-shaped lambda (`o => Mql.Field(o,"qty",ser) + 1`) both produce `{ $add: ["$qty", 1] }`. Wrap the translated body in `Expression.Lambda<Func<TSource, TResult>>(body, entityParam)` (`TResult` = the property CLR type, e.g. `int`).

**3. Assembling the pipeline update.** Build one `$set` stage as a `BsonDocument` keyed by `property.GetElementName()`, value = the rendered `BsonValue`; wrap with `PipelineDefinition<BsonDocument,BsonDocument>.Create(new[] { setStage })` and `Builders<BsonDocument>.Update.Pipeline(pipeline)`:

```csharp
var setStage = new BsonDocument("$set", new BsonDocument(property.GetElementName(), rendered));
var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(new[] { setStage });
var update   = Builders<BsonDocument>.Update.Pipeline(pipeline);   // UpdateDefinition<BsonDocument>
// pipeline form: [{ "$set" : { "qty" : { "$add" : ["$qty", 1] } } }]
```

For **Task 8**, multiple setters merge into the single `$set` stage: self-referencing values contribute their rendered `BsonValue`; constant/parameter values contribute the serialized literal (the same `SerializePropertyValue` path as the constant `$set` in Task 6). A mixed call (some constant, some self-referencing) is therefore one `$set` stage with a mix of literal and aggregation-expression values — which a pipeline `$set` handles natively (literals and `$expr`-style values coexist in `$set`). The constant-only path (Task 6) can stay on the cheaper `BsonDocumentUpdateDefinition` `{ $set: {...} }` document form; only when *any* setter is self-referencing does `BuildUpdate` switch to the pipeline form.

**4. No fallback required.** The design's documented fallback (ship constants-only `$set` in v1, defer self-referencing) is **not** invoked. The renderer is a first-class, public driver API (`ExpressionAggregateExpressionDefinition` + `Render`), reuses the exact `RenderArgs`/EF-serializer mechanism already proven in 0A, and works unchanged on EF9 and EF10. Task 8 proceeds as the pipeline-update implementation (not the throw-and-defer variant).

> After Phase 4, delete `ExecuteUpdateDeleteSpikeTests.cs` (its behavior is covered by the real tests). A cleanup step is included in Task 9.

---

## Phase 1 — The marker expression node

### Task 1: `MongoNonQueryExpression`

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoNonQueryExpression.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoNonQueryExpressionTests.cs`

- [ ] **Step 1: Write the failing unit test**

```csharp
using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.Expressions;

public class MongoNonQueryExpressionTests
{
    [Fact]
    public void Delete_node_exposes_source_and_kind()
    {
        var source = new MongoQueryExpression(Mock.EntityType()); // use existing test helper for an IEntityType-backed MongoQueryExpression
        var node = new MongoNonQueryExpression(source);

        node.Kind.Should().Be(MongoNonQueryExpression.OperationKind.Delete);
        node.SourceQuery.Should().BeSameAs(source);
        node.Type.Should().Be(typeof(int));
        node.Setters.Should().BeEmpty();
    }

    [Fact]
    public void Update_node_carries_setters()
    {
        var source = new MongoQueryExpression(Mock.EntityType());
        var property = Mock.Property();                          // existing test helper
        Expression value = Expression.Constant(5);
        var setters = new[] { new MongoNonQueryExpression.Setter(property, value, IsSelfReferencing: false) };

        var node = new MongoNonQueryExpression(source, setters);

        node.Kind.Should().Be(MongoNonQueryExpression.OperationKind.Update);
        node.Setters.Should().ContainSingle().Which.Property.Should().BeSameAs(property);
    }
}
```

Note: replace `Mock.EntityType()`/`Mock.Property()` with whatever the UnitTests project already uses to construct an `IEntityType`/`IProperty` and a `MongoQueryExpression` (search `tests/MongoDB.EntityFrameworkCore.UnitTests/Query` for existing patterns; the project has expression tests already).

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --filter "FullyQualifiedName~MongoNonQueryExpressionTests"`
Expected: FAIL — `MongoNonQueryExpression` does not exist.

- [ ] **Step 3: Implement the node**

```csharp
/* Copyright 2023-present MongoDB Inc. ... (copy the standard license header from a sibling file in Query/Expressions/) */

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Marker node produced by ExecuteDelete / ExecuteUpdate translation. Carries the source
/// query (collection + captured predicate chain) and, for updates, the ordered setters.
/// Recognized by <see cref="Visitors.MongoShapedQueryCompilingExpressionVisitor"/>.
/// </summary>
internal sealed class MongoNonQueryExpression : Expression
{
    public enum OperationKind { Delete, Update }

    public sealed record Setter(IProperty Property, Expression ValueExpression, bool IsSelfReferencing);

    public MongoNonQueryExpression(MongoQueryExpression sourceQuery)
    {
        SourceQuery = sourceQuery;
        Kind = OperationKind.Delete;
        Setters = [];
    }

    public MongoNonQueryExpression(MongoQueryExpression sourceQuery, IReadOnlyList<Setter> setters)
    {
        SourceQuery = sourceQuery;
        Kind = OperationKind.Update;
        Setters = setters;
    }

    public MongoQueryExpression SourceQuery { get; }
    public OperationKind Kind { get; }
    public IReadOnlyList<Setter> Setters { get; }

    public override ExpressionType NodeType => ExpressionType.Extension;
    public override System.Type Type => typeof(int);
    public override bool CanReduce => false;

    protected override Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor) => this;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --filter "FullyQualifiedName~MongoNonQueryExpressionTests"`
Expected: PASS.

- [ ] **Step 5: Confirm it builds under EF10 too**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Expected: build succeeds (the node is version-agnostic; no `#if` needed on it, but it is only *referenced* from `#if !EF8` code).

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoNonQueryExpression.cs tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoNonQueryExpressionTests.cs
git commit -m "EF-NNN: Add MongoNonQueryExpression marker node"
```

---

## Phase 2 — ExecuteDelete (end to end)

### Task 2: Let the ExecuteDelete/ExecuteUpdate markers reach base dispatch

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs` (the `AllowedQueryableExtensions` gate at `:69-83`)

**Context:** `VisitMethodCall` returns `NotTranslatedExpression` for any method whose declaring type isn't in `AllowedQueryableExtensions`. The `ExecuteDelete`/`ExecuteUpdate` markers are declared on `Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions`, so they'd be rejected before base dispatch. We must let them through to `base.VisitMethodCall`, which routes them to `TranslateExecuteDelete`/`TranslateExecuteUpdate`.

- [ ] **Step 1: Add the EF extensions type to the allow-list (EF9+ only)**

Edit the `AllowedQueryableExtensions` field and the early-return guard. Replace lines 69-83 region:

```csharp
    private static readonly Type[] AllowedQueryableExtensions =
        [typeof(Queryable), typeof(MongoQueryableExtensions), typeof(Driver.Linq.MongoQueryable)];

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (!AllowedQueryableExtensions.Contains(method.DeclaringType))
        {
#if !EF8
            // ExecuteDelete/ExecuteUpdate markers live on EntityFrameworkQueryableExtensions; let the
            // base visitor dispatch them to TranslateExecuteDelete/TranslateExecuteUpdate.
            if (method.DeclaringType == typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions))
            {
                return base.VisitMethodCall(methodCallExpression);
            }
#endif
            return QueryCompilationContext.NotTranslatedExpression;
        }
        // ... existing body unchanged ...
```

(Keep the rest of the method as-is.)

- [ ] **Step 2: Build EF9 and EF10 to confirm it compiles**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` and `... -c "Debug EF10"`
Expected: both succeed. (Verify `EntityFrameworkQueryableExtensions` is the correct declaring type — confirm in Task 0/the EF source; for EF8 this code is excluded.)

- [ ] **Step 3: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-NNN: Allow ExecuteDelete/Update markers through method-source gate"
```

### Task 3: `TranslateExecuteDelete` override → marker

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`

- [ ] **Step 1: Add the override (EF9+), reusing the captured-expression discipline**

Add inside the class, after `TranslateOfType` (around `:265`). The source's `MongoQueryExpression` already holds the collection and `CapturedExpression`; we set `CapturedExpression` to the full chain the same way the normal path does, then wrap in the marker. Reject unsupported source shapes by inspecting the captured chain.

```csharp
#if !EF8
    protected override Expression? TranslateExecuteDelete(ShapedQueryExpression source)
    {
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        ValidateBulkSource(mongoQueryExpression);          // throws translation-failure for non-filter chains
        return new MongoNonQueryExpression(mongoQueryExpression);
    }
#endif
```

Add the validator helper (used by both delete and update):

```csharp
#if !EF8
    // A bulk delete/update may only be scoped by Where filters (plus the implicit discriminator
    // filter EF injects for TPH). Ordering, paging, projection, set-ops, joins have no server-side
    // meaning for deleteMany/updateMany — reject them with EF's canonical translation-failure message.
    private void ValidateBulkSource(MongoQueryExpression mongoQueryExpression)
    {
        var node = mongoQueryExpression.CapturedExpression;
        while (node is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            switch (call.Method.Name)
            {
                case nameof(Queryable.Where):
                    node = call.Arguments[0];
                    break;
                default:
                    AddTranslationErrorDetails(
                        $"The '{call.Method.Name}' operator cannot be used to scope a bulk ExecuteDelete/ExecuteUpdate; only 'Where' is supported.");
                    throw new InvalidOperationException(
                        CoreStrings.NonQueryTranslationFailedWithDetails(
                            mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
            }
        }
    }
#endif
```

Notes for the implementer:
- Confirm `CoreStrings.NonQueryTranslationFailedWithDetails` exists in EF9/EF10 (it does in relational/core; verify the exact name during build). If only `TranslationFailedWithDetails` is available in core, use that.
- `CapturedExpression` may be `null` (direct `context.Set<T>().ExecuteDelete()` with no `Where`) — the `while` simply doesn't run, which is correct (delete all).
- The discriminator filter for TPH is injected upstream as part of the captured chain and is itself a `Where`/predicate; it passes the validator. Confirm in the functional test for a derived type (Task 5 optional extension).

- [ ] **Step 2: Build EF9/EF10**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` / `-c "Debug EF10"`
Expected: succeed (the executor doesn't exist yet, but `VisitExtension` will be added in Task 4 — until then a delete will fail at shaper compilation, which is fine; we add the behavior test in Task 5 after Task 4).

- [ ] **Step 3: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs
git commit -m "EF-NNN: TranslateExecuteDelete produces MongoNonQueryExpression"
```

### Task 4: Executor + `VisitExtension` dispatch for delete

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`

**Context:** Reuse the collection/session resolution pattern from `TranslateQuery<TSource>` (`:188-214`). For the filter, apply the **Spike 0A** result.

- [ ] **Step 1: Add `VisitExtension` override and the delete executor**

Add to `MongoShapedQueryCompilingExpressionVisitor`:

```csharp
#if !EF8
    protected override Expression VisitExtension(Expression extensionExpression)
        => extensionExpression is MongoNonQueryExpression nonQuery
            ? VisitNonQuery(nonQuery)
            : base.VisitExtension(extensionExpression);

    private Expression VisitNonQuery(MongoNonQueryExpression nonQuery)
    {
        var entityType = nonQuery.SourceQuery.CollectionExpression.EntityType;
        var executor = QueryCompilationContext.IsAsync
            ? (nonQuery.Kind == MongoNonQueryExpression.OperationKind.Delete
                ? ExecuteDeleteAsyncMethodInfo : ExecuteUpdateAsyncMethodInfo)
            : (nonQuery.Kind == MongoNonQueryExpression.OperationKind.Delete
                ? ExecuteDeleteMethodInfo : ExecuteUpdateMethodInfo);

        return Expression.Call(null,
            executor.MakeGenericMethod(entityType.ClrType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(entityType),
            Expression.Constant(_bsonSerializerFactory),
            Expression.Constant(nonQuery));
    }

    private static int ExecuteDelete<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter, logger) =
            PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);

        // logger.ExecutingBulkDelete(...);  // add a diagnostics event in Task 7
        var result = session == null
            ? collection.DeleteMany(filter)
            : collection.DeleteMany(session, filter);
        return checked((int)result.DeletedCount);
    }

    private static async Task<int> ExecuteDeleteAsync<TSource>(
        QueryContext queryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter, _) =
            PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);

        var ct = queryContext.CancellationToken;
        var result = session == null
            ? await collection.DeleteManyAsync(filter, ct).ConfigureAwait(false)
            : await collection.DeleteManyAsync(session, filter, options: null, ct).ConfigureAwait(false);
        return checked((int)result.DeletedCount);
    }
```

Add the shared preparation helper. **The body of `BuildFilter` is the Spike 0A result** — the snippet below is the expected shape; reconcile the `Render` call and predicate extraction with the recorded spike decision:

```csharp
    private static (IMongoCollection<BsonDocument> collection,
                    IClientSessionHandle? session,
                    FilterDefinition<BsonDocument> filter,
                    MongoQueryContext context)
        PrepareBulk<TSource>(
            QueryContext queryContext,
            IReadOnlyEntityType entityType,
            BsonSerializerFactory bsonSerializerFactory,
            MongoNonQueryExpression nonQuery)
    {
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var collectionName = nonQuery.SourceQuery.CollectionExpression.CollectionName;
        var collection = mongoQueryContext.MongoClient.GetCollection<BsonDocument>(collectionName);
        var session = (mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session;

        var filter = BuildFilter<TSource>(mongoQueryContext, entityType, bsonSerializerFactory, nonQuery.SourceQuery);
        return (collection, session, filter, mongoQueryContext);
    }

    // === Spike 0A result goes here ===
    // Expected shape (confirm Render overload + predicate extraction against driver 3.9.0):
    private static FilterDefinition<BsonDocument> BuildFilter<TSource>(
        MongoQueryContext mongoQueryContext,
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoQueryExpression sourceQuery)
    {
        var serializer = (IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer((IEntityType)entityType);

        // No predicate => match everything.
        if (sourceQuery.CapturedExpression == null)
        {
            return FilterDefinition<BsonDocument>.Empty;
        }

        // Translate the captured Where chain into a single Expression<Func<TSource,bool>> in
        // driver-LINQ terms via MongoEFToLinqTranslatingExpressionVisitor (see Spike 0A for the
        // exact extraction + combination of multiple Where lambdas under one parameter), then:
        //   var efFilter = new ExpressionFilterDefinition<TSource>(predicate);
        //   var rendered = efFilter.Render(new RenderArgs<TSource>(serializer, BsonSerializer.SerializerRegistry));
        //   return new BsonDocumentFilterDefinition<BsonDocument>(rendered);
        throw new System.NotImplementedException("Replace with Spike 0A result.");
    }

    private static readonly MethodInfo ExecuteDeleteMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor).GetTypeInfo()
            .DeclaredMethods.Single(m => m.Name == nameof(ExecuteDelete));
    private static readonly MethodInfo ExecuteDeleteAsyncMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor).GetTypeInfo()
            .DeclaredMethods.Single(m => m.Name == nameof(ExecuteDeleteAsync));
    // ExecuteUpdateMethodInfo / ExecuteUpdateAsyncMethodInfo added in Task 6.
#endif
```

Add required `using`s: `System.Threading.Tasks`, and confirm `Microsoft.EntityFrameworkCore.Metadata` (`IEntityType`) is imported.

- [ ] **Step 2: Reconcile `BuildFilter` with the Spike 0A decision**

Replace the `NotImplementedException` body with the spike-validated rendering + predicate-extraction code. Remove the `ExecuteUpdate*` `MethodInfo` references for now (or add the methods as stubs) so it compiles — simplest is to add Task 6's methods now if you prefer, but the plan keeps delete shippable on its own; temporarily comment the two update `MethodInfo` fields and the update branch in `VisitNonQuery` until Task 6.

- [ ] **Step 3: Build EF9/EF10**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` / `-c "Debug EF10"`
Expected: succeed.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs
git commit -m "EF-NNN: Execute server-side deleteMany for ExecuteDelete"
```

### Task 5: ExecuteDelete behavior tests (functional, real DB)

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs`

- [ ] **Step 1: Write failing functional tests**

Follow the existing functional-test fixture pattern (seed via `TemporaryDatabaseFixture`/the project's standard setup — copy the structure from a neighboring file in `tests/.../FunctionalTests/Query/`). Tests:

```csharp
#if !EF8
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class ExecuteDeleteTests   // : a base/fixture matching the project's convention
{
    [Fact]
    public void ExecuteDelete_with_predicate_deletes_matching_and_returns_count()
    {
        // seed N docs; M match predicate
        using var db = /* create context over a uniquely-named database, seed data */;
        var deleted = db.Set<Customer>().Where(c => c.Region == "WA").ExecuteDelete();
        deleted.Should().Be(/* M */);
        db.Set<Customer>().Count(c => c.Region == "WA").Should().Be(0);
        db.Set<Customer>().Count().Should().Be(/* N - M */);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_deletes_matching()
    {
        using var db = /* ... */;
        var deleted = await db.Set<Customer>().Where(c => c.Region == "WA").ExecuteDeleteAsync();
        deleted.Should().Be(/* M */);
    }

    [Fact]
    public void ExecuteDelete_with_ordering_throws_translation_failure()
    {
        using var db = /* ... */;
        var act = () => db.Set<Customer>().OrderBy(c => c.Name).ExecuteDelete();
        act.Should().Throw<InvalidOperationException>();
    }
}
#endif
```

Use a real mapped entity from the functional-test models (replace `Customer`/`Region`/`Name`).

- [ ] **Step 2: Run to verify they fail (then pass after build)**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` then
`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --no-build --filter "FullyQualifiedName~ExecuteDeleteTests"`
Expected: the delete tests PASS (Task 4 implemented them); the ordering test PASSES (asserts the throw). If a delete test fails, debug `BuildFilter` rendering against Spike 0A.

- [ ] **Step 3: Run the same under EF10**

Run: `... -c "Debug EF10" ...`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs
git commit -m "EF-NNN: ExecuteDelete functional tests"
```

---

## Phase 3 — ExecuteUpdate with constant/parameter setters

### Task 6: `TranslateExecuteUpdate` (EF9 + EF10 signature split) and constant-`$set` executor

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`

- [ ] **Step 1: Add the version-split override**

In `MongoQueryableMethodTranslatingExpressionVisitor`:

```csharp
#if EF10
    protected override Expression? TranslateExecuteUpdate(
        ShapedQueryExpression source,
        IReadOnlyList<ExecuteUpdateSetter> setters)
    {
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        ValidateBulkSource(mongoQueryExpression);
        var parsed = setters
            .Select(s => BuildSetter(mongoQueryExpression, s.PropertySelector, s.ValueExpression))
            .ToList();
        return new MongoNonQueryExpression(mongoQueryExpression, parsed);
    }
#elif EF9
    protected override Expression? TranslateExecuteUpdate(
        ShapedQueryExpression source,
        LambdaExpression setPropertyCalls)
    {
        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        ValidateBulkSource(mongoQueryExpression);
        var parsed = new List<MongoNonQueryExpression.Setter>();
        // Walk the chained SetPropertyCalls: ((SetPropertyCalls<T> c) => c.SetProperty(sel, val).SetProperty(...))
        var body = setPropertyCalls.Body;
        while (body is MethodCallExpression { Method.Name: "SetProperty" } call)
        {
            var selector = call.Arguments[0].UnwrapLambdaFromQuote();
            var value = call.Arguments[1];                      // may be a value or a lambda (self-referencing)
            parsed.Insert(0, BuildSetter(mongoQueryExpression, selector, value));
            body = call.Object!;
        }
        return new MongoNonQueryExpression(mongoQueryExpression, parsed);
    }
#endif
```

Add the shared setter-builder that resolves the target `IProperty` and classifies the value:

```csharp
#if !EF8
    private MongoNonQueryExpression.Setter BuildSetter(
        MongoQueryExpression mongoQueryExpression,
        LambdaExpression propertySelector,
        Expression valueExpression)
    {
        var entityType = mongoQueryExpression.CollectionExpression.EntityType;
        // propertySelector is e => e.SomeScalar ; resolve to an IProperty (root scalar only).
        var member = (propertySelector.Body as MemberExpression)
            ?? (propertySelector.Body is UnaryExpression { Operand: MemberExpression m } ? m : null);
        var property = member is null ? null : entityType.FindProperty(member.Member.Name);
        if (property == null)
        {
            AddTranslationErrorDetails(
                "ExecuteUpdate setters must target a mapped root scalar property of the entity.");
            throw new InvalidOperationException(
                CoreStrings.NonQueryTranslationFailedWithDetails(
                    mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
        }

        // Self-referencing when the value is a lambda referencing the entity parameter
        // (EF9 passes Func<T,TProp>; EF10 passes a plain value Expression for constants and a
        // parameter-referencing Expression for computed values — see Spike 0B / EF10 setter parsing).
        var isSelfReferencing = ValueReferencesEntity(valueExpression, propertySelector.Parameters);
        return new MongoNonQueryExpression.Setter(property, valueExpression, isSelfReferencing);
    }

    private static bool ValueReferencesEntity(Expression value, IReadOnlyList<ParameterExpression> entityParams)
    {
        var found = false;
        var finder = new ParameterFinder(entityParams, () => found = true);
        finder.Visit(value);
        return found;
    }

    private sealed class ParameterFinder(IReadOnlyList<ParameterExpression> targets, System.Action onFound)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (targets.Contains(node)) onFound();
            return base.VisitParameter(node);
        }
    }
#endif
```

> Note: confirm the exact EF9 `SetProperty` argument layout (selector at `[0]`, value at `[1]`) and the EF10 `ExecuteUpdateSetter` value normalization (a `Convert`-to-`object` wrapper is stripped by EF) during build, per Task 0/the EF source findings.

- [ ] **Step 2: Add the update executor (constant path) in the compiling visitor**

In `MongoShapedQueryCompilingExpressionVisitor`, add `ExecuteUpdate`/`ExecuteUpdateAsync` mirroring `ExecuteDelete`, and the `MethodInfo` fields referenced by `VisitNonQuery`. Build the update from setters:

```csharp
    private static int ExecuteUpdate<TSource>(
        QueryContext queryContext, IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory, MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter, _) =
            PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);
        var update = BuildUpdate<TSource>((MongoQueryContext)queryContext, entityType, bsonSerializerFactory, nonQuery);
        var result = session == null
            ? collection.UpdateMany(filter, update)
            : collection.UpdateMany(session, filter, update);
        return checked((int)result.ModifiedCount);
    }

    private static async Task<int> ExecuteUpdateAsync<TSource>(
        QueryContext queryContext, IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory, MongoNonQueryExpression nonQuery)
    {
        var (collection, session, filter, _) =
            PrepareBulk<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery);
        var update = BuildUpdate<TSource>((MongoQueryContext)queryContext, entityType, bsonSerializerFactory, nonQuery);
        var ct = queryContext.CancellationToken;
        var result = session == null
            ? await collection.UpdateManyAsync(filter, update, options: null, ct).ConfigureAwait(false)
            : await collection.UpdateManyAsync(session, filter, update, options: null, ct).ConfigureAwait(false);
        return checked((int)result.ModifiedCount);
    }

    private static UpdateDefinition<BsonDocument> BuildUpdate<TSource>(
        MongoQueryContext mongoQueryContext, IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory, MongoNonQueryExpression nonQuery)
    {
        if (nonQuery.Setters.Any(s => s.IsSelfReferencing))
        {
            return BuildPipelineUpdate<TSource>(mongoQueryContext, entityType, bsonSerializerFactory, nonQuery); // Task 8 / Spike 0B
        }

        // Constant/parameter $set path: serialize each value via the property serializer (same as MongoUpdate).
        var setDoc = new BsonDocument();
        foreach (var setter in nonQuery.Setters)
        {
            var elementName = setter.Property.GetElementName();
            var value = EvaluateConstant(mongoQueryContext, setter.ValueExpression);
            setDoc[elementName] = SerializePropertyValue(setter.Property, value);  // mirror MongoUpdate.WriteProperty
        }
        return new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument("$set", setDoc));
    }
```

For `EvaluateConstant` reuse the parameter/closure evaluation pattern already in `MongoEFToLinqTranslatingExpressionVisitor.TryEvaluateToConstant` (`:657-694`) — extract a small shared helper or replicate the constant/closure/query-parameter cases. For `SerializePropertyValue`, mirror how `MongoUpdate.WriteProperty` (`Storage/MongoUpdate.cs:204`) serializes a property value to a `BsonValue` via `BsonSerializerFactory.GetPropertySerializationInfo(property)`. Add `BuildPipelineUpdate` as a `throw new NotImplementedException("Task 8")` stub for now.

- [ ] **Step 3: Build EF9/EF10**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` / `-c "Debug EF10"`
Expected: succeed.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs
git commit -m "EF-NNN: TranslateExecuteUpdate + constant \$set executor"
```

### Task 7: ExecuteUpdate (constant) behavior tests + diagnostics events

**Files:**
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Diagnostics/MongoLoggerUpdateExtensions.cs` and `Diagnostics/MongoEventId.cs` (add `ExecutingBulkDelete`/`ExecutingBulkUpdate` events mirroring the existing `ExecutingBulkWrite` event), then call them from the executors.

- [ ] **Step 1: Write failing functional tests**

```csharp
#if !EF8
[Fact]
public void ExecuteUpdate_sets_constant_and_returns_count()
{
    using var db = /* seed */;
    var updated = db.Set<Customer>().Where(c => c.Region == "WA")
        .ExecuteUpdate(s => s.SetProperty(c => c.Region, "PNW"));
    updated.Should().Be(/* M */);
    db.Set<Customer>().Count(c => c.Region == "PNW").Should().Be(/* M */);
}

[Fact]
public void ExecuteUpdate_sets_parameter_value()
{
    var newRegion = "PNW";
    using var db = /* seed */;
    db.Set<Customer>().Where(c => c.Region == "WA")
        .ExecuteUpdate(s => s.SetProperty(c => c.Region, newRegion));
    db.Set<Customer>().Count(c => c.Region == "PNW").Should().Be(/* M */);
}
#endif
```

- [ ] **Step 2: Run, debug to green (EF9 then EF10)**

Run: `dotnet test ... -c "Debug EF9" --no-build --filter "FullyQualifiedName~ExecuteUpdateTests"` then EF10.
Expected: PASS. If the `$set` value serialization is wrong (e.g. enum/Guid representation), align `SerializePropertyValue` with `MongoUpdate`.

- [ ] **Step 3: Add diagnostics events and wire them into the executors**

Mirror `ExecutingBulkWrite`/`ExecutedBulkWrite` in `MongoLoggerUpdateExtensions.cs` for delete/update bulk ops; register IDs in `MongoEventId`. Call them from `ExecuteDelete*/ExecuteUpdate*`. (Run the diagnostics-reviewer mentally: event IDs are stable/append-only.)

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs src/MongoDB.EntityFrameworkCore/Diagnostics/
git commit -m "EF-NNN: ExecuteUpdate constant setters + bulk diagnostics events"
```

---

## Phase 4 — Self-referencing setters (pipeline updates)

### Task 8: `BuildPipelineUpdate` (consumes Spike 0B)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateSelfReferencingTests.cs`

> If Spike 0B selected the **constants-only fallback**, replace this task with: make `BuildPipelineUpdate` throw the canonical translation-failure for self-referencing setters, add a test asserting that throw, and file a fast-follow ticket. Skip the rest of Task 8.

- [ ] **Step 1: Write failing functional test**

```csharp
#if !EF8
[Fact]
public void ExecuteUpdate_increments_self_referencing_value()
{
    using var db = /* seed: Orders with Quantity values */;
    var updated = db.Set<Order>().Where(o => o.Status == "open")
        .ExecuteUpdate(s => s.SetProperty(o => o.Quantity, o => o.Quantity + 1));
    updated.Should().Be(/* M */);
    // assert each matched order's Quantity increased by 1
}
#endif
```

- [ ] **Step 2: Implement `BuildPipelineUpdate` from the Spike 0B decision**

Render each self-referencing setter's value expression to an aggregation-expression `BsonValue` (with EF element names) and assemble `Builders<BsonDocument>.Update.Pipeline([{ $set: { <element>: <renderedExpr> } }])`. Constant setters in the same call go into the same `$set` stage as literals. Use the exact rendering API recorded in Spike 0B.

- [ ] **Step 3: Run to green (EF9 then EF10)**

Run: `dotnet test ... --filter "FullyQualifiedName~ExecuteUpdateSelfReferencingTests"` under both configs.
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateSelfReferencingTests.cs
git commit -m "EF-NNN: Self-referencing ExecuteUpdate via pipeline update"
```

---

## Phase 5 — Spec conformance, EF8 boundary, cleanup

### Task 9: Wire up EF Core `BulkUpdates` specification tests + EF8 boundary + cleanup

**Files:**
- Create: spec override classes under `tests/MongoDB.EntityFrameworkCore.SpecificationTests/` following the existing override-and-assert pattern (find EF Core's `BulkUpdates` test bases via the Specification.Tests package; mirror how a neighboring `Northwind*MongoTest` wires a base).
- Create: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteEf8Tests.cs` (EF8 boundary).
- Delete: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs`.

- [ ] **Step 1: Add the EF8 boundary test**

```csharp
#if EF8
// On EF8, ExecuteDelete/ExecuteUpdate live in EFCore.Relational which this provider does not
// reference, so the APIs are not available. This test documents the intentional boundary.
// (If the extension methods are not even resolvable, this file may instead be a comment-only
// marker; confirm during build.)
#endif
```

If the methods aren't resolvable at all on EF8, document the boundary in `README.md`/`BREAKING-CHANGES.md` notes instead and skip the test file.

- [ ] **Step 2: Add `BulkUpdates` spec overrides; annotate unsupported shapes**

Override the EF Core bulk-update specification base. For shapes the provider doesn't support (cross-collection predicates, ordering/paging, owned-navigation setters), assert the translation failure and tag with `// Fails: <desc> EF-NNN` per `docs/failing-spec-tests.md` conventions. Update `docs/failing-spec-tests.md` with any new tickets.

- [ ] **Step 3: Delete the spike test file**

```bash
git rm tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateDeleteSpikeTests.cs
```

- [ ] **Step 4: Run the full query + spec suites for all three EF versions**

Use the `/test-all` skill (builds + tests EF8/EF9/EF10 in parallel), or:
`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --filter "FullyQualifiedName~Execute|FullyQualifiedName~BulkUpdates"` (repeat EF10; EF8 confirms the feature is absent and nothing else broke).
Expected: all PASS.

- [ ] **Step 5: Update README "Limitations"**

Remove "ExecuteUpdate & ExecuteDelete bulk operations (EF 9+)" from the *Planned for future releases* list in `README.md` (and add a line to *Supported Features* noting EF9+ bulk `ExecuteUpdate`/`ExecuteDelete`).

- [ ] **Step 6: Commit**

```bash
git add tests/ docs/failing-spec-tests.md README.md
git commit -m "EF-NNN: BulkUpdates spec conformance, EF8 boundary, docs"
```

### Task 10: Branch review

- [ ] **Step 1: Run the provider's review skill**

Invoke `/review-ef-core-provider` (it fans out the area reviewers — query, storage, diagnostics, api-stability, ef-conformance). Address findings. The `ef-conformance-reviewer` is especially relevant for the EF9-vs-EF10 signature split and the `#if !EF8` discipline; `api-stability-reviewer` confirms no public-surface break.

- [ ] **Step 2: Final full test pass via `/test-all`, then open PR**

Only push/PR when the user asks.

---

## Self-Review (performed against the spec)

- **Spec §Scope (in):** ExecuteDelete/ExecuteUpdate sync+async → Tasks 3–8. ✓
- **Spec §Scope (out → throw):** non-filter source shapes → `ValidateBulkSource` (Task 3); owned/non-scalar setters → `BuildSetter` guard (Task 6); spec overrides assert failures (Task 9). ✓
- **Spec: EF9+EF10 only, `#if !EF8`, signature split:** Tasks 2,3,6 all `#if !EF8`; Task 6 `#if EF10/#elif EF9`. ✓
- **Spec: constants/params via `$set`, self-ref via pipeline:** Task 6 (`$set`), Task 8 + Spike 0B (pipeline). ✓
- **Spec: TPH discriminator preserved in filter:** captured chain includes it; `ValidateBulkSource` passes `Where`; verify in Task 5. ✓
- **Spec: transactions match relational:** `PrepareBulk` threads `CurrentTransaction.Session` else null; no implicit txn (Task 4). ✓
- **Spec: bypass change tracker / no concurrency check:** inherent — executor never touches the state manager. ✓
- **Spec: return int (cast from long):** `checked((int)result.DeletedCount/ModifiedCount)` (Tasks 4,6). ✓
- **Spec: no new public API:** confirmed — only EF's existing extension methods; verified by api-stability-reviewer (Task 10). ✓
- **Spec §Risks: self-ref rendering spike + fallback:** Spike 0B with explicit fallback branch in Task 8. ✓
- **Spec §Risks: filter typing:** Spike 0A. ✓
- **Spec §Risks: int overflow:** `checked` cast surfaces it rather than silently wrapping (matches "documented, not guarded" intent; switch to unchecked if exact relational parity is preferred — note for reviewer). ✓

**Placeholder scan:** the only `NotImplementedException`s are deliberate, named hand-offs between sequential tasks (Task 4→reconcile, Task 6→Task 8) and are removed within the same phase. The two spike-dependent bodies (`BuildFilter`, `BuildPipelineUpdate`) are explicitly gated on Phase 0 outputs. No silent TBDs.

**Type consistency:** `MongoNonQueryExpression` / `.Setter` / `.OperationKind` used identically across Tasks 1,3,4,6,8. Executor `MethodInfo` names (`ExecuteDelete`/`ExecuteDeleteAsync`/`ExecuteUpdate`/`ExecuteUpdateAsync`) consistent between definition and reflection lookup. `PrepareBulk`/`BuildFilter`/`BuildUpdate`/`BuildPipelineUpdate` signatures consistent across tasks.
