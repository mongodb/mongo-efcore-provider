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

using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Diagnostics;
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
    private readonly IQueryableEncryptionSchemaProvider _schemaProvider;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Database.Command> _commandLogger;

    private IMongoClient? _client;
    private IMongoDatabase? _database;
    private string? _databaseName;

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
    public IMongoClient Client => _client ??= GetOrCreateMongoClient(_options, _serviceProvider);

    /// <inheritdoc />
    public IMongoDatabase Database => _database ??= Client.GetDatabase(_databaseName);

    /// <inheritdoc />
    public string DatabaseName
    {
        get
        {
            if (_databaseName is null)
            {
                _ = Client;
            }

            return _databaseName!;
        }
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
