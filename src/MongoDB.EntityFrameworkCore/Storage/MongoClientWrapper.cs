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
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
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
    // Telemetry keys on this exact string; do not change it.
    private const string LibraryName = "efcore";

    private static readonly LibraryInfo ProviderLibraryInfo = CreateLibraryInfo();

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

        // A native entity reducer (First/Single/…) has Cardinality != Enumerable but still carries a
        // NativePipeline (a synthesized $limit) — it must flow into the NativePipeline block below so EF
        // Core's base cardinality reduction runs over the returned cursor enumerable. ExecuteScalar is
        // only for the driver-LINQ scalar/reducer path, which has no NativePipeline.
        if (executableQuery.Cardinality != ResultCardinality.Enumerable && executableQuery.NativePipeline is null)
            return ExecuteScalar<T>(executableQuery);

        if (executableQuery.NativePipeline is { } stages)
        {
            // The native pipeline is already known here, so set the log action before issuing the aggregate.
            // This mirrors the driver-LINQ path (whose stages are captured at build time) and ensures the MQL
            // is logged even when execution against the server throws. Unlike the driver-LINQ path, the native
            // stages are logged directly (the driver Provider was never asked to translate, so its LoggedStages
            // would be empty) — this surfaces the real $match/$sort/$lookup pipeline in the MQL log.
            var loggedStages = stages as BsonDocument[] ?? stages.ToArray();
            log = () => _commandLogger.ExecutedMqlQuery(executableQuery.CollectionNamespace, loggedStages);
            if (executableQuery.Streaming)
            {
                Debug.Assert(executableQuery.OutputSerializer != null, "Streaming native path requires output serializer.");

                // One-pass "deserialize IS materialize": the custom output serializer runs the compiled EF
                // materializer off the cursor's own IBsonReader, so the Aggregate cursor yields finished
                // (T == the shaped entity) instances directly — a single forward pass, no RawBsonDocument
                // wrapper + second materialization pass. T is the shaped result type, so the supplied
                // serializer is an IBsonSerializer<T>.
                var entityCollection = Database.GetCollection<BsonDocument>(executableQuery.CollectionNamespace.CollectionName);
                PipelineDefinition<BsonDocument, BsonDocument> basePipe = loggedStages;
                var typedPipeline = basePipe.As((IBsonSerializer<T>)executableQuery.OutputSerializer);
                var typedCursor = executableQuery.Session is { } typedSession
                    ? entityCollection.Aggregate(typedSession, typedPipeline)
                    : entityCollection.Aggregate(typedPipeline);
                return typedCursor.ToEnumerable();
            }

            var collection = Database.GetCollection<BsonDocument>(executableQuery.CollectionNamespace.CollectionName);
            PipelineDefinition<BsonDocument, BsonDocument> pipeline = loggedStages;
            var cursor = executableQuery.Session is { } session
                ? collection.Aggregate(session, pipeline)
                : collection.Aggregate(pipeline);
            return (IEnumerable<T>)cursor.ToEnumerable();
        }

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

        if (result is null)
        {
            var underlyingType = Nullable.GetUnderlyingType(typeof(T));
            if (underlyingType != null && IsSumQuery(executableQuery.Query))
            {
                // The driver's $group over zero input documents yields zero output documents, so a
                // nullable Sum() comes back as default(T) (null). LINQ defines Sum() over an empty set
                // as 0 even for nullable projections (unlike Average(), which stays undefined), so
                // that empty-cursor result is coerced to the numeric zero here.
                result = (T)Convert.ChangeType(0, underlyingType);
            }
        }

        return [result];
    }

    private static bool IsSumQuery(Expression query)
        => query is MethodCallExpression { Method.Name: "Sum", Method.DeclaringType: var declaringType }
           && declaringType == typeof(Queryable);

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

            preconfiguredMongoClient.AppendMetadata(ProviderLibraryInfo);
            return preconfiguredMongoClient;
        }

        var mongoClientSettings = MongoClientSettingsHelper.CreateSettings(options, queryableEncryptionSchema);

        // Seeding reaches the first handshake, including the cluster's monitoring connections, whereas
        // AppendMetadata only affects connections opened after the call. Appending as well covers the
        // case where the caller declared a library of their own, which the seeding must not overwrite.
        mongoClientSettings.LibraryInfo ??= ProviderLibraryInfo;
        var mongoClient = new MongoClient(mongoClientSettings);
        mongoClient.AppendMetadata(ProviderLibraryInfo);
        return mongoClient;
    }

    private static LibraryInfo CreateLibraryInfo()
    {
        var assembly = typeof(MongoClientWrapper).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString();

        // Source Link appends the commit it was built from, which is not part of the version.
        var separatorIndex = version?.IndexOf('+') ?? -1;
        if (separatorIndex != -1)
        {
            version = version![..separatorIndex];
        }

        return new LibraryInfo(LibraryName, version);
    }
}
