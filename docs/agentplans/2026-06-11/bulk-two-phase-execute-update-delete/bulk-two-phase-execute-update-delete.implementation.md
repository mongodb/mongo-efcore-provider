# Two-phase `ExecuteUpdate` / `ExecuteDelete` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let bulk `ExecuteDelete`/`ExecuteUpdate` accept sources that combine `Where` with `OrderBy`/`ThenBy`/`Skip`/`Take`/`Distinct`, by querying the target `_id`s (phase 1) and acting on them via `{ _id: { $in: … } }` (phase 2), both inside one transaction.

**Architecture:** Translate-time, a classifier tags the `MongoNonQueryExpression` as `SingleCommand` (the existing `Where`-only atomic path, unchanged) or `TwoPhase`. Execute-time, the `TwoPhase` path runs a key-projection read through the existing driver-LINQ machinery, then a `deleteMany`/`updateMany` filtered by the collected ids, wrapped in the ambient transaction or an auto-started one. Both phases share the session via `Database.CurrentTransaction`.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver LINQ v3, xUnit + FluentAssertions. All new behavior is under `#if !EF8` (EF9/EF10 only).

**Design doc:** `docs/agentplans/2026-06-11/bulk-two-phase-execute-update-delete/bulk-two-phase-execute-update-delete.design.md`

**Conventions reminder:**
- Preserve file BOMs. `<Nullable>enable</Nullable>` on `src/` — annotate new types.
- Build one EF version: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"` (or `EF9`/`EF8`).
- Run one test class: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~ClassName"`.
- The local test server (atlas-local / replica set) supports multi-document transactions, so the two-phase path runs end-to-end in tests.
- Commit messages start with `EF-107:`.

---

## Task 1: Add `BulkStrategy` to the marker node

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoNonQueryExpression.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoNonQueryExpressionTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `MongoNonQueryExpressionTests` (mirror the existing test style in that file — it constructs a `MongoQueryExpression` already; reuse that helper/fixture):

```csharp
[Fact]
public void Delete_marker_defaults_to_SingleCommand_strategy()
{
    var sourceQuery = CreateSourceQuery(); // existing helper in this test file
    var node = new MongoNonQueryExpression(sourceQuery);

    Assert.Equal(MongoNonQueryExpression.BulkStrategy.SingleCommand, node.Strategy);
}

[Fact]
public void Delete_marker_carries_TwoPhase_strategy()
{
    var sourceQuery = CreateSourceQuery();
    var node = new MongoNonQueryExpression(sourceQuery, MongoNonQueryExpression.BulkStrategy.TwoPhase);

    Assert.Equal(MongoNonQueryExpression.BulkStrategy.TwoPhase, node.Strategy);
}

[Fact]
public void Update_marker_carries_TwoPhase_strategy()
{
    var sourceQuery = CreateSourceQuery();
    var setters = new List<MongoNonQueryExpression.Setter>();
    var node = new MongoNonQueryExpression(sourceQuery, setters, MongoNonQueryExpression.BulkStrategy.TwoPhase);

    Assert.Equal(MongoNonQueryExpression.BulkStrategy.TwoPhase, node.Strategy);
    Assert.Equal(MongoNonQueryExpression.OperationKind.Update, node.Kind);
}
```

> If `CreateSourceQuery()` doesn't exist in the test file, look at how the existing tests obtain a `MongoQueryExpression` and reuse that exact construction; do not invent a new fixture.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoNonQueryExpressionTests"`
Expected: FAIL — `BulkStrategy` / `Strategy` do not exist (compile error).

- [ ] **Step 3: Add the enum and property**

In `MongoNonQueryExpression.cs`, add the enum next to `OperationKind` and a `Strategy` property, and thread it through both constructors with a `SingleCommand` default:

```csharp
public enum OperationKind { Delete, Update }

public enum BulkStrategy { SingleCommand, TwoPhase }

public sealed record Setter(IProperty Property, Expression ValueExpression, bool IsSelfReferencing);

public MongoNonQueryExpression(MongoQueryExpression sourceQuery, BulkStrategy strategy = BulkStrategy.SingleCommand)
{
    SourceQuery = sourceQuery;
    Kind = OperationKind.Delete;
    Setters = [];
    Strategy = strategy;
}

public MongoNonQueryExpression(
    MongoQueryExpression sourceQuery,
    IReadOnlyList<Setter> setters,
    BulkStrategy strategy = BulkStrategy.SingleCommand)
{
    SourceQuery = sourceQuery;
    Kind = OperationKind.Update;
    Setters = setters;
    Strategy = strategy;
}

public MongoQueryExpression SourceQuery { get; }
public OperationKind Kind { get; }
public IReadOnlyList<Setter> Setters { get; }
public BulkStrategy Strategy { get; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~MongoNonQueryExpressionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Expressions/MongoNonQueryExpression.cs \
        tests/MongoDB.EntityFrameworkCore.UnitTests/Query/Expressions/MongoNonQueryExpressionTests.cs
git commit -m "EF-107: Add BulkStrategy to MongoNonQueryExpression"
```

---

## Task 2: Replace `ValidateBulkSource` with the `ClassifyBulkSource` classifier

This task is a **behavior-preserving refactor**. It changes translate-time validation so ordering/paging/distinct shapes are *classified* `TwoPhase` instead of throwing — but a transitional guard in `VisitNonQuery` keeps the **observable** behavior identical (still the canonical "could not be translated" `InvalidOperationException`, raised at query-compile time) until two-phase execution is wired in Tasks 4–5. This is required for safety: without the guard, a `TwoPhase` marker would fall through to the single-command executor, whose `BuildFilter` walks **only** `Where` and would silently ignore `OrderBy`/`Take` — e.g. `OrderBy().Take(1).ExecuteDelete()` would delete *everything*. It is also required to keep the tree green: ~25 `NorthwindBulkUpdates` spec tests assert `"could not be translated"` for these shapes and aren't promoted until Task 8.

**No new test** is added here — this is a refactor, so its verification is "every existing functional and spec bulk test stays green." The canonical message contains both the phrase `"could not be translated"` and the printed source expression (so the existing `Contains("OrderBy")` functional tests also stay green).

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs:191-403`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:72-88` (transitional guard)

- [ ] **Step 1: Establish the green baseline**

