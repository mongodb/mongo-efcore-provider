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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-344 native <c>GroupBy(key).Select(aggregate)</c> → <c>$group</c>. Proves that a supported grouped
/// projection (scalar or composite key + Count/Sum) executes as a native aggregation pipeline and
/// materializes correct rows, and that unsupported shapes (computed key, bare IGrouping)
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
        public Guid ExternalId { get; set; }
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
    public void GroupBy_computed_key_goes_native_under_native_only()
    {
        // EF-322 SP1: a computed key (o.OrderDate.Year) is no longer a hard decline —
        // NativeGroupByBinder.TryBindGroupKey now translates it via MongoExpressionTranslator.TryTranslateValue
        // (the same general "any translatable value" method already used for accumulator operands), so this
        // shape goes native and no longer throws under NativeOnly. This used to pin the opposite (a fallback
        // decline); GroupBy_computed_key_runs_under_native (immediately below) already proves the same shape's
        // DATA correctness under plain Native mode — this test's remaining job is just confirming NativeOnly
        // doesn't throw.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_computed_key_goes_native_under_native_only));

        var result = db.Entities
            .GroupBy(o => o.OrderDate.Year)
            .Select(g => new { g.Key, C = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([(2020, 3), (2021, 2)], result.Select(r => (r.Key, r.C)).ToArray());
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
    public void GroupBy_computed_operand_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_computed_operand_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, T = g.Sum(o => o.Amount * 2) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("FR", 600m), ("UK", 150m), ("US", 600m)], result.Select(r => (r.Key, r.T)).ToArray());
    }

    [Fact]
    public void GroupBy_constant_operand_goes_native()
    {
        // g.Sum(o => 1) — a constant accumulator operand, mirroring EF Core's own GroupBy_Sum_constant.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_constant_operand_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Count = g.Sum(o => 1) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        // FR: 1 order, UK: 2 orders, US: 2 orders.
        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_accumulator_with_dollar_prefixed_string_constant_operand_goes_native()
    {
        // A bare string constant operand starting with "$" must be $literal-wrapped in the rendered $group
        // accumulator — MongoDB otherwise reads an unwrapped leading-"$" string as a FIELD PATH, which would
        // silently aggregate the wrong value instead of returning the literal string.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_accumulator_with_dollar_prefixed_string_constant_operand_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Marker = g.Max(o => "$CustomerID") })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal(
            [("FR", "$CustomerID"), ("UK", "$CustomerID"), ("US", "$CustomerID")],
            result.Select(r => (r.Key, r.Marker)).ToArray());
    }

    [Fact]
    public void GroupBy_cast_operand_goes_native()
    {
        // g.Sum(o => (long)o.Year) — a cast accumulator operand, mirroring EF Core's own
        // GroupBy_Sum_constant_cast / GroupBy_with_cast_inside_grouping_aggregate.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_cast_operand_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, YearSum = g.Sum(o => (long)o.Year) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        // FR: 2021. UK: 2020 + 2020 = 4040. US: 2020 + 2021 = 4041.
        Assert.Equal([("FR", 2021L), ("UK", 4040L), ("US", 4041L)], result.Select(r => (r.Key, r.YearSum)).ToArray());
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
    public void GroupBy_ef_property_key_goes_native_under_native_only()
    {
        // EF-322 SP1: a grouping key expressed as EF.Property<T>(o, "…") — a MethodCallExpression — used to
        // be a hard decline (NativeGroupByBinder.TryBindGroupKey's switch only recognized NewExpression/
        // MemberExpression), falling back to driver-LINQ; under NativeOnly that meant a throw. TryBindGroupKey
        // now routes through MongoExpressionTranslator.TryTranslateValue, which already resolves EF.Property
        // the same way an ordinary member access does, so this goes native instead.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_ef_property_key_goes_native_under_native_only));

        var result = db.Entities
            .GroupBy(o => EF.Property<string>(o, nameof(Order.Country)))
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
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
    public void GroupBy_OrderBy_key_before_select_goes_native()
    {
        // OrderBy composed BEFORE the terminal Select, over g.Key — the opposite composition order from
        // GroupBy_post_group_OrderBy_by_aggregate_matches_driver_linq above (which orders AFTER Select, over a
        // projected alias, and must keep falling back).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_key_before_select_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        Assert.Equal(
            [("FR", 1), ("UK", 2), ("US", 2)],
            result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_OrderBy_key_before_select_with_wrapped_key_only_no_aggregate_goes_native()
    {
        // A pending ordering that resolves via a KEY access (not an aggregate) combined with a wrapped
        // zero-accumulator projection — a newly-reachable shape (orderAccumulators stays empty here, so it
        // isn't excluded by the guard's orderAccumulators.Count > 0 clause), confirmed correct by the final
        // review: identical $sort-after-$group machinery to the already-shipped GroupBy_OrderBy_key_before_
        // select_goes_native, just without an accumulator alongside the key.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_key_before_select_with_wrapped_key_only_no_aggregate_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Key)
            .Select(g => new { g.Key })
            .ToList();

        Assert.Equal(["FR", "UK", "US"], result.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void GroupBy_OrderBy_aggregate_before_select_goes_native()
    {
        // Orders by the SAME aggregate the Select projects (Count) — the two accumulators are deliberately
        // NOT de-duplicated (see NativeGroupByBinder.TryBindGroupProjection's remarks); this proves that's
        // still correct, not just cheap.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_aggregate_before_select_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        // Ordered by Count ascending, then Country ascending as the tie-break: FR(1), UK(2), US(2).
        Assert.Equal(
            [("FR", 1), ("UK", 2), ("US", 2)],
            result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_OrderBy_different_aggregate_before_select_goes_native()
    {
        // Orders by Count() but projects Sum() — Count must still get its own $group accumulator even though
        // it is never flattened into the output.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_different_aggregate_before_select_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => new { g.Key, Total = g.Sum(o => o.Amount) })
            .ToList();

        Assert.Equal(
            [("FR", 300m), ("UK", 75m), ("US", 300m)],
            result.Select(r => (r.Key, r.Total)).ToArray());
    }

    [Fact]
    public void GroupBy_OrderByDescending_aggregate_before_select_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderByDescending_aggregate_before_select_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        // Descending by Count: UK/US (2) before FR (1); ties broken ascending by Country.
        Assert.Equal(
            [("UK", 2), ("US", 2), ("FR", 1)],
            result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_OrderBy_before_select_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_OrderBy_before_select_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_OrderBy_before_select_matches_driver_linq) + "D");

        (string Country, int Count, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .OrderBy(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(o => o.Amount) })
                .AsEnumerable()
                .Select(x => (x.Key, x.Count, x.Total)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_OrderBy_computed_expression_before_select_falls_back_under_native_only()
    {
        // A computed ordering expression (not a bare g.Key or a plain accumulator) is out of
        // NativeGroupByBinder.TryBindAccumulator's scope — must decline cleanly, not crash or silently drop
        // the ordering.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_computed_expression_before_select_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country)
                .OrderBy(g => g.Count() * 2)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList());
    }

    [Fact]
    public void GroupBy_Skip_before_select_goes_native_under_native_only()
    {
        // EF-322 SP6: this pin previously asserted the OPPOSITE — that Skip/Take composed DIRECTLY on the
        // ungrouped GroupBy result (before the terminal Select) were explicitly out of scope and must
        // decline. That is now this task's own target shape, so it goes native instead. An explicit OrderBy
        // anchors group order (a bare $group's own document order is otherwise unspecified) so this pin
        // stays deterministic.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_Skip_before_select_goes_native_under_native_only));

        var result = db.Entities.GroupBy(o => o.Country)
            .OrderBy(g => g.Key)
            .Skip(1)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        // Ordered by Key: FR, UK, US → Skip(1) => UK, US.
        Assert.Equal(
            [("UK", 2), ("US", 2)],
            result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_OrderBy_replacing_prior_OrderBy_before_select_uses_only_the_second()
    {
        // A second OrderBy (not ThenBy) composed on the ungrouped GroupBy result REPLACES the first sort key
        // entirely — matches ordinary (non-GroupBy) OrderBy.OrderBy semantics elsewhere in this provider.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_replacing_prior_OrderBy_before_select_uses_only_the_second));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Key)       // would sort FR, UK, US if not replaced
            .OrderBy(g => g.Count())   // REPLACES the above — sorts by Count instead
            .ThenBy(g => g.Key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        // If the first OrderBy leaked through, FR/UK/US (Country-ascending) would come first regardless of
        // Count. Correct (replaced) behavior sorts by Count first: FR(1), then UK(2)/US(2) tie-broken by
        // Country.
        Assert.Equal(
            [("FR", 1), ("UK", 2), ("US", 2)],
            result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_OrderBy_computed_expression_before_select_matches_driver_linq_under_native()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_OrderBy_computed_expression_before_select_matches_driver_linq_under_native) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_OrderBy_computed_expression_before_select_matches_driver_linq_under_native) + "D");

        (string Country, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.GroupBy(o => o.Country)
                .OrderBy(g => g.Count() * 2)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable()
                .Select(x => (x.Key, x.Count)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_after_source_side_OrderBy_goes_native()
    {
        // OrderBy composed on the SOURCE, before GroupBy — order doesn't affect a scalar aggregate's result,
        // so this is a pure no-op ahead of the $group, but must still translate (not decline).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_after_source_side_OrderBy_goes_native));

        var result = db.Entities
            .OrderBy(o => o.Amount)
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .ToList();

        // FR=300, UK=25+50=75, US=100+200=300.
        Assert.Equal([75m, 300m, 300m], result.OrderBy(x => x).ToList());
    }

    [Fact]
    public void GroupBy_after_source_side_OrderBy_Skip_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_after_source_side_OrderBy_Skip_goes_native));

        var result = db.Entities
            .OrderBy(o => o.Amount)
            .Skip(1)
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .ToList();

        // Ascending by Amount: 25(UK), 50(UK), 100(US), 200(US), 300(FR). Skip(1) drops 25(UK).
        // Remaining: UK=50, US=300, FR=300.
        Assert.Equal([50m, 300m, 300m], result.OrderBy(x => x).ToList());
    }

    [Fact]
    public void GroupBy_after_source_side_OrderBy_Take_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_after_source_side_OrderBy_Take_goes_native));

        var result = db.Entities
            .OrderBy(o => o.Amount)
            .Take(3)
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .ToList();

        // Ascending by Amount: 25(UK), 50(UK), 100(US), 200(US), 300(FR). Take(3) keeps 25(UK), 50(UK), 100(US).
        // FR has zero surviving rows, so it produces NO group at all (correct GroupBy semantics, not a bug).
        Assert.Equal([75m, 100m], result.OrderBy(x => x).ToList());
    }

    [Fact]
    public void GroupBy_after_source_side_OrderBy_reasserted_after_Skip_goes_native()
    {
        // Mirrors EF Core's own GroupBy_with_order_by_skip_and_another_order_by: an OrderBy/ThenBy, a Skip,
        // then the SAME OrderBy/ThenBy re-asserted (a common EF Core pattern for stable pagination before
        // further composition) — all still before the GroupBy.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_after_source_side_OrderBy_reasserted_after_Skip_goes_native));

        var result = db.Entities
            .OrderBy(o => o.Country)
            .ThenBy(o => o.Amount)
            .Skip(1)
            .OrderBy(o => o.Country)
            .ThenBy(o => o.Amount)
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .ToList();

        // Country then Amount ascending: FR/300, UK/25, UK/50, US/100, US/200. Skip(1) drops FR/300.
        // Remaining: UK=75, US=300. FR has zero surviving rows — no group for it.
        Assert.Equal([75m, 300m], result.OrderBy(x => x).ToList());
    }

    [Fact]
    public void GroupBy_after_source_side_OrderBy_Skip_Take_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_after_source_side_OrderBy_Skip_Take_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_after_source_side_OrderBy_Skip_Take_matches_driver_linq) + "D");

        (string Country, decimal Sum)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .OrderBy(o => o.Amount)
                .Skip(1)
                .Take(2)
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
                .AsEnumerable()
                .Select(x => (x.Key, x.Sum)).ToArray();

        var native = Run(nativeDb).OrderBy(x => x.Country).ToArray();
        // Ascending by Amount: 25(UK), 50(UK), 100(US), 200(US), 300(FR). Skip(1).Take(2) keeps 50(UK), 100(US).
        Assert.Equal([("UK", 50m), ("US", 100m)], native);
        Assert.Equal(Run(driverDb).OrderBy(x => x.Country).ToArray(), native);
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
    public void GroupBy_then_scalar_aggregate_goes_native()
    {
        // EF-149 generalized the post-group terminal-aggregate carve-out (previously bare-scalar-Select-only)
        // to any ordinary GroupBy(key).Select(aggregate), so this now goes native rather than declining — see
        // that commit's NativeCardinalityBinder changes. Still must not crash with KeyNotFoundException (the
        // original pre-guard bug) or return the wrong count.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_scalar_aggregate_goes_native));

        var count = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Country = g.Key, Total = g.Sum(o => o.Amount) })
            .Count();

        Assert.Equal(3, count);
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
    public void GroupBy_then_bare_Count_after_Skip_declines_instead_of_silently_dropping_paging()
    {
        // EF-322 fix round critical finding #1: GroupBy(key).Skip(1).Count() used to SILENTLY DROP the Skip
        // and count every group instead of the paged subset — the bare-terminal-aggregate path
        // (TryBindGroupTerminalAggregate) has no mechanism to apply paging before the $count. Must decline
        // under NativeOnly and, under Native, match driver-LINQ's correct (paged) count exactly.
        var seed = SeedOrders(); // US, UK, FR — 3 distinct Country groups
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_then_bare_Count_after_Skip_declines_instead_of_silently_dropping_paging) + "D");
        var driverLinq = driverDb.Entities.GroupBy(o => o.Country).Skip(1).Count();
        Assert.Equal(2, driverLinq); // 3 groups minus 1 skipped = 2, never 3 (the un-fixed bug's answer)

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_then_bare_Count_after_Skip_declines_instead_of_silently_dropping_paging) + "N");
        Assert.Equal(driverLinq, nativeDb.Entities.GroupBy(o => o.Country).Skip(1).Count());

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_bare_Count_after_Skip_declines_instead_of_silently_dropping_paging) + "O");
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => nativeOnlyDb.Entities.GroupBy(o => o.Country).Skip(1).Count());
    }

    [Fact]
    public void GroupBy_Skip_then_HAVING_declines_instead_of_misordering()
    {
        // EF-322 fix round critical finding #2: GroupBy(key).Skip(1).Where(g => g.Count() >= 2) used to apply
        // the HAVING filter BEFORE the Skip regardless of LINQ arrival order (MongoSelectLowerer always emits
        // GroupHavingPredicate ahead of GroupPagingOps) — the wrong evaluation order. Must decline under
        // NativeOnly and, under Native, match driver-LINQ's correct (Skip-then-filter) result exactly.
        var seed = SeedOrders(); // US(2), UK(2), FR(1)
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_Skip_then_HAVING_declines_instead_of_misordering) + "D");

        (string Key, int Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .OrderBy(g => g.Key) // deterministic: FR, UK, US
                .Skip(1) // drops FR — UK(2), US(2) remain
                .Where(g => g.Count() >= 2) // both survivors pass HAVING
                .Select(g => new { g.Key, Total = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Total)).ToArray();

        var driverLinq = Run(driverDb);
        Assert.Equal([("UK", 2), ("US", 2)], driverLinq);

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_Skip_then_HAVING_declines_instead_of_misordering) + "N");
        Assert.Equal(driverLinq, Run(nativeDb));

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Skip_then_HAVING_declines_instead_of_misordering) + "O");
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => Run(nativeOnlyDb));
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

    // EF-322: g.Select(e => e.Field).Distinct().<Op>() as a GroupBy accumulator — Count/LongCount/Average/Max
    // over the DISTINCT projected values within each group, not every row. $addToSet collects the distinct
    // values per group; the flatten projection reduces the resulting array ($size for Count/LongCount, $avg/
    // $max as an array-expression operator for the others). Seed: UK has TWO rows both with Year=2020 (a
    // genuine duplicate), so distinct Years for UK = {2020} (count 1, not 2) — this is load-bearing: it would
    // be WRONG (count 2) if Distinct were silently dropped and this fell through to an ordinary $sum:1/$avg/
    // $max over every row instead of the distinct set.
    [Fact]
    public void GroupBy_Select_with_distinct_aggregate_goes_native_and_dedups()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_Select_with_distinct_aggregate_goes_native_and_dedups));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new
            {
                g.Key,
                Count = g.Select(o => o.Year).Distinct().Count(),
                LongCount = g.Select(o => o.Year).Distinct().LongCount(),
                Average = g.Select(o => o.Year).Distinct().Average(),
                Max = g.Select(o => o.Year).Distinct().Max()
            })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal(
            [("FR", 1, 1L, 2021.0, 2021), ("UK", 1, 1L, 2020.0, 2020), ("US", 2, 2L, 2020.5, 2021)],
            result.Select(r => (r.Key, r.Count, r.LongCount, r.Average, r.Max)).ToArray());
    }

    [Fact]
    public void GroupBy_Select_with_distinct_aggregate_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_Select_with_distinct_aggregate_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_Select_with_distinct_aggregate_matches_driver_linq) + "D");

        (string Key, int Count, long LongCount, double Average, int Max)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new
                {
                    g.Key,
                    Count = g.Select(o => o.Year).Distinct().Count(),
                    LongCount = g.Select(o => o.Year).Distinct().LongCount(),
                    Average = g.Select(o => o.Year).Distinct().Average(),
                    Max = g.Select(o => o.Year).Distinct().Max()
                })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Count, r.LongCount, r.Average, r.Max))
                .ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("FR", 1, 1L, 2021.0, 2021), ("UK", 1, 1L, 2020.0, 2020), ("US", 2, 2L, 2020.5, 2021)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_Select_with_distinct_min_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_Select_with_distinct_min_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Min = g.Select(o => o.Year).Distinct().Min() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("FR", 2021), ("UK", 2020), ("US", 2020)], result.Select(r => (r.Key, r.Min)).ToArray());
    }

    [Fact]
    public void GroupBy_Select_with_distinct_aggregate_over_computed_selector_falls_back_under_native_only()
    {
        // A computed selector (not a bare member access) inside the Distinct is out of scope, matching the
        // pre-existing ordinary-accumulator guard for a computed Sum/Min/Max/Average operand.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_Select_with_distinct_aggregate_over_computed_selector_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Count = g.Select(o => o.Year * 2).Distinct().Count() })
                .ToList());
    }

    [Fact]
    public void GroupBy_empty_key_bare_aggregate_goes_native()
    {
        // GroupBy(o => new { }) groups every row into ONE group — a degenerate/zero-part key.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_bare_aggregate_goes_native));

        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => g.Sum(o => o.Amount))
            .ToList();

        // 100 + 200 + 50 + 25 + 300 = 675, one group.
        Assert.Equal([675m], result);
    }

    [Fact]
    public void GroupBy_empty_key_with_Key_readback_matches_driver_linq()
    {
        // EF-322 SP7 (Task 2): a bare g.Key readback over a zero-part key now goes native (TryGetKeyMemberPath
        // no longer unconditionally declines it) — resolving to the group's own empty "_id" document, the
        // correct readback for an empty anonymous-type key. Updated from this test's previous form (which
        // asserted a NativeOnly THROW here) now that the shape is native-eligible; still differentially
        // verified against driver-LINQ per the Native == DriverLinq invariant.
        var seed = SeedOrders();

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_with_Key_readback_matches_driver_linq) + "NO");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => new { })
            .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
            .ToList();
        Assert.Single(nativeOnlyResult);
        Assert.Equal(675m, nativeOnlyResult[0].Sum);

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_empty_key_with_Key_readback_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_empty_key_with_Key_readback_matches_driver_linq) + "D");

        decimal[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { })
                .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
                .AsEnumerable()
                .Select(x => x.Sum).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([675m], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_empty_key_bare_Key_readback_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_bare_Key_readback_goes_native));

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the bare g.Key read over a zero-part key went native.
        var results = db.Entities
            .GroupBy(o => new { })
            .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
            .ToList();

        Assert.Single(results);
        Assert.Equal(675m, results[0].Sum); // sum of SeedOrders()'s 5 Amount values: 100+200+50+25+300
    }

    [Fact]
    public void GroupBy_empty_key_Where_on_bare_Key_goes_native()
    {
        // Review Focus: a zero-part key's g.Key reaching the HAVING/terminal-predicate path
        // (TryBindGroupSideOperand) with an EMPTY keyParts list must not crash.
        //
        // Deviation from the plan brief's literal test body: the brief asserted Assert.Single(results) here,
        // reasoning that "g.Key == null" is semantically non-restrictive. Running this differentially against
        // driver-LINQ shows both agree on ZERO rows — "g.Key == null" IS restrictive: an anonymous-type
        // instance is never null, so the predicate is false for the (one) group and correctly filters it out,
        // both natively (comparing the group's "_id": {} to null — not equal) and via the driver-LINQ
        // fallback. Asserting Assert.Single would have been a genuine behavioral regression, not a match for
        // this plan's Native == DriverLinq invariant. The crash-avoidance goal (no IndexOutOfRangeException
        // from an empty keyParts list reaching TryBindGroupSideOperand) is unaffected — proven below by the
        // NativeOnly leg completing without throwing.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_Where_on_bare_Key_goes_native));

        var results = db.Entities
            .GroupBy(o => new { })
            .Where(g => g.Key == null)
            .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
            .ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void GroupBy_empty_key_Where_on_bare_Key_against_non_null_value_declines_to_driver_linq()
    {
        // EF-322 SP7 fix-wave (Finding I1): a zero-part key's g.Key resolves to "_id" (Task 2) with no single
        // backing property to serialize the comparison's OTHER side against. Admitting a comparison against
        // anything other than a literal null here would defer a crash to pipeline-RENDER time (the generic
        // BsonValue.Create fallback throwing ArgumentException for a non-BSON-mappable CLR value) instead of
        // declining cleanly at bind time. `sentinel` is captured (not a literal), so it lowers to a
        // closure-field member access / query parameter, never a ConstantExpression — the same "captured
        // value, not a literal" shape SP1's own Guid-key tests already establish lowers this way.
        var seed = SeedOrders();
        // Same (compiler-shared, zero-member) anonymous type as the GroupBy key below, NOT upcast to
        // `object` — comparing against an `object`-typed sentinel instead hits an unrelated, pre-existing
        // driver-LINQ bridge bug (InvalidCastException serializing an `object`-typed constant through the
        // anonymous type's OWN serializer), which is not what this test is pinning.
        var sentinel = new { };

        List<decimal> Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { })
                .Where(g => g.Key == sentinel)
                .Select(g => new { g.Key, Sum = g.Sum(o => o.Amount) })
                .AsEnumerable()
                .Select(x => x.Sum).ToList();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_empty_key_Where_on_bare_Key_against_non_null_value_declines_to_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_empty_key_Where_on_bare_Key_against_non_null_value_declines_to_driver_linq) + "D");

        var native = Run(nativeDb);
        // Both `g.Key` and `sentinel` serialize to the SAME empty BSON document ({}) — MongoDB (and the
        // driver-LINQ fallback's own client-side evaluation) treats that as equal, so the single group
        // matches. This test isn't pinning that outcome; it's pinning that reaching it doesn't CRASH, and
        // that Native and DriverLinq modes agree (the Native == DriverLinq invariant).
        Assert.Equal([675m], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_Where_on_bare_Key_against_non_null_value_declines_to_driver_linq) + "NO");
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnlyDb));
    }

    private class CountryYearKey
    {
        public string Country { get; }
        public int Year { get; }
        public CountryYearKey(string country, int year) { Country = country; Year = year; }
        public override bool Equals(object? obj) => obj is CountryYearKey k && k.Country == Country && k.Year == Year;
        public override int GetHashCode() => HashCode.Combine(Country, Year);
    }

    [Fact]
    public void GroupBy_constructor_call_key_does_not_collapse_to_one_group()
    {
        // Regression guard for the final-review finding: a constructor-call key (Members == null, same as a
        // zero-member new{}) must NOT be mistaken for a degenerate empty key. Differential test — asserts the
        // native and driver-LINQ ROW COUNTS match, since a row-count-blind assertion wouldn't have caught this.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_constructor_call_key_does_not_collapse_to_one_group) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_constructor_call_key_does_not_collapse_to_one_group) + "D");

        int[] RunCounts(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new CountryYearKey(o.Country, o.Year))
                .Select(g => new { Count = g.Count() })
                .AsEnumerable()
                .Select(x => x.Count)
                .OrderBy(c => c)
                .ToArray();

        var native = RunCounts(nativeDb);
        // 5 rows, grouped by (Country, Year): (US,2020)=1, (US,2021)=1, (UK,2020)=2, (FR,2021)=1 → 4 groups.
        Assert.Equal(4, native.Length);
        Assert.Equal(RunCounts(driverDb), native);
    }

    [Fact]
    public void GroupBy_empty_key_then_Count_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_then_Count_goes_native));

        var result = db.Entities.GroupBy(o => new { }).Count();

        Assert.Equal(1, result); // one group (the whole non-empty collection), so exactly one "group exists" row
    }

    [Fact]
    public void GroupBy_empty_key_then_Count_over_empty_collection_goes_native()
    {
        using var db = CreateContext([], MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_then_Count_over_empty_collection_goes_native));

        var result = db.Entities.GroupBy(o => new { }).Count();

        Assert.Equal(0, result); // no rows at all → no groups
    }

    [Fact]
    public void GroupBy_anonymous_key_only_with_no_aggregate_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_anonymous_key_only_with_no_aggregate_goes_native));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal(["FR", "UK", "US"], result.Select(r => r.Key).ToArray());
    }

    [Fact]
    public void GroupBy_dollar_prefixed_string_parameter_key_groups_by_literal_value_not_field()
    {
        // Final-review fix: a captured-parameter (or constant) string key that happens to look like a field
        // path ("$Country") used to render as $group's bare _id value UNWRAPPED — the server would then
        // reinterpret it as a genuine field reference instead of the literal string, silently grouping by the
        // Country FIELD instead of the literal value "$Country". MongoPipelineFactory.RenderKeyedGroup now
        // $literal-wraps any constant/parameter key part, matching the identical existing convention for
        // accumulator operands and $set values. Every seeded row shares this literal key, so a correct
        // translation groups everything into exactly ONE group — a wrong (field-reinterpreted) grouping would
        // instead produce one group per distinct Country (three groups, per SeedOrders' FR/UK/US rows).
        var groupKeyLiteral = "$Country";
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_dollar_prefixed_string_parameter_key_groups_by_literal_value_not_field));

        var result = db.Entities
            .GroupBy(o => groupKeyLiteral)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        var row = Assert.Single(result);
        Assert.Equal("$Country", row.Key);
        Assert.Equal(SeedOrders().Length, row.Count);
    }

    [Fact]
    public void GroupBy_MemberInit_dto_key_goes_native_and_matches_driver_linq()
    {
        // EF-322 SP7: a MemberInitExpression (DTO object-initializer) GroupBy key now IS a shape
        // TryBindGroupKey builds as a genuine named composite key — its own MemberInitExpression case binds
        // each MemberAssignment through TryBindKeyPartValue, the SAME helper the anonymous-type composite-key
        // case already uses, so this now goes native (correct results, parity with DriverLinq) under Native,
        // and succeeds rather than throwing under NativeOnly. (Previously this fell back to driver-LINQ under
        // Native and threw NativeTranslationNotSupportedException under NativeOnly — see git history for that
        // superseded behavior and its own FormatException-avoidance backstory.)
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_MemberInit_dto_key_goes_native_and_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_MemberInit_dto_key_goes_native_and_matches_driver_linq) + "D");

        (int Year, string Country, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new GroupKeyDto { Year = o.Year, Country = o.Country })
                .Select(g => new { g.Key.Year, g.Key.Country, Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Year).ThenBy(r => r.Country)
                .Select(r => (r.Year, r.Country, r.Count))
                .ToArray();

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);
        Assert.NotEmpty(native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_MemberInit_dto_key_goes_native_and_matches_driver_linq) + "O");

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the MemberInitExpression key selector went native.
        var nativeOnly = nativeOnlyDb.Entities
            .GroupBy(o => new GroupKeyDto { Year = o.Year, Country = o.Country })
            .Select(g => new { g.Key.Year, g.Key.Country, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Year).ThenBy(r => r.Country)
            .Select(r => (r.Year, r.Country, r.Count))
            .ToArray();
        Assert.Equal(native, nativeOnly);
    }

    private class GroupKeyDto
    {
        public int Year { get; set; }
        public string Country { get; set; } = "";
    }

    [Fact]
    public void GroupBy_HAVING_on_accumulator_before_select_goes_native_under_native_only()
    {
        // EF-322 SP2: a Where composed BETWEEN GroupBy(key) and the terminal Select (a true HAVING clause,
        // referencing g.Count() on the IGrouping directly) — distinct from this file's existing
        // GroupBy_HAVING_on_..._matches_driver_linq tests, whose Where runs AFTER the Select over the
        // flattened alias instead (a different, already-working shape).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_HAVING_on_accumulator_before_select_goes_native_under_native_only));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        Assert.Equal([("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_HAVING_on_key_before_select_goes_native_under_native_only()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_HAVING_on_key_before_select_goes_native_under_native_only));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Where(g => g.Key == "FR")
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        var row = Assert.Single(result);
        Assert.Equal("FR", row.Key);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public void GroupBy_HAVING_on_Guid_key_matches_driver_linq()
    {
        // Final-review fix: a HAVING key comparison against a Guid key used to throw
        // ArgumentException(".NET type System.Guid cannot be mapped to a BsonValue") under Native — the
        // comparison's constant now serializes through the key's own property, matching driver-LINQ.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", ExternalId = idA },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", ExternalId = idA },
            new Order { Id = ObjectId.GenerateNewId(), Country = "UK", ExternalId = idB },
        };

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_HAVING_on_Guid_key_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_HAVING_on_Guid_key_matches_driver_linq) + "D");

        (Guid Key, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.ExternalId)
                .Where(g => g.Key == idA)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable()
                .Select(r => (r.Key, r.Count)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([(idA, 2)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_HAVING_on_Guid_key_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.ExternalId)
            .Where(g => g.Key == idA)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();
        Assert.Equal([(idA, 2)], nativeOnlyResult.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void Nested_GroupBy_preserves_first_level_HAVING_matches_driver_linq()
    {
        // Final-review fix: SnapshotPriorGroupingForNestedGroupBy used to move Grouping/Projection aside for
        // a nested GroupBy but leave GroupHavingPredicate behind — the outer GroupBy's own TryBindGroupProjection
        // then unconditionally overwrote it with its own (here, absent) HAVING, silently dropping the FIRST
        // GroupBy's filter and returning every group instead of just the ones that passed HAVING.
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US" },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US" },
            new Order { Id = ObjectId.GenerateNewId(), Country = "UK" },
            new Order { Id = ObjectId.GenerateNewId(), Country = "UK" },
            new Order { Id = ObjectId.GenerateNewId(), Country = "FR" },
        };

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Nested_GroupBy_preserves_first_level_HAVING_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Nested_GroupBy_preserves_first_level_HAVING_matches_driver_linq) + "D");

        (int Key, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Where(g => g.Count() > 1) // drops FR (1 row) — only US(2) and UK(2) survive
                .Select(g => new { g.Key, C = g.Count() })
                .GroupBy(x => x.C)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .AsEnumerable()
                .Select(r => (r.Key, r.Count)).ToArray();

        var native = Run(nativeDb);
        // Without the fix: FR's group survives the (silently dropped) first HAVING too, so the SECOND
        // GroupBy sees three {Country,C} rows (C=2,2,1) instead of two (C=2,2) — producing an EXTRA group
        // for C=1. With the fix: only {US,2} and {UK,2} survive, both fold into ONE group keyed by C=2.
        Assert.Equal([(2, 2)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Nested_GroupBy_after_first_level_paging_declines_instead_of_dropping_the_paging()
    {
        // EF-322 fix round: the first GroupBy's own Skip is recorded into GroupPagingOps, but
        // SnapshotPriorGroupingForNestedGroupBy only moves Grouping/Projection/GroupHavingPredicate aside —
        // GroupPagingOps stays behind, and the outer GroupBy's own TryBindGroupProjection call unconditionally
        // resets GroupPagingOps from its own (empty) PendingGroupPaging, silently discarding the first
        // GroupBy's paging. Must decline under NativeOnly and, under Native, match driver-LINQ exactly rather
        // than silently including the skipped group.
        var seed = SeedOrders(); // US: 2 orders, UK: 2 orders, FR: 1 order

        (int Key, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .OrderBy(g => g.Key) // deterministic order: FR, UK, US — Skip(1) always drops FR
                .Skip(1)
                .Select(g => new { g.Key, C = g.Count() })
                .GroupBy(x => x.C)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Count)).ToArray();

        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Nested_GroupBy_after_first_level_paging_declines_instead_of_dropping_the_paging) + "D");
        var driverLinq = Run(driverDb);
        // FR (C=1) dropped by Skip(1); UK(C=2) and US(C=2) remain, folding into ONE group keyed by C=2. The
        // discriminator that actually catches the bug (paging silently dropped, so all 3 Country groups reach
        // the outer GroupBy instead of 2) is the total row count the outer grouping sees: 2 with paging
        // correctly applied, 3 without (which would instead produce two outer groups, {C=2: 2, C=1: 1}).
        Assert.Equal([(2, 2)], driverLinq);

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Nested_GroupBy_after_first_level_paging_declines_instead_of_dropping_the_paging) + "N");
        Assert.Equal(driverLinq, Run(nativeDb));

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Nested_GroupBy_after_first_level_paging_declines_instead_of_dropping_the_paging) + "O");
        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => Run(nativeOnlyDb));
    }

    [Fact]
    public void GroupBy_HAVING_with_query_parameter_value_matches_driver_linq()
    {
        // Final-review coverage gap: every other HAVING test in this file uses a literal constant for the
        // comparison's non-key/non-accumulator side — this one uses a genuine captured local, which EF Core's
        // real query pipeline parameterizes (unlike a hand-built unit-test expression tree, this goes through
        // EF's actual ParameterExtractingExpressionVisitor), exercising the MongoParameterExpression arm of
        // TryTranslateComparisonConstant, not just the MongoConstantExpression one.
        var minCount = 1;
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_HAVING_with_query_parameter_value_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_HAVING_with_query_parameter_value_matches_driver_linq) + "D");

        (string Key, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Where(g => g.Count() > minCount)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Count)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("UK", 2), ("US", 2)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_Count_with_predicate_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_Count_with_predicate_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_Count_with_predicate_matches_driver_linq) + "D");

        (string Key, int Small, int Large)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Small = g.Count(o => o.Amount < 100), Large = g.Count(o => o.Amount >= 100) })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Small, r.Large)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: US 100+200 (both >=100, Large), UK 50+25 (both <100, Small), FR 300 (>=100, Large).
        Assert.Equal([("FR", 0, 1), ("UK", 2, 0), ("US", 0, 2)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Count_with_predicate_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Small = g.Count(o => o.Amount < 100), Large = g.Count(o => o.Amount >= 100) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Small, r.Large)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_Where_then_Sum_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_Where_then_Sum_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_Where_then_Sum_matches_driver_linq) + "D");

        (string Key, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Total = g.Where(o => o.Amount > 60).Sum(o => o.Amount) })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Total)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: US 100+200, UK 50+25, FR 300 (see this file's own SeedOrders). Only amounts > 60
        // contribute: US=100+200=300, UK=0 (both 50 and 25 excluded), FR=300.
        Assert.Equal([("FR", 300m), ("UK", 0m), ("US", 300m)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Where_then_Sum_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { g.Key, Total = g.Where(o => o.Amount > 60).Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Total)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_ternary_projection_over_key_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_ternary_projection_over_key_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_ternary_projection_over_key_matches_driver_linq) + "D");

        (string Label, decimal Total)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Label = g.Key == "US" ? "domestic" : "international", Total = g.Sum(o => o.Amount) })
                .AsEnumerable()
                .OrderBy(r => r.Label).ThenBy(r => r.Total)
                .Select(r => (r.Label, r.Total)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: US 100+200=300 (domestic). GroupBy(Country) still groups UK and FR SEPARATELY (the
        // ternary only relabels each group's projection; it does not merge groups), so UK (50+25=75) and FR
        // (300) each surface as their own "international" row, not combined into 375.
        Assert.Equal([("domestic", 300m), ("international", 75m), ("international", 300m)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_ternary_projection_over_key_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Label = g.Key == "US" ? "domestic" : "international", Total = g.Sum(o => o.Amount) })
            .AsEnumerable()
            .OrderBy(r => r.Label).ThenBy(r => r.Total)
            .Select(r => (r.Label, r.Total)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_coalesce_projection_over_key_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_coalesce_projection_over_key_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_coalesce_projection_over_key_matches_driver_linq) + "D");

        (string Locality, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Locality = g.Key ?? "Unknown", Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Locality)
                .Select(r => (r.Locality, r.Count)).ToArray();

        var native = Run(nativeDb);
        // SeedOrders: FR has 1 row, UK has 2, US has 2 — Country is never null in this fixture, so the
        // coalesce's fallback branch is never actually taken, but the SHAPE is still exercised and proven
        // equivalent to the driver-LINQ fallback.
        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_coalesce_projection_over_key_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Locality = g.Key ?? "Unknown", Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Locality)
            .Select(r => (r.Locality, r.Count)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_projection_ternary_on_Guid_key_comparison_matches_driver_linq()
    {
        // SP4 final-review fix: TryTranslateGroupProjectionConditionOrValue's key-vs-constant comparison arms
        // (the PROJECTION-side ternary test, as opposed to HAVING's TryBindGroupSideOperand or the
        // accumulator-condition arm) used to translate the comparison's constant with forSerialization: null —
        // the exact bug class already fixed once for HAVING (GroupBy_HAVING_on_Guid_key_matches_driver_linq)
        // and for a Count(predicate) accumulator condition (a unit test) — throwing
        // ArgumentException(".NET type System.Guid cannot be mapped to a BsonValue") under Native instead of
        // returning the correct rows.
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), ExternalId = idA },
            new Order { Id = ObjectId.GenerateNewId(), ExternalId = idA },
            new Order { Id = ObjectId.GenerateNewId(), ExternalId = idB },
        };

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_projection_ternary_on_Guid_key_comparison_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_projection_ternary_on_Guid_key_comparison_matches_driver_linq) + "D");

        (string Label, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.ExternalId)
                .Select(g => new { Label = g.Key == idA ? "match" : "other", Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Label)
                .Select(r => (r.Label, r.Count)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("match", 2), ("other", 1)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_projection_ternary_on_Guid_key_comparison_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.ExternalId)
            .Select(g => new { Label = g.Key == idA ? "match" : "other", Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Label)
            .Select(r => (r.Label, r.Count)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Fact]
    public void GroupBy_coalesce_projection_dollar_prefixed_literal_matches_driver_linq()
    {
        // SP4 final-review fix: a "$"-prefixed constant used as a $ifNull/$cond BRANCH used to render
        // unwrapped, so MongoDB read the string as a field-path reference instead of the literal value it is —
        // under Native, `g.Key ?? "$Year"` silently returned null instead of the literal string "$Year" for
        // the null-keyed group. Country is nullable at the metadata level (no NRT-derived Required convention
        // in this project) — reused rather than adding a new field, seeding one row with a null Country to
        // exercise the coalesce's fallback branch for real (the pre-existing
        // GroupBy_coalesce_projection_over_key_matches_driver_linq test's own SeedOrders fixture never has a
        // null Country, so it only proves the SHAPE, never the fallback value itself).
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = null! },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US" },
        };

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_coalesce_projection_dollar_prefixed_literal_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_coalesce_projection_dollar_prefixed_literal_matches_driver_linq) + "D");

        (string Locality, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { Locality = g.Key ?? "$Year", Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Locality)
                .Select(r => (r.Locality, r.Count)).ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("$Year", 1), ("US", 1)], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_coalesce_projection_dollar_prefixed_literal_matches_driver_linq) + "O");
        var nativeOnlyResult = nativeOnlyDb.Entities
            .GroupBy(o => o.Country)
            .Select(g => new { Locality = g.Key ?? "$Year", Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Locality)
            .Select(r => (r.Locality, r.Count)).ToArray();
        Assert.Equal(native, nativeOnlyResult);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_accumulator_then_outer_Min_goes_native(MongoQueryMode mode)
    {
        // MinMax_after_GroupBy_aggregate's exact shape: GroupBy(key).Select(g => g.Sum(...)).Min() — a
        // bare, no-selector Min() reducing the ALREADY-flattened per-group Sum. Per-country sums: US=300,
        // UK=75, FR=300 — Min is 75.
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(Bare_accumulator_then_outer_Min_goes_native) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .Min();
        Assert.Equal(75m, result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_accumulator_then_outer_Max_goes_native(MongoQueryMode mode)
    {
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(Bare_accumulator_then_outer_Max_goes_native) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => g.Sum(o => o.Amount))
            .Max();
        Assert.Equal(300m, result);
    }

    [Fact]
    public void Bare_accumulator_then_outer_Min_over_empty_source_throws()
    {
        // Review Focus: Min()/Max() over an empty result must keep BuildEmptyBehavior's existing contract —
        // decimal is non-nullable, so this throws InvalidOperationException, matching in-memory LINQ and the
        // driver-LINQ fallback, not silently returning default(decimal).
        using var db = CreateContext([], MongoQueryMode.NativeOnly, nameof(Bare_accumulator_then_outer_Min_over_empty_source_throws));

        Assert.Throws<InvalidOperationException>(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => g.Sum(o => o.Amount)).Min());
    }

    [Fact]
    public void Bare_accumulator_then_outer_Sum_still_declines_under_NativeOnly()
    {
        // Review Focus: Task 2 deliberately scopes its new binder condition to Min/Max only — a selector-less
        // Sum()/Average() in the SAME position must keep declining (falling back), not silently be admitted
        // as an untested side effect of widening isPostGroupTerminalAggregate.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_accumulator_then_outer_Sum_still_declines_under_NativeOnly));

        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => g.Sum(o => o.Amount)).Sum());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void GroupBy_skip_0_take_0_goes_native_and_returns_empty(MongoQueryMode mode)
    {
        // GroupBy_skip_0_take_0_aggregate's exact shape.
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(GroupBy_skip_0_take_0_goes_native_and_returns_empty) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .Skip(0)
            .Take(0)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToArray();

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void GroupBy_repeated_Skip_on_bare_result_goes_native(MongoQueryMode mode)
    {
        // Review Focus: repeated Skip on a bare GroupBy result must accumulate in arrival order, not
        // overwrite. Skip(1) then Skip(1) again over 3 distinct countries (US/UK/FR) skips 1+1=2, leaving 1.
        // An explicit OrderBy pins WHICH one survives deterministically — MongoDB's own $group with no $sort
        // has unspecified row order (confirmed flaky without this: the same query against the same data can
        // return groups in a different order run to run), so asserting a specific surviving key/count
        // requires fixing the order first. FR, UK, US ascending — Skip(1).Skip(1) drops FR and UK, leaving US.
        var seed = SeedOrders();
        using var db = CreateContext(seed, mode, nameof(GroupBy_repeated_Skip_on_bare_result_goes_native) + mode);

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Key)
            .Skip(1)
            .Skip(1)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToArray();

        var group = Assert.Single(result);
        Assert.Equal("US", group.Key);
        Assert.Equal(2, group.Total);
    }

    [Fact]
    public void GroupBy_Select_then_Skip_0_take_0_unaffected_by_pending_paging_carveout()
    {
        // Review Focus: paging composed AFTER the terminal Select (the OPPOSITE order) is NOT natively
        // supported for a genuine GroupBy — PostGroupOps' own EF-322 carve-out is scoped to a projected
        // Distinct only (see NativeSlotPopulator's isPostDistinctSlot: "!mongoQ.Select.IsGroupBy"), so this
        // shape still cleanly declines (falls back, throwing under NativeOnly) exactly as before this task.
        // Deviation from the brief's literal Step 9 text, which described this as an "ALREADY-supported,
        // pre-existing PostGroupOps shape" that should return Assert.Empty — running it showed that premise
        // is false for a genuine GroupBy; this task's own carve-out (pre-Select only) must not be the thing
        // that changes that, which is exactly what this test now proves.
        var seed = SeedOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_Select_then_Skip_0_take_0_unaffected_by_pending_paging_carveout));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Total = g.Count() })
                .Skip(0)
                .Take(0)
                .ToArray());
    }
}
