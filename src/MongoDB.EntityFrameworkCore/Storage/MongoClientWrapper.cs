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
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Misc;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
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
    private readonly IMongoSchemaProvider _schemaProvider;
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
    /// <param name="schemaProvider">The <see cref="IMongoSchemaProvider"/> used to obtain the Queryable Encryption schema.</param>
    /// <param name="commandLogger">The <see cref="IDiagnosticsLogger"/> used to log diagnostics events.</param>
    public MongoClientWrapper(
        IDbContextOptions dbContextOptions,
        IServiceProvider serviceProvider,
        IMongoSchemaProvider schemaProvider,
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
    {
        var existed = DatabaseExists();
        var existingCollectionNames = Database.ListCollectionNames().ToList();
        var queryableEncryptionSchemas = _schemaProvider.GetQueryableEncryptionSchema();

        if (queryableEncryptionSchemas.Any())
            Feature.Csfle2QEv2.ThrowIfNotSupported(Database.Client);

        foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            var createCollectionOptions = CreateCollectionOptions(queryableEncryptionSchemas, collectionName);

            if (!existingCollectionNames.Contains(collectionName))
            {
                try
                {
                    Database.CreateCollection(collectionName, createCollectionOptions);
                    existingCollectionNames.Add(collectionName);
                }
                catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                {
                    // Collection has been created by another instance? Still need to check the encryption schema.
                    if (createCollectionOptions != null)
                        CheckServerQueryableEncryptionCompatible(collectionName, createCollectionOptions.EncryptedFields);
                }
            }
            else
            {
                if (createCollectionOptions != null)
                    CheckServerQueryableEncryptionCompatible(collectionName, createCollectionOptions.EncryptedFields);
            }

            var indexManager = Database.GetCollection<BsonDocument>(entityType.GetCollectionName()).Indexes;
            var existingIndexNames = indexManager.List().ToList().Select(i => i["name"].AsString).ToList();
            CreateIndexes(entityType, indexManager, existingIndexNames, []);
        }

        return !existed;
    }

    private static void CreateIndexes(
        IEntityType entityType,
        IMongoIndexManager<BsonDocument> indexManager,
        List<string> existingIndexNames,
        string[] path)
    {
        foreach (var index in entityType.GetIndexes())
        {
            var name = index.Name ?? MakeIndexName(index, path);
            if (!existingIndexNames.Contains(name))
            {
                indexManager.CreateOne(CreateIndexDocument(index, name, path));
            }
        }

        foreach (var key in entityType.GetKeys().Where(k => !k.IsPrimaryKey()))
        {
            var name = MakeIndexName(key, path);
            if (!existingIndexNames.Contains(name))
            {
                indexManager.CreateOne(CreateKeyIndexDocument(key, name, path));
            }
        }

        var ownedEntityTypes = entityType.Model.GetEntityTypes()
            .Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            var elementName = ownedEntityType.GetContainingElementName()!;
            var newPath = path.Append(elementName).ToArray();
            CreateIndexes(ownedEntityType, indexManager, existingIndexNames, newPath);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CreateDatabaseAsync(IDesignTimeModel model, CancellationToken cancellationToken = default)
    {
        var existed = await DatabaseExistsAsync(cancellationToken).ConfigureAwait(false);
        using var collectionNamesCursor =
            await Database.ListCollectionNamesAsync(null, cancellationToken).ConfigureAwait(false);
        var existingCollectionNames = collectionNamesCursor.ToList(cancellationToken);

        var queryableEncryptionSchemas = _schemaProvider.GetQueryableEncryptionSchema();
        if (queryableEncryptionSchemas.Any())
            await Feature.Csfle2QEv2.ThrowIfNotSupportedAsync(Database.Client, cancellationToken);

        foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            var createCollectionOptions = CreateCollectionOptions(queryableEncryptionSchemas, collectionName);

            if (!existingCollectionNames.Contains(collectionName))
            {
                try
                {
                    await Database.CreateCollectionAsync(collectionName, createCollectionOptions, cancellationToken)
                        .ConfigureAwait(false);
                    existingCollectionNames.Add(collectionName);
                }
                catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                {
                    // Collection has been created by another instance? Still need to check the encryption schema.
                    if (createCollectionOptions != null)
                        CheckServerQueryableEncryptionCompatible(collectionName, createCollectionOptions.EncryptedFields);
                }
            }
            else
            {
                if (createCollectionOptions != null)
                    CheckServerQueryableEncryptionCompatible(collectionName, createCollectionOptions.EncryptedFields);
            }

            var indexManager = Database.GetCollection<BsonDocument>(entityType.GetCollectionName()).Indexes;
            var indexCursor = await indexManager.ListAsync(cancellationToken).ConfigureAwait(false);
            var existingIndexNames = indexCursor.ToList(cancellationToken).Select(i => i["name"].AsString).ToList();
            await CreateIndexesAsync(entityType, indexManager, existingIndexNames, [], cancellationToken).ConfigureAwait(false);
        }

        return !existed;
    }

    private static async Task CreateIndexesAsync(
        IEntityType entityType,
        IMongoIndexManager<BsonDocument> indexManager,
        List<string> existingIndexNames,
        string[] path,
        CancellationToken cancellationToken)
    {
        foreach (var index in entityType.GetIndexes())
        {
            var name = index.Name ?? MakeIndexName(index, path);
            if (!existingIndexNames.Contains(name))
            {
                await indexManager.CreateOneAsync(CreateIndexDocument(index, name, path), null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        foreach (var key in entityType.GetKeys().Where(k => !k.IsPrimaryKey()))
        {
            var name = MakeIndexName(key, path);
            if (!existingIndexNames.Contains(name))
            {
                await indexManager.CreateOneAsync(CreateKeyIndexDocument(key, name, path), null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var ownedEntityTypes = entityType.Model.GetEntityTypes()
            .Where(o => o.FindDeclaredOwnership()?.PrincipalEntityType == entityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            var elementName = ownedEntityType.GetContainingElementName()!;
            var newPath = path.Append(elementName).ToArray();
            await CreateIndexesAsync(ownedEntityType, indexManager, existingIndexNames, newPath, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string MakeIndexName(IIndex index, string[] path)
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

        return string.Join('_', path.Concat(parts));
    }

    private static string MakeIndexName(IKey key, string[] path)
    {
        var parts = new string[key.Properties.Count];

        var partsIndex = 0;
        foreach (var property in key.Properties)
        {
            parts[partsIndex++] = property.GetElementName() + "_1";
        }

        return string.Join('_', path.Concat(parts));
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

    private static CreateIndexModel<BsonDocument> CreateIndexDocument(IIndex index, string indexName, string[] path)
    {
        var doc = new BsonDocument();
        var propertyIndex = 0;

        foreach (var property in index.Properties)
        {
            doc.Add(string.Join('.', path.Append(property.GetElementName())), GetDescending(index, propertyIndex++) ? -1 : 1);
        }

        var options = index.GetCreateIndexOptions() ?? new CreateIndexOptions<BsonDocument>();
        options.Name ??= indexName;
        options.Unique ??= index.IsUnique;

        return new CreateIndexModel<BsonDocument>(doc, options);
    }

    private static CreateIndexModel<BsonDocument> CreateKeyIndexDocument(IKey key, string indexName, string[] path)
    {
        var doc = new BsonDocument();

        foreach (var property in key.Properties)
        {
            doc.Add(string.Join('.', path.Append(property.GetElementName())), 1);
        }

        var options = new CreateIndexOptions<BsonDocument> { Name = indexName, Unique = true };
        return new CreateIndexModel<BsonDocument>(doc, options);
    }

    private static bool GetDescending(IIndex index, int propertyIndex)
        => index.IsDescending switch
        {
            null => false,
            { Count: 0 } => true,
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


    private CreateCollectionOptions? CreateCollectionOptions(
        Dictionary<string, BsonDocument> queryableEncryptionSchemas,
        string collectionName)
    {
        var isEncrypted = queryableEncryptionSchemas.ContainsKey(collectionName);
        if (!isEncrypted || _options?.QueryableEncryptionSchemaMode == QueryableEncryptionSchemaMode.ServerOnly)
            return null;

        return new CreateCollectionOptions { EncryptedFields = queryableEncryptionSchemas[collectionName] };
    }

    private void CheckServerQueryableEncryptionCompatible(
        string collectionName,
        BsonDocument clientEncryptedFields)
    {
        var collection = Database
            .ListCollections(new ListCollectionsOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", collectionName) })
            .FirstOrDefault();

        if (collection == null)
            throw new InvalidOperationException($"Collection '{collectionName}' can not be checked as it does not exist.");

        if (!collection.TryGetValue("options", out var options))
            throw new InvalidOperationException($"Collection '{collectionName}' can not be checked as it does not have options.");

        if ((options as BsonDocument)?.TryGetValue("encryptedFields", out var serverEncrypted) != true)
            throw new InvalidOperationException(
                $"Collection '{collectionName}' can not be checked as it does not have encryptedFields.");

        QueryableEncryptionSchemaChecker.CheckCompatibleSchemas(collectionName, serverEncrypted as BsonDocument, clientEncryptedFields);
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
        var usesQueryableEncryption = queryableEncryptionSchema.Count > 0 ||
                                      options?.QueryableEncryptionSchemaMode == QueryableEncryptionSchemaMode.ServerOnly;

        var preconfiguredMongoClient = (IMongoClient?)serviceProvider.GetService(typeof(IMongoClient)) ?? options?.MongoClient;
        if (preconfiguredMongoClient != null)
        {
            if (usesQueryableEncryption)
            {
                throw new InvalidOperationException(
                    "Cannot activate Queryable Encryption with a pre-configured MongoClient. Either use ConnectionString or ClientSettings options instead.");
            }

            return preconfiguredMongoClient;
        }

        var clientSettings = options?.ConnectionString != null
            ? MongoClientSettings.FromConnectionString(options.ConnectionString)
            : options?.ClientSettings?.Clone();

        if (clientSettings == null)
        {
            throw new InvalidOperationException(
                "Unable to create or obtain a MongoClient. Either provide ClientSettings, a ConnectionString, or a " +
                "MongoClient via the DbContextOptions, or register an implementation of IMongoClient with the ServiceProvider.");
        }

        if (usesQueryableEncryption)
        {
            if (options == null)
            {
                throw new InvalidOperationException("Queryable Encryption requires MongoOptions to be set.");
            }

            if (_options == null || _options.QueryableEncryptionSchemaMode == QueryableEncryptionSchemaMode.ClientOnly)
            {
                var missingDataKeyIdFields = QueryableEncryptionSchemaChecker.GetFieldsWithMissingDataKeyIds(queryableEncryptionSchema);
                if (missingDataKeyIdFields.Any())
                {
                    throw new InvalidOperationException(
                        "Queryable Encryption requires DataKeyId to be specified when operating in ClientOnly mode. " +
                        $"The following elements had no DataKeyId: {string.Join(", ", missingDataKeyIdFields.SelectMany(kv => kv.Value.Select(e => kv.Key + "." + e["path"])))}");
                }
            }

            QueryableEncryptionSettingsHelper.ApplyQueryableEncryptionSettings(options, clientSettings, queryableEncryptionSchema);
        }

        return new MongoClient(clientSettings);
    }
}
