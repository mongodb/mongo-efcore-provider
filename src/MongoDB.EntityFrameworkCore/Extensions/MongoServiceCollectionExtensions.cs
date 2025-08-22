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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;
using MongoDB.EntityFrameworkCore.Query.Factories;
using MongoDB.EntityFrameworkCore.Query.Visitors.Dependencies;
using MongoDB.EntityFrameworkCore.Serializers;
using MongoDB.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.EntityFrameworkCore.ValueGeneration;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// MongoDB-specific extension methods for <see cref="IServiceCollection" />.
/// </summary>
public static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the given Entity Framework <see cref="DbContext" /> as a service in the <see cref="IServiceCollection" />
    /// and configures it to connect to a MongoDB database.
    /// </summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="connectionString">The connection string of the MongoDB server to connect to.</param>
    /// <param name="databaseName">The database name on the server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <param name="optionsAction">An optional action to configure the <see cref="DbContextOptions" /> for the context.</param>
    /// <typeparam name="TContext">The type of context to be registered.</typeparam>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddMongoDB<TContext>(
        this IServiceCollection serviceCollection,
        string connectionString,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null,
        Action<DbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => serviceCollection.AddDbContext<TContext>(
            (_, options) =>
            {
                optionsAction?.Invoke(options);
                options.UseMongoDB(connectionString, databaseName, mongoOptionsAction);
            });

    /// <summary>
    /// Registers the given Entity Framework <see cref="DbContext" /> as a service in the <see cref="IServiceCollection" />
    /// and configures it to connect to a MongoDB database.
    /// </summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="connectionString">The connection string of the MongoDB server to connect to including a default database name.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <param name="optionsAction">An optional action to configure the <see cref="DbContextOptions" /> for the context.</param>
    /// <typeparam name="TContext">The type of context to be registered.</typeparam>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddMongoDB<TContext>(
        this IServiceCollection serviceCollection,
        string connectionString,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null,
        Action<DbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => serviceCollection.AddDbContext<TContext>(
            (_, options) =>
            {
                optionsAction?.Invoke(options);
                options.UseMongoDB(connectionString, mongoOptionsAction);
            });

    /// <summary>
    /// Registers the given Entity Framework <see cref="DbContext" /> as a service in the <see cref="IServiceCollection" />
    /// and configures it to connect to a MongoDB database.
    /// </summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="mongoClient">The <see cref="IMongoClient"/> to use to connect to the MongoDB server.</param>
    /// <param name="databaseName">The database name on the server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <param name="optionsAction">An optional action to configure the <see cref="DbContextOptions" /> for the context.</param>
    /// <typeparam name="TContext">The type of context to be registered.</typeparam>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    /// <remarks>
    /// It is recommended that you use alternative UseMongoDB overloads that take either a connection string
    /// or <see cref="MongoClientSettings"/> to ensure the MongoDB EF Provider can correctly configure and
    /// dispose of its own <see cref="MongoClient"/> instances.
    /// </remarks>
    public static IServiceCollection AddMongoDB<TContext>(
        this IServiceCollection serviceCollection,
        IMongoClient mongoClient,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null,
        Action<DbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => serviceCollection.AddDbContext<TContext>(
            (_, options) =>
            {
                optionsAction?.Invoke(options);
                options.UseMongoDB(mongoClient, databaseName, mongoOptionsAction);
            });

    /// <summary>
    /// Registers the given Entity Framework <see cref="DbContext" /> as a service in the <see cref="IServiceCollection" />
    /// and configures it to connect to a MongoDB database.
    /// </summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="mongoClientSettings">The <see cref="MongoClientSettings"/> to use to connect to the MongoDB server.</param>
    /// <param name="databaseName">The database name on the server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <param name="optionsAction">An optional action to configure the <see cref="DbContextOptions" /> for the context.</param>
    /// <typeparam name="TContext">The type of context to be registered.</typeparam>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddMongoDB<TContext>(
        this IServiceCollection serviceCollection,
        MongoClientSettings mongoClientSettings,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null,
        Action<DbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => serviceCollection.AddDbContext<TContext>(
            (_, options) =>
            {
                optionsAction?.Invoke(options);
                options.UseMongoDB(mongoClientSettings, databaseName, mongoOptionsAction);
            });

    /// <summary>
    /// Adds the services required by the MongoDB provider for Entity Framework to an <see cref="IServiceCollection" />.
    /// </summary>
    /// <remarks>You probably meant to use <see cref="AddMongoDB{TContext}(Microsoft.Extensions.DependencyInjection.IServiceCollection,string,string,System.Action{MongoDB.EntityFrameworkCore.Infrastructure.MongoDbContextOptionsBuilder}?,System.Action{Microsoft.EntityFrameworkCore.DbContextOptionsBuilder}?)" /> instead.</remarks>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddEntityFrameworkMongoDB(this IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        new EntityFrameworkServicesBuilder(serviceCollection)
            .TryAdd<LoggingDefinitions, MongoLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<MongoOptionsExtension>>()
            .TryAdd<IDatabase, MongoDatabaseWrapper>()
            .TryAdd<IDbContextTransactionManager, MongoTransactionManager>()
            .TryAdd<IModelValidator, MongoModelValidator>()
            .TryAdd<IProviderConventionSetBuilder, MongoConventionSetBuilder>()
            .TryAdd<IValueGeneratorSelector, MongoValueGeneratorSelector>()
            .TryAdd<IDatabaseCreator, MongoDatabaseCreator>()
            .TryAdd<IQueryContextFactory, MongoQueryContextFactory>()
            .TryAdd<ITypeMappingSource, MongoTypeMappingSource>()
            .TryAdd<IValueConverterSelector, MongoValueConverterSelector>()
            .TryAdd<IQueryTranslationPreprocessorFactory, MongoQueryTranslationPreprocessorFactory>()
            .TryAdd<IQueryCompilationContextFactory, MongoQueryCompilationContextFactory>()
            .TryAdd<IQueryTranslationPostprocessorFactory, MongoQueryTranslationPostprocessorFactory>()
            .TryAdd<IQueryableMethodTranslatingExpressionVisitorFactory, MongoQueryableMethodTranslatingExpressionVisitorFactory>()
            .TryAdd<IShapedQueryCompilingExpressionVisitorFactory, MongoShapedQueryCompilingExpressionVisitorFactory>()
            .TryAdd<IModelRuntimeInitializer, MongoModelRuntimeInitializer>()
            .TryAddProviderSpecificServices(
                b => b
                    .TryAddScoped<IQueryableEncryptionSchemaProvider, QueryableEncryptionSchemaProvider>()
                    .TryAddScoped<IMongoClientWrapper, MongoClientWrapper>()
                    .TryAddSingleton<MongoShapedQueryCompilingExpressionVisitorDependencies,
                        MongoShapedQueryCompilingExpressionVisitorDependencies>()
                    .TryAddSingleton(new BsonSerializerFactory())
            )
            .TryAddCoreServices();

        return serviceCollection;
    }
}
