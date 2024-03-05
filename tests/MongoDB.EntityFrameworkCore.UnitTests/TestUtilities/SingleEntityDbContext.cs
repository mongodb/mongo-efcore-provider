﻿/* Copyright 2023-present MongoDB Inc.
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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

internal static class SingleEntityDbContext
{
    private static DbContextOptions<SingleEntityDbContext<T>> CreateOptionsBuilder<T>() where T : class
        => new DbContextOptionsBuilder<SingleEntityDbContext<T>>()
            .UseMongoDB("mongodb://localhost:27017", "UnitTests")
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

    public static SingleEntityDbContext<T> Create<T>(Action<ModelBuilder>? modelBuilderAction = null) where T : class =>
        new(CreateOptionsBuilder<T>(), modelBuilderAction);

    private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int __count;

        public object Create(DbContext context, bool designTime)
            => Interlocked.Increment(ref __count);
    }
}

internal class SingleEntityDbContext<T> : DbContext where T : class
{
    private readonly Action<ModelBuilder>? _modelBuilderAction;

    public DbSet<T> Entitites { get; init; }

    public SingleEntityDbContext(
        DbContextOptions options,
        Action<ModelBuilder>? modelBuilderAction = null)
        : base(options)
    {
        _modelBuilderAction = modelBuilderAction;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        _modelBuilderAction?.Invoke(modelBuilder);
    }
}
