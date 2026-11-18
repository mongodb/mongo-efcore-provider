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
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Pins the emitted pipeline for the <c>$lookup</c>-based join shapes. <see cref="MongoQueryMode.NativeOnly"/> proves
/// a join did not fall back, but not that the server does the join: these tests also assert the one executed
/// pipeline contains the expected number of <c>$lookup</c> stages (and no driver-LINQ <c>_outer</c>/<c>_inner</c>
/// join shape), so a regression that joined on the client, or fetched a collection per row, fails here.
/// </summary>
public class NativeJoinPipelineShapeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Inner_Join_is_one_lookup()
    {
        using var db = CreateContext(nameof(Inner_Join_is_one_lookup), out var spy);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Total).Select(x => (x.Name, x.Total)).ToList();

        Assert.Equal([("Alice", 10m), ("Alice", 20m), ("Bob", 30m)], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void Inner_Join_with_outer_filter_is_one_lookup()
    {
        using var db = CreateContext(nameof(Inner_Join_with_outer_filter_is_one_lookup), out var spy);

        var result = db.Owners.Where(o => o.Region == "North")
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable().OrderBy(x => x.Total).Select(x => (x.Name, x.Total)).ToList();

        Assert.Equal([("Alice", 10m), ("Alice", 20m)], result);
        AssertLookups(spy, 1);
    }

#if !EF8 && !EF9
    [Fact]
    public void LeftJoin_keeps_unmatched_owner_with_one_lookup()
    {
        using var db = CreateContext(nameof(LeftJoin_keeps_unmatched_owner_with_one_lookup), out var spy);

        var result = db.Owners
            .LeftJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, Total = (decimal?)r!.Total })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Total).Select(x => (x.Name, x.Total)).ToList();

        Assert.Equal([("Alice", (decimal?)10m), ("Alice", 20m), ("Bob", 30m), ("Carol", null)], result);
        AssertLookups(spy, 1);
    }