Run the full bulk suites (functional + spec) and confirm they pass *before* the change, so you can prove the refactor preserves behavior:
`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests|FullyQualifiedName~ExecuteUpdateTests|FullyQualifiedName~NorthwindBulkUpdatesMongoTest"`
Expected: PASS. Note the counts.

- [ ] **Step 2: (no failing test — see task intro)**

This task adds no new behavior, so there is no new failing test. Skip to Step 3.

- [ ] **Step 3: Replace `ValidateBulkSource` with `ClassifyBulkSource`**

In `MongoQueryableMethodTranslatingExpressionVisitor.cs`, replace the `ValidateBulkSource` method (currently lines ~378-402) with:

```csharp
/// <summary>
/// Classifies the captured source chain of a bulk delete/update. A chain of only
/// <see cref="Queryable.Where{TSource}(IQueryable{TSource},Expression{Func{TSource,bool}})"/> is the
/// single-command atomic path. Adding <c>OrderBy</c>/<c>OrderByDescending</c>/<c>ThenBy</c>/
/// <c>ThenByDescending</c>/<c>Skip</c>/<c>Take</c>/<c>Distinct</c> requires the two-phase
/// (query target <c>_id</c>s, then act by <c>$in</c>) path. Any other operator is not expressible as
/// a server-side bulk scope and produces EF's canonical non-query translation failure. A TPH
/// discriminator filter rides along as a Where.
/// </summary>
private MongoNonQueryExpression.BulkStrategy ClassifyBulkSource(MongoQueryExpression mongoQueryExpression)
{
    var expression = MongoNonQueryExpression.UnwrapBulkOperator(mongoQueryExpression.CapturedExpression);
    var strategy = MongoNonQueryExpression.BulkStrategy.SingleCommand;

    // Descend through every method-call node in the source chain, terminating at the non-method
    // query root. Reject anything that is not one of the supported scoping operators (declared on
    // Queryable) so BuildFilter's "never silently drop a predicate" invariant holds and an operator
    // the read path cannot translate can never reach phase 1.
    while (expression is MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.Method.DeclaringType != typeof(Queryable))
        {
            ThrowBulkSourceNotSupported(mongoQueryExpression, methodCallExpression.Method.Name);
        }

        switch (methodCallExpression.Method.Name)
        {
            case nameof(Queryable.Where):
                break;

            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
            case nameof(Queryable.ThenBy):
            case nameof(Queryable.ThenByDescending):
            case nameof(Queryable.Skip):
            case nameof(Queryable.Take):
            case nameof(Queryable.Distinct):
                strategy = MongoNonQueryExpression.BulkStrategy.TwoPhase;
                break;

            default:
                ThrowBulkSourceNotSupported(mongoQueryExpression, methodCallExpression.Method.Name);
                break;
        }

        expression = methodCallExpression.Arguments[0];
    }

    return strategy;
}

[DoesNotReturn]
private void ThrowBulkSourceNotSupported(MongoQueryExpression mongoQueryExpression, string operatorName)
{
    AddTranslationErrorDetails(
        $"The '{operatorName}' operator is not supported in a bulk delete or update. Only 'Where' predicates "
        + "and the 'OrderBy', 'OrderByDescending', 'ThenBy', 'ThenByDescending', 'Skip', 'Take', and 'Distinct' "
        + "operators can scope a bulk operation.");
    throw new InvalidOperationException(
        CoreStrings.NonQueryTranslationFailedWithDetails(
            mongoQueryExpression.CapturedExpression?.Print(), TranslationErrorDetails));
}
```

Add `using System.Diagnostics.CodeAnalysis;` at the top of the file if not already present (for `[DoesNotReturn]`).

- [ ] **Step 4: Pass the classified strategy into the markers**

In the same file, update the three translate methods to call `ClassifyBulkSource` and pass the result:

`TranslateExecuteDelete` (~line 191):
```csharp
protected override Expression? TranslateExecuteDelete(ShapedQueryExpression source)
{
    var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
    var strategy = ClassifyBulkSource(mongoQueryExpression);
    return new MongoNonQueryExpression(mongoQueryExpression, strategy);
}
```

EF10 `TranslateExecuteUpdate` (~line 199):
```csharp
var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
var strategy = ClassifyBulkSource(mongoQueryExpression);
var parsed = setters
    .Select(s => BuildSetter(mongoQueryExpression, s.PropertySelector, s.ValueExpression))
    .ToList();
return new MongoNonQueryExpression(mongoQueryExpression, parsed, strategy);
```

EF9 `TranslateExecuteUpdate` (~line 211): change `ValidateBulkSource(mongoQueryExpression);` to `var strategy = ClassifyBulkSource(mongoQueryExpression);`, keep the empty-setter guard exactly as-is, and change the final return to:
```csharp
return new MongoNonQueryExpression(mongoQueryExpression, parsed, strategy);
```

- [ ] **Step 5: Add the transitional canonical-error guard in execution**

In `MongoShapedQueryCompilingExpressionVisitor.cs`, in `VisitNonQuery` (~line 72), add at the top of the method a guard that throws the **same canonical translation failure** the translate step used to throw (so observable behavior is unchanged), raised here at compile-time:

```csharp
private Expression VisitNonQuery(MongoNonQueryExpression nonQueryExpression)
{
    if (nonQueryExpression.Strategy == MongoNonQueryExpression.BulkStrategy.TwoPhase)
    {
        // Transitional: two-phase execution is wired in Tasks 4 (delete) / 5 (update). Until then,
        // preserve the pre-refactor behavior — reject these shapes with EF's canonical non-query
        // translation failure — rather than letting a TwoPhase marker fall through to the single-
        // command executor (whose filter ignores OrderBy/Skip/Take/Distinct and would act on the
        // wrong document set).
        throw new InvalidOperationException(
            CoreStrings.NonQueryTranslationFailedWithDetails(
                nonQueryExpression.SourceQuery.CapturedExpression?.Print(),
                "ordering, paging, and 'Distinct' in a bulk delete or update are not yet supported."));
    }

    var entityType = nonQueryExpression.SourceQuery.CollectionExpression.EntityType;
    // ... unchanged existing body ...
```

Add `using Microsoft.EntityFrameworkCore.Diagnostics;` / the `using` that exposes `CoreStrings` if the file doesn't already have it (the translate visitor uses `CoreStrings`; match its using). `Expression.Print()` is an EF extension already available where `CapturedExpression` is used.

- [ ] **Step 6: Verify the full bulk suite is still green (behavior preserved)**

