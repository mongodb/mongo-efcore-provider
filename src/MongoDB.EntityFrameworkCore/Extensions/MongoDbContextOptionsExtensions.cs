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
using MongoDB.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extension methods for <see cref="DbContextOptionsBuilder" />.
/// </summary>
public static class MongoDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the <see cref="DbContext"/> subclass to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">The connection string used to identify the MongoDB server to use.</param>
    /// <param name="databaseName">The name of the database to use on the MongoDB server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <typeparam name="TContext">The type of context to be configured.</typeparam>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseMongo<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseMongo(
            (DbContextOptionsBuilder)optionsBuilder,
            connectionString,
            databaseName,
            mongoOptionsAction);

    /// <summary>
    /// Configures the context to connect to a MongoDB database using the <see cref="DbContextOptionsBuilder"/>.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="connectionString">The connection string used to identify the MongoDB server to use.</param>
    /// <param name="databaseName">The name of the database to use on the MongoDB server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder UseMongo(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        var extension = (optionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithConnectionString(connectionString)
            .WithDatabaseName(databaseName);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        mongoOptionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }

    /// <summary>
    /// Configures the <see cref="DbContext"/> subclass to connect to a MongoDB database.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="mongoClient">The connections string used to identify the MongoDB server to use.</param>
    /// <param name="databaseName">The name of the MongoDB database to use on the server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <typeparam name="TContext">The type of context to be configured.</typeparam>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder<TContext> UseMongo<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        IMongoClient mongoClient,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseMongo(
            (DbContextOptionsBuilder)optionsBuilder,
            mongoClient,
            databaseName,
            mongoOptionsAction);

    /// <summary>
    /// Configures the context to connect to a MongoDB database using the <see cref="DbContextOptionsBuilder"/>.
    /// </summary>
    /// <param name="optionsBuilder">The builder being used to configure the context.</param>
    /// <param name="mongoClient">The connections string used to identify the MongoDB server to use.</param>
    /// <param name="databaseName">The name of the MongoDB database to use on the server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <returns>The options builder so that further configuration can be chained.</returns>
    public static DbContextOptionsBuilder UseMongo(
        this DbContextOptionsBuilder optionsBuilder,
        IMongoClient mongoClient,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);

        var extension = (optionsBuilder.Options.FindExtension<MongoOptionsExtension>()
                         ?? new MongoOptionsExtension())
            .WithMongoClient(mongoClient)
            .WithDatabaseName(databaseName);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        mongoOptionsAction?.Invoke(new MongoDbContextOptionsBuilder(optionsBuilder));
        return optionsBuilder;
    }
}
