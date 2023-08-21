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
    private static readonly ConcurrentDictionary<object, DbContextOptions> __collectionOptionsCache = new();

    private static DbContextOptions<SingleEntityDbContext<T>> GetOrCreateOptionsBuilder<T>(IMongoCollection<T> collection) where T:class
    {
        if (__collectionOptionsCache.TryGetValue(collection, out var existingOptions))
            return  (DbContextOptions<SingleEntityDbContext<T>>) existingOptions;

        var newOptions = new DbContextOptionsBuilder<SingleEntityDbContext<T>>()
            .UseMongoDB(collection.Database.Client, collection.Database.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        __collectionOptionsCache.TryAdd(collection, newOptions);

        return newOptions;
    }

    public static SingleEntityDbContext<T> Create<T>(IMongoCollection<T> collection, Action<ModelBuilder>? modelBuilderAction = null) where T:class =>
        new (GetOrCreateOptionsBuilder(collection), collection.CollectionNamespace.CollectionName, modelBuilderAction);

    private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int __count;

        public object Create(DbContext context, bool designTime)
            => Interlocked.Increment(ref __count);
    }
}

internal class SingleEntityDbContext<T> : DbContext where T:class
{
    private readonly string _collectionName;
    private readonly Action<ModelBuilder>? _modelBuilderAction;

    public DbSet<T> Entitites { get; init; }

    public SingleEntityDbContext(DbContextOptions options, string collectionName, Action<ModelBuilder>? modelBuilderAction)
        : base(options)
    {
        _collectionName = collectionName;
        _modelBuilderAction = modelBuilderAction;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<T>().ToCollection(_collectionName);
        _modelBuilderAction?.Invoke(modelBuilder);
    }
}