#endif

    [Fact]
    public void GroupJoin_SelectMany_DefaultIfEmpty_left_join_is_one_lookup()
    {
        using var db = CreateContext(nameof(GroupJoin_SelectMany_DefaultIfEmpty_left_join_is_one_lookup), out var spy);

        var result = db.Owners
            .GroupJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o, rs })
            .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o.Name, Total = (decimal?)r!.Total })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Total).Select(x => (x.Name, x.Total)).ToList();

        Assert.Equal([("Alice", (decimal?)10m), ("Alice", 20m), ("Bob", 30m), ("Carol", null)], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void GroupJoin_aggregate_is_computed_from_one_lookup()
    {
        using var db = CreateContext(nameof(GroupJoin_aggregate_is_computed_from_one_lookup), out var spy);

        var result = db.Owners
            .GroupJoin(db.Orders, o => o.Id, r => r.OwnerId,
                (o, rs) => new { o.Name, Count = rs.Count(), Sum = rs.Sum(r => r.Total) })
            .AsEnumerable().OrderBy(x => x.Name).Select(x => (x.Name, x.Count, x.Sum)).ToList();

        Assert.Equal([("Alice", 2, 30m), ("Bob", 1, 30m), ("Carol", 0, 0m)], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void SelectMany_over_a_collection_navigation_is_one_lookup()
    {
        using var db = CreateContext(nameof(SelectMany_over_a_collection_navigation_is_one_lookup), out var spy);

        var result = db.Owners
            .SelectMany(o => o.Orders, (o, r) => new { o.Name, r.Total })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Total).Select(x => (x.Name, x.Total)).ToList();

        Assert.Equal([("Alice", 10m), ("Alice", 20m), ("Bob", 30m)], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void Collection_Include_is_one_lookup()
    {
        using var db = CreateContext(nameof(Collection_Include_is_one_lookup), out var spy);

        var result = db.Owners.Include(o => o.Orders).AsEnumerable()
            .OrderBy(o => o.Name).Select(o => (o.Name, o.Orders.Count)).ToList();

        Assert.Equal([("Alice", 2), ("Bob", 1), ("Carol", 0)], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void Reference_navigation_projection_is_one_lookup()
    {
        using var db = CreateContext(nameof(Reference_navigation_projection_is_one_lookup), out var spy);

        var result = db.Orders.Select(r => new { r.Total, OwnerName = r.Owner!.Name })
            .AsEnumerable().OrderBy(x => x.Total).Select(x => (x.Total, x.OwnerName)).ToList();

        Assert.Equal([(10m, "Alice"), (20m, "Alice"), (30m, "Bob")], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void Composite_key_Join_is_one_lookup()
    {
        using var db = CreateContext(nameof(Composite_key_Join_is_one_lookup), out var spy);

        // Two key parts: the id and the region must both agree.
        var result = db.Owners
            .Join(db.Orders, o => new { o.Id, o.Region }, r => new { Id = r.OwnerId, r.Region },
                (o, r) => new { o.Name, r.Total })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Total).Select(x => (x.Name, x.Total)).ToList();

        Assert.Equal([("Alice", 10m), ("Alice", 20m), ("Bob", 30m)], result);
        AssertLookups(spy, 1);
    }

    [Fact]
    public void Chained_Join_is_one_lookup_per_join()
    {
        using var db = CreateContext(nameof(Chained_Join_is_one_lookup_per_join), out var spy);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, OrderId = r.Id })
            .Join(db.OrderLines, x => x.OrderId, l => l.OrderId, (x, l) => new { x.Name, l.Sku })
            .AsEnumerable().OrderBy(x => x.Sku).Select(x => (x.Name, x.Sku)).ToList();

        Assert.Equal([("Alice", "a-1"), ("Alice", "a-2"), ("Bob", "b-1")], result);
        AssertLookups(spy, 2);
    }

    // The query ran once, natively (NativeOnly would have thrown otherwise), and the server did the join.
    private static void AssertLookups(SpyLoggerProvider spy, int expected)
    {
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var count = mql.Split("$lookup").Length - 1;
        Assert.True(count == expected, $"Expected {expected} $lookup stage(s) but found {count} in:\n{mql}");
        Assert.DoesNotContain("_outer", mql);
        Assert.DoesNotContain("_inner", mql);
    }

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public decimal Total { get; set; }
        public string Region { get; set; } = "";
    }

    public class OrderLine
    {
        public ObjectId Id { get; set; }
        public ObjectId OrderId { get; set; }
        public string Sku { get; set; } = "";
    }

    private JoinShapeDbContext CreateContext(string name, out SpyLoggerProvider spy)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spy = provider;

        var alice = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var bob = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South" };
        var carol = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol", Region = "East" };
        var o1 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, Total = 10m, Region = "North" };
        var o2 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, Total = 20m, Region = "North" };
        var o3 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = bob.Id, Total = 30m, Region = "South" };

        var suffix = Guid.NewGuid().ToString("N")[..8]; // no test name: the shape asserts search the MQL for "_outer"
        var ownersName = "Owners" + suffix;
        var ordersName = "Orders" + suffix;
        var linesName = "Lines" + suffix;

        database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany([alice, bob, carol]);
        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany([o1, o2, o3]);
        database.MongoDatabase.GetCollection<OrderLine>(linesName).InsertMany(
        [
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o1.Id, Sku = "a-1" },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o2.Id, Sku = "a-2" },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o3.Id, Sku = "b-1" },
        ]);

        return new JoinShapeDbContext(database, ownersName, ordersName, linesName, loggerFactory);
    }

    private sealed class JoinShapeDbContext(
        TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, string linesCollection,
        ILoggerFactory loggerFactory)
        : DbContext(BuildOptions(database, loggerFactory))
    {
        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<OrderLine> OrderLines { get; set; } = null!;

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, ILoggerFactory loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<JoinShapeDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .UseLoggerFactory(loggerFactory)
                .EnableSensitiveDataLogging();
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(MongoQueryMode.NativeOnly);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(ownersCollection);
                b.HasMany(o => o.Orders).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<Order>().ToCollection(ordersCollection);
            modelBuilder.Entity<OrderLine>().ToCollection(linesCollection);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
