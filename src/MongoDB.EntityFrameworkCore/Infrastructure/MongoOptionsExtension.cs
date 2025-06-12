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
using MongoDB.EntityFrameworkCore;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extensions for <see cref="IDbContextOptionsExtension"/>.
/// </summary>
public class MongoOptionsExtension : IDbContextOptionsExtension
{
    private const string MultipleConnectionConfigSpecifiedException =
        "Both ConnectionString and MongoClient were specified. Specify only one set of connection details.";

    private string? _loggableConnectionString;
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
    public MongoOptionsExtension(MongoOptionsExtension copyFrom)
    {
        ConnectionString = copyFrom.ConnectionString;
        DatabaseName = copyFrom.DatabaseName;
        MongoClient = copyFrom.MongoClient;
        _loggableConnectionString = SanitizeConnectionStringForLogging(ConnectionString);
    }

    /// <summary>
    /// Obtains the <see cref="DbContextOptionsExtensionInfo"/>.
    /// </summary>
    public virtual DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <summary>
    /// Obtains the current connection string.
    /// </summary>
    public string? ConnectionString { get; private set; }

    /// <summary>
    /// Specifies a connection string to use to connect to a MongoDB server.
    /// </summary>
    /// <param name="connectionString">The connection string (URI) to use.</param>
    /// <returns>The <see cref="MongoOptionsExtension"/> to continue chaining configuration.</returns>
    public virtual MongoOptionsExtension WithConnectionString(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var clone = Clone();
        clone.ConnectionString = connectionString;
        clone._loggableConnectionString = SanitizeConnectionStringForLogging(connectionString);
        return clone;
    }

    /// <summary>
    /// Obtains the current database name if one is specified, otherwise null.
    /// </summary>
    public string? DatabaseName { get; private set; }

    /// <summary>
    /// Specifies a database name to use on the MongoDB server.
    /// </summary>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <returns>The <see cref="MongoOptionsExtension"/> to continue chaining configuration.</returns>
    public virtual MongoOptionsExtension WithDatabaseName(string databaseName)
    {
        databaseName.ThrowArgumentExceptionIfNullOrEmpty();

        var clone = Clone();
        clone.DatabaseName = databaseName;
        return clone;
    }


    /// <summary>
    /// Obtains the current <see cref="IMongoClient"/> if one is specified, otherwise null.
    /// </summary>
    public IMongoClient? MongoClient { get; private set; }

    /// <summary>
    /// Specify a <see cref="IMongoClient"/> to use when communicating with the MongoDB server.
    /// </summary>
    /// <param name="mongoClient">The <see cref="IMongoClient"/> to use when communicating with the MongoDB server.</param>
    /// <returns>The <see cref="MongoOptionsExtension"/> to continue chaining configuration.</returns>
    public virtual MongoOptionsExtension WithMongoClient(IMongoClient mongoClient)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);

        var clone = Clone();
        clone.MongoClient = mongoClient;
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
        if (ConnectionString != null && MongoClient != null)
        {
            throw new InvalidOperationException(MultipleConnectionConfigSpecifiedException);
        }
    }

    private static string? SanitizeConnectionStringForLogging(string? connectionString)
    {
        if (connectionString == null) return null;

        var builder = new MongoUrlBuilder(connectionString);
        builder.Password = string.IsNullOrWhiteSpace(builder.Password) ? builder.Password : "redacted";
        return builder.ToString();
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private string? _logFragment;
        private int? _serviceProviderHash;
        private new MongoOptionsExtension Extension => (MongoOptionsExtension)base.Extension;

        /// <inheritdoc/>
        public override bool IsDatabaseProvider => true;

        /// <inheritdoc/>
        public override int GetServiceProviderHashCode()
        {
            _serviceProviderHash ??= HashCode.Combine(Extension.ConnectionString, Extension.DatabaseName);
            return _serviceProviderHash.Value;
        }

        /// <inheritdoc/>
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo otherInfo
               && Extension.ConnectionString == otherInfo.Extension.ConnectionString
               && Extension.MongoClient == otherInfo.Extension.MongoClient
               && Extension.DatabaseName == otherInfo.Extension.DatabaseName;

        /// <inheritdoc/>
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            AddDebugInfo(debugInfo, nameof(ConnectionString), Extension.ConnectionString);
            AddDebugInfo(debugInfo, nameof(MongoClientSettings), Extension.MongoClient);
            AddDebugInfo(debugInfo, nameof(DatabaseName), Extension.DatabaseName);
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

            if (Extension.MongoClient != null)
            {
                builder.Append("MongoClient=").Append(Extension.MongoClient).Append(' ');
            }

            builder.Append("DatabaseName=").Append(Extension.DatabaseName).Append(' ');
            return builder.ToString();
        }
    }
}
