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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides the implementation of the <see cref="IMongoClientWrapper"/> between the MongoDB Entity Framework provider
/// and the underlying <see cref="IMongoClient"/>.
/// </summary>
public class MongoClientWrapper : IMongoClientWrapper
{
    private readonly IDiagnosticsLogger<DbLoggerCategory.Database.Command> _commandLogger;
    private readonly IMongoClient _client;
    private readonly IMongoDatabase _database;
    private readonly bool _wrapSaveChangesInTransaction;

    private string DatabaseName => _database.DatabaseNamespace.DatabaseName;

    /// <summary>
    /// Create a new instance of <see cref="MongoClientWrapper"/> with the supplied parameters.
    /// </summary>
    /// <param name="dbContextOptions">The <see cref="IDbContextOptions"/> that specify how this provider is configured.</param>
    /// <param name="serviceProvider">The <see cref="IServiceProvider"/> used to resolve dependencies.</param>
    /// <param name="commandLogger">The <see cref="IDiagnosticsLogger"/> used to log diagnostics events.</param>
    public MongoClientWrapper(
        IDbContextOptions dbContextOptions,
        IServiceProvider serviceProvider,
        IDiagnosticsLogger<DbLoggerCategory.Database.Command> commandLogger)
    {
        var options = dbContextOptions.FindExtension<MongoOptionsExtension>();
        _commandLogger = commandLogger;

        _client = GetOrCreateMongoClient(options, serviceProvider);
        _database = _client.GetDatabase(options!.DatabaseName);

        // Ideally this would be in the driver and check version numbers as well
        var serverSupportsTransactions = _client.Cluster.Description.Type is ClusterType.ReplicaSet or ClusterType.Sharded;
        _wrapSaveChangesInTransaction = serverSupportsTransactions;
    }

    /// <summary>
    /// Execute a <see cref="MongoExecutableQuery"/> and return  a <see cref="Action"/>
    /// that should be executed once the first item has been enumerated.
    /// </summary>
    /// <param name="executableQuery">The <see cref="MongoExecutableQuery"/> containing everything needed to run the query.</param>
    /// <param name="log">The <see cref="Action"/> returned that will perform the MQL log once evaluation has happened.</param>
    /// <typeparam name="T">The type of items being returned by the query.</typeparam>
    /// <returns>An <see cref="IEnumerable{T}"/> containing the items returned by the query.</returns>
    public IEnumerable<T> Execute<T>(MongoExecutableQuery executableQuery, out Action log)
    {
        log = () => { };

        if (executableQuery.Cardinality != ResultCardinality.Enumerable)
            return ExecuteScalar<T>(executableQuery);

        var queryable = (IMongoQueryable<T>)executableQuery.Provider.CreateQuery<T>(executableQuery.Query);
        log = () => _commandLogger.ExecutedMqlQuery(executableQuery);
        return queryable;
    }

    private IEnumerable<T> ExecuteScalar<T>(MongoExecutableQuery executableQuery)
    {
        T? result;
        try
        {
            result = executableQuery.Provider.Execute<T>(executableQuery.Query);
        }
        catch
        {
            _commandLogger.ExecutedMqlQuery(executableQuery);
            throw;
        }

        _commandLogger.ExecutedMqlQuery(executableQuery);
        return [result];
    }

    /// <summary>
    /// Get an <see cref="IMongoCollection{T}"/> associated with a MongoDB collection by name.
    /// </summary>
    /// <param name="collectionName">The name of the collection that should be queried.</param>
    /// <typeparam name="T">The type of items returned by the collection.</typeparam>
    /// <returns>A <see cref="IMongoCollection{T}"/> for the named collection.</returns>
    public IMongoCollection<T> GetCollection<T>(string collectionName)
        => _database.GetCollection<T>(collectionName);