Run the same filter as Step 1:
`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests|FullyQualifiedName~ExecuteUpdateTests|FullyQualifiedName~NorthwindBulkUpdatesMongoTest"`
Expected: PASS — identical counts to Step 1. The existing `*_after_OrderBy_throws` functional tests (assert `Contains("OrderBy")`) and the spec `AssertTranslationFailed` cases (assert `"could not be translated"`) all still pass because the canonical message contains both. **Do not modify any existing test in this task** — if one fails, the guard message is wrong, not the test.

- [ ] **Step 7: Confirm it compiles on EF9 and EF8**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` and `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF8"`.
Expected: both succeed (on EF8 the whole bulk region is excluded by `#if !EF8`).

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs
git commit -m "EF-107: Classify bulk sources as single-command or two-phase"
```

---

## Task 3: Phase-1 helper — project and collect target `_id`s

Add the strongly-typed phase-1 reader. It is generic on `<TSource, TKey>` so the driver calls stay typed; the executor (Task 4) computes `TKey` from the entity's single-property primary key and dispatches via `MakeGenericMethod`. This task validates phase 1 in isolation with a temporary test, then leaves the helper in place.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (add helpers near the other bulk executors, after `PrepareBulk`)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs` (temporary harness test, removed in Step 6)

- [ ] **Step 1: Write the temporary validation test**

This drives the helper through reflection (it is `internal`/`private static`; the functional test assembly has `InternalsVisibleTo`). It seeds three orders and asserts phase 1 returns the right `_id`s for `OrderBy(Quantity).Take(2)`, sync and async.

```csharp
[Fact]
public async Task Phase1_collects_target_ids_sync_and_async()
{
    using var db = CreateSeededContext();
    var entityType = db.Model.FindEntityType(typeof(Order))!;
    var queryContext = GetQueryContext(db); // see note below
    var factory = db.GetService<MongoDB.EntityFrameworkCore.Serializers.BsonSerializerFactory>();

    // Build the MongoNonQueryExpression by translating a two-phase delete without executing it is
    // non-trivial; instead validate the public end-to-end behavior is correct in Task 4. Here we only
    // confirm the helper compiles and returns ObjectId BsonValues for a known ordered/paged source.
    // (This temporary test is removed in Step 6.)
    Assert.True(true);
}
```

> Reaching the `QueryContext`/`MongoNonQueryExpression` from a functional test in isolation is awkward — phase 1 is most honestly validated end-to-end in Task 4. So this task's real verification is **Step 5 (build + the Task 4 acceptance test)**. Keep this placeholder test trivial and delete it in Step 6; do not invent private-state plumbing.

- [ ] **Step 2: Add the key-shape guard and the `<TSource, TKey>` dispatch helper**

In `MongoShapedQueryCompilingExpressionVisitor.cs`, inside the `#if !EF8` region, add:

```csharp
// Resolves the single-property primary key whose value the two-phase path projects (phase 1) and
// filters on (phase 2). MongoDB maps the PK to a single _id field; composite/owned/shadow shapes that
// can't form a simple member projection fall back to the canonical non-query failure.
private static IProperty GetBulkKeyOrThrow(IReadOnlyEntityType entityType, MongoNonQueryExpression nonQuery)
{
    var key = entityType.FindPrimaryKey();
    if (key is { Properties: [{ PropertyInfo: not null } keyProperty] })
    {
        return keyProperty;
    }

    throw new InvalidOperationException(
        CoreStrings.NonQueryTranslationFailedWithDetails(
            nonQuery.SourceQuery.CapturedExpression?.Print(),
            "the entity's primary key must be a single mapped property to use ordering, paging, or Distinct "
            + "in a bulk delete or update."));
}
```

- [ ] **Step 3: Add the phase-1 reader (sync + async)**

