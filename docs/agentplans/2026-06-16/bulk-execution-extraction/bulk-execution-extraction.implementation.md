# Bulk Execution Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the bulk `ExecuteDelete`/`ExecuteUpdate` *execution* logic (transaction orchestration, driver writes, diagnostics, id materialization) out of the Query-pipeline visitor `MongoShapedQueryCompilingExpressionVisitor` into a new Storage-area `MongoBulkOperationExecutor`, leaving *translation* (building the filter/update/id-query from expressions) in Query.

**Architecture:** A behavior-preserving refactor addressing PR #311 review comments `:548` (execution code in the wrong layer) and `:385` (transaction routing). The seam is a `MongoBulkPlan` record built in Query at compile time and embedded into the compiled query via `Expression.Constant`; it carries `Func<QueryContext, …>` delegates that perform the (runtime, parameter-dependent) translation. The new executor is non-generic and lives in Storage next to `MongoTransactionManager`/`MongoDatabaseWrapper`; it resolves the client wrapper, logger, and transaction from the `QueryContext`. Translation stays generic-over-`TSource` inside the delegates. No public API changes — everything is `internal`/private. Bulk is EF9+, so all new code is guarded `#if !EF8`.

**Tech Stack:** C# / .NET (net8.0 for EF8/EF9, net10.0 for EF10), EF Core provider internals, MongoDB C# driver, xUnit + FluentAssertions. Build configs `Debug EF8|EF9|EF10`.

---

## Background facts (verified against the working tree)

- `MongoShapedQueryCompilingExpressionVisitor.cs` is 1174 lines; the bulk feature spans ~lines 366–1068 plus eight `MethodInfo` fields at 1069–1116.
- **Translation members (STAY in the visitor — they use Query-internal visitors):** `VisitNonQuery` (75–109), `BuildFilter<TSource>` (871–909), `BuildUpdate<TSource>` (917–957), `BuildIdDocumentQuery<TSource>` (851–863, calls the shared private `TranslateQuery<TSource>` at 259), `SerializeConstant` (964), `RenderSelfReferencingValue<TSource>` (976), `RenderAggregateExpression<TSource,TResult>` (995), `EvaluateToConstant` (1013), `CompileAndEvaluate` (1052), `ParameterRebindingExpressionVisitor` (1059), `TranslateBulkOrThrow<T>` (761), `EnsureBulkKeyOrThrow` (794), and the shared `TranslateQuery<TSource>` (259, also used by the read path — do not move).
- **Execution members (MOVE to the new executor):** `RunInBulkTransaction` (382) + `RunInBulkTransactionAsync` (426), `SafeRollback` (474) + `SafeRollbackAsync` (480), `NoAutoTransactionError` (486), `StandaloneTransactionError` (492), `IsTransactionsUnsupported` (505), `ExecuteTwoPhaseDelete` (510) + `…Async` (519), `ExecuteTwoPhaseUpdate` (531) + `…Async` (547), `UpdateByIds` (564) + `…Async` (583), `DeleteByIds` (604) + `…Async` (620), `GetBulkCollectionAndSession` (639), `PrepareBulk` (777), `ExecuteDelete<TSource>` (649) + `…Async` (671), `ExecuteUpdate<TSource>` (694) + `…Async` (721), `GetUpdateLogger` (752). The materialization halves of `SelectTargetIds` (809) / `SelectTargetIdsAsync` (826) move (as `MaterializeIds`/`MaterializeIdsAsync`); the IQueryable-building half is `BuildIdDocumentQuery`, which stays.
- `RunInBulkTransaction(QueryContext, MongoNonQueryExpression nonQuery, Func<int> body)` — the `nonQuery` parameter is **unused** in the body; drop it during the move.
- The executor resolves dependencies from `QueryContext` (no new DI): client via `queryContext.Context.GetService<IMongoClientWrapper>()` (registered `Scoped`, exposes `IMongoCollection<T> GetCollection<T>(string)`), logger via `queryContext.Context.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>()` (matches existing `GetUpdateLogger`), session via `(queryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session`.
- `database.BeginTransaction()` → `MongoTransactionManager` (registered `IDbContextTransactionManager`) → `MongoTransaction.Start(...)`. The `IDbContextTransaction` returned is a `MongoTransaction`, so commit/rollback logging + the `TransactionId` Guid are already preserved. `MongoTransaction.Start` (`Storage/MongoTransaction.cs:73–87`) already maps the standalone `NotSupportedException` to a guidance-rich message; `RunInBulkTransaction` then re-wraps it as `StandaloneTransactionError` — a redundant second layer to collapse.
- `MongoNonQueryExpression` (`Query/Expressions/MongoNonQueryExpression.cs`) exposes `OperationKind Kind` (`Delete`/`Update`), `BulkStrategy Strategy` (`SingleCommand`/`TwoPhase`), `SourceQuery` (a `MongoQueryExpression` with `.CollectionExpression.CollectionName` / `.EntityType` and `.CapturedExpression`), and `Setters`.
- All source `.cs` files use a UTF-8 BOM (`efbbbf`). New `.cs` files MUST start with a BOM.
- Tests assert on **log message text** via `SpyLoggerProvider.GetLogMessageByEventId(EventId)`, which searches across all category loggers (so transaction-category events are observable from a bulk test). Existing two-phase tests live in `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs` / `ExecuteDeleteTests.cs`.

