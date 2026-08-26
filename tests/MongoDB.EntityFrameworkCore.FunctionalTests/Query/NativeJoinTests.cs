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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-392 (native-join-translation plan): a genuine two-sided <c>Join</c>/<c>LeftJoin</c> is permanently out
/// of scope for native translation — EF Core's <c>NavigationExpandingExpressionVisitor</c> normalizes such a
/// join into the same shape as an Include-generated one, so the two are indistinguishable at bind time. The
/// maintainer ruled against building a provider-side disambiguation mechanism, so every genuine join declines
/// to the driver-LINQ fallback indefinitely; the classification/registration scaffolding this plan built
/// (<c>MongoJoinBinder</c>, <c>MongoJoinScope</c>) was subsequently removed rather than kept dormant, since it
/// could never run. These tests pin the permanent behavior: correct results under the default
/// <see cref="MongoQueryMode.Native"/> (via fallback), and a clean decline under
/// <see cref="MongoQueryMode.NativeOnly"/>. See
/// <c>docs/superpowers/specs/2026-08-26-native-join-translation-design.md</c>'s "Blocker found during
/// implementation" section and EF-439.
/// </summary>
public class NativeJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Genuine_two_sided_join_returns_correct_results_via_fallback()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Genuine_two_sided_join_returns_correct_results_via_fallback));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Genuine_two_sided_join_declines_cleanly_under_NativeOnly()
    {
        // Permanent, correct behavior per EF-439 — a genuine two-sided join can never be
        // distinguished from an Include-generated one at bind time (EF Core limitation), so
        // it must always decline to the driver-LINQ fallback, never attempt native translation.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuine_two_sided_join_declines_cleanly_under_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }

    [Fact]
    public void GroupJoin_array_result_shape_still_declines_cleanly_in_NativeOnly()
    {
        // Owned by EF-436, not this ticket — must stay declining, not silently "fixed" by this work.
        // Raw GroupJoin with collection result shape is unsupported outright, in every mode (Native/Fallback/DriverLinq).
        // This decline is NOT NativeOnly-specific: it fails the same way in all modes because raw GroupJoin's
        // collection-result shape isn't supported by the projection binder — when rs gets substituted with a
        // single-entity shaper in TranslateJoinCore, there's a type mismatch that MongoExpressionTranslator
        // can't handle in any routing path.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupJoin_array_result_shape_still_declines_cleanly_in_NativeOnly));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            db.Owners
                .GroupJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o.Name, Orders = rs })
                .ToList());

        // GroupJoin's collection result shape can't be bound; this throws during projection binding in all modes
        Assert.Contains("could not be translated", ex.Message);
    }

    [Fact]
    public void Chained_second_join_still_declines_cleanly_in_NativeOnly()
    {
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_second_join_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(db.OrderLines, x => x.r.Id, l => l.OrderId, (x, l) => new { x.o.Name, l.Sku })
                .AsEnumerable()
                .ToList());
    }

    [Fact]
    public void Query_filtered_join_target_still_declines_cleanly_in_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Query_filtered_join_target_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders.Where(r => r.Total > 0), o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }

    [Fact]
    public void Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly()
    {
        // A Join on two properties with no corresponding model navigation between the two entity types.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Region, r => r.Region, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }

    public static IEnumerable<object[]> JoinOracleCases()
    {
        // Each case is a Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<...>> so the SAME expression
        // tree runs against both the native-backed DbSet and the in-memory seed collection, following the
        // NativeOwnedCollectionAllTests.cs oracle pattern exactly.
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total }))
        ];
#if !EF8 && !EF9
        // Queryable.LeftJoin itself only dispatches on EF10 (EF-X020, per RequiredNavigationUnwindTests.cs) —
        // on EF8/EF9, EF Core's OWN NavigationExpandingExpressionVisitor throws "could not be translated"
        // for ANY LeftJoin call, before the expression ever reaches this (or any) provider. That is an EF
        // Core-version limitation, not a Mongo provider gap, so this case is compiled out rather than run
        // and expected to fail on EF8/EF9.
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.LeftJoin(orders, o => o.Id, r => r.OwnerId,
                    (o, r) => new { o.Name, Total = r == null ? (decimal?)null : r.Total }))
        ];
