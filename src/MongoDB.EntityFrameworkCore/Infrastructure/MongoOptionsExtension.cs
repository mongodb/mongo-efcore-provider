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
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extensions for <see cref="IDbContextOptionsExtension"/>.
/// </summary>
public class MongoOptionsExtension : IDbContextOptionsExtension
{
    const string MultipleConnectionConfigSpecifiedException =
        "Both ConnectionString and MongoClient were specified. Specify only one set of connection details.";

    private string? _connectionString;
    private string? _databaseName;
    private string? _loggableConnectionString;
    private IMongoClient? _mongoClient;
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>
    /// Creates a <see cref="MongoOptionsExtension"/>.
    /// </summary>
    public MongoOptionsExtension()
    {
    }

    /// <summary>
    /// Creates a <see cref="MongoOptionsExtension"/> by copying from an existing instance.
    /// </summary>
    protected MongoOptionsExtension(MongoOptionsExtension copyFrom)
    {
        _connectionString = copyFrom._connectionString;
        _databaseName = copyFrom._databaseName;
        _mongoClient = copyFrom._mongoClient;
        _loggableConnectionString = SanitizeConnectionStringForLogging(_connectionString);
    }

    /// <summary>
    /// Obtains the <see cref="DbContextOptionsExtensionInfo"/>.
    /// </summary>
    public virtual DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <summary>
    /// Obtains the current connection string.
    /// </summary>
    public string? ConnectionString => _connectionString;

    /// <summary>
    /// Specifies a connection string to use to connect to a MongoDB server.
    /// </summary>
    /// <param name="connectionString">The connection string (URI) to use.</param>
    /// <returns>The <see cref="MongoOptionsExtension"/> to continue chaining configuration.</returns>
    public virtual MongoOptionsExtension WithConnectionString(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        if (_mongoClient != null)
        {
            throw new InvalidOperationException(MultipleConnectionConfigSpecifiedException);
        }

        var clone = Clone();
        clone._connectionString = connectionString;
        clone._loggableConnectionString = SanitizeConnectionStringForLogging(connectionString);
        return clone;
    }

    /// <summary>
    /// Obtains the current database name if one is specified, otherwise null.
    /// </summary>
    public string? DatabaseName => _databaseName;

    /// <summary>
    /// Specifies a database name to use on the MongoDB server.
    /// </summary>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <returns>The <see cref="MongoOptionsExtension"/> to continue chaining configuration.</returns>
    public virtual MongoOptionsExtension WithDatabaseName(string databaseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        var clone = Clone();
        clone._databaseName = databaseName;
        return clone;
    }

    /// <summary>
    /// Obtains the current <see cref="IMongoClient"/> if one is specified, otherwise null.
    /// </summary>
    public IMongoClient? MongoClient => _mongoClient;

    /// <summary>
    /// Specify a <see cref="IMongoClient"/> to use when communicating with the MongoDB server.
    /// </summary>
    /// <param name="mongoClient">The <see cref="IMongoClient"/> to use when communicating with the MongoDB server.</param>
    /// <returns>The <see cref="MongoOptionsExtension"/> to continue chaining configuration.</returns>
    public virtual MongoOptionsExtension WithMongoClient(IMongoClient mongoClient)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);

        if (_connectionString != null)
        {
            throw new InvalidOperationException(MultipleConnectionConfigSpecifiedException);
        }

        var clone = Clone();
        clone._mongoClient = mongoClient;
        return clone;
    }

    /// <summary>
    /// Clones the current <see cref="MongoOptionsExtension"/>.
    /// </summary>
    /// <returns>A new clone.</returns>
    protected virtual MongoOptionsExtension Clone() => new(this);

    /// <inheritdoc />
    public virtual void ApplyServices(IServiceCollection services) => services.AddEntityFrameworkMongoDB();

    /// <inheritdoc />
    public virtual void Validate(IDbContextOptions options)
    {
    }

    private static string? SanitizeConnectionStringForLogging(string? connectionString)
    {
        if (connectionString == null) return null;

        var uriBuilder = new UriBuilder(connectionString);
        uriBuilder.Password = string.IsNullOrWhiteSpace(uriBuilder.Password) ? uriBuilder.Password : "redacted";
        return uriBuilder.ToString();
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        private string? _logFragment;
        private int? _serviceProviderHash;
        private new MongoOptionsExtension Extension => (MongoOptionsExtension)base.Extension;

        public ExtensionInfo(IDbContextOptionsExtension extension)
            : base(extension)
        {
        }

        /// <inheritdoc/>
        public override bool IsDatabaseProvider => true;

        /// <inheritdoc/>
        public override int GetServiceProviderHashCode()
        {
            _serviceProviderHash ??= HashCode.Combine(Extension._connectionString, Extension._databaseName);
            return _serviceProviderHash.Value;
        }

        /// <inheritdoc/>
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo otherInfo
               && Extension._connectionString == otherInfo.Extension._connectionString
               && Extension._mongoClient == otherInfo.Extension._mongoClient
               && Extension._databaseName == otherInfo.Extension._databaseName;

        /// <inheritdoc/>
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            AddDebugInfo(debugInfo, nameof(ConnectionString), Extension._connectionString);
            AddDebugInfo(debugInfo, nameof(MongoClientSettings), Extension._mongoClient);
            AddDebugInfo(debugInfo, nameof(DatabaseName), Extension._databaseName);
        }

        /// <inheritdoc/>
        public override string LogFragment
        {
            get => _logFragment ??= CreateLogFragment();
        }

        private static void AddDebugInfo(IDictionary<string, string> debugInfo, string key, object? value)
        {
            debugInfo["Mongo:" + key] = (value?.GetHashCode() ?? 0L).ToString(CultureInfo.InvariantCulture);
        }

        private string CreateLogFragment()
        {
            var builder = new StringBuilder();
            if (Extension._loggableConnectionString != null)
            {
                builder.Append("ConnectionString=").Append(Extension._loggableConnectionString).Append(' ');
            }

            if (Extension._mongoClient != null)
            {
                builder.Append("MongoClient=").Append(Extension._mongoClient).Append(' ');
            }

            builder.Append("DatabaseName=").Append(Extension._databaseName).Append(' ');
            return builder.ToString();
        }
    }
}
