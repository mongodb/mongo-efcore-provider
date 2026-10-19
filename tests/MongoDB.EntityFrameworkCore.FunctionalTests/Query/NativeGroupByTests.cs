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
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-344 native <c>GroupBy(key).Select(aggregate)</c> → <c>$group</c>. Proves that a supported grouped
/// projection (scalar or composite key + Count/Sum) executes as a native aggregation pipeline and
/// materializes correct rows, and that unsupported shapes (computed key, computed operand, bare IGrouping)
/// fall back to driver-LINQ under <see cref="MongoQueryMode.Native"/> yet throw
/// <see cref="NativeTranslationNotSupportedException"/> under <see cref="MongoQueryMode.NativeOnly"/>.
/// <see cref="MongoQueryMode.NativeOnly"/> is the "went native" signal (the emitted MQL is otherwise
/// indistinguishable from the driver-LINQ fallback).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeGroupByTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private enum OrderStatus { New, Shipped, Cancelled }

    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public int Year { get; set; }
        public decimal Amount { get; set; }
        public DateTime OrderDate { get; set; }
        public OrderStatus Status { get; set; }
    }

    private static Order[] SeedOrders() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Country = "US", Year = 2020, Amount = 100, OrderDate = new DateTime(2020, 1, 1), Status = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Country = "US", Year = 2021, Amount = 200, OrderDate = new DateTime(2021, 1, 1), Status = OrderStatus.Shipped },
        new() { Id = ObjectId.GenerateNewId(), Country = "UK", Year = 2020, Amount = 50, OrderDate = new DateTime(2020, 1, 1), Status = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Country = "UK", Year = 2020, Amount = 25, OrderDate = new DateTime(2020, 1, 1), Status = OrderStatus.Shipped },
        new() { Id = ObjectId.GenerateNewId(), Country = "FR", Year = 2021, Amount = 300, OrderDate = new DateTime(2021, 1, 1), Status = OrderStatus.New },
    ];

    private SingleEntityDbContext<Order> CreateContext(Order[] seed, MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Order>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed);

        return Make(collection, mode, null);
    }

    private static SingleEntityDbContext<Order> Make(
        IMongoCollection<Order> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void GroupBy_scalar_key_with_count_and_sum_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_scalar_key_with_count_and_sum_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Country = g.Key, Count = g.Count(), Total = g.Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Country)
            .ToList();

        Assert.Equal(
            [("FR", 1, 300m), ("UK", 2, 75m), ("US", 2, 300m)],
            result.Select(r => (r.Country, r.Count, r.Total)).ToArray());
    }

    [Fact]
    public void GroupBy_composite_key_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_composite_key_goes_native));

        var result = db.Entities
            .GroupBy(o => new { o.Country, o.Year })
            .Select(g => new { g.Key.Country, g.Key.Year, Count = g.Count(), Total = g.Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Country).ThenBy(r => r.Year)
            .ToList();

        Assert.Equal(
            [("FR", 2021, 1, 300m), ("UK", 2020, 2, 75m), ("US", 2020, 1, 100m), ("US", 2021, 1, 200m)],
            result.Select(r => (r.Country, r.Year, r.Count, r.Total)).ToArray());
    }

    [Fact]
    public void GroupBy_computed_key_falls_back_under_native_only()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_computed_key_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.OrderDate.Year).Select(g => new { g.Key, C = g.Count() }).ToList());
    }

    [Fact]
    public void GroupBy_computed_key_runs_under_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.Native,
            nameof(GroupBy_computed_key_runs_under_native));

        var result = db.Entities
            .GroupBy(o => o.OrderDate.Year)
            .Select(g => new { g.Key, C = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([(2020, 3), (2021, 2)], result.Select(r => (r.Key, r.C)).ToArray());
    }

    [Fact]
    public void GroupBy_computed_operand_falls_back_under_native_only()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_computed_operand_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => new { g.Key, T = g.Sum(o => o.Amount * 2) }).ToList());
    }

    [Fact]
    public void GroupBy_computed_operand_runs_under_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.Native,
            nameof(GroupBy_computed_operand_runs_under_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, T = g.Sum(o => o.Amount * 2) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("FR", 600m), ("UK", 150m), ("US", 600m)], result.Select(r => (r.Key, r.T)).ToArray());
    }

    [Fact]
    public void Bare_grouping_sequence_falls_back_under_native_only()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_grouping_sequence_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country).ToList());
    }

    [Fact]
    public void GroupBy_bson_represented_key_falls_back()
    {
        // A key with a non-default BsonRepresentation (enum stored as string) must NOT go native: the grouped
        // shaper reads the group _id back through a generic CLR-type serializer, which cannot reproduce the
        // string-stored enum — it would throw at materialization under Native, diverging from DriverLinq.
        // The fix rejects such keys so the query falls back (throws only under NativeOnly).
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(GroupBy_bson_represented_key_falls_back))
                             + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Order>(collectionName);
        Action<ModelBuilder> configure = mb =>
            mb.Entity<Order>().Property(o => o.Status).HasBsonRepresentation(BsonType.String);

        // Seed via EF so the stored representation matches the configured (string) representation.
        using (var seedDb = Make(collection, MongoQueryMode.Native, configure))
        {
            seedDb.Entities.AddRange(SeedOrders());
            seedDb.SaveChanges();
        }

        // Native: falls back to driver-LINQ and returns correct results (parity with DriverLinq).
        using (var nativeDb = Make(collection, MongoQueryMode.Native, configure))
        {
            var result = nativeDb.Entities
                .GroupBy(o => o.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(r => r.Key).ToList();

            Assert.Equal(
                [(OrderStatus.New, 3), (OrderStatus.Shipped, 2)],
                result.Select(r => (r.Key, r.Count)).ToArray());
        }

        // NativeOnly: the represented key forbids native execution and fallback is disallowed → throws.
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly, configure);
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToList());
    }

    [Fact]
    public void GroupBy_aggregate_over_a_non_grouping_source_falls_back_under_native_only()
    {
        // The projected aggregate's SOURCE is a correlated subquery over the DbSet, NOT the grouping
        // parameter g. It must NOT be bound to a $group accumulator (which would silently drop the subquery
        // and return the group's row count); the whole shape must fall back to driver-LINQ. Under NativeOnly,
        // fallback is disallowed, so it throws (the "did not go native" signal). Regression guard for the
        // root-cause bug proven at the binder level in NativeGroupByBinderTests
        // (Aggregate_over_non_grouping_source_returns_false / Sum_over_non_grouping_source_returns_false).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_aggregate_over_a_non_grouping_source_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Sub = db.Entities.Count(o => o.Year == 2020) })
                .ToList());
    }

    [Fact]
    public void GroupBy_combined_with_Join_throws_clean_translation_failure_under_native()
    {
        // A GroupBy combined with a Join projecting a non-entity result (here the joined Region entity) is a
        // shape the native path cannot represent AND whose driver-LINQ fallback silently returns WRONG data
        // (the joined entity is empty for every grouped row). Unlike computed-key/operand grouping (which
        // falls back to a CORRECT driver-LINQ execution under Native), this shape must fail cleanly rather
        // than return wrong data. Mirrors the spec suite's GroupBy_Aggregate_Join. Regression guard for EF-344.
        using var db = CreateGroupByJoinContext(MongoQueryMode.Native,
            nameof(GroupBy_combined_with_Join_throws_clean_translation_failure_under_native));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Max = g.Max(o => o.Amount) })
                .Join(db.Regions, a => a.Country, r => r.Country, (a, r) => new { Region = r })
                .ToList());
    }

    [Fact]
    public void GroupBy_combined_with_Join_still_runs_under_driver_linq()
    {
        // The clean-failure applies only to Native/NativeOnly; explicit DriverLinq is the user's opt-in and
        // must still execute the query through the driver-LINQ provider (results are the driver's concern),
        // never throwing NativeTranslationNotSupportedException.
        using var db = CreateGroupByJoinContext(MongoQueryMode.DriverLinq,
            nameof(GroupBy_combined_with_Join_still_runs_under_driver_linq));

        var ex = Record.Exception(() =>
            db.Orders
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Max = g.Max(o => o.Amount) })
                .Join(db.Regions, a => a.Country, r => r.Country, (a, r) => new { Region = r })
                .ToList());

        Assert.IsNotType<NativeTranslationNotSupportedException>(ex);
    }

    [Fact]
    public void GroupBy_over_a_joined_source_runs_correctly_under_native()
    {
        // Reverse ordering of GroupBy_combined_with_Join: the Join comes FIRST, then a GroupBy over the join
        // result with a SCALAR aggregate projection (Key + Max). Unlike the group-then-join shape (whose
        // driver-LINQ fallback returns wrong data and must fail cleanly), this join-then-group shape falls
        // back to driver-LINQ and returns CORRECT data (verified equal to explicit DriverLinq). It must NOT be
        // forced to throw — the fallback-unsafe marker is scoped to group-then-join only. Guard for EF-344.
        using var nativeDb = CreateGroupByJoinContext(MongoQueryMode.Native,
            nameof(GroupBy_over_a_joined_source_runs_correctly_under_native) + "N");
        using var driverDb = CreateGroupByJoinContext(MongoQueryMode.DriverLinq,
            nameof(GroupBy_over_a_joined_source_runs_correctly_under_native) + "D");

        (string Key, decimal Max)[] Run(GroupByJoinDbContext db) =>
            db.Orders
                .Join(db.Regions, o => o.Country, x => x.Country, (o, x) => o)
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Max = g.Max(o => o.Amount) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Max)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("FR", 300m), ("UK", 50m), ("US", 200m)], native);
        Assert.Equal(Run(driverDb), native);
    }

    private GroupByJoinDbContext CreateGroupByJoinContext(MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "O" + suffix;
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "R" + suffix;

        var orders = database.MongoDatabase.GetCollection<Order>(ordersName);
        orders.InsertMany(SeedOrders());
        database.MongoDatabase.GetCollection<Region>(regionsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Continent = "NA" },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Continent = "EU" },
            new() { Id = ObjectId.GenerateNewId(), Country = "FR", Continent = "EU" },
        ]);

        return new GroupByJoinDbContext(database, ordersName, regionsName, mode);
    }

    private class Region
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string Continent { get; set; } = "";
    }

    private class GroupByJoinDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _regionsCollection;

        public GroupByJoinDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string regionsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<GroupByJoinDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _regionsCollection = regionsCollection;
        }

        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Region> Regions { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().ToCollection(_ordersCollection);
            modelBuilder.Entity<Region>().ToCollection(_regionsCollection);
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void GroupBy_plain_member_key_with_lone_count_goes_native()
    {
        // A plain-member key with a LONE Count() and nothing else goes NATIVE: it is NOT fused by EF into a
        // GroupBy(key, resultSelector) form, and Count() → $sum:1 is a supported accumulator. Succeeding under
        // NativeOnly (with correct data) is the "went native" proof. Guard against re-introducing the incorrect
        // "lone Count falls back" doc claim.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_plain_member_key_with_lone_count_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_ef_property_key_falls_back_under_native_only()
    {
        // A grouping key expressed as EF.Property<T>(o, "…") is a MethodCallExpression, which
        // NativeGroupByBinder.TryBindGroupKey's switch (NewExpression / MemberExpression only) rejects → the
        // shape falls back to driver-LINQ. Under NativeOnly fallback is disallowed, so it throws. This is the
        // real key-shape nuance (NOT Count-vs-LongCount).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_ef_property_key_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities
                .GroupBy(o => EF.Property<string>(o, nameof(Order.Country)))
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList());
    }

    [Fact]
    public void GroupBy_accumulator_named_id_falls_back_and_matches_driver_linq()
    {
        // A group projection whose accumulator member is literally "_id" makes the accumulator OutputField
        // "_id" — which collides with the reserved $group id field (the $group document already carries the
        // grouping key under "_id"). Before the guard this threw a BsonDocument duplicate-key exception at
        // pipeline BUILD (an unhandled crash, not a clean fallback). The guard rejects the shape so it falls
        // back to driver-LINQ under Native (correct results, parity with DriverLinq) and throws
        // NativeTranslationNotSupportedException — never a MongoDB.Bson duplicate-key error — under NativeOnly.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_accumulator_named_id_falls_back_and_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_accumulator_named_id_falls_back_and_matches_driver_linq) + "D");

        int[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { _id = g.Count() })
                .AsEnumerable().OrderBy(r => r._id)
                .Select(r => r._id).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_accumulator_named_id_falls_back_and_matches_driver_linq) + "O");
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.GroupBy(o => o.Country).Select(g => new { _id = g.Count() }).ToList());
    }

    [Fact]
    public void GroupBy_HAVING_on_aggregate_alias_colliding_with_property_matches_driver_linq()
    {
        // The repro (EF-344 review). A post-group Where (HAVING) whose predicate references an aggregate
        // ALIAS ("Amount") that COLLIDES with a real entity property name ("Amount"). Before the guard, the
        // post-group Where was resolved against the ENTITY type by member name and emitted a PRE-$group
        // $match on the raw Amount field — the filter ran BEFORE aggregation (returning US=200, the single
        // 2021 row, instead of the aggregated US=300) → silently wrong data under Native. The guard forces a
        // clean driver-LINQ fallback so Native == DriverLinq.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_HAVING_on_aggregate_alias_colliding_with_property_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_HAVING_on_aggregate_alias_colliding_with_property_matches_driver_linq) + "D");

        (string Country, decimal Amount)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Amount = g.Sum(o => o.Amount) })
                .Where(x => x.Amount > 150)
                .OrderBy(x => x.Country)
                .AsEnumerable()
                .Select(x => (x.Country, x.Amount)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("FR", 300m), ("US", 300m)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_HAVING_on_non_colliding_alias_matches_driver_linq()
    {
        // Same HAVING shape but the aggregate alias ("Total") does NOT collide with any entity property.
        // This already fell back today (member resolution against the entity type happens to fail), but
        // it must stay a clean fallback — locked in as parity so a future translator change that starts
        // resolving the alias can't silently regress it.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_HAVING_on_non_colliding_alias_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_HAVING_on_non_colliding_alias_matches_driver_linq) + "D");

        (string Country, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Where(x => x.Total > 150)
                .OrderBy(x => x.Country)
                .AsEnumerable()
                .Select(x => (x.Country, x.Total)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("FR", 300m), ("US", 300m)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_post_group_OrderBy_by_aggregate_matches_driver_linq()
    {
        // A post-group OrderBy over the grouped result (ordering by an aggregate alias). Must fall back
        // cleanly and match DriverLinq — a native $sort emitted here would sort the wrong (pre-group) rows.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_post_group_OrderBy_by_aggregate_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_post_group_OrderBy_by_aggregate_matches_driver_linq) + "D");

        (string Country, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .OrderBy(x => x.Total).ThenBy(x => x.Country)
                .AsEnumerable()
                .Select(x => (x.Country, x.Total)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("UK", 75m), ("FR", 300m), ("US", 300m)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_post_group_Skip_Take_matches_driver_linq()
    {
        // Post-group Skip/Take (paging) over the grouped result. Must fall back cleanly and match DriverLinq.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_post_group_Skip_Take_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_post_group_Skip_Take_matches_driver_linq) + "D");

        (string Country, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .OrderBy(x => x.Country)
                .Skip(1).Take(1)
                .AsEnumerable()
                .Select(x => (x.Country, x.Total)).ToArray();

        var native = Run(nativeDb);
        // Ordered by Country: FR, UK, US → Skip(1).Take(1) => UK.
        Assert.Equal([("UK", 75m)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_aggregate_Select_with_no_post_group_op_goes_native()
    {
        // The supported shape (GroupBy(key).Select(aggregate) with NO post-group operator) must STILL go
        // native after the guard — proven by succeeding under NativeOnly with correct data. If the guard
        // were mis-scoped to fire for the aggregate Select itself, this would flip to fallback and throw.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_aggregate_Select_with_no_post_group_op_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Country = g.Key, Amount = g.Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Country)
            .ToList();

        Assert.Equal(
            [("FR", 300m), ("UK", 75m), ("US", 300m)],
            result.Select(r => (r.Country, r.Amount)).ToArray());
    }

    [Fact]
    public void GroupBy_results_match_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_results_match_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_results_match_driver_linq) + "D");

        var native = nativeDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Country = g.Key, Count = g.Count(), Total = g.Sum(o => o.Amount), Max = g.Max(o => o.Amount) })
            .AsEnumerable().OrderBy(r => r.Country).ToList();

        var driver = driverDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Country = g.Key, Count = g.Count(), Total = g.Sum(o => o.Amount), Max = g.Max(o => o.Amount) })
            .AsEnumerable().OrderBy(r => r.Country).ToList();

        Assert.Equal(
            driver.Select(r => (r.Country, r.Count, r.Total, r.Max)).ToArray(),
            native.Select(r => (r.Country, r.Count, r.Total, r.Max)).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------
    // EF-344 pass-2 regression: a scalar aggregate / cardinality reducer applied AFTER a finalized
    // GroupBy(key).Select(anon) reaches NativeCardinalityBinder.TryBindAggregate / TryBindReducer, which
    // (before the guard) had NO IsGroupBy check. It set Cardinality on an already-grouped select; Route
    // then flipped to ScalarAggregate while the lowerer's grouping branch still ran, emitting a
    // [$group, $project] pipeline with no terminal $count/aggregate stage — the scalar shaper then read a
    // nonexistent "v" element and crashed with KeyNotFoundException instead of the documented graceful
    // driver-LINQ fallback. The guard makes every post-group cardinality operator fall back cleanly,
    // symmetric to the post-group slot-operator guard in NativeSlotPopulator.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void GroupBy_then_Count_matches_driver_linq()
    {
        // THE REPRO. Post-group Count() over GroupBy(key).Select(anon). Pre-guard this crashed with
        // KeyNotFoundException ("Element 'v' not found.") under Native; the guard forces a clean fallback so
        // Native == DriverLinq (== 3 groups: US, UK, FR).
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Count_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Count_matches_driver_linq) + "D");

        int Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Count();

        var native = Run(nativeDb);
        Assert.Equal(3, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_LongCount_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_LongCount_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_LongCount_matches_driver_linq) + "D");

        long Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .LongCount();

        var native = Run(nativeDb);
        Assert.Equal(3L, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Sum_matches_driver_linq()
    {
        // Sum over the grouped per-group totals: US=300, UK=75, FR=300 => 675.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Sum_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Sum_matches_driver_linq) + "D");

        decimal Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Sum(x => x.Total);

        var native = Run(nativeDb);
        Assert.Equal(675m, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Min_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Min_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Min_matches_driver_linq) + "D");

        decimal Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Min(x => x.Total);

        var native = Run(nativeDb);
        Assert.Equal(75m, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Max_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Max_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Max_matches_driver_linq) + "D");

        decimal Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Max(x => x.Total);

        var native = Run(nativeDb);
        Assert.Equal(300m, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Average_matches_driver_linq()
    {
        // Average over per-group totals: (300 + 75 + 300) / 3 = 225.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Average_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Average_matches_driver_linq) + "D");

        decimal Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Average(x => x.Total);

        var native = Run(nativeDb);
        Assert.Equal(225m, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_First_matches_driver_linq()
    {
        // A post-group reducer (First). Before the guard this "worked" via Route=GroupBy + EF base reduction;
        // after guarding TryBindReducer it falls back — still correct. A stable OrderBy makes the pick
        // deterministic so Native and DriverLinq compare a single well-defined row.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_First_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_First_matches_driver_linq) + "D");

        (string Country, decimal Total) Run(SingleEntityDbContext<Order> db)
        {
            var r = db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .OrderBy(x => x.Country)
                .First();
            return (r.Country, r.Total);
        }

        var native = Run(nativeDb);
        Assert.Equal(("FR", 300m), native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Single_matches_driver_linq()
    {
        // A post-group Single over a filtered-to-one grouped result. Falls back after the reducer guard —
        // still correct (parity with DriverLinq).
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Single_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Single_matches_driver_linq) + "D");

        (string Country, decimal Total) Run(SingleEntityDbContext<Order> db)
        {
            var r = db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Single(x => x.Country == "UK");
            return (r.Country, r.Total);
        }

        var native = Run(nativeDb);
        Assert.Equal(("UK", 75m), native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Any_matches_driver_linq()
    {
        // Post-group Any. Did not crash pre-guard (presence-only path) but must stay correct after the guard
        // flips it to fallback.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_Any_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_Any_matches_driver_linq) + "D");

        bool Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Any();

        var native = Run(nativeDb);
        Assert.True(native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_scalar_aggregate_throws_clean_decline_under_native_only()
    {
        // Under NativeOnly a post-group scalar aggregate must decline cleanly with
        // NativeTranslationNotSupportedException — NOT crash with KeyNotFoundException (the pre-guard bug).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_scalar_aggregate_throws_clean_decline_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
                .Count());
    }

    [Fact]
    public void Select_after_GroupBy_is_unsupported_and_never_returns_silent_null_data()
    {
        // A second projected Select applied AFTER a native GroupBy(key).Select(aggregate) must NEVER silently
        // go native and return null-valued rows. Structural hazard (guarded in TranslateSelect's non-grouped
        // projection branch): the second Select reaches that branch (the shaper is no longer a
        // GroupByShaperExpression — the grouped-aggregate Select already replaced it), bypassing the IsGroupBy
        // slot/cardinality guards; without the guard TryPopulateNativeProjection would APPEND its field-ref onto
        // the grouped Projection while Grouping is still set, and the lowerer would emit a flatten $project over
        // fields gone after the $group → nulls.
        //
        // In practice this provider cannot build a shaper reading a prior grouped/anonymous projection's members
        // (MongoProjectionBindingExpressionVisitor throws on the nested ProjectionBindingExpression BEFORE the
        // gate), so the shape is UNSUPPORTED and throws during translation in EVERY mode — Native, DriverLinq,
        // NativeOnly alike. The property this locks in: Native does NOT diverge from DriverLinq by silently
        // returning null rows — both fail identically (no wrong/null data). The supported single grouped Select
        // (GroupBy(k).Select(aggregate) with no further Select) still goes native — see
        // GroupBy_aggregate_Select_with_no_post_group_op_goes_native.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Select_after_GroupBy_is_unsupported_and_never_returns_silent_null_data) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Select_after_GroupBy_is_unsupported_and_never_returns_silent_null_data) + "D");

        Exception? Run(SingleEntityDbContext<Order> db) => Record.Exception(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => new { g.Key, c = g.Count() }).Select(r => new { r.Key }).ToList());

        Assert.NotNull(Run(nativeDb));   // Native throws — NOT a silent null-data success
        Assert.NotNull(Run(driverDb));   // DriverLinq throws the same way — no Native-vs-DriverLinq divergence
    }

    // ---------------------------------------------------------------------------------------------------
    // EF-449: GroupBy(key).{Count()|LongCount()|Any()|Any(pred)|All(pred)|Count(pred)|LongCount(pred)} with
    // NO intervening Select — the EF Core spec suite's "GroupBy_without_aggregate" family
    // (NorthwindGroupByQueryTestBase). Previously an unimplemented native shape (bare GroupBy sets
    // IsGroupBy unconditionally, so NativeCardinalityBinder.TryBindAggregate's post-terminal guard always
    // declined it) that silently fell back to driver-LINQ under Native. Succeeding under NativeOnly with
    // correct data is the "went native" proof for each shape.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void GroupBy_then_bare_Count_goes_native()
    {
        // Number of distinct groups (US, UK, FR) => 3.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_bare_Count_goes_native));

        var result = db.Entities.GroupBy(o => o.Country).Count();

        Assert.Equal(3, result);
    }

    [Fact]
    public void GroupBy_then_bare_LongCount_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_bare_LongCount_goes_native));

        var result = db.Entities.GroupBy(o => o.Country).LongCount();

        Assert.Equal(3L, result);
    }

    [Fact]
    public void GroupBy_then_bare_Any_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_bare_Any_goes_native));

        Assert.True(db.Entities.GroupBy(o => o.Country).Any());
    }

    [Fact]
    public void GroupBy_then_bare_Any_over_empty_source_goes_native()
    {
        using var db = CreateContext([], MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_bare_Any_over_empty_source_goes_native));

        Assert.False(db.Entities.GroupBy(o => o.Country).Any());
    }

    [Fact]
    public void GroupBy_then_Any_with_count_predicate_goes_native()
    {
        // Groups with more than one order: US (2), UK (2). FR has only 1.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_Any_with_count_predicate_goes_native));

        Assert.True(db.Entities.GroupBy(o => o.Country).Any(g => g.Count() > 1));
    }

    [Fact]
    public void GroupBy_then_Any_with_count_predicate_matching_nothing_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_Any_with_count_predicate_matching_nothing_goes_native));

        Assert.False(db.Entities.GroupBy(o => o.Country).Any(g => g.Count() > 10));
    }

    [Fact]
    public void GroupBy_then_All_with_count_predicate_goes_native()
    {
        // FR has only 1 order, so not every group has more than one.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_All_with_count_predicate_goes_native));

        Assert.False(db.Entities.GroupBy(o => o.Country).All(g => g.Count() > 1));
    }

    [Fact]
    public void GroupBy_then_All_with_count_predicate_matching_every_group_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_All_with_count_predicate_matching_every_group_goes_native));

        Assert.True(db.Entities.GroupBy(o => o.Country).All(g => g.Count() >= 1));
    }

    [Fact]
    public void GroupBy_then_Count_with_count_predicate_goes_native()
    {
        // Two groups (US, UK) have more than one order.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_Count_with_count_predicate_goes_native));

        Assert.Equal(2, db.Entities.GroupBy(o => o.Country).Count(g => g.Count() > 1));
    }

    [Fact]
    public void GroupBy_then_LongCount_with_count_predicate_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_LongCount_with_count_predicate_goes_native));

        Assert.Equal(2L, db.Entities.GroupBy(o => o.Country).LongCount(g => g.Count() > 1));
    }

    [Fact]
    public void GroupBy_then_bare_Count_matches_driver_linq()
    {
        var seed = SeedOrders();
        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_bare_Count_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_bare_Count_matches_driver_linq) + "D");

        int Run(SingleEntityDbContext<Order> db) => db.Entities.GroupBy(o => o.Country).Count();

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_All_with_count_predicate_matches_driver_linq()
    {
        var seed = SeedOrders();
        using var nativeDb = CreateContext(seed, MongoQueryMode.Native, nameof(GroupBy_then_All_with_count_predicate_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq, nameof(GroupBy_then_All_with_count_predicate_matches_driver_linq) + "D");

        bool Run(SingleEntityDbContext<Order> db) => db.Entities.GroupBy(o => o.Country).All(g => g.Count() > 1);

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_then_Any_with_compound_predicate_falls_back_under_native_only()
    {
        // Out of EF-449's scope: a compound (&&) predicate over more than one group-level aggregate. Must
        // fall back cleanly (throws under NativeOnly), not be mistranslated.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_Any_with_compound_predicate_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country).Any(g => g.Count() > 1 && g.Count() < 10));
    }

    [Fact]
    public void GroupBy_aggregate_projection_with_count_still_goes_native_after_guard()
    {
        // Positive guard: the supported GroupBy(key).Select(anonymous-with-Count) projection is bound by the
        // GroupBy projection path (TryBindGroupProjection), NOT the cardinality binder — so the cardinality
        // guard must NOT touch it. Succeeding under NativeOnly (with correct data) is the "went native" proof
        // that the cardinality-binder change didn't disturb the projection path. Seed => US=2, UK=2, FR=1.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_aggregate_projection_with_count_still_goes_native_after_guard));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Count).ThenBy(r => r.Key)
            .ToList();

        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
    }
}
