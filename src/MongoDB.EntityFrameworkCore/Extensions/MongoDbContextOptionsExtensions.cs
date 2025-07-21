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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extension methods for <see cref="DbContextOptionsBuilder" />.
/// </summary>
public static class MongoDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">The connection string used to connect to MongoDB.</param>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <typeparam name="TContext">The type of context to be configured.</typeparam>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseMongoDB<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseMongoDB(
            (DbContextOptionsBuilder)optionsBuilder,
            connectionString,
            databaseName,
            optionsAction);

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">The connection string used to connect to MongoDB.</param>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder UseMongoDB(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        ConfigureWarnings(optionsBuilder);

        var extension = (optionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithConnectionString(connectionString)
            .WithDatabaseName(databaseName);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        optionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">The connection string used to connect to MongoDB which must include a database name.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <typeparam name="TContext">The type of context to be configured.</typeparam>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseMongoDB<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseMongoDB(
            (DbContextOptionsBuilder)optionsBuilder,
            connectionString,
            optionsAction);

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">The connection string used to connect to MongoDB which must include a database name.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder UseMongoDB(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connectionString);

        ConfigureWarnings(optionsBuilder);

        var extension = (optionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithConnectionString(connectionString);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        optionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="mongoClient">The pre-configured <see cref="IMongoClient"/> for connecting to the server.</param>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <typeparam name="TContext">The type of context to be configured.</typeparam>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    /// <remarks>
    /// It is recommended that you use alternative UseMongoDB overloads that take either a connection string
    /// or <see cref="MongoClientSettings"/> to ensure the MongoDB EF Core Provider can correctly configure and
    /// dispose of its own <see cref="MongoClient"/> instances.
    /// </remarks>
    public static DbContextOptionsBuilder<TContext> UseMongoDB<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        IMongoClient mongoClient,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseMongoDB(
            (DbContextOptionsBuilder)optionsBuilder,
            mongoClient,
            databaseName,
            optionsAction);

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="mongoClient">The pre-configured <see cref="IMongoClient"/> for connecting to the server.</param>
    /// <param name="databaseName">The name of the MongoDB database to use.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    /// <remarks>
    /// It is recommended that you use alternative UseMongoDB overloads that take either a connection string
    /// or <see cref="MongoClientSettings"/> to ensure the MongoDB EF Core Provider can correctly configure and
    /// dispose of its own <see cref="MongoClient"/> instances.
    /// </remarks>
    public static DbContextOptionsBuilder UseMongoDB(
        this DbContextOptionsBuilder optionsBuilder,
        IMongoClient mongoClient,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        ConfigureWarnings(optionsBuilder);

        var extension = (optionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithMongoClient(mongoClient)
            .WithDatabaseName(databaseName);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        optionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="mongoClientSettings">The <see cref="MongoClientSettings"/> to use as a base when connecting to the server.</param>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <typeparam name="TContext">The type of context to be configured.</typeparam>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    /// <remarks>
    /// In some scenarios the MongoDB EF Core Provider may need to change some client settings before connecting depending on
    /// what features are configured.
    /// </remarks>
    public static DbContextOptionsBuilder<TContext> UseMongoDB<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        MongoClientSettings mongoClientSettings,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseMongoDB(
            (DbContextOptionsBuilder)optionsBuilder,
            mongoClientSettings,
            databaseName,
            optionsAction);

    /// <summary>
    /// Configures the context to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="mongoClientSettings">The <see cref="MongoClientSettings"/> to use as a base when connecting to the server.</param>
    /// <param name="databaseName">The name of the database to use.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    /// <remarks>
    /// In some scenarios the MongoDB EF Core Provider may need to change some client settings before connecting depending on
    /// what features are configured.
    /// </remarks>
    public static DbContextOptionsBuilder UseMongoDB(
        this DbContextOptionsBuilder optionsBuilder,
        MongoClientSettings mongoClientSettings,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(mongoClientSettings);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        ConfigureWarnings(optionsBuilder);

        var extension = (optionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithClientSettings(mongoClientSettings)
            .WithDatabaseName(databaseName);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        optionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the context to connect to a MongoDB database using the <see cref="DbContextOptionsBuilder"/>.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="extension">The <see cref="MongoOptionsExtension"/> to configure.</param>
    /// <param name="optionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder UseMongoDB(
        this DbContextOptionsBuilder optionsBuilder,
        MongoOptionsExtension extension,
        Action<MongoDbContextOptionsBuilder>? optionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(extension);

        ConfigureWarnings(optionsBuilder);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        optionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    private static void ConfigureWarnings(DbContextOptionsBuilder optionsBuilder)
    {
        var coreOptionsExtension
            = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()
              ?? new CoreOptionsExtension();

        coreOptionsExtension = coreOptionsExtension.WithWarningsConfiguration(
            coreOptionsExtension.WarningsConfiguration.TryWithExplicit(
                MongoEventId.ColumnAttributeWithTypeUsed, WarningBehavior.Throw));

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(coreOptionsExtension);
    }
}