#endif
    }

    [Theory]
    [MemberData(nameof(JoinOracleCases))]
    public void Join_result_matches_in_memory_oracle_including_unmatched_rows(
        Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>> query)
    {
        // Seed must include: an Owner with a matching Order, an Owner with NO Order (left-join/unmatched
        // case), and an Order whose OwnerId matches nothing (dangling FK, dropped by inner Join).
        // Runs under the default MongoQueryMode.Native — genuine two-sided joins execute via the driver-LINQ
        // fallback (see EF-439), not natively; this proves that fallback path is correct, not new native
        // coverage. Do NOT use MongoQueryMode.NativeOnly here — it would always throw for this shape, by design.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Join_result_matches_in_memory_oracle_including_unmatched_rows));

        // Ordered by a stable key before comparing — an unordered List<object> equality check would only
        // pass by coincidence of seed/pipeline ordering. Mirrors the OrderBy(x => x.Name).ThenBy(x => x.Total)
        // pattern used on both sides three methods up (Genuine_two_sided_join_returns_correct_results_via_fallback);
        // dynamic is used here (rather than the typed lambda that test uses) because this method's result
        // shape is erased to IQueryable<object> by JoinOracleCases' shared delegate signature.
        var actualResult = query(db.Owners, db.Orders).AsEnumerable()
            .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
            .ToList();
        var oracleResult = query(seed.Owners.AsQueryable(), seed.Orders.AsQueryable()).AsEnumerable()
            .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
            .ToList();

        Assert.Equal(oracleResult, actualResult);
    }

    private sealed record Seed(Owner[] Owners, Order[] Orders, OrderLine[] OrderLines = default!);

    private static Seed SeedOwnersAndOrders()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South" };
        var owners = new[] { ownerA, ownerB };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m, Region = "South" },
        };

        return new Seed(owners, orders, []);
    }

    // Covers, in one fixture, the three row shapes a Join/LeftJoin differential must exercise: an Owner
    // with a matching Order (both cases include it), an Owner with NO Order (unmatched — dropped by inner
    // Join, kept with a null Total by LeftJoin), and an Order whose OwnerId matches no seeded Owner (a
    // dangling FK — dropped by inner Join and, since LeftJoin here is driven from the Owner side, also
    // absent from the LeftJoin result).
    private static Seed SeedOwnersAndOrdersWithUnmatchedRows()
    {
        var ownerWithOrder = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var ownerWithoutOrder = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South" };
        var owners = new[] { ownerWithOrder, ownerWithoutOrder };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerWithOrder.Id, Total = 10m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), Total = 99m, Region = "East" },
        };

        return new Seed(owners, orders, []);
    }

    private static Seed SeedOwnersOrdersAndLines()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South" };
        var owners = new[] { ownerA, ownerB };

        var order1 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m, Region = "North" };
        var order2 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m, Region = "North" };
        var order3 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m, Region = "South" };
        var orders = new[] { order1, order2, order3 };

        var lines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order1.Id, Sku = "SKU-1" },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order1.Id, Sku = "SKU-2" },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order2.Id, Sku = "SKU-3" },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order3.Id, Sku = "SKU-4" },
        };

        return new Seed(owners, orders, lines);
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
        public List<OrderLine> OrderLines { get; set; } = [];
    }

    public class OrderLine
    {
        public ObjectId Id { get; set; }
        public ObjectId OrderId { get; set; }
        public Order? Order { get; set; }
        public string Sku { get; set; } = "";
    }

    private JoinTestDbContext CreateContext(Seed seed, MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Owners" + suffix;
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Orders" + suffix;
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OrderLines" + suffix;

        database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany(seed.Owners);
        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(seed.Orders);
        if (seed.OrderLines.Length > 0)
            database.MongoDatabase.GetCollection<OrderLine>(linesName).InsertMany(seed.OrderLines);

        return new JoinTestDbContext(database, ownersName, ordersName, linesName, mode);
    }

    private sealed class JoinTestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _ordersCollection;
        private readonly string _linesCollection;

        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<OrderLine> OrderLines { get; set; } = null!;

        public JoinTestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, string linesCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode))
        {
            _ownersCollection = ownersCollection;
            _ordersCollection = ordersCollection;
            _linesCollection = linesCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var optionsBuilder = new DbContextOptionsBuilder<JoinTestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Orders).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.HasMany(o => o.OrderLines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId);
            });
            modelBuilder.Entity<OrderLine>(b => b.ToCollection(_linesCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
