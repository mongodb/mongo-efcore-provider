﻿/* Copyright 2023-present MongoDB Inc.
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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides EF database-level operations using the underlying <see cref="IMongoClientWrapper"/>.
/// </summary>
public class MongoDatabaseWrapper : Database
{
    private readonly ICurrentDbContext _currentDbContext;
    private readonly IMongoClientWrapper _mongoClient;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Update> _updateLogger;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> _transactionLogger;
    private readonly TransactionOptions _transactionOptions = new();

    /// <summary>
    /// Creates a <see cref="MongoDatabaseWrapper"/> with the required dependencies, client wrapper and logging options.
    /// </summary>
    /// <param name="dependencies">The <see cref="DatabaseDependencies"/> this object should use.</param>
    /// <param name="currentDbContext">The <see cref="ICurrentDbContext"/> this should use to interact with the current context.</param>
    /// <param name="mongoClient">The <see cref="IMongoClientWrapper"/> this should use to interact with MongoDB.</param>
    /// <param name="updateLogger">The <see cref="IDiagnosticsLogger"/> for <see cref="DbLoggerCategory.Update"/>.</param>
    /// <param name="transactionLogger">The <see cref="IDiagnosticsLogger"/> for <see cref="DbLoggerCategory.Database.Transaction"/>.</param>
    public MongoDatabaseWrapper(
        DatabaseDependencies dependencies,
        ICurrentDbContext currentDbContext,
        IMongoClientWrapper mongoClient,
        IDiagnosticsLogger<DbLoggerCategory.Update> updateLogger,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> transactionLogger)
        : base(dependencies)
    {
        _currentDbContext = currentDbContext;
        _mongoClient = mongoClient;
        _updateLogger = updateLogger;
        _transactionLogger = transactionLogger;
    }

    /// <summary>
    /// Save all the changes detected in the entities collection to the underlying MongoDB database.
    /// </summary>
    /// <param name="entries">A list of <see cref="IUpdateEntry"/> indicating which changes to process.</param>
    /// <returns>Number of documents affected during saving changes.</returns>
    public override int SaveChanges(IList<IUpdateEntry> entries)
    {
        var rootEntries = GetAllChangedRootEntries(entries);
        var updates = MongoUpdate.CreateAll(rootEntries);

        using var session = _mongoClient.StartSession();

        return ShouldStartTransaction(rootEntries.Count)
            ? ExecuteInTransaction(() => WriteBatches(updates, session), session)
            : WriteBatches(updates, session);
    }

    /// <summary>
    /// Save all the changes detected in the entities collection to the underlying MongoDB database asynchronously.
    /// </summary>
    /// <param name="entries">A list of <see cref="IUpdateEntry"/> indicating which changes to process.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that might be cancelled during the save.</param>
    /// <returns>Task that when resolved contains the number of documents affected during saving changes.</returns>
    public override async Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
    {
        var rootEntries = GetAllChangedRootEntries(entries);
        var updates = MongoUpdate.CreateAll(rootEntries);

        using var session = await _mongoClient.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return ShouldStartTransaction(rootEntries.Count)
            ? await ExecuteInTransactionAsync(c => WriteBatchesAsync(updates, session, c), session, cancellationToken).ConfigureAwait(false)
            : await WriteBatchesAsync(updates, session, cancellationToken).ConfigureAwait(false);
    }

    private T ExecuteInTransaction<T>(Func<T> operation, IClientSession session)
    {
        using var transaction =
            MongoTransaction.Start(session, _currentDbContext.Context, false, _transactionOptions, _transactionLogger);

        T result;
        try
        {
            result = operation();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        transaction.Commit();

        return result;
    }

    private async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, IClientSession session, CancellationToken cancellationToken)
    {
        await using var transaction =
            MongoTransaction.Start(session, _currentDbContext.Context, true, _transactionOptions, _transactionLogger);

        T result;
        try
        {
            result = await operation(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private int WriteBatches(IEnumerable<MongoUpdate> updates, IClientSessionHandle session)
    {
        var stopwatch = new Stopwatch();
        var documentsAffected = 0;

        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            stopwatch.Restart();
            var collection = _mongoClient.GetCollection<BsonDocument>(batch.CollectionName);
            _updateLogger.ExecutingBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, batch.Inserts, batch.Deletes, batch.Modified);
            var result = collection.BulkWrite(session, batch.Updates.Select(u => u.Model));
            documentsAffected += AssertWritesApplied(batch, result, collection.CollectionNamespace);
            _updateLogger.ExecutedBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, result.InsertedCount, result.DeletedCount, result.ModifiedCount);
        }

        return documentsAffected;
    }

    private async Task<int> WriteBatchesAsync(IEnumerable<MongoUpdate> updates, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var stopwatch = new Stopwatch();
        var documentsAffected = 0;

        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            stopwatch.Restart();
            var collection = _mongoClient.GetCollection<BsonDocument>(batch.CollectionName);
            _updateLogger.ExecutingBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, batch.Inserts, batch.Deletes, batch.Modified);
            var result = await collection.BulkWriteAsync(session, batch.Updates.Select(u => u.Model), cancellationToken: cancellationToken).ConfigureAwait(false);
            documentsAffected += AssertWritesApplied(batch, result, collection.CollectionNamespace);
            _updateLogger.ExecutedBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, result.InsertedCount, result.DeletedCount, result.ModifiedCount);
        }

        return documentsAffected;
    }

    /// <summary>
    /// Asserts the bulk writes sent to the server matches were actually applied by comparing the counts.
    /// When they do not match either a document was deleted or a concurrency token changed so the filter did not find the document.
    /// </summary>
    /// <param name="batch">The <see cref="MongoUpdateBatch"/> containing details of the updates that were sent to the server.</param>
    /// <param name="result">The <see cref="BulkWriteResult{TDocument}"/> containing the results of the bulk write operation.</param>
    /// <param name="collectionNamespace">The <see class="CollectionNamespace"/> used to build the exception message on failure.</param>
    /// <returns>Number of documents affected by the bulk write operation.</returns>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Thrown if the counts in <paramref name="batch"/> does not match the counts in <paramref name="result"/>.
    /// </exception>
    private static int AssertWritesApplied(MongoUpdateBatch batch, BulkWriteResult<BsonDocument> result, CollectionNamespace collectionNamespace)
    {
        // Modified count does not include docs that that did not need modification - i.e. no modifications - so we use matched count instead.
        if (result.MatchedCount != batch.Modified || result.DeletedCount != batch.Deletes || result.InsertedCount != batch.Inserts)
        {
            var modifiedVariance = batch.Modified - result.MatchedCount;
            var insertedVariance = batch.Inserts - result.InsertedCount;
            var deletedVariance = batch.Deletes - result.DeletedCount;

            // We can't determine the exact entry that caused the conflict but we can narrow it down to which type(s) of entries
            // within this bulk write batch.
            var conflictingEntries = batch.Updates.Where(u => u.Model.ModelType == WriteModelType.DeleteOne && deletedVariance != 0
                                                              || u.Model.ModelType == WriteModelType.InsertOne
                                                              && insertedVariance != 0
                                                              || u.Model.ModelType == WriteModelType.UpdateOne
                                                              && modifiedVariance != 0).Select(u => u.Entry).ToList();

            throw new DbUpdateConcurrencyException($"Conflicts were detected when performing updates to '{collectionNamespace.FullName}'. " +
                $"Did not perform {modifiedVariance} modifications, {insertedVariance} insertions, and {deletedVariance} deletions.", conflictingEntries);
        }

        return (int)(result.ModifiedCount + result.DeletedCount + result.InsertedCount);
    }

    private bool ShouldStartTransaction(int operationCount)
        => _currentDbContext.Context.Database.AutoTransactionBehavior switch
        {
            AutoTransactionBehavior.Always => true,
            AutoTransactionBehavior.Never => false,
            AutoTransactionBehavior.WhenNeeded => operationCount > 1,
            _ => throw new InvalidOperationException("Invalid AutoTransactionBehavior value.")
        };

    /// <summary>
    /// We only care about updating root entities as non-root/owned entities must be contained within one.
    /// Convert the list of entries modified by EF Core into a list of modified root entries that were either
    /// directly modified themselves or indirectly modified by one of the entries they own.
    /// </summary>
    /// <param name="entries">The list of modified <see cref="IUpdateEntry"/> as determined by EF Core.</param>
    /// <returns>The actual list of changed root entities as required by MongoDB.</returns>
    private static HashSet<IUpdateEntry> GetAllChangedRootEntries(IList<IUpdateEntry> entries)
    {
        var changedRootEntries = new HashSet<IUpdateEntry>(entries);
        foreach (var entry in entries)
        {
            if (!entry.EntityType.IsDocumentRoot())
            {
                var root = GetRootEntry((InternalEntityEntry)entry);
                if (root.EntityState == EntityState.Unchanged)
                {
                    root.EntityState = EntityState.Modified;
                }

                changedRootEntries.Remove(entry);
                changedRootEntries.Add(root);
            }
        }

        return changedRootEntries;
    }

    private static IUpdateEntry GetRootEntry(InternalEntityEntry entry)
    {
        while (true)
        {
            var stateManager = entry.StateManager;
            var ownership = entry.EntityType.FindOwnership();
            if (ownership == null) continue;

            var principal = stateManager.FindPrincipal(entry, ownership);
            if (principal == null)
            {
                throw new InvalidOperationException(
                    $"The entity of type '{entry.EntityType.DisplayName()}' is mapped as a part of the document mapped to '{
                        ownership.PrincipalEntityType.DisplayName()
                    }', but there is no tracked entity of this type with the corresponding key value.");
            }

            if (principal.EntityType.IsDocumentRoot()) return principal;
            entry = principal;
        }
    }
}