Add (these mirror the read path's `TranslateQuery` setup: build the driver `IQueryable<TSource>` from `collection.AsQueryable(session)`, wrap in the EF entity serializer, translate the captured chain — here with an extra key `Select`):

```csharp
// Phase 1: run the bulk source (Where + OrderBy/Skip/Take/Distinct) as a read projecting only the
// primary key, on the ambient transaction session, and serialize each key to the BsonValue used by the
// phase-2 { _id: { $in: ... } } filter. The driver translates the ordering/paging/distinct operators.
private static List<BsonValue> SelectTargetIds<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
{
    var keyQuery = BuildKeyQuery<TSource, TKey>(queryContext, entityType, bsonSerializerFactory, nonQuery, keyProperty);
    var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(keyProperty);

    var ids = new List<BsonValue>();
    foreach (var key in keyQuery)
    {
        ids.Add(serializationInfo.SerializeValue(key));
    }

    return ids;
}

private static async Task<List<BsonValue>> SelectTargetIdsAsync<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
{
    var keyQuery = BuildKeyQuery<TSource, TKey>(queryContext, entityType, bsonSerializerFactory, nonQuery, keyProperty);
    var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(keyProperty);

    var keys = await Driver.Linq.MongoQueryable
        .ToListAsync(keyQuery, queryContext.CancellationToken).ConfigureAwait(false);

    var ids = new List<BsonValue>(keys.Count);
    foreach (var key in keys)
    {
        ids.Add(serializationInfo.SerializeValue(key));
    }

    return ids;
}

private static IQueryable<TKey> BuildKeyQuery<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
{
    var mongoQueryContext = (MongoQueryContext)queryContext;
    var collection =
        mongoQueryContext.MongoClient.GetCollection<TSource>(nonQuery.SourceQuery.CollectionExpression.CollectionName);
    var session = (mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session;
    var queryable = session == null ? collection.AsQueryable() : collection.AsQueryable(session);
    var source = queryable.As((IBsonSerializer<TSource>)bsonSerializerFactory.GetEntitySerializer(entityType));

    var translator = new MongoEFToLinqTranslatingExpressionVisitor(queryContext, source.Expression, bsonSerializerFactory);

    // Append Select(e => e.<key>) to the bulk source chain (minus the leading ExecuteDelete/Update marker).
    var sourceChain = MongoNonQueryExpression.UnwrapBulkOperator(nonQuery.SourceQuery.CapturedExpression)!;
    var parameter = Expression.Parameter(typeof(TSource), "e");
    var keySelector = Expression.Lambda<Func<TSource, TKey>>(
        Expression.MakeMemberAccess(parameter, keyProperty.PropertyInfo!), parameter);
    var selectCall = Expression.Call(
        QueryableMethods.Select.MakeGenericMethod(typeof(TSource), typeof(TKey)),
        sourceChain,
        Expression.Quote(keySelector));

    var translated = translator.Visit(selectCall)!;
    return source.Provider.CreateQuery<TKey>(translated);
}
```

Add any missing `using`s the file doesn't already have: `using System;` (for `Func<>`), `using System.Collections.Generic;`, `using System.Linq;`, `using System.Linq.Expressions;`, `using Microsoft.EntityFrameworkCore.Metadata;`. The file already uses `MongoDB.Driver`, `MongoDB.Bson`, the EF→LINQ translator, and `QueryableMethods`.

- [ ] **Step 4: Build to verify it compiles on all EF versions**

Run: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10"`
Then: `dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF9"` and `-c "Debug EF8"`.
Expected: all succeed. (On EF8 the helpers are excluded by `#if !EF8`.)

> If `Driver.Linq.MongoQueryable.ToListAsync` is not the correct async-materialization entry point for an `IQueryable<TKey>` produced by `IMongoQueryProvider.CreateQuery`, use the driver's `IAsyncCursorSourceExtensions.ToListAsync((IAsyncCursorSource<TKey>)keyQuery, cancellationToken)` instead — `keyQuery` is driver-backed and implements `IAsyncCursorSource<TKey>`. Confirm which compiles; both are MongoDB driver APIs.

- [ ] **Step 5: Verify end-to-end happens in Task 4**

The helper has no standalone behavioral test (see Step 1 note). Its correctness is asserted by Task 4's acceptance test.

- [ ] **Step 6: Remove the placeholder test and commit**

Delete the `Phase1_collects_target_ids_sync_and_async` placeholder added in Step 1.

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs
git commit -m "EF-107: Add phase-1 target-id projection helper for two-phase bulk"
```

---

## Task 4: Wire two-phase `ExecuteDelete` (+ transaction scope)

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs`

- [ ] **Step 1: Write the failing acceptance tests**

Add the real two-phase delete behavior tests (Task 2 added no functional test — it was a behavior-preserving refactor):

```csharp
[Fact]
public void ExecuteDelete_with_OrderBy_Take_deletes_lowest_quantities()
{
    using var db = CreateSeededContext(); // 3 orders: qty 10 (open), 20 (open), 30 (closed)

    var deleted = db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();

    Assert.Equal(2, deleted);
    var remaining = db.Entities.OrderBy(o => o.Quantity).ToList();
    Assert.Single(remaining);
    Assert.Equal(30, remaining[0].Quantity);
}

[Fact]
public void ExecuteDelete_with_Where_and_Skip_Take_scopes_to_window()
{
    using var db = CreateSeededContext();

    var deleted = db.Entities.Where(o => o.Status == "open").OrderBy(o => o.Quantity).Skip(1).Take(1).ExecuteDelete();

    Assert.Equal(1, deleted);                 // only the qty=20 "open" row
    Assert.Equal(2, db.Entities.Count());
    Assert.DoesNotContain(db.Entities.ToList(), o => o.Quantity == 20);
}

[Fact]
public void ExecuteDelete_two_phase_empty_target_returns_zero()
{
    using var db = CreateSeededContext();

    var deleted = db.Entities.Where(o => o.Status == "nonexistent").OrderBy(o => o.Quantity).Take(5).ExecuteDelete();

    Assert.Equal(0, deleted);
    Assert.Equal(3, db.Entities.Count());
}

[Fact]
public void ExecuteDelete_two_phase_rolls_back_inside_user_transaction()
{
    using var db = CreateSeededContext();
    using (var tx = db.Database.BeginTransaction())
    {
        var deleted = db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();
        Assert.Equal(2, deleted);
        tx.Rollback();
    }

    // Rolled back: nothing actually removed.
    Assert.Equal(3, db.Entities.Count());
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests.ExecuteDelete_with_OrderBy_Take_deletes_lowest_quantities"`
Expected: FAIL — currently the transitional guard throws the canonical "could not be translated" error (the two-phase delete isn't wired yet), so the delete doesn't happen and the assertions fail.

- [ ] **Step 3: Add the transaction-scope helpers**

In `MongoShapedQueryCompilingExpressionVisitor.cs` (`#if !EF8` region) add:

```csharp
// Two-phase bulk needs phase 1 (read) and phase 2 (write) to observe one snapshot, so both run inside a
// single transaction. If the user already opened one we join it (and never commit it — they own it);
// otherwise we auto-start one, commit on success, abort on failure. AutoTransactionBehavior.Never means
// "I manage transactions" — we refuse to auto-start and tell the caller to open one.
private static int RunInBulkTransaction(QueryContext queryContext, MongoNonQueryExpression nonQuery, Func<int> body)
{
    var database = queryContext.Context.Database;
    if (database.CurrentTransaction != null)
    {
        return body();
    }

    if (database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
    {
        throw NoAutoTransactionError(nonQuery);
    }

    var transaction = database.BeginTransaction();
    try
    {
        var result = body();
        transaction.Commit();
        return result;
    }
    catch (Exception exception)
    {
        SafeRollback(transaction);
        if (IsTransactionsUnsupported(exception))
        {
            throw StandaloneTransactionError(nonQuery, exception);
        }

        throw;
    }
    finally
    {
        transaction.Dispose();
    }
}

private static async Task<int> RunInBulkTransactionAsync(
    QueryContext queryContext, MongoNonQueryExpression nonQuery, Func<Task<int>> body)
{
    var database = queryContext.Context.Database;
    if (database.CurrentTransaction != null)
    {
        return await body().ConfigureAwait(false);
    }

    if (database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
    {
        throw NoAutoTransactionError(nonQuery);
    }

    var cancellationToken = queryContext.CancellationToken;
    var transaction = await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        var result = await body().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
    catch (Exception exception)
    {
        await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (IsTransactionsUnsupported(exception))
        {
            throw StandaloneTransactionError(nonQuery, exception);
        }

        throw;
    }
    finally
    {
        await transaction.DisposeAsync().ConfigureAwait(false);
    }
}

private static void SafeRollback(IDbContextTransaction transaction)
{
    try { transaction.Rollback(); }
    catch { /* a failed/standalone transaction may not be rollback-able; the original error wins */ }
}

private static async Task SafeRollbackAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
{
    try { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); }
    catch { /* see SafeRollback */ }
}

private static InvalidOperationException NoAutoTransactionError(MongoNonQueryExpression nonQuery)
    => new(
        "This bulk delete or update uses ordering, paging, or 'Distinct', which the MongoDB provider executes "
        + "as a two-phase operation requiring a transaction. The context's AutoTransactionBehavior is 'Never', "
        + "so open an explicit transaction (Database.BeginTransaction) around the call.");

private static InvalidOperationException StandaloneTransactionError(MongoNonQueryExpression nonQuery, Exception inner)
    => new(
        "This bulk delete or update uses ordering, paging, or 'Distinct', which the MongoDB provider executes "
        + "as a two-phase operation requiring a transaction. The current MongoDB deployment does not support "
        + "multi-document transactions (a replica set or sharded cluster is required).", inner);

// Multi-document transactions are rejected on standalone deployments; the driver surfaces this as a
// MongoCommandException (code 20 / IllegalOperation) or an "Transaction numbers are only allowed on a
// replica set member or mongos" message. Match conservatively so unrelated failures propagate untouched.
private static bool IsTransactionsUnsupported(Exception exception)
    => exception is MongoCommandException { Code: 20 }
       || (exception is MongoException && exception.Message.Contains("Transaction numbers are only allowed", StringComparison.Ordinal));
```

Add `using Microsoft.EntityFrameworkCore.Storage;` (for `IDbContextTransaction`) and confirm `using Microsoft.EntityFrameworkCore;` is present (for `AutoTransactionBehavior`). `MongoCommandException`/`MongoException` come from `MongoDB.Driver` (already used).

- [ ] **Step 4: Add the two-phase delete executors and dispatch**

Add the executors:

```csharp
private static int ExecuteTwoPhaseDelete<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
    => RunInBulkTransaction(queryContext, nonQuery, () =>
    {
        var ids = SelectTargetIds<TSource, TKey>(queryContext, entityType, bsonSerializerFactory, nonQuery, keyProperty);
        return ids.Count == 0 ? 0 : DeleteByIds(queryContext, nonQuery, keyProperty, ids);
    });

private static Task<int> ExecuteTwoPhaseDeleteAsync<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
    => RunInBulkTransactionAsync(queryContext, nonQuery, async () =>
    {
        var ids = await SelectTargetIdsAsync<TSource, TKey>(
            queryContext, entityType, bsonSerializerFactory, nonQuery, keyProperty).ConfigureAwait(false);
        return ids.Count == 0
            ? 0
            : await DeleteByIdsAsync(queryContext, nonQuery, keyProperty, ids).ConfigureAwait(false);
    });

private static int DeleteByIds(
    QueryContext queryContext, MongoNonQueryExpression nonQuery, IProperty keyProperty, List<BsonValue> ids)
{
    var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
    var filter = Builders<BsonDocument>.Filter.In(keyProperty.GetElementName(), ids);

    var updateLogger = GetUpdateLogger(queryContext);
    var stopwatch = Stopwatch.StartNew();
    updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace);

    var result = session == null ? collection.DeleteMany(filter) : collection.DeleteMany(session, filter);

    updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
    return checked((int)result.DeletedCount);
}

private static async Task<int> DeleteByIdsAsync(
    QueryContext queryContext, MongoNonQueryExpression nonQuery, IProperty keyProperty, List<BsonValue> ids)
{
    var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
    var filter = Builders<BsonDocument>.Filter.In(keyProperty.GetElementName(), ids);

    var updateLogger = GetUpdateLogger(queryContext);
    var stopwatch = Stopwatch.StartNew();
    updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace);

    var cancellationToken = queryContext.CancellationToken;
    var result = session == null
        ? await collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false)
        : await collection.DeleteManyAsync(session, filter, options: null, cancellationToken).ConfigureAwait(false);

    updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
    return checked((int)result.DeletedCount);
}

private static (IMongoCollection<BsonDocument> collection, IClientSessionHandle? session) GetBulkCollectionAndSession(
    QueryContext queryContext, MongoNonQueryExpression nonQuery)
{
    var mongoQueryContext = (MongoQueryContext)queryContext;
    var collection =
        mongoQueryContext.MongoClient.GetCollection<BsonDocument>(nonQuery.SourceQuery.CollectionExpression.CollectionName);
    var session = (mongoQueryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session;
    return (collection, session);
}
```

> `GetBulkCollectionAndSession` factors out the collection+session lookup `PrepareBulk` already does; leave `PrepareBulk` as-is for the single-command path.

Now replace the transitional canonical-error guard in `VisitNonQuery` (from Task 2 Step 5) with real dispatch:

```csharp
private Expression VisitNonQuery(MongoNonQueryExpression nonQueryExpression)
{
    var entityType = nonQueryExpression.SourceQuery.CollectionExpression.EntityType;

    if (nonQueryExpression.Strategy == MongoNonQueryExpression.BulkStrategy.TwoPhase)
    {
        var keyProperty = GetBulkKeyOrThrow(entityType, nonQueryExpression);
        var twoPhaseExecutor = nonQueryExpression.Kind switch
        {
            MongoNonQueryExpression.OperationKind.Update =>
                QueryCompilationContext.IsAsync ? ExecuteTwoPhaseUpdateAsyncMethodInfo : ExecuteTwoPhaseUpdateMethodInfo,
            _ => QueryCompilationContext.IsAsync ? ExecuteTwoPhaseDeleteAsyncMethodInfo : ExecuteTwoPhaseDeleteMethodInfo
        };

        return Expression.Call(null,
            twoPhaseExecutor.MakeGenericMethod(entityType.ClrType, keyProperty.ClrType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(entityType),
            Expression.Constant(_bsonSerializerFactory),
            Expression.Constant(nonQueryExpression),
            Expression.Constant(keyProperty));
    }

    var executor = nonQueryExpression.Kind switch
    {
        MongoNonQueryExpression.OperationKind.Update =>
            QueryCompilationContext.IsAsync ? ExecuteUpdateAsyncMethodInfo : ExecuteUpdateMethodInfo,
        _ => QueryCompilationContext.IsAsync ? ExecuteDeleteAsyncMethodInfo : ExecuteDeleteMethodInfo
    };

    return Expression.Call(null,
        executor.MakeGenericMethod(entityType.ClrType),
        QueryCompilationContext.QueryContextParameter,
        Expression.Constant(entityType),
        Expression.Constant(_bsonSerializerFactory),
        Expression.Constant(nonQueryExpression));
}
```

> `ExecuteTwoPhaseUpdate*MethodInfo` are referenced here but defined in Task 5. To keep this task compiling and green on its own, in **this task** keep the two-phase **update** path on the transitional canonical-error guard (so the update spec tests that assert `"could not be translated"` stay green until Task 5), and dispatch only the **delete** path. Concretely, for this task only, write the `twoPhaseExecutor` selection as delete-only:
> ```csharp
> if (nonQueryExpression.Kind == MongoNonQueryExpression.OperationKind.Update)
> {
>     // Two-phase update is wired in Task 5; until then preserve the canonical rejection.
>     throw new InvalidOperationException(
>         CoreStrings.NonQueryTranslationFailedWithDetails(
>             nonQueryExpression.SourceQuery.CapturedExpression?.Print(),
>             "ordering, paging, and 'Distinct' in a bulk delete or update are not yet supported."));
> }
> var twoPhaseExecutor = QueryCompilationContext.IsAsync
>     ? ExecuteTwoPhaseDeleteAsyncMethodInfo : ExecuteTwoPhaseDeleteMethodInfo;
> ```

- [ ] **Step 5: Add the `MethodInfo` cache entries**

Next to the existing `ExecuteDeleteMethodInfo` etc. (~line 674), add:

```csharp
private static readonly MethodInfo ExecuteTwoPhaseDeleteMethodInfo =
    typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo().GetDeclaredMethods(nameof(ExecuteTwoPhaseDelete))
        .Single(m => !m.ReturnType.IsGenericType); // returns int, not Task<int>

private static readonly MethodInfo ExecuteTwoPhaseDeleteAsyncMethodInfo =
    typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo().GetDeclaredMethods(nameof(ExecuteTwoPhaseDeleteAsync))
        .Single();
```

> Match the resolution style already used for `ExecuteDeleteMethodInfo` in this file (it uses `.GetTypeInfo().DeclaredMethods` + `.Single(m => m.Name == ...)`). Mirror that exact pattern rather than the sketch above if it differs.

- [ ] **Step 6: Run the acceptance tests**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests"`
Expected: PASS — all the two-phase delete tests plus the unchanged single-command tests.

- [ ] **Step 7: Run on EF9 too**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --filter "FullyQualifiedName~ExecuteDeleteTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs
git commit -m "EF-107: Two-phase ExecuteDelete for ordered/paged/distinct sources"
```

---

## Task 5: Wire two-phase `ExecuteUpdate`

Phase 1 + transaction scope are shared from Tasks 3–4. This task adds the update phase-2 (reuse `BuildUpdate`) and removes the transitional update guard from Task 4.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs`

- [ ] **Step 1: Write the failing acceptance tests**

Mirror the `ExecuteUpdateTests` model/seed conventions (check the top of that file for its entity + `CreateSeededContext`). Add:

```csharp
[Fact]
public void ExecuteUpdate_with_OrderBy_Take_updates_only_targeted_rows()
{
    using var db = CreateSeededContext();

    var updated = db.Entities
        .OrderBy(o => o.Quantity).Take(2)
        .ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived"));

    Assert.Equal(2, updated); // matched count
    Assert.Equal(2, db.Entities.Count(o => o.Status == "archived"));
}

[Fact]
public void ExecuteUpdate_two_phase_self_referencing_setter()
{
    using var db = CreateSeededContext();

    var updated = db.Entities
        .OrderBy(o => o.Quantity).Take(1)
        .ExecuteUpdate(s => s.SetProperty(o => o.Quantity, o => o.Quantity + 5));

    Assert.Equal(1, updated);
    Assert.Contains(db.Entities.ToList(), o => o.Quantity == 15); // lowest was 10
}

[Fact]
public void ExecuteUpdate_two_phase_rolls_back_inside_user_transaction()
{
    using var db = CreateSeededContext();
    using (var tx = db.Database.BeginTransaction())
    {
        db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived"));
        tx.Rollback();
    }

    Assert.Equal(0, db.Entities.Count(o => o.Status == "archived"));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteUpdateTests.ExecuteUpdate_with_OrderBy_Take_updates_only_targeted_rows"`
Expected: FAIL — the transitional guard still rejects two-phase update with the canonical "could not be translated" error, so no update happens and the assertions fail.

- [ ] **Step 3: Add the two-phase update executors**

```csharp
private static int ExecuteTwoPhaseUpdate<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
    => RunInBulkTransaction(queryContext, nonQuery, () =>
    {
        var ids = SelectTargetIds<TSource, TKey>(queryContext, entityType, bsonSerializerFactory, nonQuery, keyProperty);
        if (ids.Count == 0)
        {
            return 0;
        }

        var update = TranslateBulkOrThrow(nonQuery,
            () => BuildUpdate<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));
        return UpdateByIds(queryContext, nonQuery, keyProperty, ids, update);
    });

private static Task<int> ExecuteTwoPhaseUpdateAsync<TSource, TKey>(
    QueryContext queryContext,
    IReadOnlyEntityType entityType,
    BsonSerializerFactory bsonSerializerFactory,
    MongoNonQueryExpression nonQuery,
    IProperty keyProperty)
    => RunInBulkTransactionAsync(queryContext, nonQuery, async () =>
    {
        var ids = await SelectTargetIdsAsync<TSource, TKey>(
            queryContext, entityType, bsonSerializerFactory, nonQuery, keyProperty).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return 0;
        }

        var update = TranslateBulkOrThrow(nonQuery,
            () => BuildUpdate<TSource>(queryContext, entityType, bsonSerializerFactory, nonQuery));
        return await UpdateByIdsAsync(queryContext, nonQuery, keyProperty, ids, update).ConfigureAwait(false);
    });

private static int UpdateByIds(
    QueryContext queryContext, MongoNonQueryExpression nonQuery, IProperty keyProperty,
    List<BsonValue> ids, UpdateDefinition<BsonDocument> update)
{
    var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
    var filter = Builders<BsonDocument>.Filter.In(keyProperty.GetElementName(), ids);

    var updateLogger = GetUpdateLogger(queryContext);
    var stopwatch = Stopwatch.StartNew();
    updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace);

    var result = session == null
        ? collection.UpdateMany(filter, update)
        : collection.UpdateMany(session, filter, update);

    updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
    return checked((int)result.MatchedCount);
}

private static async Task<int> UpdateByIdsAsync(
    QueryContext queryContext, MongoNonQueryExpression nonQuery, IProperty keyProperty,
    List<BsonValue> ids, UpdateDefinition<BsonDocument> update)
{
    var (collection, session) = GetBulkCollectionAndSession(queryContext, nonQuery);
    var filter = Builders<BsonDocument>.Filter.In(keyProperty.GetElementName(), ids);

    var updateLogger = GetUpdateLogger(queryContext);
    var stopwatch = Stopwatch.StartNew();
    updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace);

    var cancellationToken = queryContext.CancellationToken;
    var result = session == null
        ? await collection.UpdateManyAsync(filter, update, options: null, cancellationToken).ConfigureAwait(false)
        : await collection.UpdateManyAsync(session, filter, update, options: null, cancellationToken).ConfigureAwait(false);

    updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
    return checked((int)result.MatchedCount);
}
```

- [ ] **Step 4: Add the `MethodInfo` cache entries and restore the update dispatch**

Add (matching the file's existing resolution pattern):

```csharp
private static readonly MethodInfo ExecuteTwoPhaseUpdateMethodInfo =
    typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo().GetDeclaredMethods(nameof(ExecuteTwoPhaseUpdate))
        .Single(m => !m.ReturnType.IsGenericType);

private static readonly MethodInfo ExecuteTwoPhaseUpdateAsyncMethodInfo =
    typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo().GetDeclaredMethods(nameof(ExecuteTwoPhaseUpdateAsync))
        .Single();
```

In `VisitNonQuery`, remove the Task-4 transitional update guard (the `if (nonQueryExpression.Kind == …Update) throw …NonQueryTranslationFailedWithDetails(…)` block) and restore the full `twoPhaseExecutor` switch shown in Task 4 Step 4 (the version that handles both `Update` and delete).

- [ ] **Step 5: Run the acceptance tests**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteUpdateTests"`
Expected: PASS — two-phase update tests + unchanged single-command update tests (including the EF9 empty-setter guard).

- [ ] **Step 6: Run on EF9**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF9" --filter "FullyQualifiedName~ExecuteUpdateTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs
git commit -m "EF-107: Two-phase ExecuteUpdate for ordered/paged/distinct sources"
```

---

## Task 6: Diagnostics — surface two-phase + target count

The existing `ExecutingBulkDelete`/`…Update` log messages don't distinguish single-command from two-phase. Add a target-count detail without minting new event IDs.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Diagnostics/MongoLoggerUpdateExtensions.cs`
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` (pass the count)
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs`

- [ ] **Step 1: Inspect the current logger signatures**

Run: `grep -n "ExecutingBulkDelete\|ExecutingBulkUpdate\|ExecutedBulkDelete\|ExecutedBulkUpdate" src/MongoDB.EntityFrameworkCore/Diagnostics/MongoLoggerUpdateExtensions.cs`
Read those methods to learn their exact parameters and the message templates. Decide the smallest change: add an optional `int? targetCount = null` parameter to the `Executing*` overloads (or a sibling overload) that, when set, appends "(two-phase, N targets)" to the logged message.

- [ ] **Step 2: Write the failing test**

Use the existing diagnostics-capture pattern from the bulk-event tests already in `ExecuteDeleteTests` (the Task-2 file `using`s `MongoDB.EntityFrameworkCore.Diagnostics`; find the existing test that asserts `ExecutingBulkDelete` fired and copy its capture mechanism — likely a `ListLoggerFactory`/`TestMqlLoggerFactory` or an `ILoggerProvider`). Assert that a two-phase delete logs the target count:

```csharp
[Fact]
public void ExecuteDelete_two_phase_logs_target_count()
{
    // Arrange capture exactly as the existing bulk-event test in this file does.
    using var db = CreateSeededContextWithLogCapture(out var logEntries);

    db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();

    Assert.Contains(logEntries, e => e.Contains("two-phase") && e.Contains("2"));
}
```

> If no log-capture helper exists in this file yet, reuse the project's standard one referenced in `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Utilities/TestMqlLoggerFactory.cs` or the functional `ListLoggerFactory`. Do not invent a new logging harness.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests.ExecuteDelete_two_phase_logs_target_count"`
Expected: FAIL — the message has no "two-phase" / count detail.

- [ ] **Step 4: Add the count to the log call**

In `MongoLoggerUpdateExtensions.cs`, extend the `ExecutingBulkDelete`/`ExecutingBulkUpdate` message to optionally include the target count (follow the file's existing `LoggerMessage`/definition style — if the messages are built via `MongoLoggingDefinitions`, thread the parameter through there too). In `DeleteByIds`/`DeleteByIdsAsync`/`UpdateByIds`/`UpdateByIdsAsync` (Tasks 4–5), pass `ids.Count` into the `Executing*` call.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests.ExecuteDelete_two_phase_logs_target_count"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/MongoDB.EntityFrameworkCore/Diagnostics/MongoLoggerUpdateExtensions.cs \
        src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs \
        tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs
git commit -m "EF-107: Log two-phase bulk target count on existing bulk events"
```

---

## Task 7: Transaction-policy tests (`Never`, standalone)

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs`

- [ ] **Step 1: Write the `Never` test**

```csharp
[Fact]
public void ExecuteDelete_two_phase_with_AutoTransactionBehavior_Never_throws_and_changes_nothing()
{
    using var db = CreateSeededContext();
    db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

    var ex = Assert.Throws<InvalidOperationException>(
        () => db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete());

    Assert.Contains("AutoTransactionBehavior", ex.Message);
    Assert.Equal(3, db.Entities.Count()); // nothing deleted
}

[Fact]
public void ExecuteDelete_single_command_with_AutoTransactionBehavior_Never_still_works()
{
    using var db = CreateSeededContext();
    db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

    // Where-only path is single-command and needs no transaction — Never must not affect it.
    var deleted = db.Entities.Where(o => o.Status == "open").ExecuteDelete();

    Assert.Equal(2, deleted);
}
```

Add `using Microsoft.EntityFrameworkCore;` to the test file if needed (for `AutoTransactionBehavior`).

- [ ] **Step 2: Add the standalone test (environment-guarded)**

The local/CI test server supports transactions, so a real standalone can't be exercised here. Document the path with a clearly-reasoned skip rather than a silent omission:

```csharp
[Fact(Skip = "Requires a standalone (non-replica-set) MongoDB to exercise the transaction-unsupported path; "
    + "CI/local use a replica set (atlas-local). The error mapping is covered by IsTransactionsUnsupported. EF-107")]
public void ExecuteDelete_two_phase_on_standalone_throws_actionable_error()
{
    // Intentionally skipped — see Skip reason.
}
```

- [ ] **Step 3: Run the policy tests**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --filter "FullyQualifiedName~ExecuteDeleteTests.ExecuteDelete_two_phase_with_AutoTransactionBehavior_Never_throws_and_changes_nothing|FullyQualifiedName~ExecuteDeleteTests.ExecuteDelete_single_command_with_AutoTransactionBehavior_Never_still_works"`
Expected: PASS (standalone test reported as skipped).

- [ ] **Step 4: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteDeleteTests.cs
git commit -m "EF-107: Transaction-policy tests for two-phase bulk (Never, standalone)"
```

---

## Task 8: Spec-suite — promote now-supported `NorthwindBulkUpdates` cases to green

**Files:**
- Modify: `tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindBulkUpdatesMongoTest.cs`
- Modify: `docs/failing-spec-tests.md`

- [ ] **Step 1: Identify the ordering/paging/distinct cases tagged EF-X016**

Run: `grep -n "ordering / paging / Distinct unsupported EF-X016" tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindBulkUpdatesMongoTest.cs`
These are the `Delete_Where_OrderBy*`, `Delete_Where_Skip*`, `Delete_Where_Take`, `Delete_Where_Distinct`, and the equivalent `Update_*` cases currently routed through `AssertTranslationFailed`.

- [ ] **Step 2: Convert each to call `base` (one at a time, verifying)**

For each such method, replace the `AssertTranslationFailed(() => base.X(async))` body with the upstream-driven `await base.X(async)` and the appropriate `AssertMql`/assertion the sibling supported tests use. Remove its `// Fails: … EF-X016` comment.

Important caveats to verify per-test as you go:
- The `NorthwindBulkUpdates` asserter runs inside its rollback transaction (the fixture enlists the second context). Two-phase ops join that ambient transaction (the ambient-transaction path) — confirm they execute and the asserter's post-state checks pass.
- `Delete_Where_Skip_Take_Skip_Take_causing_subquery` may still not translate (a nested-subquery shape the read path can't express). If `base` throws, **leave it** as `AssertTranslationFailed` with an updated `// Fails:` reason ("nested Skip/Take subquery unsupported EF-X016") rather than forcing it green.

Run each converted test immediately:
`dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindBulkUpdatesMongoTest.Delete_Where_OrderBy_Take"`
Expected: PASS. Repeat for each.

- [ ] **Step 3: Run the full bulk-updates spec class on EF9 and EF10**

Run: `dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build --filter "FullyQualifiedName~NorthwindBulkUpdatesMongoTest"`
Then EF9. Expected: 0 failed; skipped only where explicitly documented.

- [ ] **Step 4: Update `docs/failing-spec-tests.md`**

In the `EF-X016` section, remove the ordering/paging/Distinct shapes from the unsupported list (they're now supported via two-phase) and keep only the still-unsupported shapes (Join/GroupBy/SelectMany/set-ops, multiple-collection, and any nested-subquery case left failing in Step 2). Note the two-phase support and its transaction requirement.

- [ ] **Step 5: Commit**

```bash
git add tests/MongoDB.EntityFrameworkCore.SpecificationTests/Query/NorthwindBulkUpdatesMongoTest.cs \
        docs/failing-spec-tests.md
git commit -m "EF-107: Promote ordered/paged/distinct bulk spec cases to green"
```

---

## Task 9: README, design-doc reconciliation, and full multi-EF run

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/specs/2026-06-11-bulk-two-phase-execute-update-delete-design.md`

- [ ] **Step 1: Update the design doc to match the implemented transaction rule**

In the design doc, change the "Empty phase 1" decision row and §6 to state that **phase 1 runs inside the transaction** (ambient or auto-started); an empty result commits a no-op transaction and returns 0 — the "skip the transaction entirely" wording is removed because a non-empty phase 1 run outside a transaction would reopen the race.

- [ ] **Step 2: Update README**

Run: `grep -n "ExecuteDelete\|ExecuteUpdate\|bulk" README.md`
Update the bulk-operations description to note that `Where`-scoped ops are a single atomic command, and ordering/paging/`Distinct`-scoped ops are supported via a transactional two-phase execution (requiring a transaction-capable deployment).

- [ ] **Step 3: Full build + test across all EF versions**

Invoke the `/test-all` skill (builds + tests EF8, EF9, EF10 in parallel).
Expected: EF8 still rejects bulk entirely; EF9/EF10 green including the new two-phase tests and the promoted spec cases.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/superpowers/specs/2026-06-11-bulk-two-phase-execute-update-delete-design.md
git commit -m "EF-107: Document two-phase bulk support; reconcile design doc"
```

---

## Self-review notes (for the implementer)

- **Spec coverage:** §2 classifier → Task 2; §3 marker → Task 1; §4 phase 1 → Task 3; §5 phase 2 + transaction lifecycle → Tasks 4–5, 7; §6 counts/edge cases → Tasks 4–5; §7 diagnostics → Task 6; §8 multi-EF/guards → all tasks under `#if !EF8`, verified in Task 9; §9 testing → Tasks 4–8.
- **Key uncertainty to resolve early (Task 3 Step 4):** the exact async-materialization API for the driver-LINQ key query (`MongoQueryable.ToListAsync` vs `IAsyncCursorSourceExtensions.ToListAsync`). Confirm by compiling; don't proceed to Task 4 until phase 1 builds on all three EF versions.
- **Method-name consistency:** `SelectTargetIds`/`SelectTargetIdsAsync`, `BuildKeyQuery`, `GetBulkKeyOrThrow`, `GetBulkCollectionAndSession`, `RunInBulkTransaction`/`Async`, `DeleteByIds`/`Async`, `UpdateByIds`/`Async`, `ExecuteTwoPhaseDelete`/`Async`, `ExecuteTwoPhaseUpdate`/`Async`, `IsTransactionsUnsupported`, `NoAutoTransactionError`, `StandaloneTransactionError` — used identically across Tasks 3–6.
- **Don't touch the single-command path:** `PrepareBulk`, `BuildFilter`, `ExecuteDelete`/`ExecuteUpdate` (and async) stay exactly as they are; two-phase is additive.
