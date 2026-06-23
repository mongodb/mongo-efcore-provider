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
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    // Dispatch on the (strategy, kind) pair. A new MongoBulkStrategy (e.g. a future $merge path) adds an arm here;
    // the explicit-arms-plus-throw shape routes any unhandled combination to a loud failure rather than silently
    // falling through to single-command/delete.
    public static int Execute(QueryContext queryContext, MongoBulkPlan plan)
        => (plan.Strategy, plan.Kind) switch
        {
            (MongoBulkStrategy.SingleCommand, MongoBulkOperationKind.Delete) => ExecuteDelete(queryContext, plan),
            (MongoBulkStrategy.SingleCommand, MongoBulkOperationKind.Update) => ExecuteUpdate(queryContext, plan),
            (MongoBulkStrategy.TwoPhase, MongoBulkOperationKind.Delete) => ExecuteTwoPhaseDelete(queryContext, plan),
            (MongoBulkStrategy.TwoPhase, MongoBulkOperationKind.Update) => ExecuteTwoPhaseUpdate(queryContext, plan),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), $"Unsupported bulk plan: {plan.Strategy}/{plan.Kind}.")
        };

    public static Task<int> ExecuteAsync(QueryContext queryContext, MongoBulkPlan plan)
        => (plan.Strategy, plan.Kind) switch
        {
            (MongoBulkStrategy.SingleCommand, MongoBulkOperationKind.Delete) => ExecuteDeleteAsync(queryContext, plan),
            (MongoBulkStrategy.SingleCommand, MongoBulkOperationKind.Update) => ExecuteUpdateAsync(queryContext, plan),
            (MongoBulkStrategy.TwoPhase, MongoBulkOperationKind.Delete) => ExecuteTwoPhaseDeleteAsync(queryContext, plan),
            (MongoBulkStrategy.TwoPhase, MongoBulkOperationKind.Update) => ExecuteTwoPhaseUpdateAsync(queryContext, plan),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), $"Unsupported bulk plan: {plan.Strategy}/{plan.Kind}.")
        };

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
