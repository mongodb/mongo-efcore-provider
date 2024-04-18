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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
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
        {
            // TODO: Figure out MQL capture for non-enumerable cardinality
            return new[]
            {
                executableQuery.Provider.Execute<T>(executableQuery.Query)
            };
        }

        var queryable = (IMongoQueryable<T>)executableQuery.Provider.CreateQuery<T>(executableQuery.Query);
        log = () => _commandLogger.ExecutedMqlQuery(executableQuery.CollectionNamespace, queryable.LoggedStages);
        return queryable;
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
        long documentsAffected = 0;
        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            var collection = _database.GetCollection<BsonDocument>(batch.CollectionName);
            var result = collection.BulkWrite(session, batch.Models);
            documentsAffected += result.ModifiedCount + result.InsertedCount + result.DeletedCount;
        }

        return documentsAffected;
    }

    /// <summary>
    /// Save the supplied <see cref="MongoUpdate"/> operations to the database asynchronously.
    /// </summary>
    /// <param name="updates">An <see cref="IEnumerable{MongoUpdate}"/> containing the updates to apply to the database.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the operation.</param>
    /// <returns>A <see cref="Task{long}"/> that when resolved gives the number of documents modified.</returns>
    public async Task<long> SaveUpdatesAsync(IEnumerable<MongoUpdate> updates, CancellationToken cancellationToken)
    {
        using var session = await _client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        long documentsAffected = 0;
        foreach (var batch in MongoUpdateBatch.CreateBatches(updates))
        {
            var collection = _database.GetCollection<BsonDocument>(batch.CollectionName);
            var result = await collection.BulkWriteAsync(session, batch.Models, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            documentsAffected += result.ModifiedCount + result.InsertedCount + result.DeletedCount;
        }

        return documentsAffected;
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