---

## File Structure

- **Create** `src/MongoDB.EntityFrameworkCore/Storage/MongoBulkPlan.cs` — the compile-time seam record (delegates + flags). `#if !EF8`. ~45 lines.
- **Create** `src/MongoDB.EntityFrameworkCore/Storage/MongoBulkOperationExecutor.cs` — non-generic runtime executor: dispatch, transaction orchestration, driver writes, id materialization, diagnostics, transaction-unsupported error mapping. `#if !EF8`. ~230 lines.
- **Modify** `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` — rewrite `VisitNonQuery`, add `CreateBulkPlan<TSource>`, delete the moved execution members and their six now-dead `MethodInfo` fields, replace them with three `MethodInfo` fields; remove usings the compiler flags as unused.
- **Modify (test)** `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs` — add one characterization test pinning that the auto-started two-phase path logs transaction lifecycle events.

---

## Task 1: Characterization test — auto-started two-phase goes through MongoTransaction

Pins the behavior damieng's `:385` comment worried about (commit/rollback logging + Guid) **before** the refactor, so the move is guarded. The test passes against current code.

**Files:**
- Test: `tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs` (add a method before the final closing `}` / `#endif`, i.e. after `ExecuteUpdate_two_phase_logs_ExecutingBulkUpdate_with_target_count` which ends at line 425).

- [ ] **Step 1: Add the characterization test**

