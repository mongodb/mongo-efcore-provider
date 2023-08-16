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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata.Conventions;
using MongoDB.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Factories;
using MongoDB.EntityFrameworkCore.Storage;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// MongoDB-specific extension methods for <see cref="IServiceCollection" />.
/// </summary>
public static class MongoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the given Entity Framework <see cref="DbContext" /> as a service in the <see cref="IServiceCollection" />
    /// and configures it to connect to an MongoDB database.
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
            (serviceProvider, options) =>
            {
                optionsAction?.Invoke(options);
                options.UseMongoDB(connectionString, databaseName, mongoOptionsAction);
            });

    /// <summary>
    /// Registers the given Entity Framework <see cref="DbContext" /> as a service in the <see cref="IServiceCollection" />
    /// and configures it to connect to an MongoDB database.
    /// </summary>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="mongoClient">The <see cref="IMongoClient"/> to use to connect to the MongoDB server.</param>
    /// <param name="databaseName">The database name on the server.</param>
    /// <param name="mongoOptionsAction">An optional action to allow additional MongoDB-specific configuration.</param>
    /// <param name="optionsAction">An optional action to configure the <see cref="DbContextOptions" /> for the context.</param>
    /// <typeparam name="TContext">The type of context to be registered.</typeparam>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddMongoDB<TContext>(
        this IServiceCollection serviceCollection,
        IMongoClient mongoClient,
        string databaseName,
        Action<MongoDbContextOptionsBuilder>? mongoOptionsAction = null,
        Action<DbContextOptionsBuilder>? optionsAction = null)
        where TContext : DbContext
        => serviceCollection.AddDbContext<TContext>(
            (serviceProvider, options) =>
            {
                optionsAction?.Invoke(options);
                options.UseMongoDB(mongoClient, databaseName, mongoOptionsAction);
            });

    /// <summary>
    /// Adds the services required by the MongoDB provider for Entity Framework to an <see cref="IServiceCollection" />.
    /// </summary>
    /// <remarks>You probably meant to use <see cref="AddMongoDB{TContext}" /> instead.</remarks>
    /// <param name="serviceCollection">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddEntityFrameworkMongoDB(this IServiceCollection serviceCollection)
    {
        var builder = new EntityFrameworkServicesBuilder(serviceCollection)
            .TryAdd<LoggingDefinitions, MongoLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<MongoOptionsExtension>>()
            .TryAdd<IDatabase, MongoDatabaseWrapper>()
            .TryAdd<IModelValidator, MongoModelValidator>()
            .TryAdd<IProviderConventionSetBuilder, MongoConventionSetBuilder>()
            .TryAdd<IQueryContextFactory, MongoQueryContextFactory>()
            .TryAdd<ITypeMappingSource, MongoTypeMappingSource>()
            .TryAdd<IQueryCompilationContextFactory, MongoQueryCompilationContextFactory>()
            .TryAdd<IQueryableMethodTranslatingExpressionVisitorFactory, MongoQueryableMethodTranslatingExpressionVisitorFactory>()
            .TryAdd<IShapedQueryCompilingExpressionVisitorFactory, MongoShapedQueryCompilingExpressionVisitorFactory>()
            .TryAddProviderSpecificServices(
                b => b
                    .TryAddScoped<IMongoClientWrapper, MongoClientWrapper>()
            );

        builder.TryAddCoreServices();

        return serviceCollection;
    }
}
