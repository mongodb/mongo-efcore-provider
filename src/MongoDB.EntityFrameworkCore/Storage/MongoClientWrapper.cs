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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides the implementation of the <see cref="IMongoClientWrapper"/> between the MongoDB Entity Framework Core
/// provider and the underlying <see cref="IMongoClient"/>.
/// </summary>
public class MongoClientWrapper : IMongoClientWrapper
{
    private readonly MongoOptionsExtension? _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IQueryableEncryptionSchemaProvider _schemaProvider;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Database.Command> _commandLogger;

    private IMongoClient? _client;
    private IMongoDatabase? _database;
    private string? _databaseName;
    private bool _useDatabaseNameFilter = true;

    private IMongoClient Client => _client ??= GetOrCreateMongoClient(_options, _serviceProvider);
    private IMongoDatabase Database => _database ??= Client.GetDatabase(_databaseName);

    /// <summary>
    /// Create a new instance of <see cref="MongoClientWrapper"/> with the supplied parameters.
    /// </summary>
    /// <param name="dbContextOptions">The <see cref="IDbContextOptions"/> that specify how this provider is configured.</param>
    /// <param name="serviceProvider">The <see cref="IServiceProvider"/> used to resolve dependencies.</param>
    /// <param name="schemaProvider">The <see cref="IQueryableEncryptionSchemaProvider"/> used to obtain the Queryable Encryption schema.</param>
    /// <param name="commandLogger">The <see cref="IDiagnosticsLogger"/> used to log diagnostics events.</param>
    public MongoClientWrapper(
        IDbContextOptions dbContextOptions,
        IServiceProvider serviceProvider,
        IQueryableEncryptionSchemaProvider schemaProvider,
        IDiagnosticsLogger<DbLoggerCategory.Database.Command> commandLogger)
    {
        _options = dbContextOptions.FindExtension<MongoOptionsExtension>();
        _serviceProvider = serviceProvider;
        _schemaProvider = schemaProvider;
        _commandLogger = commandLogger;
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
        => Database.GetCollection<T>(collectionName);

    /// <inheritdoc />
    public IClientSessionHandle StartSession()
        => Client.StartSession();

    /// <inheritdoc />
    public async Task<IClientSessionHandle> StartSessionAsync(CancellationToken cancellationToken = default)
        => await Client.StartSessionAsync(null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public bool CreateDatabase(IDesignTimeModel model)
        => CreateDatabase(model, new(), null);

    /// <inheritdoc />
    public bool CreateDatabase(IDesignTimeModel model, MongoDatabaseCreationOptions options, Action? seed)
    {
        var existed = DatabaseExists();

        if (options.CreateMissingCollections)
        {
            using var collectionNamesCursor = Database.ListCollectionNames();
            var collectionNames = collectionNamesCursor.ToList();

            foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
            {
                var collectionName = entityType.GetCollectionName();
                if (!collectionNames.Contains(collectionName))
                {
                    collectionNames.Add(collectionName);
                    try
                    {
                        Database.CreateCollection(collectionName);
                    }
                    catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                    {
                    }
                }
            }
        }

        if (!existed)
        {
            seed?.Invoke();
        }

        if (options.CreateMissingIndexes)
        {
            CreateMissingIndexes(model.Model);
        }

        if (options.CreateMissingVectorIndexes)
        {
            CreateMissingVectorIndexes(model.Model);
        }

        if (options.WaitForVectorIndexes)
        {
            WaitForVectorIndexes(model.Model, options.IndexCreationTimeout);
        }

        return !existed;
    }

    /// <inheritdoc />
    public Task<bool> CreateDatabaseAsync(IDesignTimeModel model, CancellationToken cancellationToken = default)
        => CreateDatabaseAsync(model, new(), null, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> CreateDatabaseAsync(
        IDesignTimeModel model,
        MongoDatabaseCreationOptions options,
        Func<CancellationToken, Task>? seedAsync, CancellationToken cancellationToken = default)
    {
        var existed = await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);

        if (options.CreateMissingCollections)
        {
            using var collectionNamesCursor =
                await Database.ListCollectionNamesAsync(null, cancellationToken).ConfigureAwait(false);
            var collectionNames = await collectionNamesCursor.ToListAsync(cancellationToken);

            foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
            {
                var collectionName = entityType.GetCollectionName();
                if (!collectionNames.Contains(collectionName))
                {
                    collectionNames.Add(collectionName);
                    try
                    {
                        await Database.CreateCollectionAsync(collectionName, null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                    {
                    }
                }
            }
        }

        if (!existed && seedAsync != null)
        {
            await seedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.CreateMissingIndexes)
        {
            await CreateMissingIndexesAsync(model.Model, cancellationToken).ConfigureAwait(false);
        }

        if (options.CreateMissingVectorIndexes)
        {
            await CreateMissingVectorIndexesAsync(model.Model, cancellationToken).ConfigureAwait(false);
        }

        if (options.WaitForVectorIndexes)
        {
            await WaitForVectorIndexesAsync(model.Model, options.IndexCreationTimeout, cancellationToken).ConfigureAwait(false);
        }

        return !existed;
    }

    /// <inheritdoc />
    public void CreateMissingIndexes(IModel model)
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateIndexModel<BsonDocument>>?>();

        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }

            if (!existingIndexesMap.TryGetValue(collectionName, out var indexes))
            {
                using var cursor = Database.GetCollection<BsonDocument>(collectionName).Indexes.List();
                indexes = cursor.ToList().Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] =  indexes;
            }

            BuildIndexes(model, entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var indexManager = Database.GetCollection<BsonDocument>(collectionName).Indexes;
                indexManager.CreateMany(indexModels);
            }
        }
    }