Insert after the existing `ExecuteUpdate_two_phase_logs_ExecutingBulkUpdate_with_target_count` method (after line 425, before the class's closing brace):

```csharp
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteUpdate_two_phase_auto_transaction_logs_transaction_lifecycle(bool async)
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var collection = database.CreateCollection<Order>(
            nameof(ExecuteUpdate_two_phase_auto_transaction_logs_transaction_lifecycle), async);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureModel))
        {
            seedDb.AddRange(
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 10 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 20 });
            seedDb.SaveChanges();
        }

        using var db = SingleEntityDbContext.Create(collection, loggerFactory, ConfigureModel);

        // No explicit BeginTransaction: the two-phase path (OrderBy + Take) auto-starts one,
        // which must go through MongoTransaction (commit/rollback logging + TransactionId Guid).
        var updated = async
            ? await db.Entities.OrderBy(o => o.Quantity).Take(1)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "archived"))
            : db.Entities.OrderBy(o => o.Quantity).Take(1)
                .ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived"));

        Assert.Equal(1, updated);

        // Proves the auto-started transaction is a MongoTransaction: both lifecycle events fire.
        Assert.Contains("Committing", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionStarted));
        Assert.NotNull(spyLogger.GetLogMessageByEventId(MongoEventId.TransactionCommitted));
    }
```

> Note: `GetLogMessageByEventId` returns the message string and asserts a single matching record. `TransactionStarted`'s message does not contain "Committing"; if the first assertion's substring is wrong for the actual message text, replace it with `Assert.NotNull(spyLogger.GetLogMessageByEventId(MongoEventId.TransactionStarted));`. The intent is only that both events were emitted.

- [ ] **Step 2: Build EF10 and run the new test — verify it PASSES (characterization, current behavior)**

Run:
```bash
dotnet build MongoDB.EFCoreProvider.sln -c "Debug EF10" -v quiet
dotnet test MongoDB.EFCoreProvider.sln -c "Debug EF10" --no-build \
  --filter "FullyQualifiedName~ExecuteUpdate_two_phase_auto_transaction_logs_transaction_lifecycle"
```
Expected: PASS (both async values). If the first `Assert.Contains` fails on message text, apply the fallback in the note above and re-run.

- [ ] **Step 3: Commit**

```bash
git add "tests/MongoDB.EntityFrameworkCore.FunctionalTests/Query/ExecuteUpdateTests.cs"
git commit -m "EF-107: Characterize two-phase auto-started transaction logging before bulk-execution extraction"
```

---

## Task 2: Add the `MongoBulkPlan` seam record

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Storage/MongoBulkPlan.cs`

- [ ] **Step 1: Create the file (remember the BOM is added in Step 2)**

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#if !EF8

using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Compile-time plan describing a bulk <c>ExecuteDelete</c>/<c>ExecuteUpdate</c> operation, produced by the query
/// pipeline and consumed by <see cref="MongoBulkOperationExecutor"/> at execution time. The delegates defer the
/// (runtime, parameter-dependent) translation of the filter / update / target-id query to the Query area while the
/// executor owns the driver writes, transaction orchestration, and diagnostics.
/// </summary>
internal sealed class MongoBulkPlan
{
    /// <summary>True for <c>ExecuteUpdate</c>; false for <c>ExecuteDelete</c>.</summary>
    public required bool IsUpdate { get; init; }

    /// <summary>True when the source requires the transactional two-phase (collect ids → act by <c>$in</c>) strategy.</summary>
    public required bool IsTwoPhase { get; init; }

    /// <summary>The MongoDB collection the operation targets.</summary>
    public required string CollectionName { get; init; }

    /// <summary>Builds the server-side filter for a single-command operation. <see langword="null"/> for two-phase.</summary>
    public Func<QueryContext, FilterDefinition<BsonDocument>>? BuildFilter { get; init; }

    /// <summary>Builds the server-side update. <see langword="null"/> for deletes.</summary>
    public Func<QueryContext, UpdateDefinition<BsonDocument>>? BuildUpdate { get; init; }

    /// <summary>Builds the phase-1 read query yielding the target documents. <see langword="null"/> for single-command.</summary>
    public Func<QueryContext, IQueryable<BsonDocument>>? BuildTargetIdQuery { get; init; }
}

#endif
```

- [ ] **Step 2: Add the BOM**

Run:
```bash
p="src/MongoDB.EntityFrameworkCore/Storage/MongoBulkPlan.cs"
printf '\xef\xbb\xbf' | cat - "$p" > "$p.tmp" && mv "$p.tmp" "$p"
head -c3 "$p" | xxd | head -1   # expect: efbb bf
```

- [ ] **Step 3: Build all three EF versions — verify clean (file is unused but must compile; excluded on EF8)**

Run:
```bash
for v in EF8 EF9 EF10; do dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug $v" -v quiet --no-incremental; done
```
Expected: `Build succeeded. 0 Error(s)` for all three. (EF8 compiles the file as empty due to `#if !EF8`.)

- [ ] **Step 4: Commit**

```bash
git add "src/MongoDB.EntityFrameworkCore/Storage/MongoBulkPlan.cs"
git commit -m "EF-107: Add MongoBulkPlan seam for bulk-execution extraction"
```

---

## Task 3: Create `MongoBulkOperationExecutor` (Storage) with the moved execution logic

Copy the execution members from the visitor into a new Storage class, adapting them to be **non-generic** and **plan-driven**. The visitor keeps its originals for now (transient duplication; both compile). This task folds in the `:385` cleanup (drop the redundant `StandaloneTransactionError` re-wrap, keeping `MongoTransaction.Start`'s message) and the unused `nonQuery` parameter removal.

**Files:**
- Create: `src/MongoDB.EntityFrameworkCore/Storage/MongoBulkOperationExecutor.cs`
- Reference (source of the verbatim bodies being adapted): `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs:382-844`

- [ ] **Step 1: Create the file (BOM added in Step 2)**

```csharp
/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#if !EF8

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Executes a <see cref="MongoBulkPlan"/> (server-side <c>ExecuteDelete</c>/<c>ExecuteUpdate</c>): runs the
/// <c>deleteMany</c>/<c>updateMany</c> driver writes, orchestrates the two-phase transaction, materializes target
/// ids, and emits the bulk diagnostics events. Translation of the filter/update/target query is deferred to the
/// plan's delegates (owned by the query pipeline).
/// </summary>
internal static class MongoBulkOperationExecutor
{
    public static int Execute(QueryContext queryContext, MongoBulkPlan plan)
        => plan.IsTwoPhase
            ? (plan.IsUpdate ? ExecuteTwoPhaseUpdate(queryContext, plan) : ExecuteTwoPhaseDelete(queryContext, plan))
            : (plan.IsUpdate ? ExecuteUpdate(queryContext, plan) : ExecuteDelete(queryContext, plan));

    public static Task<int> ExecuteAsync(QueryContext queryContext, MongoBulkPlan plan)
        => plan.IsTwoPhase
            ? (plan.IsUpdate ? ExecuteTwoPhaseUpdateAsync(queryContext, plan) : ExecuteTwoPhaseDeleteAsync(queryContext, plan))
            : (plan.IsUpdate ? ExecuteUpdateAsync(queryContext, plan) : ExecuteDeleteAsync(queryContext, plan));

    // ---- Single-command path: one atomic deleteMany/updateMany, no transaction. ----

    private static int ExecuteDelete(QueryContext queryContext, MongoBulkPlan plan)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = plan.BuildFilter!(queryContext);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace);

        var result = session == null ? collection.DeleteMany(filter) : collection.DeleteMany(session, filter);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
        return checked((int)result.DeletedCount);
    }

    private static async Task<int> ExecuteDeleteAsync(QueryContext queryContext, MongoBulkPlan plan)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = plan.BuildFilter!(queryContext);

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

    private static int ExecuteUpdate(QueryContext queryContext, MongoBulkPlan plan)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = plan.BuildFilter!(queryContext);
        var update = plan.BuildUpdate!(queryContext);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace);

        var result = session == null
            ? collection.UpdateMany(filter, update)
            : collection.UpdateMany(session, filter, update);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
        // (ExecutedBulkUpdate reports ModifiedCount — the genuinely-modified subset.)
        return checked((int)result.MatchedCount);
    }

    private static async Task<int> ExecuteUpdateAsync(QueryContext queryContext, MongoBulkPlan plan)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = plan.BuildFilter!(queryContext);
        var update = plan.BuildUpdate!(queryContext);

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

    // ---- Two-phase path: collect target _ids inside a transaction, then act by { _id: { $in: ids } }. ----

    private static int ExecuteTwoPhaseDelete(QueryContext queryContext, MongoBulkPlan plan)
        => RunInBulkTransaction(queryContext, () =>
        {
            var ids = MaterializeIds(plan.BuildTargetIdQuery!(queryContext));
            return ids.Count == 0 ? 0 : DeleteByIds(queryContext, plan, ids);
        });

    private static Task<int> ExecuteTwoPhaseDeleteAsync(QueryContext queryContext, MongoBulkPlan plan)
        => RunInBulkTransactionAsync(queryContext, async () =>
        {
            var ids = await MaterializeIdsAsync(plan.BuildTargetIdQuery!(queryContext), queryContext.CancellationToken)
                .ConfigureAwait(false);
            return ids.Count == 0 ? 0 : await DeleteByIdsAsync(queryContext, plan, ids).ConfigureAwait(false);
        });

    private static int ExecuteTwoPhaseUpdate(QueryContext queryContext, MongoBulkPlan plan)
        => RunInBulkTransaction(queryContext, () =>
        {
            var ids = MaterializeIds(plan.BuildTargetIdQuery!(queryContext));
            if (ids.Count == 0)
            {
                return 0;
            }

            var update = plan.BuildUpdate!(queryContext);
            return UpdateByIds(queryContext, plan, ids, update);
        });

    private static Task<int> ExecuteTwoPhaseUpdateAsync(QueryContext queryContext, MongoBulkPlan plan)
        => RunInBulkTransactionAsync(queryContext, async () =>
        {
            var ids = await MaterializeIdsAsync(plan.BuildTargetIdQuery!(queryContext), queryContext.CancellationToken)
                .ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return 0;
            }

            var update = plan.BuildUpdate!(queryContext);
            return await UpdateByIdsAsync(queryContext, plan, ids, update).ConfigureAwait(false);
        });

    private static int DeleteByIds(QueryContext queryContext, MongoBulkPlan plan, List<BsonValue> ids)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var result = session == null ? collection.DeleteMany(filter) : collection.DeleteMany(session, filter);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
        return checked((int)result.DeletedCount);
    }

    private static async Task<int> DeleteByIdsAsync(QueryContext queryContext, MongoBulkPlan plan, List<BsonValue> ids)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var cancellationToken = queryContext.CancellationToken;
        var result = session == null
            ? await collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false)
            : await collection.DeleteManyAsync(session, filter, options: null, cancellationToken).ConfigureAwait(false);

        updateLogger.ExecutedBulkDelete(stopwatch.Elapsed, collection.CollectionNamespace, result.DeletedCount);
        return checked((int)result.DeletedCount);
    }

    private static int UpdateByIds(QueryContext queryContext, MongoBulkPlan plan, List<BsonValue> ids, UpdateDefinition<BsonDocument> update)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var result = session == null
            ? collection.UpdateMany(filter, update)
            : collection.UpdateMany(session, filter, update);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
        return checked((int)result.MatchedCount);
    }

    private static async Task<int> UpdateByIdsAsync(QueryContext queryContext, MongoBulkPlan plan, List<BsonValue> ids, UpdateDefinition<BsonDocument> update)
    {
        var (collection, session) = GetBulkCollectionAndSession(queryContext, plan.CollectionName);
        var filter = Builders<BsonDocument>.Filter.In("_id", ids);

        var updateLogger = GetUpdateLogger(queryContext);
        var stopwatch = Stopwatch.StartNew();
        updateLogger.ExecutingBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, ids.Count);

        var cancellationToken = queryContext.CancellationToken;
        var result = session == null
            ? await collection.UpdateManyAsync(filter, update, options: null, cancellationToken).ConfigureAwait(false)
            : await collection.UpdateManyAsync(session, filter, update, options: null, cancellationToken).ConfigureAwait(false);

        updateLogger.ExecutedBulkUpdate(stopwatch.Elapsed, collection.CollectionNamespace, result.ModifiedCount);
        return checked((int)result.MatchedCount);
    }

    private static List<BsonValue> MaterializeIds(IQueryable<BsonDocument> documents)
    {
        var ids = new List<BsonValue>();
        foreach (var document in documents)
        {
            ids.Add(document["_id"]);
        }

        return ids;
    }

    private static async Task<List<BsonValue>> MaterializeIdsAsync(IQueryable<BsonDocument> documents, CancellationToken cancellationToken)
    {
        var materialized = await IAsyncCursorSourceExtensions
            .ToListAsync((IAsyncCursorSource<BsonDocument>)documents, cancellationToken).ConfigureAwait(false);

        var ids = new List<BsonValue>(materialized.Count);
        foreach (var document in materialized)
        {
            ids.Add(document["_id"]);
        }

        return ids;
    }

    // ---- Transaction orchestration. Phase 1 (read) and phase 2 (write) share one transaction so they observe a
    // single snapshot. If the user already opened one we join it (and never commit it). Otherwise we auto-start one,
    // commit on success, abort on failure. AutoTransactionBehavior.Never means "I manage transactions" — refuse to
    // auto-start. database.BeginTransaction() routes through MongoTransactionManager -> MongoTransaction, so
    // commit/rollback logging and the transaction id are preserved. ----

    private static int RunInBulkTransaction(QueryContext queryContext, Func<int> body)
    {
        var database = queryContext.Context.Database;
        if (database.CurrentTransaction != null)
        {
            return body();
        }

        if (database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
        {
            throw NoAutoTransactionError();
        }

        // BeginTransaction is inside the try: on a non-transactional deployment MongoTransaction.Start throws here,
        // and we want that mapped to the actionable StandaloneTransactionError like any other transaction failure.
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = database.BeginTransaction();
            var result = body();
            transaction.Commit();
            return result;
        }
        catch (Exception exception)
        {
            if (transaction != null)
            {
                SafeRollback(transaction);
            }

            if (IsTransactionsUnsupported(exception))
            {
                throw StandaloneTransactionError(exception);
            }

            throw;
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private static async Task<int> RunInBulkTransactionAsync(QueryContext queryContext, Func<Task<int>> body)
    {
        var database = queryContext.Context.Database;
        if (database.CurrentTransaction != null)
        {
            return await body().ConfigureAwait(false);
        }

        if (database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
        {
            throw NoAutoTransactionError();
        }

        var cancellationToken = queryContext.CancellationToken;
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await body().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            if (transaction != null)
            {
                await SafeRollbackAsync(transaction, cancellationToken).ConfigureAwait(false);
            }

            if (IsTransactionsUnsupported(exception))
            {
                throw StandaloneTransactionError(exception);
            }

            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
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

    private static InvalidOperationException NoAutoTransactionError()
        => new(
            "This bulk delete or update uses ordering, paging, or 'Distinct', which the MongoDB provider executes "
            + "as a two-phase operation requiring a transaction. The context's AutoTransactionBehavior is 'Never', "
            + "so open an explicit transaction (Database.BeginTransaction) around the call.");

    private static InvalidOperationException StandaloneTransactionError(Exception inner)
        => new(
            "This bulk delete or update uses ordering, paging, or 'Distinct', which the MongoDB provider executes "
            + "as a two-phase operation requiring a transaction. The current MongoDB deployment does not support "
            + "multi-document transactions (a replica set or sharded cluster is required).", inner);

    // Multi-document transactions are rejected when the deployment doesn't support them; the failure surfaces in
    // three shapes: (1) MongoTransaction.Start's NotSupportedException ("does not support transactions"); (2) a raw
    // driver MongoCommandException (code 20 / IllegalOperation); (3) "Transaction numbers are only allowed on a
    // replica set member or mongos". Match all three; match conservatively so unrelated failures propagate untouched.
    private static bool IsTransactionsUnsupported(Exception exception)
        => exception is MongoCommandException { Code: 20 }
           || (exception is MongoException && exception.Message.Contains("Transaction numbers are only allowed", StringComparison.Ordinal))
           || (exception is NotSupportedException && exception.Message.Contains("does not support transactions", StringComparison.Ordinal));

    private static (IMongoCollection<BsonDocument> collection, IClientSessionHandle? session) GetBulkCollectionAndSession(
        QueryContext queryContext, string collectionName)
    {
        var collection = queryContext.Context.GetService<IMongoClientWrapper>().GetCollection<BsonDocument>(collectionName);
        var session = (queryContext.Context.Database.CurrentTransaction as MongoTransaction)?.Session;
        return (collection, session);
    }

    // Server-side ExecuteDelete/ExecuteUpdate bypass SaveChanges, so MongoDatabaseWrapper's bulk-write logging never
    // fires for them. Resolve the Update-category logger from the context's service provider for the dedicated bulk events.
    private static IDiagnosticsLogger<DbLoggerCategory.Update> GetUpdateLogger(QueryContext queryContext)
        => queryContext.Context.GetService<IDiagnosticsLogger<DbLoggerCategory.Update>>();
}

#endif
```

> `:385` cleanup note: This executor keeps `StandaloneTransactionError` because it adds *bulk-specific* guidance the generic `MongoTransaction.Start` message lacks. The redundancy to remove is in `MongoTransaction.Start` only if we wanted a single message — but `Start` serves SaveChanges too, so leave `Start` as-is and keep this mapping. (No change to `MongoTransaction.cs`.) If reviewers still want dedup, that is a separate, broader change to `MongoTransaction.Start` and is out of scope here.

- [ ] **Step 2: Add the BOM**

Run:
```bash
p="src/MongoDB.EntityFrameworkCore/Storage/MongoBulkOperationExecutor.cs"
printf '\xef\xbb\xbf' | cat - "$p" > "$p.tmp" && mv "$p.tmp" "$p"
head -c3 "$p" | xxd | head -1   # expect: efbb bf
```

- [ ] **Step 3: Build all three EF versions — verify clean (executor compiles; not yet called → "unused private member" warnings are acceptable here only if the build treats them as warnings, not errors; the class is `internal` and methods are reachable via `Execute`/`ExecuteAsync` which are `public`, so no unused warnings expected)**

Run:
```bash
for v in EF8 EF9 EF10; do dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug $v" -v quiet --no-incremental; done
```
Expected: `Build succeeded. 0 Error(s) 0 Warning(s)` for all three.

- [ ] **Step 4: Commit**

```bash
git add "src/MongoDB.EntityFrameworkCore/Storage/MongoBulkOperationExecutor.cs"
git commit -m "EF-107: Add MongoBulkOperationExecutor (Storage) for bulk execution"
```

---

## Task 4: Rewire `VisitNonQuery` to the plan + executor, delete the moved members

Now point the compiled query at the executor and remove the dead execution code from the visitor.

**Files:**
- Modify: `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs`

- [ ] **Step 1: Replace the body of `VisitNonQuery` (lines 75-109)**

Replace the entire `VisitNonQuery` method with:

```csharp
    private Expression VisitNonQuery(MongoNonQueryExpression nonQueryExpression)
    {
        var entityType = nonQueryExpression.SourceQuery.CollectionExpression.EntityType;

        if (nonQueryExpression.Strategy == MongoNonQueryExpression.BulkStrategy.TwoPhase)
        {
            // Two-phase needs the entity's _id key to project phase-1 targets and act by { _id: $in }.
            EnsureBulkKeyOrThrow(entityType, nonQueryExpression);
        }

        // The plan closes over the entity type / serializer factory / non-query expression (all compile-time
        // constants) and is embedded into the compiled query. Its delegates perform the runtime translation;
        // MongoBulkOperationExecutor (Storage) runs the writes, transaction, and diagnostics.
        var plan = (MongoBulkPlan)CreateBulkPlanMethodInfo
            .MakeGenericMethod(entityType.ClrType)
            .Invoke(null, [entityType, _bsonSerializerFactory, nonQueryExpression])!;

        var executor = QueryCompilationContext.IsAsync
            ? MongoBulkExecuteAsyncMethodInfo
            : MongoBulkExecuteMethodInfo;

        return Expression.Call(
            null,
            executor,
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(plan));
    }
```

- [ ] **Step 2: Add `CreateBulkPlan<TSource>` to the visitor**

Insert immediately after `VisitNonQuery` (inside the `#if !EF8` region — `VisitNonQuery` is already inside it):

```csharp
    // Builds the compile-time plan for a bulk operation. Generic over TSource so the deferred translation
    // delegates can close over the correctly-typed serializer/queryable; invoked once via reflection from
    // VisitNonQuery with entityType.ClrType. The translation helpers stay here in the query pipeline; only
    // the resulting FilterDefinition / UpdateDefinition / IQueryable<BsonDocument> cross to the executor.
    private static MongoBulkPlan CreateBulkPlan<TSource>(
        IReadOnlyEntityType entityType,
        BsonSerializerFactory bsonSerializerFactory,
        MongoNonQueryExpression nonQuery)
    {
        var isUpdate = nonQuery.Kind == MongoNonQueryExpression.OperationKind.Update;
        var isTwoPhase = nonQuery.Strategy == MongoNonQueryExpression.BulkStrategy.TwoPhase;

        return new MongoBulkPlan
        {
            IsUpdate = isUpdate,
            IsTwoPhase = isTwoPhase,
            CollectionName = nonQuery.SourceQuery.CollectionExpression.CollectionName,
            BuildFilter = isTwoPhase
                ? null
                : qc => TranslateBulkOrThrow(nonQuery, () => BuildFilter<TSource>(qc, entityType, bsonSerializerFactory, nonQuery)),
            BuildUpdate = isUpdate
                ? qc => TranslateBulkOrThrow(nonQuery, () => BuildUpdate<TSource>(qc, entityType, bsonSerializerFactory, nonQuery))
                : null,
            BuildTargetIdQuery = isTwoPhase
                ? qc => BuildIdDocumentQuery<TSource>(qc, entityType, bsonSerializerFactory, nonQuery)
                : null,
        };
    }
```

- [ ] **Step 3: Delete the moved execution members from the visitor**

Delete these methods entirely (they now live in `MongoBulkOperationExecutor`): `RunInBulkTransaction`, `RunInBulkTransactionAsync`, `SafeRollback`, `SafeRollbackAsync`, `NoAutoTransactionError`, `StandaloneTransactionError`, `IsTransactionsUnsupported`, `ExecuteTwoPhaseDelete`, `ExecuteTwoPhaseDeleteAsync`, `ExecuteTwoPhaseUpdate`, `ExecuteTwoPhaseUpdateAsync`, `UpdateByIds`, `UpdateByIdsAsync`, `DeleteByIds`, `DeleteByIdsAsync`, `GetBulkCollectionAndSession`, `PrepareBulk`, `ExecuteDelete<TSource>`, `ExecuteDeleteAsync<TSource>`, `ExecuteUpdate<TSource>`, `ExecuteUpdateAsync<TSource>`, `GetUpdateLogger`, `SelectTargetIds<TSource>`, `SelectTargetIdsAsync<TSource>`.

> KEEP: `VisitNonQuery`, `CreateBulkPlan`, `EnsureBulkKeyOrThrow`, `BuildIdDocumentQuery<TSource>`, `BuildFilter<TSource>`, `BuildUpdate<TSource>`, `TranslateBulkOrThrow<T>`, `SerializeConstant`, `RenderSelfReferencingValue<TSource>`, `RenderAggregateExpression<TSource,TResult>`, `EvaluateToConstant`, `CompileAndEvaluate`, `ParameterRebindingExpressionVisitor`, `TranslateQuery<TSource>` (shared with the read path), `ExecuteShapedQuery`, `ExecuteProjectedQuery`, `GetOnZeroResultsAction`, and everything else.

> Note on `PrepareBulk`: it was only used by the single-command `ExecuteDelete`/`ExecuteUpdate` (now in the executor, which calls `plan.BuildFilter` instead). It is safe to delete. Its filter-building responsibility lives in `BuildFilter` (kept) wrapped by the plan delegate.

- [ ] **Step 4: Replace the eight bulk `MethodInfo` fields (lines 1069-1116) with three**

Delete `ExecuteDeleteMethodInfo`, `ExecuteDeleteAsyncMethodInfo`, `ExecuteUpdateMethodInfo`, `ExecuteUpdateAsyncMethodInfo`, `ExecuteTwoPhaseDeleteMethodInfo`, `ExecuteTwoPhaseDeleteAsyncMethodInfo`, `ExecuteTwoPhaseUpdateMethodInfo`, `ExecuteTwoPhaseUpdateAsyncMethodInfo`. Replace with:

```csharp
    private static readonly MethodInfo CreateBulkPlanMethodInfo =
        typeof(MongoShapedQueryCompilingExpressionVisitor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(CreateBulkPlan));

    private static readonly MethodInfo MongoBulkExecuteMethodInfo =
        typeof(MongoBulkOperationExecutor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(MongoBulkOperationExecutor.Execute));

    private static readonly MethodInfo MongoBulkExecuteAsyncMethodInfo =
        typeof(MongoBulkOperationExecutor)
            .GetTypeInfo()
            .DeclaredMethods
            .Single(m => m.Name == nameof(MongoBulkOperationExecutor.ExecuteAsync));
```

- [ ] **Step 5: Remove now-unused usings the compiler flags**

After deletion, some `#if !EF8` usings may be unused in this file (likely candidates: `System.Threading`, `Microsoft.EntityFrameworkCore.Storage`, possibly `System.Diagnostics`). Build (Step 6); for each `CS8019 Unnecessary using directive` warning in this file, remove that using. Do not remove `MongoDB.Driver`, `MongoDB.Driver.Linq`, `MongoDB.Bson*`, `Microsoft.EntityFrameworkCore.Diagnostics` (still used by translation/read-path), or `MongoDB.EntityFrameworkCore.Storage` if `MongoBulkPlan`/`MongoBulkOperationExecutor` references resolve through it (they are in that namespace — keep it).

- [ ] **Step 6: Build all three EF versions — verify clean**

Run:
```bash
for v in EF8 EF9 EF10; do echo "== $v =="; dotnet build src/MongoDB.EntityFrameworkCore/MongoDB.EntityFrameworkCore.csproj -c "Debug $v" -v quiet --no-incremental; done
```
Expected: `Build succeeded. 0 Error(s) 0 Warning(s)` for all three. Fix any `CS8019` by removing the flagged using (Step 5); fix any `CS0103`/`CS0117` by confirming the kept/deleted member lists above.

- [ ] **Step 7: Commit**

```bash
git add "src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs"
git commit -m "EF-107: Route bulk execution through MongoBulkOperationExecutor; drop moved code from the query visitor"
```

---

## Task 5: Full verification across EF8/EF9/EF10

**Files:** none (verification only).

- [ ] **Step 1: Build the full solution for EF9 and EF10**

Run:
```bash
for v in EF9 EF10; do echo "== $v =="; dotnet build MongoDB.EFCoreProvider.sln -c "Debug $v" -v quiet; done
```
Expected: `Build succeeded. 0 Error(s)` for both.

- [ ] **Step 2: Run the bulk + diagnostics tests (EF9 and EF10)**

Run:
```bash
for v in EF9 EF10; do echo "== $v =="; dotnet test MongoDB.EFCoreProvider.sln -c "Debug $v" --no-build \
  --filter "FullyQualifiedName~ExecuteDeleteTests|FullyQualifiedName~ExecuteUpdateTests|FullyQualifiedName~LoggingTests|FullyQualifiedName~TransactionTests"; done
```
Expected: all pass, 0 failed — including the Task 1 characterization test and the existing two-phase / `AutoTransactionBehavior.Never` / diagnostics tests.

- [ ] **Step 3: Run the full suite across all three EF versions via the test-all skill**

Invoke the `/test-all` skill (it builds + tests EF8, EF9, EF10). Expected: EF8/EF9/EF10 all 0 failed (same totals as before the refactor: this is behavior-preserving). EF8 is unaffected (bulk is `#if !EF8`).

- [ ] **Step 4: Confirm the `:85` verbosity nit is resolved**

Open `src/MongoDB.EntityFrameworkCore/Query/Visitors/MongoShapedQueryCompilingExpressionVisitor.cs` and confirm the old two-phase executor-selection `?:`/pattern-match block (formerly ~lines 79-108) is gone, replaced by the single `CreateBulkPlan` + `Expression.Call` dispatch from Task 4 Step 1. No code action if already clean — this records that damieng's `:85` comment is addressed by the rewrite.

- [ ] **Step 5: Final commit (only if Step 5/earlier required follow-up edits; otherwise skip)**

```bash
git add -A
git commit -m "EF-107: Tidy after bulk-execution extraction"
```

---

## Self-Review

**Spec coverage:**
- `:548` (move execution out of the visitor) → Tasks 3 + 4 (executor created; visitor delegates to it).
- `:385` (transaction routing/logging) → confirmed no functional gap; Task 1 pins it; the executor keeps `database.BeginTransaction()` → `MongoTransaction`. Reply already drafted separately.
- `:85` (verbose two-phase selector) → resolved by the `VisitNonQuery` rewrite (Task 4 Step 1; verified Task 5 Step 4).
- Seam = option (a) delegate record (`MongoBulkPlan`) → Task 2.
- Behavior preservation → existing suite + characterization test (Tasks 1, 5).

**Placeholder scan:** No `TBD`/`implement later`/"similar to". Moved-method bodies are shown in full in Task 3; deletions in Task 4 list exact member names; kept members enumerated to prevent over-deletion.

**Type consistency:** `MongoBulkPlan` properties (`IsUpdate`, `IsTwoPhase`, `CollectionName`, `BuildFilter`, `BuildUpdate`, `BuildTargetIdQuery`) are produced identically in `CreateBulkPlan` (Task 4) and consumed identically in `MongoBulkOperationExecutor` (Task 3). `Execute`/`ExecuteAsync` signatures `(QueryContext, MongoBulkPlan)` match the `Expression.Call` in `VisitNonQuery`. `CreateBulkPlan<TSource>(IReadOnlyEntityType, BsonSerializerFactory, MongoNonQueryExpression)` matches the reflective `Invoke` arg array `[entityType, _bsonSerializerFactory, nonQueryExpression]`.

**Known follow-ups (out of scope):** `MongoTransaction.Start` message dedup (broader, touches SaveChanges); the `:512` file-split (declined by author); `:29`/`:242`/`:403` (answered in replies).
