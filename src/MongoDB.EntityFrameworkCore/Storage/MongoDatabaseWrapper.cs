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

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// Creates a <see cref="MongoDatabaseWrapper"/> with the required dependencies, client wrapper and logging options.
    /// </summary>
    /// <param name="dependencies">The <see cref="DatabaseDependencies"/> this object should use.</param>
    /// <param name="currentDbContext">The <see cref="ICurrentDbContext"/> this should use to interact with the current context.</param>
    /// <param name="mongoClient">The <see cref="IMongoClientWrapper"/> this should use to interact with MongoDB.</param>
    public MongoDatabaseWrapper(
        DatabaseDependencies dependencies,
        ICurrentDbContext currentDbContext,
        IMongoClientWrapper mongoClient,
        IDiagnosticsLogger<DbLoggerCategory.Update> updateLogger)
        : base(dependencies)
    {
        _currentDbContext = currentDbContext;
        _mongoClient = mongoClient;
        _updateLogger = updateLogger;
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
        if (ShouldStartTransaction(rootEntries.Count)) session.StartTransaction();

        int documentsAffected;
        try
        {
            documentsAffected = WriteBatches(updates, session);
        }
        catch
        {
            if (session.IsInTransaction) session.AbortTransaction();
            throw;
        }

        if (session.IsInTransaction) session.CommitTransaction();
        return documentsAffected;
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
        if (ShouldStartTransaction(rootEntries.Count)) session.StartTransaction();

        int documentsAffected;
        try
        {
            documentsAffected = await WriteBatchesAsync(updates, session, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (session.IsInTransaction) await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        return documentsAffected;
    }

    private int WriteBatches(IEnumerable<MongoUpdate> updates, IClientSessionHandle session)
    {
        var stopwatch = new Stopwatch();
        var documentsAffected = 0;

        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            stopwatch.Restart();
            var collection = _mongoClient.GetCollection<BsonDocument>(batch.CollectionName);
            var result = collection.BulkWrite(session, batch.Models);
            _updateLogger.ExecutedBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, result.InsertedCount, result.DeletedCount, result.ModifiedCount);
            documentsAffected += AssertWritesApplied(batch, result, collection.CollectionNamespace);
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
            var result = await collection.BulkWriteAsync(session, batch.Models, cancellationToken: cancellationToken).ConfigureAwait(false);
            _updateLogger.ExecutedBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, result.InsertedCount, result.DeletedCount, result.ModifiedCount);
            documentsAffected += AssertWritesApplied(batch, result, collection.CollectionNamespace);
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
    /// <returns></returns>
    /// <exception cref="DbUpdateConcurrencyException">
    /// Thrown if the number of expected operations in <paramref name="batch"/> does not match the counts in <paramref name="result"/>.
    /// </exception>
    private static int AssertWritesApplied(MongoUpdateBatch batch, BulkWriteResult<BsonDocument> result, CollectionNamespace collectionNamespace)
    {
        var modifiedVariance = batch.Modified - result.ModifiedCount;
        var insertedVariance = batch.Inserts - result.InsertedCount;
        var deletedVariance = batch.Deletes - result.DeletedCount;

        if (deletedVariance != 0 || insertedVariance != 0 || modifiedVariance != 0)
        {
            throw new DbUpdateConcurrencyException($"Conflicts were detected when performing updates to '{collectionNamespace.FullName}'. " +
                $"Did not perform {modifiedVariance} modifications, {insertedVariance} insertions, and {deletedVariance} deletions.");
        }

        return (int) (result.ModifiedCount + result.InsertedCount + result.DeletedCount);
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
