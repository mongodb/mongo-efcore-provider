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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Creates and deletes databases on MongoDB servers.
/// </summary>
/// <remarks>
/// This class is not typically used directly from application code.
/// </remarks>
public class MongoDatabaseCreator(
    IMongoClientWrapper clientWrapper,
    IDesignTimeModel designTimeModel,
    IUpdateAdapterFactory updateAdapterFactory,
    IDatabase database,
    IDiagnosticsLogger<DbLoggerCategory.Database> logger)
    : IMongoDatabaseCreator
{
    private bool _useDatabaseNameFilter = true;

    /// <inheritdoc/>
    public bool EnsureDeleted()
    {
        if (!DatabaseExists())
            return false;

        clientWrapper.Client.DropDatabase(clientWrapper.DatabaseName);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default)
    {
        if (!await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false))
            return false;

        await clientWrapper.Client.DropDatabaseAsync(clientWrapper.DatabaseName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public bool EnsureCreated()
        => EnsureCreated(new());

    /// <inheritdoc/>
    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
        => EnsureCreatedAsync(new(), cancellationToken);

    /// <inheritdoc/>
    public bool EnsureCreated(MongoDatabaseCreationOptions options)
    {
        var existed = DatabaseExists();

        if (options.CreateMissingCollections)
        {
            using var collectionNamesCursor = clientWrapper.Database.ListCollectionNames();
            var collectionNames = collectionNamesCursor.ToList();

            foreach (var entityType in designTimeModel.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
            {
                var collectionName = entityType.GetCollectionName();
                if (!collectionNames.Contains(collectionName))
                {
                    collectionNames.Add(collectionName);
                    try
                    {
                        clientWrapper.Database.CreateCollection(collectionName, options.CreateCollectionOptions);
                    }
                    catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                    {
                    }
                }
            }
        }

        if (!existed)
        {
            SeedFromModel();
        }

        if (options.CreateMissingIndexes)
        {
            CreateMissingIndexes();
        }

        if (options.CreateMissingVectorIndexes)
        {
            CreateMissingVectorIndexes();
        }

        if (options.WaitForVectorIndexes)
        {
            WaitForVectorIndexes(options.IndexCreationTimeout);
        }

        return !existed;
    }

    /// <inheritdoc/>
    public async Task<bool> EnsureCreatedAsync(MongoDatabaseCreationOptions options, CancellationToken cancellationToken = default)
    {
        var existed = await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);

        if (options.CreateMissingCollections)
        {
            using var collectionNamesCursor =
                await clientWrapper.Database.ListCollectionNamesAsync(null, cancellationToken).ConfigureAwait(false);
            var collectionNames = await collectionNamesCursor.ToListAsync(cancellationToken);

            foreach (var entityType in designTimeModel.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
            {
                var collectionName = entityType.GetCollectionName();
                if (!collectionNames.Contains(collectionName))
                {
                    collectionNames.Add(collectionName);
                    try
                    {
                        await clientWrapper.Database.CreateCollectionAsync(collectionName, options.CreateCollectionOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                    {
                    }
                }
            }
        }

        if (!existed)
        {
            await SeedFromModelAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.CreateMissingIndexes)
        {
            await CreateMissingIndexesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.CreateMissingVectorIndexes)
        {
            await CreateMissingVectorIndexesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.WaitForVectorIndexes)
        {
            await WaitForVectorIndexesAsync(options.IndexCreationTimeout, cancellationToken).ConfigureAwait(false);
        }

        return !existed;
    }

    // Used by tests. The EF8 tests use sync infra, and hence this method is used when building against EF8.
    internal void SeedFromModel()
        => database.SaveChanges(AddModelData().GetEntriesToSave());

    // Used by tests. The EF9 tests were updated to async infra, and hence this method is used when building against EF9.
    internal async Task SeedFromModelAsync(CancellationToken cancellationToken = default)
        => await database.SaveChangesAsync(AddModelData().GetEntriesToSave(), cancellationToken).ConfigureAwait(false);

    private IUpdateAdapter AddModelData()
    {
        var updateAdapter = updateAdapterFactory.CreateStandalone();
        foreach (var entityType in designTimeModel.Model.GetEntityTypes())
        {
            foreach (var targetSeed in entityType.GetSeedData())
            {
                var runtimeEntityType = updateAdapter.Model.FindEntityType(entityType.Name)!;
                var entry = updateAdapter.CreateEntry(targetSeed, runtimeEntityType);
                entry.EntityState = EntityState.Added;
            }
        }

        return updateAdapter;
    }

    /// <inheritdoc/>
    public bool CanConnect()
    {
        try
        {
            // Do anything that causes an actual database connection with no side effects
            DatabaseExists();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Do anything that causes an actual database connection with no side effects
            await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void CreateMissingIndexes()
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateIndexModel<BsonDocument>>?>();

        foreach (var entityType in designTimeModel.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }

            if (!existingIndexesMap.TryGetValue(collectionName, out var indexes))
            {
                using var cursor = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).Indexes.List();
                indexes = cursor.ToList().Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] = indexes;
            }

            BuildIndexes(entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var indexManager = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).Indexes;
                indexManager.CreateMany(indexModels);
            }
        }
    }

    /// <inheritdoc />
    public void CreateMissingVectorIndexes()
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateSearchIndexModel>?>();

        foreach (var entityType in designTimeModel.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
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
                using var cursor = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.List();
                indexes = cursor.ToList().Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] = indexes;
            }

            BuildVectorIndexes(entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var searchIndexManager = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes;
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
            ? clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes
                .CreateOne(index.CreateVectorIndexDocument(vectorIndexOptions.Value))
            : clientWrapper.Database.GetCollection<BsonDocument>(collectionName).Indexes
                .CreateOne(index.CreateIndexDocument());
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(IIndex index, CancellationToken cancellationToken = default)
    {
        var collectionName = index.DeclaringEntityType.GetCollectionName();
        var vectorIndexOptions = index.GetVectorIndexOptions();

        _ = vectorIndexOptions.HasValue
            ? await clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes
                .CreateOneAsync(index.CreateVectorIndexDocument(vectorIndexOptions.Value), cancellationToken)
            : await clientWrapper.Database.GetCollection<BsonDocument>(collectionName).Indexes
                .CreateOneAsync(index.CreateIndexDocument(), cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public void WaitForVectorIndexes(TimeSpan? timeout = null)
    {
        // Don't try to access Atlas-specific features unless an Atlas vector index is defined.
        if (designTimeModel.Model.GetEntityTypes().All(e => !HasAtlasIndexes(e)))
        {
            return;
        }

        var failAfter = CalculateTimeoutDateTime(timeout);

        foreach (var collectionName in designTimeModel.Model.GetCollectionNames())
        {
            var delay = 1;
            bool isReady;
            do
            {
                isReady = true;
                using var cursor = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.List();

                foreach (var indexModel in cursor.ToList())
                {
                    var status = indexModel["status"].AsString;

                    if (status == "FAILED")
                    {
                        throw new InvalidOperationException(
                            $"Failed to build the vector index '{indexModel["name"]}' for path '{indexModel["latestDefinition"]["fields"][0]["path"]}'.");
                    }

                    var remainingBeforeTimeout = failAfter - DateTime.UtcNow;
                    if (status != "READY" && remainingBeforeTimeout > TimeSpan.Zero)
                    {
                        logger.WaitingForVectorIndex(remainingBeforeTimeout);

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
        timeout ??= TimeSpan.FromSeconds(60);
        return timeout.Value == TimeSpan.Zero
            ? DateTime.MaxValue
            : DateTime.UtcNow.Add(timeout.Value);
    }

    /// <inheritdoc />
    public async Task CreateMissingIndexesAsync(CancellationToken cancellationToken = default)
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateIndexModel<BsonDocument>>?>();

        foreach (var entityType in designTimeModel.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!collectionNames.Contains(collectionName, StringComparer.Ordinal))
            {
                collectionNames.Add(collectionName);
            }

            if (!existingIndexesMap.TryGetValue(collectionName, out var indexes))
            {
                using var cursor = await clientWrapper.Database.GetCollection<BsonDocument>(collectionName).Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
                indexes = (await cursor.ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] = indexes;
            }

            BuildIndexes(entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var indexManager = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).Indexes;
                await indexManager.CreateManyAsync(indexModels, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task CreateMissingVectorIndexesAsync(CancellationToken cancellationToken = default)
    {
        var collectionNames = new List<string>();
        var existingIndexesMap = new Dictionary<string, List<string>?>();
        var indexModelsMap = new Dictionary<string, List<CreateSearchIndexModel>?>();

        foreach (var entityType in designTimeModel.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
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
                using var cursor = await clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                indexes = (await cursor.ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Select(i => i["name"].AsString).ToList();
                existingIndexesMap[collectionName] = indexes;
            }

            BuildVectorIndexes(entityType, collectionName, existingIndexesMap, indexModelsMap);
        }

        foreach (var collectionName in collectionNames)
        {
            if (indexModelsMap.TryGetValue(collectionName, out var indexModels) && indexModels!.Count > 0)
            {
                var searchIndexManager = clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes;
                await searchIndexManager.CreateManyAsync(indexModels, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task WaitForVectorIndexesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // Don't try to access Atlas-specific features unless an Atlas vector index is defined.
        if (designTimeModel.Model.GetEntityTypes().All(e => !HasAtlasIndexes(e)))
        {
            return;
        }

        var failAfter = CalculateTimeoutDateTime(timeout);
        foreach (var collectionName in designTimeModel.Model.GetCollectionNames())
        {
            var delay = 1;
            bool isReady;
            do
            {
                isReady = true;
                using var cursor = await clientWrapper.Database.GetCollection<BsonDocument>(collectionName).SearchIndexes.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var indexModels = await cursor.ToListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                foreach (var indexModel in indexModels)
                {
                    var status = indexModel["status"].AsString;

                    if (status == "FAILED")
                    {
                        throw new InvalidOperationException(
                            $"Failed to build the vector index '{indexModel["name"]}' for path '{indexModel["latestDefinition"]["fields"][0]["path"]}'.");
                    }

                    var remainingBeforeTimeout = failAfter - DateTime.UtcNow;
                    if (status != "READY" && remainingBeforeTimeout > TimeSpan.Zero)
                    {
                        logger.WaitingForVectorIndex(remainingBeforeTimeout);

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

        var ownedEntityTypes = designTimeModel.Model.GetEntityTypes().Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            BuildIndexes(ownedEntityType, collectionName, existingIndexesMap, indexModelsMap);
        }
    }

    private void BuildVectorIndexes(
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

        var ownedEntityTypes = designTimeModel.Model.GetEntityTypes().Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            BuildVectorIndexes(ownedEntityType, collectionName, existingIndexesMap, indexModelsMap);
        }
    }

    /// <inheritdoc/>
    public bool DatabaseExists()
    {
        if (_useDatabaseNameFilter)
        {
            try
            {
                return clientWrapper.Client.ListDatabaseNames(BuildListDbNameFilterOptions()).Any();
            }
            catch (MongoCommandException ex) when (ex.ErrorMessage.Contains("filter"))
            {
                // Shared cluster does not support filtering database names so fallback
                _useDatabaseNameFilter = false;
            }
        }

        return clientWrapper.Client.ListDatabaseNames().ToList().Any(d => d == clientWrapper.DatabaseName);
    }

    /// <inheritdoc/>
    public async Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default)
    {
        if (_useDatabaseNameFilter)
        {
            try
            {
                using var cursor = await clientWrapper.Client
                    .ListDatabaseNamesAsync(BuildListDbNameFilterOptions(), cancellationToken).ConfigureAwait(false);
                return await cursor.AnyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.ErrorMessage.Contains("filter"))
            {
                // Shared cluster does not support filtering database names so fallback
                _useDatabaseNameFilter = false;
            }
        }

        using var allCursor = await clientWrapper.Client.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false);
        var listOfDatabases = await allCursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return listOfDatabases.Any(d => d == clientWrapper.DatabaseName);
    }

    private ListDatabaseNamesOptions BuildListDbNameFilterOptions()
        => new() { Filter = Builders<BsonDocument>.Filter.Eq("name", clientWrapper.DatabaseName) };

}
