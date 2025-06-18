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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;

internal static class SingleEntityDbContext
{
    private static readonly ConcurrentDictionary<object, DbContextOptions> CollectionOptionsCache = new();

    private static DbContextOptions<SingleEntityDbContext<T2>> GetOrCreateOptionsBuilder<T1, T2>(IMongoCollection<T1> collection)
        where T1 : class where T2 : class
    {
        if (CollectionOptionsCache.TryGetValue(collection, out var existingOptions))
            return (DbContextOptions<SingleEntityDbContext<T2>>)existingOptions;

        var newOptions = new DbContextOptionsBuilder<SingleEntityDbContext<T2>>()
            .UseMongoDB(collection.Database.Client, collection.Database.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        CollectionOptionsCache.TryAdd(collection, newOptions);

        return newOptions;
    }

    public static SingleEntityDbContext<T> Create<T>(
        IMongoCollection<T> collection,
        Action<ModelBuilder>? modelBuilderAction = null,
        Action<ModelConfigurationBuilder>? configBuilderAction = null) where T : class =>
        new(GetOrCreateOptionsBuilder<T, T>(collection), collection.CollectionNamespace.CollectionName, modelBuilderAction, configBuilderAction);

    public static SingleEntityDbContext<T2> Create<T1, T2>(
        IMongoCollection<T1> collection,
        Action<ModelBuilder>? modelBuilderAction = null,
        Action<ModelConfigurationBuilder>? configBuilderAction = null) where T1 : class where T2 : class
        => new(GetOrCreateOptionsBuilder<T1, T2>(collection), collection.CollectionNamespace.CollectionName, modelBuilderAction, configBuilderAction);
}

internal class SingleEntityDbContext<T>(
    DbContextOptions options,
    string collectionName,
    Action<ModelBuilder>? modelBuilderAction = null,
    Action<ModelConfigurationBuilder>? configBuilderAction = null)
    : DbContext(options)
    where T : class
{
    public DbSet<T> Entities { get; init; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<T>().ToCollection(collectionName);
        modelBuilderAction?.Invoke(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configBuilderAction?.Invoke(configurationBuilder);
    }
}