    /// <inheritdoc />
    public void CreateMissingVectorIndexes(IModel model)
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateSearchIndexModel>?>();

        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            // Don't try to access Atlas-specific features unless an Atlas vector index is defined.
            if (!HasAtlasIndexes(entityType))
            {
                continue;
            }

            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }

            if (!existingIndexesMap.TryGetValue(collectionName, out var indexes))
            {
                using var cursor = Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.List();
                indexes = cursor.ToList().Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] = indexes;
            }

            BuildVectorIndexes(model, entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var searchIndexManager = Database.GetCollection<BsonDocument>(collectionName).SearchIndexes;
                searchIndexManager.CreateMany(indexModels);
            }
        }
    }

    private static bool HasAtlasIndexes(IEntityType entityType)
    {
        if (entityType.GetIndexes().Any(i => i.GetVectorIndexOptions().HasValue))
        {
            return true;
        }

        foreach (var ownedEntityType in entityType.Model.GetEntityTypes().Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType))
        {
            return HasAtlasIndexes(ownedEntityType);
        }

        return false;
    }

    /// <inheritdoc />
    public void CreateIndex(IIndex index)
    {
        var collectionName = index.DeclaringEntityType.GetCollectionName();
        var vectorIndexOptions = index.GetVectorIndexOptions();

        _ = vectorIndexOptions.HasValue
            ? Database.GetCollection<BsonDocument>(collectionName).SearchIndexes
                .CreateOne(index.CreateVectorIndexDocument(vectorIndexOptions.Value))
            : Database.GetCollection<BsonDocument>(collectionName).Indexes
                .CreateOne(index.CreateIndexDocument());
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(IIndex index, CancellationToken cancellationToken = default)
    {
        var collectionName = index.DeclaringEntityType.GetCollectionName();
        var vectorIndexOptions = index.GetVectorIndexOptions();

        _ = vectorIndexOptions.HasValue
            ? await Database.GetCollection<BsonDocument>(collectionName).SearchIndexes
                .CreateOneAsync(index.CreateVectorIndexDocument(vectorIndexOptions.Value), cancellationToken)
            : await Database.GetCollection<BsonDocument>(collectionName).Indexes
                .CreateOneAsync(index.CreateIndexDocument(), cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public void WaitForVectorIndexes(IModel model, TimeSpan? timeout = null)
    {
        // Don't try to access Atlas-specific features unless an Atlas vector index is defined.
        if (model.GetEntityTypes().All(e => !HasAtlasIndexes(e)))
        {
            return;
        }

        var failAfter = CalculateTimeoutDateTime(timeout);

        foreach (var collectionName in model.GetCollectionNames())
        {
            var delay = 1;
            bool isReady;
            do
            {
                isReady = true;
                using var cursor = Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.List();

                foreach (var indexModel in cursor.ToList())
                {
                    var status = indexModel["status"].AsString;

                    if (status == "FAILED")
                    {
                        throw new InvalidOperationException(
                            $"Failed to build the vector index '{indexModel["name"]}' for path '{indexModel["latestDefinition"]["fields"][0]["path"]}'.");
                    }

                    if (status != "READY")
                    {
                        isReady = false;
                        Thread.Sleep(delay *= 2);
                        break;
                    }
                }

                if (!isReady && DateTime.UtcNow >= failAfter)
                {
                    throw new InvalidOperationException(
                        "Index creation timed out. Please create indexes using MongoDB Compass or the mongosh shell.");
                }
            } while (!isReady);
        }
    }

    private static DateTimeOffset CalculateTimeoutDateTime(TimeSpan? timeout)
    {
        timeout ??= TimeSpan.FromSeconds(15);
        var failAfter = timeout.Value == TimeSpan.Zero
            ? DateTime.MaxValue
            : DateTimeOffset.UtcNow.Add(timeout.Value);
        return failAfter;
    }

    /// <inheritdoc />
    public async Task CreateMissingIndexesAsync(IModel model, CancellationToken cancellationToken = default)
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateIndexModel<BsonDocument>>?>();

        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }

            if (!existingIndexesMap.TryGetValue(collectionName, out var indexes))
            {
                using var cursor = await Database.GetCollection<BsonDocument>(collectionName).Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
                indexes = (await cursor.ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] =  indexes;
            }

            BuildIndexes(model, entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var indexManager = Database.GetCollection<BsonDocument>(collectionName).Indexes;
                await indexManager.CreateManyAsync(indexModels, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task CreateMissingVectorIndexesAsync(IModel model, CancellationToken cancellationToken = default)
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateSearchIndexModel>?>();

        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            // Don't try to access Atlas-specific features unless an Atlas vector index is defined.
            if (!HasAtlasIndexes(entityType))
            {
                continue;
            }

            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }

            if (!existingIndexesMap.TryGetValue(collectionName, out var indexes))
            {
                using var cursor = await Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                indexes = (await cursor.ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] = indexes;
            }

            BuildVectorIndexes(model, entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var searchIndexManager = Database.GetCollection<BsonDocument>(collectionName).SearchIndexes;
                await searchIndexManager.CreateManyAsync(indexModels, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task WaitForVectorIndexesAsync(IModel model, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // Don't try to access Atlas-specific features unless an Atlas vector index is defined.
        if (model.GetEntityTypes().All(e => !HasAtlasIndexes(e)))
        {
            return;
        }

        var failAfter = CalculateTimeoutDateTime(timeout);
        foreach (var collectionName in model.GetCollectionNames())
        {
            var delay = 1;
            bool isReady;
            do
            {
                isReady = true;
                using var cursor = await Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var indexModels = await cursor.ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var indexModel in indexModels)
                {
                    var status = indexModel["status"].AsString;

                    if (status == "FAILED")
                    {
                        throw new InvalidOperationException(
                            $"Failed to build the vector index '{indexModel["name"]}' for path '{indexModel["latestDefinition"]["fields"][0]["path"]}'.");
                    }

                    if (status != "READY")
                    {
                        isReady = false;
                        await Task.Delay(delay *= 2, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                }

                if (!isReady && DateTime.UtcNow >= failAfter)
                {
                    throw new InvalidOperationException(
                        "Index creation timed out. Please create indexes using MongoDB Compass or the mongosh shell.");
                }
            } while (!isReady);
        }
    }

    private void BuildIndexes(
        IModel model,
        IEntityType entityType,
        string collectionName,
        Dictionary<string, List<string>?> existingIndexesMap,
        Dictionary<string, List<CreateIndexModel<BsonDocument>>?> indexModelsMap)
    {
        var existingIndexes = existingIndexesMap[collectionName]!;

        if (!indexModelsMap.TryGetValue(collectionName, out var indexModels))
        {
            indexModels = [];
            indexModelsMap[collectionName] = indexModels;
        }

        foreach (var index in entityType.GetIndexes().Where(i => !i.GetVectorIndexOptions().HasValue))
        {
            var name = index.Name;
            Debug.Assert(name != null, "Index name should have been set by IndexNamingConvention.");

            if (!existingIndexes.Contains(name))
            {
                existingIndexes.Add(name);
                indexModels!.Add(index.CreateIndexDocument());
            }
        }

        foreach (var key in entityType.GetKeys().Where(k => !k.IsPrimaryKey()))
        {
            var name = key.MakeIndexName();
            if (!existingIndexes.Contains(name))
            {
                existingIndexes.Add(name);
                indexModels!.Add(key.CreateKeyIndexDocument(name));
            }
        }

        var ownedEntityTypes = model.GetEntityTypes().Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            BuildIndexes(model, ownedEntityType, collectionName, existingIndexesMap, indexModelsMap);
        }
    }

    private void BuildVectorIndexes(
        IModel model,
        IEntityType entityType,
        string collectionName,
        Dictionary<string, List<string>?> existingIndexesMap,
        Dictionary<string, List<CreateSearchIndexModel>?> indexModelsMap)
    {
        if (!existingIndexesMap.TryGetValue(collectionName, out var existingIndexes))
        {
            existingIndexesMap[collectionName] = existingIndexes = new List<string>();
        }

        if (!indexModelsMap.TryGetValue(collectionName, out var indexModels))
        {
            indexModels = [];
            indexModelsMap[collectionName] = indexModels;
        }

        foreach (var index in entityType.GetIndexes().Where(i => i.GetVectorIndexOptions().HasValue))
        {
            var name = index.Name;
            Debug.Assert(name != null, "Index name should have been set by IndexNamingConvention.");

            var options = index.GetVectorIndexOptions()!.Value;
            if (!existingIndexes!.Contains(name))
            {
                indexModels!.Add(index.CreateVectorIndexDocument(options));
                existingIndexes.Add(name);
            }
        }

        var ownedEntityTypes = model.GetEntityTypes().Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            BuildVectorIndexes(model, ownedEntityType, collectionName, existingIndexesMap, indexModelsMap);
        }
    }

    /// <inheritdoc />
    public bool DeleteDatabase()
    {
        if (!DatabaseExists())
            return false;

        Client.DropDatabase(_databaseName);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (!await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await Client.DropDatabaseAsync(_databaseName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public bool DatabaseExists()
    {
        if (_useDatabaseNameFilter)
        {
            try
            {
                return Client.ListDatabaseNames(BuildListDbNameFilterOptions()).Any();
            }
            catch (MongoCommandException ex) when (ex.ErrorMessage.Contains("filter"))
            {
                // Shared cluster does not support filtering database names so fallback
                _useDatabaseNameFilter = false;
            }
        }

        return Client.ListDatabaseNames().ToList().Any(d => d == _databaseName);
    }

    /// <inheritdoc/>
    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        if (_useDatabaseNameFilter)
        {
            try
            {
                using var cursor = await Client
                    .ListDatabaseNamesAsync(BuildListDbNameFilterOptions(), cancellationToken).ConfigureAwait(false);
                return await cursor.AnyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.ErrorMessage.Contains("filter"))
            {
                // Shared cluster does not support filtering database names so fallback
                _useDatabaseNameFilter = false;
            }
        }

        using var allCursor = await Client.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
        var listOfDatabases = await allCursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return listOfDatabases.Any(d => d == _databaseName);
    }

    private ListDatabaseNamesOptions BuildListDbNameFilterOptions()
        => new() { Filter = Builders<BsonDocument>.Filter.Eq("name", _databaseName) };

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

private IMongoClient GetOrCreateMongoClient(MongoOptionsExtension? options, IServiceProvider serviceProvider)
    {
        _databaseName = _options?.DatabaseName;
        if (_databaseName == null && options?.ConnectionString != null)
        {
            try
            {
                var connectionString = new MongoUrl(options.ConnectionString);
                _databaseName = connectionString.DatabaseName;
            }
            catch (FormatException)
            {
            }
        }

        var queryableEncryptionSchema = _schemaProvider.GetQueryableEncryptionSchema();
        var applyQueryableEncryptionSchema = queryableEncryptionSchema.Count > 0 &&
                                                   options?.QueryableEncryptionSchemaMode != QueryableEncryptionSchemaMode.Ignore;

        var createOwnMongoClient = applyQueryableEncryptionSchema || MongoClientSettingsHelper.HasMongoClientOptions(options);

        var preconfiguredMongoClient = (IMongoClient?)serviceProvider.GetService(typeof(IMongoClient)) ?? options?.MongoClient;
        if (preconfiguredMongoClient != null)
        {
            if (createOwnMongoClient)
            {
                throw new InvalidOperationException(
                    "Cannot activate encryption with a pre-configured MongoClient. Either use ConnectionString or ClientSettings options instead.");
            }

            return preconfiguredMongoClient;
        }

        var mongoClientSettings = MongoClientSettingsHelper.CreateSettings(options, queryableEncryptionSchema);
        return new MongoClient(mongoClientSettings);
    }
}
