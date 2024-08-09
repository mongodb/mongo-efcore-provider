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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
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
    }

    /// <inheritdoc />
    public IEnumerable<T> Execute<T>(MongoExecutableQuery executableQuery, out Action log)
    {
        log = () => { };

        if (executableQuery.Cardinality != ResultCardinality.Enumerable)
            return ExecuteScalar<T>(executableQuery);

        var queryable = (IMongoQueryable<T>)executableQuery.Provider.CreateQuery<T>(executableQuery.Query);
        log = () => _commandLogger.ExecutedMqlQuery(executableQuery);
        return queryable;
    }

    /// <inheritdoc />
    public IMongoCollection<T> GetCollection<T>(string collectionName)
        => _database.GetCollection<T>(collectionName);

    /// <inheritdoc />
    public IClientSessionHandle StartSession()
        => _client.StartSession();

    /// <inheritdoc />
    public async Task<IClientSessionHandle> StartSessionAsync(CancellationToken cancellationToken = default)
        => await _client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public bool CreateDatabase(IModel model)
    {
        var existed = DatabaseExists();
        var existingCollectionNames = _database.ListCollectionNames().ToList();

        foreach (var collectionName in model.GetEntityTypes().Where(e => e.IsDocumentRoot()).Select(e => e.GetCollectionName()))
        {
            if (existingCollectionNames.Contains(collectionName)) continue;

            try
            {
                _database.CreateCollection(collectionName);
            }
            catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
            {
                // Ignore collection already exists in cases of concurrent creation
            }
        }

        return !existed;
    }

    /// <inheritdoc />
    public async Task<bool> CreateDatabaseAsync(IModel model, CancellationToken cancellationToken = default)
    {
        var existed = await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
        var collectionNamesCursor = await _database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var existingCollectionNames = collectionNamesCursor.ToList(cancellationToken);

        foreach (var collectionName in model.GetEntityTypes().Where(e => e.IsDocumentRoot()).Select(e => e.GetCollectionName()))
        {
            if (existingCollectionNames.Contains(collectionName)) continue;

            try
            {
                await _database.CreateCollectionAsync(collectionName, null, cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
            {
                // Ignore collection already exists in cases of concurrent creation
            }
        }

        return !existed;
    }

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
        using var cursor = _client.ListDatabaseNames(BuildListDbNameOptions());
        return cursor.Any();
    }

    /// <inheritdoc/>
    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        using var cursor = await _client
            .ListDatabaseNamesAsync(BuildListDbNameOptions(), cancellationToken).ConfigureAwait(false);
        return await cursor.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    private ListDatabaseNamesOptions BuildListDbNameOptions()
        => new() {Filter = Builders<BsonDocument>.Filter.Eq("name", DatabaseName)};

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