    /// <summary>
    /// Save the supplied <see cref="MongoUpdate"/> operations to the database.
    /// </summary>
    /// <param name="updates">An <see cref="IEnumerable{MongoUpdate}"/> containing the updates to apply to the database.</param>
    /// <returns>The number of documents modified.</returns>
    public long SaveUpdates(IEnumerable<MongoUpdate> updates)
    {
        using var session = _client.StartSession();
        if (_wrapSaveChangesInTransaction) session.StartTransaction();

        long documentsAffected;
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
    /// Save the supplied <see cref="MongoUpdate"/> operations to the database asynchronously.
    /// </summary>
    /// <param name="updates">An <see cref="IEnumerable{MongoUpdate}"/> containing the updates to apply to the database.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> that, when resolved, gives the number of documents modified.</returns>
    public async Task<long> SaveUpdatesAsync(IEnumerable<MongoUpdate> updates, CancellationToken cancellationToken)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_wrapSaveChangesInTransaction) session.StartTransaction();

        long documentsAffected;
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

    private long WriteBatches(IEnumerable<MongoUpdate> updates, IClientSessionHandle session)
    {
        var stopwatch = new Stopwatch();
        long documentsAffected = 0;

        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            stopwatch.Restart();
            var collection = _database.GetCollection<BsonDocument>(batch.CollectionName);
            var result = collection.BulkWrite(session, batch.Models);
            _commandLogger.ExecutedBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, result.InsertedCount, result.DeletedCount, result.ModifiedCount);
            documentsAffected += result.ModifiedCount + result.InsertedCount + result.DeletedCount;
        }

        return documentsAffected;
    }

    private async Task<long> WriteBatchesAsync(IEnumerable<MongoUpdate> updates, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var stopwatch = new Stopwatch();
        long documentsAffected = 0;

        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            stopwatch.Restart();
            var collection = _database.GetCollection<BsonDocument>(batch.CollectionName);
            var result = await collection.BulkWriteAsync(session, batch.Models, cancellationToken: cancellationToken).ConfigureAwait(false);
            _commandLogger.ExecutedBulkWrite(stopwatch.Elapsed, collection.CollectionNamespace, result.InsertedCount, result.DeletedCount, result.ModifiedCount);
            documentsAffected += result.ModifiedCount + result.InsertedCount + result.DeletedCount;
        }

        return documentsAffected;
    }

    /// <inheritdoc />
    public bool CreateDatabase()
        => !DatabaseExists();

    /// <inheritdoc />
    public async Task<bool> CreateDatabaseAsync(CancellationToken cancellationToken = default)
        => !await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public bool DeleteDatabase()
    {
        if (!DatabaseExists()) return false;

        _client.DropDatabase(DatabaseName);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (!await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false)) return false;

        await _client.DropDatabaseAsync(DatabaseName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public bool DatabaseExists()
    {
        using var cursor = _client.ListDatabaseNames(new ListDatabaseNamesOptions {
            Filter = Builders<BsonDocument>.Filter.Eq("name", DatabaseName)
        });
        return cursor.Any();
    }

    /// <inheritdoc/>
    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        using var cursor = await _client.ListDatabaseNamesAsync(new ListDatabaseNamesOptions {
            Filter = Builders<BsonDocument>.Filter.Eq("name", DatabaseName)
        }, cancellationToken).ConfigureAwait(false);
        return await cursor.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IMongoClient GetOrCreateMongoClient(MongoOptionsExtension? options, IServiceProvider serviceProvider)
    {
        var injectedClient = (IMongoClient?)serviceProvider.GetService(typeof(IMongoClient));
        if (injectedClient != null)
            return injectedClient;

        if (options?.ConnectionString != null)
            return new MongoClient(options.ConnectionString);

        if (options?.MongoClient != null)
            return options.MongoClient;

        throw new InvalidOperationException(
            "An implementation of IMongoClient must be registered with the ServiceProvider or a ConnectionString set via DbOptions to connect to MongoDB.");
    }
}
