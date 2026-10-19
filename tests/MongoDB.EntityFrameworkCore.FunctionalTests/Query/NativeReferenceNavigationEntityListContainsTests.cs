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
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Generalizes <c>MongoExpressionTranslator.EntityEquality.cs</c>'s <c>TryTranslateEntityListContains</c>
/// (<c>customers.Contains(c)</c>, root entity as the Contains item) to the shape EF Core's own
/// <c>Where_navigation_contains</c> spec test exercises: <c>customer.Orders.Contains(od.Order)</c>, where the
/// Contains item is a TO-ONE REFERENCE NAVIGATION off the root entity, not the root entity itself. Unlike a
/// two-sided <c>Join</c>, this needs no <c>$lookup</c> at all — the navigation's own foreign-key property,
/// already present on the root document, stands in for the target's principal-key value, so the rewrite is a
/// primary-key-vs-foreign-key <c>$in</c> exactly like the whole-entity case, just keyed off the FK field.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferenceNavigationEntityListContainsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private sealed record Seed(Parent[] Parents, Child[] Children);

    public class Parent
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Child
    {
        public ObjectId Id { get; set; }
        public ObjectId ParentId { get; set; }
        public Parent? Parent { get; set; }
        public string Name { get; set; } = "";
    }

    private static Seed SeedParentsAndChildren()
    {
        var alice = new Parent { Id = ObjectId.GenerateNewId(), Name = "Alice" };
        var bob = new Parent { Id = ObjectId.GenerateNewId(), Name = "Bob" };

        var children = new[]
        {
            new Child { Id = ObjectId.GenerateNewId(), ParentId = alice.Id, Name = "Alice-Child-1" },
            new Child { Id = ObjectId.GenerateNewId(), ParentId = alice.Id, Name = "Alice-Child-2" },
            new Child { Id = ObjectId.GenerateNewId(), ParentId = bob.Id, Name = "Bob-Child-1" },
        };

        return new Seed([alice, bob], children);
    }

    private ReferenceNavContainsDbContext CreateContext(Seed seed, MongoQueryMode mode, string name, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parentsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Parents" + suffix;
        var childrenName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Children" + suffix;

        database.MongoDatabase.GetCollection<Parent>(parentsName).InsertMany(seed.Parents);
        database.MongoDatabase.GetCollection<Child>(childrenName).InsertMany(seed.Children);

        return new ReferenceNavContainsDbContext(database, parentsName, childrenName, mode, loggerFactory);
    }

    [Fact]
    public void Reference_navigation_entity_list_contains_goes_native_under_NativeOnly()
    {
        var seed = SeedParentsAndChildren();
        using var db = CreateContext(
            seed, MongoQueryMode.NativeOnly, nameof(Reference_navigation_entity_list_contains_goes_native_under_NativeOnly),
            out var spyLogger);

        List<Parent?> parents = seed.Parents.Where(p => p.Name == "Alice").ToList<Parent?>();

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves `parents.Contains(c.Parent)` went through the native FK-based $in path rather than
        // driver-LINQ.
        var results = db.Children.AsNoTracking().Where(c => parents.Contains(c.Parent)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, c => Assert.StartsWith("Alice-", c.Name));
        spyLogger.AssertExecutedMqlContains("\"$in\"");
    }

    [Fact]
    public void Reference_navigation_entity_list_contains_with_no_match_returns_empty()
    {
        var seed = SeedParentsAndChildren();
        using var db = CreateContext(
            seed, MongoQueryMode.NativeOnly, nameof(Reference_navigation_entity_list_contains_with_no_match_returns_empty),
            out _);

        var parents = new List<Parent?> { new() { Id = ObjectId.GenerateNewId(), Name = "Nobody" } };

        var results = db.Children.AsNoTracking().Where(c => parents.Contains(c.Parent)).ToList();

        Assert.Empty(results);
    }

    private sealed class ReferenceNavContainsDbContext : DbContext
    {
        private readonly string _parentsCollection;
        private readonly string _childrenCollection;

        public DbSet<Parent> Parents { get; set; } = null!;
        public DbSet<Child> Children { get; set; } = null!;

        public ReferenceNavContainsDbContext(
            TemporaryDatabaseFixture database, string parentsCollection, string childrenCollection,
            MongoQueryMode mode, ILoggerFactory? loggerFactory)
            : base(BuildOptions(database, mode, loggerFactory))
        {
            _parentsCollection = parentsCollection;
            _childrenCollection = childrenCollection;
        }

        private static DbContextOptions BuildOptions(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ReferenceNavContainsDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            if (loggerFactory != null)
            {
                optionsBuilder = optionsBuilder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            }

            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Parent>().ToCollection(_parentsCollection);
            modelBuilder.Entity<Child>(b =>
            {
                b.ToCollection(_childrenCollection);
                b.HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
