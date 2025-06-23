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
    private string _databaseName;
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
        var didCreateNewDatabase = !DatabaseExists();
        var existingCollectionNames = Database.ListCollectionNames().ToList();

        foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!existingCollectionNames.Contains(collectionName))
            {
                try
                {
                    Database.CreateCollection(collectionName);
                    existingCollectionNames.Add(collectionName);
                }
                catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                {
                    // Ignore collection already exists in cases of concurrent creation
                }
            }

            var indexManager = Database.GetCollection<BsonDocument>(entityType.GetCollectionName()).Indexes;
            var existingIndexNames = indexManager.List().ToList().Select(i => i["name"].AsString).ToList();
            CreateIndexes(entityType, indexManager, existingIndexNames, []);
        }

        return didCreateNewDatabase;
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

        foreach (var entityType in model.Model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var collectionName = entityType.GetCollectionName();
            if (!existingCollectionNames.Contains(collectionName))
            {
                try
                {
                    await Database.CreateCollectionAsync(collectionName, null, cancellationToken).ConfigureAwait(false);
                }
                catch (MongoCommandException ex) when (ex.Message.Contains("already exists"))
                {
                    // Ignore collection already exists in cases of concurrent creation
                }
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

    private IMongoClient GetOrCreateMongoClient(MongoOptionsExtension? options, IServiceProvider serviceProvider)
    {
        var queryableEncryptionSchema = _schemaProvider.GetQueryableEncryptionSchema();
        var usesQueryableEncryption = queryableEncryptionSchema.Count > 0;

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

        if (usesQueryableEncryption)
        {
            ApplyQueryableEncryptionSettings(options, clientSettings, queryableEncryptionSchema);
        }

        return new MongoClient(clientSettings);
    }

    private void ApplyQueryableEncryptionSettings(MongoOptionsExtension? options, MongoClientSettings clientSettings,
        Dictionary<string, BsonDocument> queryableEncryptionSchema)
    {
        var extraOptions = options?.CryptProvider switch
        {
            CryptProvider.AutoEncryptSharedLibrary => ExtraOptionsForCryptShared(options.CryptProviderPath!),
            CryptProvider.Mongocryptd => ExtraOptionsForMongocryptd(options.CryptProviderPath!),
            _ => new Dictionary<string, object>()
        };

        if (clientSettings.AutoEncryptionOptions?.ExtraOptions != null)
        {
            foreach (var kvp in clientSettings.AutoEncryptionOptions.ExtraOptions)
            {
                extraOptions[kvp.Key] = kvp.Value;
            }
        }

        var keyVaultNamespace = clientSettings.AutoEncryptionOptions?.KeyVaultNamespace ?? options?.KeyVaultNamespace ??
            throw new InvalidOperationException(
                "No KeyVaultNamespace specified for Queryable Encryption. Either specify it via DbContextOptions or MongoClientSettings.");

        var kmsProviders = clientSettings.AutoEncryptionOptions?.KmsProviders ?? options?.KmsProviders ??
            throw new InvalidOperationException(
                "No KmsProviders specified for Queryable Encryption. Either specify it via DbContextOptions or MongoClientSettings.");

        clientSettings.AutoEncryptionOptions = new AutoEncryptionOptions(
            keyVaultNamespace,
            kmsProviders,
            encryptedFieldsMap: queryableEncryptionSchema.ToDictionary(d => _options!.DatabaseName + "." + d.Key, d => d.Value),
            extraOptions: extraOptions);
    }

    private static Dictionary<string, object> ExtraOptionsForCryptShared(string cryptSharedLibPath) =>
        new() { { "cryptSharedLibPath", cryptSharedLibPath }, { "cryptSharedLibRequired", true } };

    private static Dictionary<string, object> ExtraOptionsForMongocryptd(string mongocryptdSpawnPath) =>
        new() { { "mongocryptdSpawnPath", mongocryptdSpawnPath } };
}
