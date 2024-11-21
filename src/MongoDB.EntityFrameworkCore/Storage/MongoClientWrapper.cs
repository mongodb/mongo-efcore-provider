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
    private bool _useDatabaseNameFilter = true;

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

        var queryable = executableQuery.Provider.CreateQuery<T>(executableQuery.Query);
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
        => await _client.StartSessionAsync(null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public bool CreateDatabase(IDesignTimeModel model)
    {
        var didCreateNewDatabase = !DatabaseExists();
        var existingCollectionNames = _database.ListCollectionNames().ToList();

        foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!existingCollectionNames.Contains(collectionName))
            {
                try
                {
                    _database.CreateCollection(collectionName);
                    existingCollectionNames.Add(collectionName);
                }
                catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                {
                    // Ignore collection already exists in cases of concurrent creation
                }
            }

            CreateIndexes(entityType);
        }

        return didCreateNewDatabase;
    }

    private void CreateIndexes(IEntityType entityType)
    {
        var indexManager = _database.GetCollection<BsonDocument>(entityType.GetCollectionName()).Indexes;
        var existingIndexNames = indexManager.List().ToList().Select(i => i["name"].AsString).ToList();

        foreach (var index in entityType.GetIndexes())
        {
            var name = index.Name ?? DefaultIndexName(index);
            if (!existingIndexNames.Contains(name))
            {
                indexManager.CreateOne(CreateIndexDocument(index, name));
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> CreateDatabaseAsync(IDesignTimeModel model, CancellationToken cancellationToken = default)
    {
        var existed = await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
        using var collectionNamesCursor =
            await _database.ListCollectionNamesAsync(null, cancellationToken).ConfigureAwait(false);
        var existingCollectionNames = collectionNamesCursor.ToList(cancellationToken);

        foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!existingCollectionNames.Contains(collectionName))
            {
                try
                {
                    await _database.CreateCollectionAsync(collectionName, null, cancellationToken).ConfigureAwait(false);
                }
                catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                {
                    // Ignore collection already exists in cases of concurrent creation
                }
            }

            await CreateIndexesAsync(entityType, cancellationToken).ConfigureAwait(false);
        }

        return !existed;
    }

    private async Task CreateIndexesAsync(IEntityType entityType, CancellationToken cancellationToken)
    {
        var indexManager = _database.GetCollection<BsonDocument>(entityType.GetCollectionName()).Indexes;
        var indexCursor = await indexManager.ListAsync(cancellationToken).ConfigureAwait(false);
        var existingIndexNames = indexCursor.ToList(cancellationToken).Select(i => i["name"].AsString).ToList();

        foreach (var index in entityType.GetIndexes())
        {
            var name = index.Name ?? DefaultIndexName(index);
            if (!existingIndexNames.Contains(name))
            {
                await indexManager.CreateOneAsync(CreateIndexDocument(index, name), null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static string DefaultIndexName(IIndex index)
    {
        // Mimic the servers index naming convention using the property names and directions
        var parts = new string[index.Properties.Count * 2];

        var propertyIndex = 0;
        var partsIndex = 0;
        foreach (var property in index.Properties)
        {
            parts[partsIndex++] = property.GetElementName();
            parts[partsIndex++] = GetDescending(index, propertyIndex++) ? "-1" : "1";
        }

        return string.Join('_', parts);
    }

    /// <inheritdoc />
    public bool DeleteDatabase()
    {
        if (!DatabaseExists())
            return false;

        _client.DropDatabase(DatabaseName);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (!await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await _client.DropDatabaseAsync(DatabaseName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public bool DatabaseExists()
    {
        if (_useDatabaseNameFilter)
        {
            try
            {
                return _client.ListDatabaseNames(BuildListDbNameFilterOptions()).Any();
            }
            catch (MongoCommandException ex) when (ex.ErrorMessage.Contains("filter"))
            {
                // Shared cluster does not support filtering database names so fallback
                _useDatabaseNameFilter = false;
            }
        }

        return _client.ListDatabaseNames().ToList().Any(d => d == DatabaseName);
    }

    /// <inheritdoc/>
    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        if (_useDatabaseNameFilter)
        {
            try
            {
                using var cursor = await _client
                    .ListDatabaseNamesAsync(BuildListDbNameFilterOptions(), cancellationToken).ConfigureAwait(false);
                return await cursor.AnyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.ErrorMessage.Contains("filter"))
            {
                // Shared cluster does not support filtering database names so fallback
                _useDatabaseNameFilter = false;
            }
        }

        using var allCursor = await _client.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
        var listOfDatabases = await allCursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return listOfDatabases.Any(d => d == DatabaseName);
    }

    private ListDatabaseNamesOptions BuildListDbNameFilterOptions()
        => new() {Filter = Builders<BsonDocument>.Filter.Eq("name", DatabaseName)};

    private static CreateIndexModel<BsonDocument> CreateIndexDocument(IIndex index, string indexName)
    {
        var doc = new BsonDocument();
        var propertyIndex = 0;

        foreach (var property in index.Properties)
        {
            doc.Add(property.GetElementName(), GetDescending(index, propertyIndex++) ? -1 : 1);
        }

        var options = index.GetCreateIndexOptions() ?? new CreateIndexOptions<BsonDocument>();
        options.Name ??= indexName;
        options.Unique ??= index.IsUnique;

        return new CreateIndexModel<BsonDocument>(doc, options);
    }

    private static bool GetDescending(IIndex index, int propertyIndex)
        => index.IsDescending switch
        {
            null => false,
            {Count: 0} => true,
            { } i when i.Count < propertyIndex => false,
            { } i => i.ElementAtOrDefault(propertyIndex)
        };

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
