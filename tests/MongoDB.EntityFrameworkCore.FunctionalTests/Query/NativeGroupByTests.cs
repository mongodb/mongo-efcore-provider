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
/// Native <c>GroupBy(key).Select(aggregate)</c> → <c>$group</c>: supported grouped projections execute natively
/// with correct rows; unsupported shapes fall back under <see cref="MongoQueryMode.Native"/> and throw
/// <see cref="NativeTranslationNotSupportedException"/> under <see cref="MongoQueryMode.NativeOnly"/>, which is
/// the "went native" signal (the MQL is otherwise indistinguishable from the fallback).
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
        // A computed key (o.OrderDate.Year) translates via TryTranslateValue, so NativeOnly doesn't throw.
        // Data correctness: GroupBy_computed_key_runs_under_native.
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

        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void GroupBy_accumulator_with_dollar_prefixed_string_constant_operand_goes_native()
    {
        // A "$"-prefixed string constant operand must be $literal-wrapped in the $group accumulator, or MongoDB
        // reads it as a field path and silently aggregates the wrong value.
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
        // A key with a non-default BsonRepresentation (enum as string) must not go native: the grouped shaper
        // reads _id through a CLR-type serializer and would throw at materialization. It falls back instead.
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

        // Native: falls back and matches DriverLinq.
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
        // The aggregate's source is a correlated subquery over the DbSet, not g. Binding it to a $group
        // accumulator would silently return the group's row count, so the shape must fall back (throw under
        // NativeOnly). Binder-level: NativeGroupByBinderTests.*_over_non_grouping_source_returns_false.
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
        // GroupBy then a Join projecting the joined entity: not representable natively, and the driver-LINQ
        // fallback silently returns empty joined entities, so it must fail cleanly. Mirrors the spec suite's
        // GroupBy_Aggregate_Join.
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
        // The clean failure applies only to Native/NativeOnly; explicit DriverLinq still executes via the driver.
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
        // Join-then-GroupBy with a scalar aggregate: the fallback returns correct data (equal to DriverLinq), so it
        // must not be forced to throw — the fallback-unsafe marker is scoped to group-then-join.
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
        // A plain-member key with a lone Count() goes native: EF doesn't fuse it into GroupBy(key,
        // resultSelector), and Count() → $sum: 1 is supported.
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
        // An EF.Property<T>(o, "…") key translates via TryTranslateValue like an ordinary member access.
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
        // An accumulator member named "_id" collides with $group's key field; unguarded it throws a BSON
        // duplicate-key error at pipeline build. Must fall back under Native (matching DriverLinq) and throw
        // NativeTranslationNotSupportedException under NativeOnly.
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
        // A post-group Where (HAVING) on an aggregate alias ("Amount") that collides with an entity property.
        // Resolved by member name against the entity, it would emit a pre-$group $match on the raw field
        // (US=200 instead of 300) — silently wrong. Must fall back so Native == DriverLinq.
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
        // Same HAVING shape with a non-colliding alias ("Total"); pinned so a future translator change that starts
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
        // Post-group OrderBy by an aggregate alias must fall back — a native $sort here would sort pre-group rows.
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
        // Post-group Skip/Take must fall back cleanly and match DriverLinq.
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
        // The supported shape with no post-group operator must still go native — if the post-group guard were
        // mis-scoped to the aggregate Select itself, this would throw.
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
        // OrderBy over g.Key before the terminal Select — the opposite order from
        // GroupBy_post_group_OrderBy_by_aggregate_matches_driver_linq, which must fall back.
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
        // A key ordering with a wrapped zero-accumulator projection (orderAccumulators empty): same
        // $sort-after-$group machinery as GroupBy_OrderBy_key_before_select_goes_native.
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
        // Orders by the same aggregate the Select projects; the two accumulators are intentionally not
        // de-duplicated (see NativeGroupByBinder.TryBindGroupProjection) — pins that this is still correct.
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
        // Orders by Count() but projects Sum(): Count still needs its own $group accumulator.
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
        // A computed ordering expression is outside TryBindAccumulator's scope — must decline cleanly, not crash
        // or drop the ordering.
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
        // Skip/Take directly on the GroupBy result (before the terminal Select) goes native. The OrderBy anchors
        // group order, which a bare $group leaves unspecified.
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
        // A second OrderBy (not ThenBy) on the GroupBy result replaces the first sort key.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_OrderBy_replacing_prior_OrderBy_before_select_uses_only_the_second));

        var result = db.Entities
            .GroupBy(o => o.Country)
            .OrderBy(g => g.Key)       // would sort FR, UK, US if not replaced
            .OrderBy(g => g.Count())   // REPLACES the above — sorts by Count instead
            .ThenBy(g => g.Key)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList();

        // If the first OrderBy leaked through, Country order would win; replaced behavior sorts by Count first.
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
        // OrderBy on the source before GroupBy doesn't affect a scalar aggregate, but must still translate.
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
        // FR has no surviving rows, so no group.
        Assert.Equal([75m, 100m], result.OrderBy(x => x).ToList());
    }

    [Fact]
    public void GroupBy_after_source_side_OrderBy_reasserted_after_Skip_goes_native()
    {
        // Mirrors EF Core's GroupBy_with_order_by_skip_and_another_order_by: OrderBy/ThenBy, Skip, then the same
        // OrderBy/ThenBy again, all before the GroupBy.
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
        // Remaining: UK=75, US=300; no group for FR.
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
    // Scalar aggregates / reducers after a finalized GroupBy(key).Select(anon) must fall back. Unguarded,
    // Cardinality on a grouped select flips Route to ScalarAggregate while the lowerer still emits
    // [$group, $project] with no terminal stage, so the scalar shaper crashes with KeyNotFoundException ("v").
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void GroupBy_then_Count_matches_driver_linq()
    {
        // Post-group Count() over GroupBy(key).Select(anon): must fall back so Native == DriverLinq (3 groups).
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
        // A post-group reducer (First) falls back and stays correct. The OrderBy makes the pick deterministic.
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
        // A post-group Single over a filtered-to-one grouped result falls back and matches DriverLinq.
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
        // Post-group Any falls back and stays correct.
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
        // The post-group terminal-aggregate carve-out covers any GroupBy(key).Select(aggregate), so this goes
        // native; it must not crash with KeyNotFoundException or return the wrong count.
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
        // A second projected Select after a native GroupBy(key).Select(aggregate) bypasses the IsGroupBy guards
        // (the shaper is no longer a GroupByShaperExpression). Unguarded, TryPopulateNativeProjection would append
        // onto the grouped Projection and the lowerer would read fields gone after $group → null rows.
        // In practice the shape throws during translation in every mode (MongoProjectionBindingExpressionVisitor
        // can't bind the nested ProjectionBindingExpression); this pins that Native fails identically to
        // DriverLinq rather than returning null rows.
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
    // GroupBy(key).{Count()|LongCount()|Any()|Any(pred)|All(pred)|Count(pred)|LongCount(pred)} with no
    // intervening Select — the spec suite's "GroupBy_without_aggregate" family. NativeOnly success is the proof.
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
        // GroupBy(key).Skip(1).Count(): the bare-terminal-aggregate path (TryBindGroupTerminalAggregate) can't
        // page before $count and would silently count every group. Must decline under NativeOnly and match
        // driver-LINQ's paged count under Native.
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
        // GroupBy(key).Skip(1).Where(g => g.Count() >= 2): the lowerer emits GroupHavingPredicate before
        // GroupPagingOps, which would filter before skipping. Must decline under NativeOnly and match driver-LINQ
        // under Native.
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
        // A compound (&&) predicate over multiple group-level aggregates is out of scope; must fall back, not
        // be mistranslated.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_then_Any_with_compound_predicate_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.GroupBy(o => o.Country).Any(g => g.Count() > 1 && g.Count() < 10));
    }

    [Fact]
    public void GroupBy_aggregate_projection_with_count_still_goes_native_after_guard()
    {
        // GroupBy(key).Select(anonymous-with-Count) is bound by TryBindGroupProjection, not the cardinality
        // binder, so the cardinality guard must not touch it. Seed => US=2, UK=2, FR=1.
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

    // g.Select(e => e.Field).Distinct().<Op>() as a GroupBy accumulator: $addToSet collects distinct values per
    // group, then $size/$avg/$max reduces the array. UK has two rows with Year=2020, so its distinct count is 1;
    // a dropped Distinct would give 2.
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
        // A computed selector inside the Distinct is out of scope, like a computed Sum/Min/Max/Average operand.
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
        // GroupBy(o => new { }) puts every row in one group (zero-part key).
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
        // A bare g.Key over a zero-part key goes native, reading the group's empty "_id" document. Checked
        // against driver-LINQ.
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
        // A zero-part key's g.Key reaching TryBindGroupSideOperand with empty keyParts must not crash.
        // "g.Key == null" is restrictive (an anonymous instance is never null), so both modes return zero rows.
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
        // A zero-part key's g.Key has no backing property to serialize the other side against, so comparing it
        // to anything but a literal null must decline at bind time rather than crash at render time
        // (BsonValue.Create ArgumentException). `sentinel` is captured, so it lowers to a parameter.
        var seed = SeedOrders();
        // Same compiler-shared anonymous type as the key, not upcast to `object` — an `object`-typed sentinel
        // hits an unrelated driver-LINQ bridge InvalidCastException.
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
        // Both sides serialize to {}, which compare equal, so the one group matches. The point is no crash and
        // Native == DriverLinq.
        Assert.Equal([675m], native);
        Assert.Equal(Run(driverDb), native);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_Where_on_bare_Key_against_non_null_value_declines_to_driver_linq) + "NO");
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnlyDb));
    }

    [Fact]
    public void GroupBy_empty_key_Key_null_check_inside_accumulator_goes_native()
    {
        // A zero-part key's g.Key inside an accumulator condition must not index an empty key-part list. The key is always the non-null empty document {}, so "== null" counts no
        // rows and "!= null" counts every row. Checked against driver-LINQ.
        var seed = SeedOrders();

        (int IsNull, int NotNull, decimal FilteredSum)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { })
                .Select(g => new
                {
                    IsNull = g.Count(o => g.Key == null),
                    NotNull = g.Count(o => null != g.Key),
                    FilteredSum = g.Where(o => g.Key != null).Sum(o => o.Amount)
                })
                .AsEnumerable()
                .Select(x => (x.IsNull, x.NotNull, x.FilteredSum))
                .ToArray();

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_Key_null_check_inside_accumulator_goes_native) + "NO");
        var native = Run(nativeOnlyDb);
        Assert.Equal([(0, 5, 675m)], native);

        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_empty_key_Key_null_check_inside_accumulator_goes_native) + "D");
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_empty_key_Key_vs_non_null_value_inside_accumulator_declines()
    {
        // A zero-part key's g.Key has no backing property to serialize a non-null comparand against, so the
        // accumulator declines (falls back under Native, throws under NativeOnly) instead of crashing.
        var seed = SeedOrders();
        var sentinel = new { };

        int[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { })
                .Select(g => new { Count = g.Count(o => g.Key == sentinel) })
                .AsEnumerable()
                .Select(x => x.Count)
                .ToArray();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_empty_key_Key_vs_non_null_value_inside_accumulator_declines) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_empty_key_Key_vs_non_null_value_inside_accumulator_declines) + "D");
        Assert.Equal(Run(driverDb), Run(nativeDb));

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_empty_key_Key_vs_non_null_value_inside_accumulator_declines) + "NO");
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnlyDb));
    }

    [Fact]
    public void GroupBy_single_member_anonymous_key_Key_member_inside_accumulator_goes_native()
    {
        // A one-member anonymous key is composite (_id: {Country: ...}); g.Key.Country inside the accumulator
        // resolves to the raw per-document "$Country". Asserted against the LINQ-to-objects answer.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(GroupBy_single_member_anonymous_key_Key_member_inside_accumulator_goes_native));

        var results = db.Entities
            .GroupBy(o => new { o.Country })
            .Select(g => new { g.Key.Country, UsCount = g.Count(o => g.Key.Country == "US") })
            .AsEnumerable()
            .Select(x => (x.Country, x.UsCount))
            .OrderBy(x => x.Country)
            .ToArray();

        Assert.Equal([("FR", 0), ("UK", 0), ("US", 2)], results);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_single_member_anonymous_key_Key_member_inside_accumulator_matches_oracle(MongoQueryMode mode)
    {
        // EF-457: a g.Key member inside an aggregate predicate must mean the group key, on every path.
        var seed = SeedOrders();
        var expected = seed
            .GroupBy(o => new { o.Country })
            .Select(g => (g.Key.Country, UsCount: g.Count(o => g.Key.Country == "US")))
            .OrderBy(x => x.Country)
            .ToArray();
        Assert.Equal([("FR", 0), ("UK", 0), ("US", 2)], expected);

        using var db = CreateContext(seed, mode,
            nameof(GroupBy_single_member_anonymous_key_Key_member_inside_accumulator_matches_oracle) + mode);

        var results = db.Entities
            .GroupBy(o => new { o.Country })
            .Select(g => new { g.Key.Country, UsCount = g.Count(o => g.Key.Country == "US") })
            .AsEnumerable()
            .Select(x => (x.Country, x.UsCount))
            .OrderBy(x => x.Country)
            .ToArray();

        Assert.Equal(expected, results);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_scalar_key_Key_inside_accumulator_matches_oracle(MongoQueryMode mode)
    {
        // EF-457, scalar key: g.Key inside Count/Sum over the group means the group key.
        var seed = SeedOrders();
        var expected = seed
            .GroupBy(o => o.Country)
            .Select(g => (Country: g.Key, UsCount: g.Count(o => g.Key == "US"), UsSum: g.Sum(o => g.Key == "US" ? o.Amount : 0m)))
            .OrderBy(x => x.Country)
            .ToArray();
        Assert.Equal([("FR", 0, 0m), ("UK", 0, 0m), ("US", 2, 300m)], expected);

        using var db = CreateContext(seed, mode, nameof(GroupBy_scalar_key_Key_inside_accumulator_matches_oracle) + mode);

        var results = db.Entities
            .GroupBy(o => o.Country)
            .Select(g => new
            {
                Country = g.Key,
                UsCount = g.Count(o => g.Key == "US"),
                UsSum = g.Sum(o => g.Key == "US" ? o.Amount : 0m)
            })
            .AsEnumerable()
            .Select(x => (x.Country, x.UsCount, x.UsSum))
            .OrderBy(x => x.Country)
            .ToArray();

        Assert.Equal(expected, results);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_anonymous_key_Key_member_inside_Where_and_Sum_matches_oracle(MongoQueryMode mode)
    {
        // EF-457: g.Key members inside a Where-then-Sum and inside a Sum selector over the group. The Where makes
        // the driver keep the $push-then-$project form (where "$_id" is the group key), so this guards that the
        // key substitution is also correct there.
        var seed = SeedOrders();
        var expected = seed
            .GroupBy(o => new { o.Country, o.Year })
            .Select(g => (
                g.Key.Country,
                g.Key.Year,
                WhereSum: g.Where(o => g.Key.Country == "US" && o.Amount > 100).Sum(o => o.Amount),
                SelectorSum: g.Sum(o => g.Key.Year == 2020 ? o.Amount : 0m)))
            .OrderBy(x => x.Country).ThenBy(x => x.Year)
            .ToArray();
        Assert.Equal(
            [("FR", 2021, 0m, 0m), ("UK", 2020, 0m, 75m), ("US", 2020, 0m, 100m), ("US", 2021, 200m, 0m)],
            expected);

        using var db = CreateContext(seed, mode,
            nameof(GroupBy_anonymous_key_Key_member_inside_Where_and_Sum_matches_oracle) + mode);

        var results = db.Entities
            .GroupBy(o => new { o.Country, o.Year })
            .Select(g => new
            {
                g.Key.Country,
                g.Key.Year,
                WhereSum = g.Where(o => g.Key.Country == "US" && o.Amount > 100).Sum(o => o.Amount),
                SelectorSum = g.Sum(o => g.Key.Year == 2020 ? o.Amount : 0m)
            })
            .AsEnumerable()
            .Select(x => (x.Country, x.Year, x.WhereSum, x.SelectorSum))
            .OrderBy(x => x.Country).ThenBy(x => x.Year)
            .ToArray();

        Assert.Equal(expected, results);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_with_element_selector_Key_inside_accumulator_matches_oracle(MongoQueryMode mode)
    {
        // EF-457 with an element selector: the aggregate lambda's parameter is the projected element, not the
        // source document (EF Core inlines the element selector into the aggregate lambda).
        var seed = SeedOrders();
        var expected = seed
            .GroupBy(o => o.Country, o => o.Amount)
            .Select(g => (Country: g.Key, UsSum: g.Sum(a => g.Key == "US" ? a : 0m)))
            .OrderBy(x => x.Country)
            .ToArray();
        Assert.Equal([("FR", 0m), ("UK", 0m), ("US", 300m)], expected);

        using var db = CreateContext(seed, mode,
            nameof(GroupBy_with_element_selector_Key_inside_accumulator_matches_oracle) + mode);

        var results = db.Entities
            .GroupBy(o => o.Country, o => o.Amount)
            .Select(g => new { Country = g.Key, UsSum = g.Sum(a => g.Key == "US" ? a : 0m) })
            .AsEnumerable()
            .Select(x => (x.Country, x.UsSum))
            .OrderBy(x => x.Country)
            .ToArray();

        Assert.Equal(expected, results);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_Key_inside_aggregate_in_Where_over_groups_matches_oracle(MongoQueryMode mode)
    {
        // EF-457 where the aggregate sits in a predicate over the groupings rather than the final projection.
        var seed = SeedOrders();
        var expected = seed
            .GroupBy(o => new { o.Country })
            .Where(g => g.Count(o => g.Key.Country != "UK") > 0)
            .Select(g => (g.Key.Country, Count: g.Count()))
            .OrderBy(x => x.Country)
            .ToArray();
        Assert.Equal([("FR", 1), ("US", 2)], expected);

        using var db = CreateContext(seed, mode,
            nameof(GroupBy_Key_inside_aggregate_in_Where_over_groups_matches_oracle) + mode);

        var results = db.Entities
            .GroupBy(o => new { o.Country })
            .Where(g => g.Count(o => g.Key.Country != "UK") > 0)
            .Select(g => new { g.Key.Country, Count = g.Count() })
            .AsEnumerable()
            .Select(x => (x.Country, x.Count))
            .OrderBy(x => x.Country)
            .ToArray();

        Assert.Equal(expected, results);
    }

    [Fact]
    public void GroupBy_single_member_anonymous_key_whole_Key_null_check_inside_accumulator_declines()
    {
        // The whole composite g.Key has no single raw expression, so a g.Key == null accumulator condition declines.
        var seed = SeedOrders();

        int[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { o.Country })
                .Select(g => new { g.Key.Country, Count = g.Count(o => g.Key == null) })
                .AsEnumerable()
                .OrderBy(x => x.Country)
                .Select(x => x.Count)
                .ToArray();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_single_member_anonymous_key_whole_Key_null_check_inside_accumulator_declines) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_single_member_anonymous_key_whole_Key_null_check_inside_accumulator_declines) + "D");
        Assert.Equal(Run(driverDb), Run(nativeDb));

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_single_member_anonymous_key_whole_Key_null_check_inside_accumulator_declines) + "NO");
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnlyDb));
    }

    [Fact]
    public void GroupBy_nested_anonymous_key_Key_member_inside_accumulator_declines()
    {
        // A nested anonymous key part (new { Inner = new { o.Country } }) declines at key binding, so an inner
        // g.Key.Inner.Country accumulator condition never reaches the raw-key resolver. The fallback (and explicit
        // DriverLinq) must still give the LINQ-to-objects answer (EF-457).
        var seed = SeedOrders();
        var expected = seed
            .GroupBy(o => new { Inner = new { o.Country } })
            .Select(g => new { g.Key.Inner.Country, UsCount = g.Count(o => g.Key.Inner.Country == "US") })
            .OrderBy(x => x.Country)
            .Select(x => x.UsCount)
            .ToArray();
        Assert.Equal([0, 0, 2], expected);

        int[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { Inner = new { o.Country } })
                .Select(g => new { g.Key.Inner.Country, UsCount = g.Count(o => g.Key.Inner.Country == "US") })
                .AsEnumerable()
                .OrderBy(x => x.Country)
                .Select(x => x.UsCount)
                .ToArray();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_nested_anonymous_key_Key_member_inside_accumulator_declines) + "N");
        Assert.Equal(expected, Run(nativeDb));

        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_nested_anonymous_key_Key_member_inside_accumulator_declines) + "D");
        Assert.Equal(expected, Run(driverDb));

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_nested_anonymous_key_Key_member_inside_accumulator_declines) + "NO");
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
        // A constructor-call key (Members == null, like new{}) must not be mistaken for an empty key. Compares
        // row counts, which a row-count-blind assertion would miss.
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
        // A constant/parameter string key that looks like a field path ("$Country") must be $literal-wrapped in
        // $group's _id, or the server groups by the Country field instead. Every row shares the literal key, so
        // the correct result is one group (field-reinterpreted grouping would give three).
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
        // A MemberInitExpression (DTO initializer) key binds as a named composite key via TryBindKeyPartValue,
        // like the anonymous-type case, and goes native.
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
        // A true HAVING: Where between GroupBy(key) and the terminal Select, on g.Count() — distinct from the
        // GroupBy_HAVING_on_..._matches_driver_linq tests, whose Where runs after the Select.
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
        // A HAVING comparison against a Guid key must serialize the constant through the key's property, or
        // Native throws ArgumentException (Guid cannot be mapped to a BsonValue).
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
        // SnapshotPriorGroupingForNestedGroupBy must carry GroupHavingPredicate aside with Grouping/Projection;
        // otherwise the outer GroupBy overwrites it and silently drops the first GroupBy's filter.
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
        // If the first HAVING were dropped, FR's C=1 group would produce an extra outer group. Correct: only
        // {US,2} and {UK,2} survive, folding into one group keyed by C=2.
        Assert.Equal([(2, 2)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Nested_GroupBy_after_first_level_paging_declines_instead_of_dropping_the_paging()
    {
        // The first GroupBy's Skip lives in GroupPagingOps, which SnapshotPriorGroupingForNestedGroupBy doesn't
        // move aside, so the outer TryBindGroupProjection would reset it and silently drop the paging. Must
        // decline under NativeOnly and match driver-LINQ under Native.
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
        // FR (C=1) is dropped by Skip(1); UK and US (C=2) fold into one group. Without paging all three would
        // reach the outer GroupBy, giving {C=2: 2, C=1: 1}.
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
        // A captured local (parameterized by EF's real pipeline) as the comparison's non-key side, exercising
        // TryTranslateComparisonConstant's MongoParameterExpression arm.
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
        // SeedOrders: US 100+200, UK 50+25, FR 300. Only amounts > 60 count: US=300, UK=0, FR=300.
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
        // US 100+200=300 (domestic). UK (75) and FR (300) stay separate groups — the ternary only relabels.
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
        // Country is never null here, so the coalesce fallback isn't taken; this proves the shape matches
        // driver-LINQ.
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
        // A projection-side ternary's key-vs-constant comparison (TryTranslateGroupProjectionConditionOrValue)
        // must serialize the constant through the key's property, or Native throws ArgumentException for a Guid.
        // Same bug class as GroupBy_HAVING_on_Guid_key_matches_driver_linq.
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
        // A "$"-prefixed constant as a $ifNull/$cond branch must be $literal-wrapped, or `g.Key ?? "$Year"`
        // returns null for the null-keyed group. One row has a null Country to exercise the fallback value
        // (GroupBy_coalesce_projection_over_key_matches_driver_linq only covers the shape).
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
        // MinMax_after_GroupBy_aggregate's shape: GroupBy(key).Select(g => g.Sum(...)).Min() over the flattened
        // per-group sums (US=300, UK=75, FR=300) — 75.
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
        // Min()/Max() over an empty non-nullable result must throw InvalidOperationException (BuildEmptyBehavior),
        // matching LINQ, not return default(decimal).
        using var db = CreateContext([], MongoQueryMode.NativeOnly, nameof(Bare_accumulator_then_outer_Min_over_empty_source_throws));

        Assert.Throws<InvalidOperationException>(() =>
            db.Entities.GroupBy(o => o.Country).Select(g => g.Sum(o => o.Amount)).Min());
    }

    [Fact]
    public void Bare_accumulator_then_outer_Sum_still_declines_under_NativeOnly()
    {
        // The selector-less post-group carve-out is Min/Max only; Sum()/Average() in the same position must
        // keep falling back.
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
        // Repeated Skip on a bare GroupBy result must accumulate in arrival order (1+1=2 of 3). The OrderBy is
        // required because $group row order is unspecified (flaky without it). FR, UK dropped; US remains.
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
        // Paging after the terminal Select isn't native for a genuine GroupBy (the PostGroupOps carve-out is for
        // projected Distinct only — see NativeSlotPopulator's isPostDistinctSlot), so this falls back.
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

    // Nested-construction GroupBy projection members (e.g. `Container = new LastInChain { Name = "x", Value =
    // g.Sum(...) }`), as in NorthwindGroupByQueryMongoTest.Odata_groupby_empty_key. See NativeGroupByBinderTests.
    private class NestedAggregateContainer
    {
        public string Name { get; set; } = "";
        public object Value { get; set; } = null!;
    }

    private class NestedAggregateWrapper
    {
        public NestedAggregateContainer Container { get; set; } = null!;
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_projection_member_goes_native()
    {
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_projection_member_goes_native));

        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedAggregateWrapper
            {
                Container = new NestedAggregateContainer
                {
                    Name = "TotalAmount",
                    Value = g.Sum(o => o.Amount)
                }
            })
            .AsEnumerable()
            .Single();

        Assert.Equal("TotalAmount", result.Container.Name);
        Assert.Equal(30m, result.Container.Value);
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_referencing_per_element_value_declines_cleanly()
    {
        var seed = new[] { new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 } };
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_referencing_per_element_value_declines_cleanly));

        // A per-element reference (o.Country via g.First()) beside an accumulator in the same nested
        // construction isn't the grouping parameter — must decline the whole projection.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities
                .GroupBy(o => new { })
                .Select(g => new NestedAggregateWrapper
                {
                    Container = new NestedAggregateContainer
                    {
                        Name = g.First().Country,
                        Value = g.Sum(o => o.Amount)
                    }
                })
                .AsEnumerable()
                .Single());
    }

    [Fact]
    public void GroupBy_select_with_doubly_nested_construction_goes_native()
    {
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_doubly_nested_construction_goes_native));

        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => new OuterNestedWrapper
            {
                Mid = new MidNestedWrapper
                {
                    Inner = new NestedAggregateContainer
                    {
                        Name = "Deep",
                        Value = g.Sum(o => o.Amount)
                    }
                }
            })
            .AsEnumerable()
            .Single();

        Assert.Equal("Deep", result.Mid.Inner.Name);
        Assert.Equal(30m, result.Mid.Inner.Value);
    }

    private class OuterNestedWrapper
    {
        public MidNestedWrapper Mid { get; set; } = null!;
    }

    private class MidNestedWrapper
    {
        public NestedAggregateContainer Inner { get; set; } = null!;
    }

    // Both accumulators nested in the same construction (sibling top-level accumulators never reach
    // TryBindNestedGroupProjectionConstruction), exercising the two-accumulator synthetic field naming.
    [Fact]
    public void GroupBy_select_with_two_nested_accumulators_uses_distinct_synthetic_fields()
    {
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_two_nested_accumulators_uses_distinct_synthetic_fields));

        var result = db.Entities
            .GroupBy(o => new { })
            .Select(g => new ThreeMemberNestedWrapper
            {
                Container = new TwoAccumulatorContainer
                {
                    Sum = g.Sum(o => o.Amount),
                    Count = g.Count()
                }
            })
            .AsEnumerable()
            .Single();

        Assert.Equal(30m, result.Container.Sum);
        Assert.Equal(2, result.Container.Count);
    }

    private class ThreeMemberNestedWrapper
    {
        public TwoAccumulatorContainer Container { get; set; } = null!;
    }

    private class TwoAccumulatorContainer
    {
        public decimal Sum { get; set; }
        public int Count { get; set; }
    }

    // Constant/parameter members beside an accumulator in a MongoDocumentConstructionExpression must render via
    // RenderBranch ($literal-wrapped), not plain Render: $project misreads a bare number/bool as an
    // inclusion/exclusion flag and a "$"-prefixed string as a field path.
    private class NestedConstantContainer
    {
        public string DollarString { get; set; } = "";
        public int IntConstant { get; set; }
        public int ZeroConstant { get; set; }
        public bool BoolConstant { get; set; }
        public int CapturedParameter { get; set; }
        public decimal Sum { get; set; }
    }

    private class NestedConstantWrapper
    {
        public NestedConstantContainer Container { get; set; } = null!;
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_dollar_prefixed_string_constant_matches_driver_linq()
    {
        // A "$"-prefixed string constant member must read back as the literal, not a field path.
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_select_with_nested_construction_dollar_prefixed_string_constant_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(GroupBy_select_with_nested_construction_dollar_prefixed_string_constant_matches_driver_linq) + "D");

        NestedConstantContainer Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .GroupBy(o => new { })
                .Select(g => new NestedConstantWrapper
                {
                    Container = new NestedConstantContainer
                    {
                        DollarString = "$Country",
                        Sum = g.Sum(o => o.Amount)
                    }
                })
                .AsEnumerable()
                .Single()
                .Container;

        var native = Run(nativeDb);
        var driver = Run(driverDb);
        Assert.Equal("$Country", native.DollarString);
        Assert.Equal(driver.DollarString, native.DollarString);
        Assert.Equal(driver.Sum, native.Sum);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_dollar_prefixed_string_constant_matches_driver_linq) + "O");
        var nativeOnly = nativeOnlyDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer
                {
                    DollarString = "$Country",
                    Sum = g.Sum(o => o.Amount)
                }
            })
            .AsEnumerable()
            .Single()
            .Container;
        Assert.Equal("$Country", nativeOnly.DollarString);
        Assert.Equal(30m, nativeOnly.Sum);
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_int_constant_matches_in_memory_linq()
    {
        // An int constant member must not be misread as a $project inclusion flag ("element is missing").
        // Oracle is in-memory LINQ: driver LINQ v3 has the same missing-$literal bug here.
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };

        NestedConstantContainer expected = seed.AsQueryable()
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { IntConstant = 7, Sum = g.Sum(o => o.Amount) }
            })
            .Single()
            .Container;

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_select_with_nested_construction_int_constant_matches_in_memory_linq) + "N");
        var native = nativeDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { IntConstant = 7, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;

        Assert.Equal(expected.IntConstant, native.IntConstant);
        Assert.Equal(expected.Sum, native.Sum);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_int_constant_matches_in_memory_linq) + "O");
        var nativeOnly = nativeOnlyDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { IntConstant = 7, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;
        Assert.Equal(expected.IntConstant, nativeOnly.IntConstant);
        Assert.Equal(expected.Sum, nativeOnly.Sum);
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_literal_zero_constant_matches_in_memory_linq()
    {
        // A literal 0 member must not be misread as an exclusion flag (MongoCommandException). Driver LINQ v3
        // hits the same server error, so it can't be the oracle.
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };

        NestedConstantContainer expected = seed.AsQueryable()
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { ZeroConstant = 0, Sum = g.Sum(o => o.Amount) }
            })
            .Single()
            .Container;

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_select_with_nested_construction_literal_zero_constant_matches_in_memory_linq) + "N");
        var native = nativeDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { ZeroConstant = 0, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;

        Assert.Equal(expected.ZeroConstant, native.ZeroConstant);
        Assert.Equal(expected.Sum, native.Sum);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_literal_zero_constant_matches_in_memory_linq) + "O");
        var nativeOnly = nativeOnlyDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { ZeroConstant = 0, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;
        Assert.Equal(expected.ZeroConstant, nativeOnly.ZeroConstant);
        Assert.Equal(expected.Sum, nativeOnly.Sum);
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_bool_constant_matches_in_memory_linq()
    {
        // A bool constant member has the same hazard; driver LINQ v3 shares the bug, so no DriverLinq oracle.
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };

        NestedConstantContainer expected = seed.AsQueryable()
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { BoolConstant = true, Sum = g.Sum(o => o.Amount) }
            })
            .Single()
            .Container;

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_select_with_nested_construction_bool_constant_matches_in_memory_linq) + "N");
        var native = nativeDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { BoolConstant = true, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;

        Assert.Equal(expected.BoolConstant, native.BoolConstant);
        Assert.Equal(expected.Sum, native.Sum);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_bool_constant_matches_in_memory_linq) + "O");
        var nativeOnly = nativeOnlyDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { BoolConstant = true, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;
        Assert.Equal(expected.BoolConstant, nativeOnly.BoolConstant);
        Assert.Equal(expected.Sum, nativeOnly.Sum);
    }

    [Fact]
    public void GroupBy_select_with_nested_construction_captured_parameter_matches_in_memory_linq()
    {
        // A captured parameter member has the same missing-$literal hazard; no DriverLinq oracle, as above.
        var seed = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 10 },
            new Order { Id = ObjectId.GenerateNewId(), Country = "US", Amount = 20 },
        };
        var capturedValue = 42;

        NestedConstantContainer expected = seed.AsQueryable()
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { CapturedParameter = capturedValue, Sum = g.Sum(o => o.Amount) }
            })
            .Single()
            .Container;

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(GroupBy_select_with_nested_construction_captured_parameter_matches_in_memory_linq) + "N");
        var native = nativeDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { CapturedParameter = capturedValue, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;

        Assert.Equal(expected.CapturedParameter, native.CapturedParameter);
        Assert.Equal(expected.Sum, native.Sum);

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupBy_select_with_nested_construction_captured_parameter_matches_in_memory_linq) + "O");
        var nativeOnly = nativeOnlyDb.Entities
            .GroupBy(o => new { })
            .Select(g => new NestedConstantWrapper
            {
                Container = new NestedConstantContainer { CapturedParameter = capturedValue, Sum = g.Sum(o => o.Amount) }
            })
            .AsEnumerable()
            .Single()
            .Container;
        Assert.Equal(expected.CapturedParameter, nativeOnly.CapturedParameter);
        Assert.Equal(expected.Sum, nativeOnly.Sum);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Relational comparisons over a possibly-null group operand. In the aggregation dialect null (and missing) orders
    // below every value, so a bare $lt/$lte answers true for null, where C# lifted semantics (null < c) answer false.
    // Expectations are hand-computed: driver-LINQ gets several of these wrong (noted per test), so it is only used as
    // an oracle where it is right.
    // ---------------------------------------------------------------------------------------------------------------

    private class RankedOwner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        public int? Rank { get; set; }
    }

    // By Name: Alice 7, Bob {null, null} (all-null group), Cara 150, Dan 50, Eve {null, 20} (Max 20).
    // By Region: North {7, 150}, South {null, null}, East {50, null, 20}.
    // By Rank: 7, 20, 50, 150 (one row each) and null (Bob, Bob, Eve).
    private static RankedOwner[] SeedRankedOwners() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = null },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = null },
        new() { Id = ObjectId.GenerateNewId(), Name = "Cara", Region = "North", Rank = 150 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Dan", Region = "East", Rank = 50 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Eve", Region = "East", Rank = null },
        new() { Id = ObjectId.GenerateNewId(), Name = "Eve", Region = "East", Rank = 20 },
    ];

    private List<T> RunRanked<T>(MongoQueryMode mode, string name, Func<IQueryable<RankedOwner>, List<T>> query)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + mode
            + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<RankedOwner>(collectionName);
        collection.InsertMany(SeedRankedOwners());

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

        return query(db.Entities);
    }

    [Fact]
    public void Having_less_than_over_an_all_null_accumulator_excludes_the_group()
    {
        // Bob's Max is null: null < 100 is false. Driver-LINQ includes Bob (for < and <= alike), so the hand oracle
        // is the only check.
        var result = RunRanked(MongoQueryMode.NativeOnly, nameof(Having_less_than_over_an_all_null_accumulator_excludes_the_group),
            q => q.GroupBy(o => o.Name)
                .Where(g => g.Max(x => x.Rank) < 100)
                .Select(g => new { g.Key, M = g.Max(x => x.Rank) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.M)).ToList());

        Assert.Equal([("Alice", (int?)7), ("Dan", 50), ("Eve", 20)], result);
    }

    [Fact]
    public void Having_less_than_or_equal_and_constant_on_the_left_exclude_the_all_null_group()
    {
        var name = nameof(Having_less_than_or_equal_and_constant_on_the_left_exclude_the_all_null_group);

        var lessOrEqual = RunRanked(MongoQueryMode.NativeOnly, name + "Le",
            q => q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) <= 50)
                .Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => x.Key).OrderBy(k => k).ToList());
        Assert.Equal(["Alice", "Dan", "Eve"], lessOrEqual);

        // 100 > Max flips to Max < 100.
        var flippedGreater = RunRanked(MongoQueryMode.NativeOnly, name + "Gt",
            q => q.GroupBy(o => o.Name).Where(g => 100 > g.Max(x => x.Rank))
                .Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => x.Key).OrderBy(k => k).ToList());
        Assert.Equal(["Alice", "Dan", "Eve"], flippedGreater);

        // 50 >= Max flips to Max <= 50.
        var flippedGreaterOrEqual = RunRanked(MongoQueryMode.NativeOnly, name + "Ge",
            q => q.GroupBy(o => o.Name).Where(g => 50 >= g.Max(x => x.Rank))
                .Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => x.Key).OrderBy(k => k).ToList());
        Assert.Equal(["Alice", "Dan", "Eve"], flippedGreaterOrEqual);
    }

    [Fact]
    public void Terminal_count_after_a_having_less_than_over_a_nullable_accumulator_excludes_the_all_null_group()
    {
        // The P05 shape. Driver-LINQ answers 4 (Bob counted), so the hand oracle is the only check.
        var result = RunRanked(MongoQueryMode.NativeOnly,
            nameof(Terminal_count_after_a_having_less_than_over_a_nullable_accumulator_excludes_the_all_null_group),
            q => new List<int> { q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) < 100).Count() });

        Assert.Equal([3], result);
    }

    [Fact]
    public void Terminal_all_over_a_nullable_accumulator_is_false_for_an_all_null_group()
    {
        // All renders the negated comparison: !(null < 100) is true, so Bob's group fails All. Cara (150) is filtered
        // out so Bob is the only failing group. Driver-LINQ answers true (it $not-wraps the same bare $lt), so the hand
        // oracle is the only check.
        var name = nameof(Terminal_all_over_a_nullable_accumulator_is_false_for_an_all_null_group);
        var withBob = RunRanked(MongoQueryMode.NativeOnly, name + "B",
            q => new List<bool> { q.Where(o => o.Name != "Cara").GroupBy(o => o.Name).All(g => g.Max(x => x.Rank) < 100) });
        Assert.Equal([false], withBob);

        var withoutBob = RunRanked(MongoQueryMode.NativeOnly, name + "N",
            q => new List<bool>
            {
                q.Where(o => o.Name != "Cara" && o.Name != "Bob").GroupBy(o => o.Name).All(g => g.Max(x => x.Rank) < 100)
            });
        Assert.Equal([true], withoutBob);
    }

    [Fact]
    public void Post_group_bare_accumulator_alias_all_is_false_for_an_all_null_group()
    {
        // The bare-accumulator-alias branch of MongoExpressionTranslator compares the flattened output field, and
        // NativeGroupByBinder.TryNegateGroupComparison $not-wraps it for All.
        var name = nameof(Post_group_bare_accumulator_alias_all_is_false_for_an_all_null_group);
        var result = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name,
            q => new List<bool>
            {
                q.Where(o => o.Name != "Cara").GroupBy(o => o.Name).Select(g => g.Max(x => x.Rank)).All(v => v < 100)
            }));
        Assert.Equal([false], result);
    }

    [Fact]
    public void Post_group_bare_accumulator_alias_where_excludes_the_all_null_group()
    {
        // A Where over the bare accumulator alias (EF's normalization of Count(pred)) resolves the bare parameter to the
        // sole "_v" output (MongoExpressionTranslator.ProjectedAliasScope). Bob's Max is null: null < 100 is false, and
        // v == null must compare the alias, not the whole document.
        var name = nameof(Post_group_bare_accumulator_alias_where_excludes_the_all_null_group);
        var result = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name, q =>
        {
            var maxima = q.GroupBy(o => o.Name).Select(g => g.Max(x => x.Rank));
            return new List<int> { maxima.Count(v => v < 100), maxima.Count(v => v == null) };
        }));
        Assert.Equal([3, 1], result);
    }

    [Fact]
    public void Post_group_named_accumulator_alias_all_is_false_for_an_all_null_group()
    {
        // Not folded into the terminal All path: the Select is kept, so this goes through the post-group aggregate
        // path, which compares the flattened "M" alias in a $match after the $project. Driver-LINQ gets this shape
        // right (it renders the query-dialect { M: { $not: { $lt: 100 } } }), unlike the terminal All in
        // Terminal_all_over_a_nullable_accumulator_is_false_for_an_all_null_group, hence full parity here.
        var name = nameof(Post_group_named_accumulator_alias_all_is_false_for_an_all_null_group);
        var result = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name,
            q => new List<bool>
            {
                q.Where(o => o.Name != "Cara").GroupBy(o => o.Name)
                    .Select(g => new { g.Key, M = g.Max(x => x.Rank) }).All(x => x.M < 100)
            }));
        Assert.Equal([false], result);
    }

    [Fact]
    public void Greater_than_equal_and_not_equal_over_a_nullable_accumulator_stay_correct()
    {
        var name = nameof(Greater_than_equal_and_not_equal_over_a_nullable_accumulator_stay_correct);

        List<int> Run(MongoQueryMode mode) =>
        [
            RunRanked(mode, name + "Gt", q => new List<int> { q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) > 10).Count() })[0],
            RunRanked(mode, name + "Eq", q => new List<int> { q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) == 7).Count() })[0],
            RunRanked(mode, name + "Ne", q => new List<int> { q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) != 7).Count() })[0],
            RunRanked(mode, name + "Null", q => new List<int> { q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) == null).Count() })[0],
            RunRanked(mode, name + "NotNull", q => new List<int> { q.GroupBy(o => o.Name).Where(g => g.Max(x => x.Rank) != null).Count() })[0],
        ];

        // > 10: Cara, Dan, Eve. == 7: Alice. != 7: Bob (null != 7), Cara, Dan, Eve. == null: Bob. != null: the other 4.
        var result = NativeModeAssert.NativeAndParity(Run);
        Assert.Equal([3, 1, 4, 1, 4], result);
    }

    [Fact]
    public void Having_less_than_over_a_nullable_key_excludes_the_null_key_group()
    {
        var name = nameof(Having_less_than_over_a_nullable_key_excludes_the_null_key_group);
        var result = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name,
            q => q.GroupBy(o => o.Rank).Where(g => g.Key < 100)
                .Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.C)).ToList()));
        Assert.Equal([((int?)7, 1), (20, 1), (50, 1)], result);

        var nullKey = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name + "Null",
            q => q.GroupBy(o => o.Rank).Where(g => g.Key == null)
                .Select(g => new { g.Key, C = g.Count() })
                .AsEnumerable().Select(x => (x.Key, x.C)).ToList()));
        Assert.Equal([((int?)null, 3)], nullKey);
    }

    [Fact]
    public void Per_element_accumulator_condition_over_a_nullable_property_does_not_count_null()
    {
        // South is {null, null}: null < 60 is false, so 0. East is {50, null, 20}: 2. Driver-LINQ counts the nulls
        // (South 2, East 3), so the hand oracle is the only check.
        var result = RunRanked(MongoQueryMode.NativeOnly,
            nameof(Per_element_accumulator_condition_over_a_nullable_property_does_not_count_null),
            q => q.GroupBy(o => o.Region)
                .Select(g => new { g.Key, C = g.Count(x => x.Rank < 60), Mirrored = g.Count(x => 61 > x.Rank + 1) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.C, x.Mirrored)).ToList());

        // Mirrored keeps its operand order ($gt: [61, {$add: [Rank, 1]}]), so the nullable operand is the right-hand,
        // lower side of $gt: the guard must follow the operator, not the position.
        Assert.Equal([("East", 2, 2), ("North", 1, 1), ("South", 0, 0)], result);
    }

    [Fact]
    public void Key_only_accumulator_condition_over_a_nullable_key_does_not_count_the_null_key()
    {
        // g.Count(x => g.Key < 100) counts every element when the key passes, none otherwise. Driver-LINQ answers 0
        // for every group, so the hand oracle is the only check.
        var result = RunRanked(MongoQueryMode.NativeOnly,
            nameof(Key_only_accumulator_condition_over_a_nullable_key_does_not_count_the_null_key),
            q => q.GroupBy(o => o.Rank)
                .Select(g => new { g.Key, C = g.Count(x => g.Key < 100) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.C)).ToList());

        Assert.Equal([((int?)null, 0), (7, 1), (20, 1), (50, 1), (150, 0)], result);
    }

    [Fact]
    public void Projection_ternary_over_a_nullable_key_treats_null_as_not_less()
    {
        // Driver-LINQ answers 1 for the null key, so the hand oracle is the only check.
        var result = RunRanked(MongoQueryMode.NativeOnly,
            nameof(Projection_ternary_over_a_nullable_key_treats_null_as_not_less),
            q => q.GroupBy(o => o.Rank)
                .Select(g => new { g.Key, F = g.Key < 100 ? 1 : 0 })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.F)).ToList());

        Assert.Equal([((int?)null, 0), (7, 1), (20, 1), (50, 1), (150, 0)], result);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Post-group Where over a keyed GroupBy(key).Select(...)'s projected aliases, and the post-group terminal-aggregate
    // path's alias resolution. Members resolve against the Select's output alias, never a key-part name or the entity.
    // SeedOrders: US {100, 200}, UK {50, 25}, FR {300}.
    // ---------------------------------------------------------------------------------------------------------------

    private List<T> RunOrders<T>(MongoQueryMode mode, string name, Func<IQueryable<Order>, List<T>> query)
    {
        using var db = CreateContext(SeedOrders(), mode, name + mode);
        return query(db.Entities);
    }

    [Fact]
    public void Post_group_where_over_a_constant_key_goes_native()
    {
        // The NorthwindGroupByQueryMongoTest.GroupBy_count_filter shape: nav-expansion folds the first Select into
        // the key, which arrives as the constant "Order".
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode, nameof(Post_group_where_over_a_constant_key_goes_native),
            q => q.Select(e => new { e.Id, Name = "Order" })
                .GroupBy(o => o.Name)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .Where(o => o.Count > 0)
                .AsEnumerable()
                .Select(x => (x.Name, x.Count)).ToList()));

        Assert.Equal([("Order", 5)], result);
    }

    [Fact]
    public void Post_group_where_over_an_accumulator_alias_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode, nameof(Post_group_where_over_an_accumulator_alias_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Total = g.Sum(o => o.Amount) })
                .Where(x => x.Total > 100m)
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Total)).ToList()));

        Assert.Equal([("FR", 300m), ("US", 300m)], result);
    }

    [Fact]
    public void Post_group_where_over_a_renamed_key_alias_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode, nameof(Post_group_where_over_a_renamed_key_alias_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { Land = g.Key, N = g.Count() })
                .Where(x => x.Land == "UK")
                .AsEnumerable()
                .Select(x => (x.Land, x.N)).ToList()));

        Assert.Equal([("UK", 2)], result);
    }

    [Fact]
    public void Post_group_where_over_an_alias_shadowing_a_key_part_name_reads_the_alias()
    {
        // Review Focus #1. The alias Country holds the count, while the key part is also named Country (a string).
        // Resolving by key-part name would bind the string key property and serialize 1 as a string.
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Post_group_where_over_an_alias_shadowing_a_key_part_name_reads_the_alias),
            q => q.GroupBy(o => new { o.Country })
                .Select(g => new { Country = g.Count(), C = g.Key.Country })
                .Where(x => x.Country > 1)
                .AsEnumerable().OrderBy(x => x.C)
                .Select(x => (x.C, x.Country)).ToList()));

        Assert.Equal([("UK", 2), ("US", 2)], result);
    }

    [Fact]
    public void Post_group_terminal_aggregates_over_an_alias_shadowing_a_key_part_name_read_the_alias()
    {
        // The terminal-aggregate path (All, and Any/Count with a predicate) had the same key-part-name resolution.
        // Counts: US 2, UK 2, FR 1.
        var name = nameof(Post_group_terminal_aggregates_over_an_alias_shadowing_a_key_part_name_read_the_alias);
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode, name, q =>
        {
            var groups = q.GroupBy(o => new { o.Country }).Select(g => new { Country = g.Count(), C = g.Key.Country });
            return new List<(bool, bool, bool, int)>
            {
                (groups.All(x => x.Country > 1), groups.All(x => x.Country > 0), groups.Any(x => x.Country > 1),
                    groups.Count(x => x.Country > 1))
            };
        }));

        Assert.Equal([(false, true, true, 2)], result);
    }

    [Fact]
    public void Post_group_where_less_than_over_a_nullable_key_alias_excludes_the_null_key_group()
    {
        // Review Focus #2. In the aggregation dialect null orders below every value; C# null < 100 is false.
        // By Rank: 7, 20, 50, 150 (one row each) and null (three rows).
        var name = nameof(Post_group_where_less_than_over_a_nullable_key_alias_excludes_the_null_key_group);
        var result = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name,
            q => q.GroupBy(o => o.Rank)
                .Select(g => new { g.Key, C = g.Count() })
                .Where(x => x.Key < 100)
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.C)).ToList()));
        Assert.Equal([((int?)7, 1), (20, 1), (50, 1)], result);

        // Renamed alias, <=, and the constant on the left (100 > R flips to R < 100).
        var renamed = NativeModeAssert.NativeAndParity(mode => RunRanked(mode, name + "R",
            q => q.GroupBy(o => o.Rank)
                .Select(g => new { R = g.Key, C = g.Count() })
                .Where(x => x.R <= 20 || 100 > x.R)
                .AsEnumerable().OrderBy(x => x.R)
                .Select(x => (x.R, x.C)).ToList()));
        Assert.Equal([((int?)7, 1), (20, 1), (50, 1)], renamed);
    }

    [Fact]
    public void Post_group_where_with_a_compound_predicate_over_two_aliases_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Post_group_where_with_a_compound_predicate_over_two_aliases_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { Land = g.Key, N = g.Count() })
                .Where(x => x.N > 1 && x.Land != "US")
                .AsEnumerable()
                .Select(x => (x.Land, x.N)).ToList()));

        Assert.Equal([("UK", 2)], result);
    }

    [Theory]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Post_group_where_over_a_case_mapped_alias_throws(MongoQueryMode mode)
    {
        // ToUpper has no native translation (and the alias scope never falls through to the entity, which has a Country
        // property of its own), so NativeOnly declines. DriverLinq and Native throw by deliberate decline: driver-LINQ
        // would answer this comparison correctly (a Unicode-aware case-insensitive match), but the case-mapping gate is
        // broad over grouped chains, and v10.0.4 threw for this query too.
        var exception = Assert.ThrowsAny<Exception>(() => RunOrders(mode,
            nameof(Post_group_where_over_a_case_mapped_alias_throws),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { Country = g.Key, N = g.Count() })
                .Where(x => x.Country.ToUpper() == "UK")
                .AsEnumerable()
                .Select(x => (x.Country, x.N)).ToList()));

        if (mode == MongoQueryMode.NativeOnly)
            Assert.IsType<NativeTranslationNotSupportedException>(exception);
        else
            Assert.Contains("$toUpper/$toLower", Assert.IsType<InvalidOperationException>(exception).Message);
    }

    [Fact]
    public void Post_group_order_by_declines_cleanly()
    {
        // Only Where is admitted after a keyed GroupBy(key).Select(...); OrderBy/ThenBy/Skip/Take/Distinct over the
        // grouped output are a separate follow-up.
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode, nameof(Post_group_order_by_declines_cleanly),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { Land = g.Key, N = g.Count() })
                .OrderBy(x => x.N).ThenBy(x => x.Land)
                .AsEnumerable()
                .Select(x => (x.Land, x.N)).ToList()));

        Assert.Equal([("FR", 1), ("UK", 2), ("US", 2)], result);
    }

    [Fact]
    public void Post_group_where_before_a_nested_group_by_filters_the_first_grouping()
    {
        // The Where lands in PostGroupOps, which the lowerer emits after the first grouping's flatten $project and
        // before the nested $group. Without FR: US 2, UK 2, so one group of N = 2 with two members.
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Post_group_where_before_a_nested_group_by_filters_the_first_grouping),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { Land = g.Key, N = g.Count() })
                .Where(x => x.Land != "FR")
                .GroupBy(x => x.N)
                .Select(g => new { g.Key, M = g.Count() })
                .AsEnumerable()
                .Select(x => (x.Key, x.M)).ToList()));

        Assert.Equal([(2, 2)], result);
    }

    [Fact]
    public void Post_group_where_after_a_nested_group_by_declines_cleanly()
    {
        // With a prior grouping, PostGroupOps lower after the prior stage's flatten $project, ahead of the outer
        // $group, where the outer aliases don't exist yet. So a Where over the outer grouping declines.
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode,
            nameof(Post_group_where_after_a_nested_group_by_declines_cleanly),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { Land = g.Key, N = g.Count() })
                .GroupBy(x => x.N)
                .Select(g => new { g.Key, M = g.Count() })
                .Where(x => x.M > 1)
                .AsEnumerable()
                .Select(x => (x.Key, x.M)).ToList()));

        Assert.Equal([(2, 2)], result);
    }

    [Fact]
    public void Post_group_terminal_aggregate_predicate_after_a_nested_group_by_declines_cleanly()
    {
        // Same placement hazard for the terminal-aggregate path: the predicate would run ahead of the outer $group
        // and answer All(M > 0) = false. Groups by N: {2: US, UK}, {1: FR}, so M = 2, 1.
        var name = nameof(Post_group_terminal_aggregate_predicate_after_a_nested_group_by_declines_cleanly);
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode, name, q =>
        {
            var groups = q.GroupBy(o => o.Country)
                .Select(g => new { Land = g.Key, N = g.Count() })
                .GroupBy(x => x.N)
                .Select(g => new { g.Key, M = g.Count() });
            return new List<(bool, bool)> { (groups.All(x => x.M > 0), groups.All(x => x.M < 2)) };
        }));

        Assert.Equal([(true, false)], result);
    }

    [Fact]
    public void Post_group_terminal_aggregate_predicate_after_a_whole_entity_concat_declines()
    {
        // TrailingOps lower ahead of a GroupBy composed after a whole-entity set op, so the predicate would run
        // before the $group. US 4, UK 4, FR 2 over the concatenation, so All(N < 3) is false. Driver-LINQ throws a
        // NullReferenceException for this shape, so there's no oracle: only the NativeOnly decline is asserted.
        Assert.Throws<NativeTranslationNotSupportedException>(() => RunOrders(MongoQueryMode.NativeOnly,
            nameof(Post_group_terminal_aggregate_predicate_after_a_whole_entity_concat_declines),
            q => new List<bool>
            {
                q.Concat(q).GroupBy(o => o.Country).Select(g => new { g.Key, N = g.Count() }).All(x => x.N < 3)
            }));
    }

    [Fact]
    public void Post_group_where_over_enum_and_date_key_aliases_with_parameters_goes_native()
    {
        // An alias has no IProperty, so a parameter compared with it serializes by CLR type. Status: New {US 2020,
        // UK 2020, FR}, Shipped {US 2021, UK 2020}. OrderDate: 2020-01-01 x3, 2021-01-01 x2.
        var status = OrderStatus.Shipped;
        var after = new DateTime(2020, 6, 1);
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders<string>(mode,
            nameof(Post_group_where_over_enum_and_date_key_aliases_with_parameters_goes_native), q =>
            [
                .. q.GroupBy(o => o.Status).Select(g => new { S = g.Key, N = g.Count() })
                    .Where(x => x.S == status).AsEnumerable().Select(x => $"{x.S}:{x.N}"),
                .. q.GroupBy(o => o.OrderDate).Select(g => new { D = g.Key, N = g.Count() })
                    .Where(x => x.D > after).AsEnumerable().Select(x => $"{x.D.Year}:{x.N}")
            ]));

        Assert.Equal(["Shipped:2", "2021:2"], result);
    }

    [Fact]
    public void Post_group_where_over_a_guid_key_alias_declines_cleanly()
    {
        // A Guid constant can't serialize by CLR type (BsonValue.Create throws), and the alias has no IProperty to
        // serialize through, so the alias scope declines rather than crash at render time. Every ExternalId is empty.
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode,
            nameof(Post_group_where_over_a_guid_key_alias_declines_cleanly),
            q => q.GroupBy(o => o.ExternalId)
                .Select(g => new { E = g.Key, N = g.Count() })
                .Where(x => x.E == Guid.Empty)
                .AsEnumerable()
                .Select(x => x.N).ToList()));

        Assert.Equal([5], result);
    }

    private class Parcel
    {
        public ObjectId Id { get; set; }
        public string Region { get; set; } = "";
        public ParcelInfo Info { get; set; } = new();
    }

    private class ParcelInfo
    {
        public int N { get; set; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Post_group_where_over_a_nested_alias_never_resolves_an_owned_navigation_of_the_same_name(bool nullCheck)
    {
        // The entity has an owned Info (stored as "info") with an int N, and the Select projects an alias Info with
        // an N of its own. Neither x.Info.N nor x.Info != null may walk the entity's owned path ("info"/"info.N",
        // which the flattened output doesn't have: the null check would answer "missing, so null" for every group).
        // A construction alias isn't comparable and a member chain through it isn't supported, so both decline.
        // Regions: A has two parcels, B one.
        var name = nameof(Post_group_where_over_a_nested_alias_never_resolves_an_owned_navigation_of_the_same_name);
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            var collection = database.MongoDatabase.GetCollection<Parcel>(
                TemporaryDatabaseFixtureBase.CreateCollectionName(name) + nullCheck + mode + Guid.NewGuid().ToString("N")[..8]);
            using var db = SingleEntityDbContext.Create(
                collection,
                modelBuilderAction: mb => mb.Entity<Parcel>().OwnsOne(p => p.Info, b => b.HasElementName("info")),
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                });
            db.Entities.AddRange(
                new Parcel { Id = ObjectId.GenerateNewId(), Region = "A", Info = new() { N = 5 } },
                new Parcel { Id = ObjectId.GenerateNewId(), Region = "A", Info = new() { N = 5 } },
                new Parcel { Id = ObjectId.GenerateNewId(), Region = "B", Info = new() { N = 5 } });
            db.SaveChanges();

            var groups = db.Entities
                .GroupBy(p => p.Region)
                .Select(g => new { g.Key, Info = new { N = g.Count() } });
            return (nullCheck ? groups.Where(x => x.Info != null) : groups.Where(x => x.Info.N > 1))
                .AsEnumerable()
                .OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Info.N)).ToList();
        });

        Assert.Equal(nullCheck ? [("A", 2), ("B", 1)] : [("A", 2)], result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Post_group_get_type_comparison_over_a_grouped_row_declines_cleanly(bool equal)
    {
        // The row is an anonymous grouped output, never an Order, so GetType() == typeof(Order) is false for every group
        // (and != true). Folding it as if the parameter were the root entity would invert both. See
        // MongoExpressionTranslator.IsSelfParamTheEntity.
        var name = nameof(Post_group_get_type_comparison_over_a_grouped_row_declines_cleanly) + equal;
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode, name, q =>
        {
            var groups = q.GroupBy(o => o.Country).Select(g => new { g.Key, N = g.Count() });
            var filtered = equal
                ? groups.Where(x => x.GetType() == typeof(Order))
                : groups.Where(x => x.GetType() != typeof(Order));
            return filtered.AsEnumerable().Select(x => x.Key).OrderBy(k => k).ToList();
        }));

        Assert.Equal(equal ? [] : ["FR", "UK", "US"], result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Post_group_terminal_all_over_a_get_type_comparison_declines_cleanly(bool equal)
    {
        var name = nameof(Post_group_terminal_all_over_a_get_type_comparison_declines_cleanly) + equal;
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode, name, q =>
        {
            var groups = q.GroupBy(o => o.Country).Select(g => new { g.Key, N = g.Count() });
            return new List<bool>
            {
                equal ? groups.All(x => x.GetType() == typeof(Order)) : groups.All(x => x.GetType() != typeof(Order))
            };
        }));

        Assert.Equal([!equal], result);
    }

    [Fact]
    public void Get_type_comparison_over_a_projected_distinct_row_declines_cleanly()
    {
        // Same fold hazard under DistinctAliasScope (no ProjectedAliasScope): the structural half of
        // MongoExpressionTranslator.IsSelfParamTheEntity (the parameter's CLR type isn't the entity's) guards it.
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode,
            nameof(Get_type_comparison_over_a_projected_distinct_row_declines_cleanly),
            q => q.Select(o => new { o.Country }).Distinct()
                .Where(x => x.GetType() == typeof(Order))
                .AsEnumerable().Select(x => x.Country).ToList()));

        Assert.Equal([], result);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // $push list projection members: g.Select(e => e.X).ToList() / ToArray(). Element order inside a list follows the
    // $group's input order, which is unspecified without a prior sort (as for driver-LINQ), so every list is compared
    // sorted. SeedOrders: US {2020 100, 2021 200}, UK {2020 50, 2020 25}, FR {2021 300}.
    // ---------------------------------------------------------------------------------------------------------------

    // Formats a list order-insensitively, with null spelled out and decimals normalized (Decimal128 may keep a scale).
    private static string SortedList<T>(IEnumerable<T>? items)
        => items is null
            ? "<null list>"
            : string.Join(",", items
                .Select(i => i switch
                {
                    null => "null",
                    decimal d => (d / 1.0000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                    _ => i.ToString()!
                })
                .OrderBy(s => s, StringComparer.Ordinal));

    [Fact]
    public void Push_list_of_the_grouping_key_property_goes_native()
    {
        // The NorthwindGroupByQueryMongoTest.GroupBy_selecting_grouping_key_list shape.
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Push_list_of_the_grouping_key_property_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Data = g.Select(e => e.Country).ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, SortedList(x.Data))).ToList()));

        Assert.Equal([("FR", "FR"), ("UK", "UK,UK"), ("US", "US,US")], result);
    }

    [Fact]
    public void Push_array_and_list_of_numeric_elements_go_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Push_array_and_list_of_numeric_elements_go_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Years = g.Select(e => e.Year).ToArray(), Amounts = g.Select(e => e.Amount).ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Years.GetType().IsArray, SortedList(x.Years), SortedList(x.Amounts))).ToList()));

        Assert.Equal(
            [("FR", true, "2021", "300"), ("UK", true, "2020,2020", "25,50"), ("US", true, "2020,2021", "100,200")],
            result);
    }

    [Fact]
    public void Push_list_inside_a_nested_construction_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Push_list_inside_a_nested_construction_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Inner = new { N = g.Count(), Years = g.Select(e => e.Year).ToList() } })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Inner.N, SortedList(x.Inner.Years))).ToList()));

        Assert.Equal([("FR", 1, "2021"), ("UK", 2, "2020,2020"), ("US", 2, "2020,2021")], result);
    }

    [Fact]
    public void Push_list_of_a_computed_element_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Push_list_of_a_computed_element_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Doubled = g.Select(e => e.Amount * 2).ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, SortedList(x.Doubled))).ToList()));

        Assert.Equal([("FR", "600"), ("UK", "100,50"), ("US", "200,400")], result);
    }

    [Fact]
    public void Push_list_over_a_group_by_element_selector_goes_native()
    {
        // GroupBy(key, elementSelector) with g.ToList(): EF re-expresses it as g.AsQueryable().Select(elementSelector),
        // so it reaches the $push arm with the element selector inlined.
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Push_list_over_a_group_by_element_selector_goes_native),
            q => q.GroupBy(o => o.Country, o => o.Year)
                .Select(g => new { g.Key, Years = g.ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, SortedList(x.Years))).ToList()));

        Assert.Equal([("FR", "2021"), ("UK", "2020,2020"), ("US", "2020,2021")], result);
    }

    [Fact]
    public void Bare_push_list_projection_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(mode => RunOrders(mode,
            nameof(Bare_push_list_projection_goes_native),
            q => q.GroupBy(o => o.Country)
                .Select(g => g.Select(e => e.Year).ToList())
                .AsEnumerable()
                .Select(SortedList).OrderBy(s => s, StringComparer.Ordinal).ToList()));

        Assert.Equal(["2020,2020", "2020,2021", "2021"], result);
    }

    [Fact]
    public void Push_list_over_a_ragged_nullable_element_holds_null_for_null_and_missing()
    {
        // By Region: North {7, 150}, South {null, null}, East {50, null, 20} plus Fay, inserted without a Rank element
        // at all. C#'s g.Select(x => x.Rank) yields null for both the null and the missing Rank, so East has two nulls. A
        // bare $push skips a missing value, so the operand must be read null-safe. Driver-LINQ pushes the bare field and
        // answers East "20,50,null" (Fay's missing Rank dropped), so the hand oracle is the only check.
        var name = nameof(Push_list_over_a_ragged_nullable_element_holds_null_for_null_and_missing);
        List<(string, string)> Run(MongoQueryMode mode)
        {
            var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + mode + Guid.NewGuid().ToString("N")[..8];
            var collection = database.MongoDatabase.GetCollection<RankedOwner>(collectionName);
            collection.InsertMany(SeedRankedOwners());
            database.MongoDatabase.GetCollection<BsonDocument>(collectionName).InsertOne(
                new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Fay" }, { "Region", "East" } });

            using var db = SingleEntityDbContext.Create(
                collection,
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                });

            return db.Entities
                .GroupBy(o => o.Region)
                .Select(g => new { g.Key, Ranks = g.Select(x => x.Rank).ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, SortedList(x.Ranks))).ToList();
        }

        var result = Run(MongoQueryMode.NativeOnly);

        Assert.Equal([("East", "20,50,null,null"), ("North", "150,7"), ("South", "null,null")], result);
    }

    [Theory]
    [InlineData("g.ToList()")]
    [InlineData("g.Select(e => e).ToList()")]
    public void Push_list_of_whole_entity_elements_declines(string shape)
    {
        // Pushing whole entities needs per-element entity materialization and tracking, which the $push arm doesn't
        // do; an entity-typed element must never be taken as a scalar $push. NativeOnly only: driver-LINQ fails to
        // deserialize the pushed entities (FormatException wrapping NotImplementedException, pre-existing), so there is
        // no oracle for DeclinesCleanly; the throw is pinned so a driver fix is noticed.
        var name = nameof(Push_list_of_whole_entity_elements_declines) + shape.Length;
        List<(string, int)> Run(MongoQueryMode mode) => RunOrders(mode, name, q =>
            (shape == "g.ToList()"
                ? q.GroupBy(o => o.Country).Select(g => new { g.Key, Items = g.ToList() })
                : q.GroupBy(o => o.Country).Select(g => new { g.Key, Items = g.Select(e => e).ToList() }))
            .AsEnumerable().Select(x => (x.Key, x.Items.Count)).ToList());

        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        Assert.ThrowsAny<FormatException>(() => Run(MongoQueryMode.DriverLinq));
    }

    [Fact]
    public void Push_list_of_owned_entity_elements_declines()
    {
        // An owned reference is entity-typed too: pushing it would need owned-entity materialization.
        var name = nameof(Push_list_of_owned_entity_elements_declines);
        List<(string, int)> Run(MongoQueryMode mode)
        {
            var collection = database.MongoDatabase.GetCollection<Parcel>(
                TemporaryDatabaseFixtureBase.CreateCollectionName(name) + mode + Guid.NewGuid().ToString("N")[..8]);
            using var db = SingleEntityDbContext.Create(
                collection,
                modelBuilderAction: mb => mb.Entity<Parcel>().OwnsOne(p => p.Info),
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                });
            db.Entities.AddRange(
                new Parcel { Id = ObjectId.GenerateNewId(), Region = "A", Info = new() { N = 1 } },
                new Parcel { Id = ObjectId.GenerateNewId(), Region = "A", Info = new() { N = 2 } },
                new Parcel { Id = ObjectId.GenerateNewId(), Region = "B", Info = new() { N = 3 } });
            db.SaveChanges();

            return db.Entities
                .GroupBy(p => p.Region)
                .Select(g => new { g.Key, Infos = g.Select(p => p.Info).ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Infos.Sum(i => i.N))).ToList();
        }

        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
    }

    [Fact]
    public void Post_group_where_over_a_pushed_list_alias_declines_cleanly()
    {
        // A list alias isn't a comparable scalar, so a post-group predicate over it (here its Count) declines.
        var result = NativeModeAssert.DeclinesCleanly(mode => RunOrders(mode,
            nameof(Post_group_where_over_a_pushed_list_alias_declines_cleanly),
            q => q.GroupBy(o => o.Country)
                .Select(g => new { g.Key, Years = g.Select(e => e.Year).ToList() })
                .Where(x => x.Years.Count > 1)
                .AsEnumerable().Select(x => x.Key).OrderBy(k => k).ToList()));

        Assert.Equal(["UK", "US"], result);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void Push_list_over_a_date_time_kind_configured_property_matches_entity_materialization(DateTimeKind kind)
    {
        // HasDateTimeKind changes how the property reads back, but the pushed list is read through a generic
        // List<DateTime> serializer that knows nothing of it, so the element must decline rather than return the wrong
        // Kind. The oracle is the entity's own materialized OrderDate.
        var name = nameof(Push_list_over_a_date_time_kind_configured_property_matches_entity_materialization) + kind;
        List<(string, string)> Run(MongoQueryMode mode, bool viaEntities)
        {
            var collection = database.MongoDatabase.GetCollection<Order>(
                TemporaryDatabaseFixtureBase.CreateCollectionName(name) + mode + viaEntities + Guid.NewGuid().ToString("N")[..8]);
            using var db = Make(collection, mode, mb => mb.Entity<Order>().Property(o => o.OrderDate).HasDateTimeKind(kind));
            db.Entities.AddRange(SeedOrders());
            db.SaveChanges();

            return viaEntities
                ? db.Entities.AsNoTracking().AsEnumerable().GroupBy(o => o.Country).OrderBy(g => g.Key)
                    .Select(g => (g.Key, SortedList(g.Select(e => e.OrderDate.Kind + "@" + e.OrderDate.Ticks)))).ToList()
                : db.Entities.GroupBy(o => o.Country)
                    .Select(g => new { g.Key, Dates = g.Select(e => e.OrderDate).ToList() })
                    .AsEnumerable().OrderBy(x => x.Key)
                    .Select(x => (x.Key, SortedList(x.Dates.Select(d => d.Kind + "@" + d.Ticks)))).ToList();
        }

        var oracle = Run(MongoQueryMode.NativeOnly, viaEntities: true);
        Assert.All(oracle, g => Assert.StartsWith(kind.ToString(), g.Item2));

        var native = Run(MongoQueryMode.Native, viaEntities: false);
        Assert.Equal(oracle, native);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly, viaEntities: false));
    }

    [Fact]
    public void Push_list_over_a_value_converted_property_declines_cleanly()
    {
        // Status is stored as its string name. The pushed list would be read back through a generic List<OrderStatus>
        // serializer that expects the integer form, so the element must decline.
        var name = nameof(Push_list_over_a_value_converted_property_declines_cleanly);
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            var collection = database.MongoDatabase.GetCollection<Order>(
                TemporaryDatabaseFixtureBase.CreateCollectionName(name) + mode + Guid.NewGuid().ToString("N")[..8]);
            using var db = Make(collection, mode, mb => mb.Entity<Order>().Property(o => o.Status).HasConversion<string>());
            db.Entities.AddRange(SeedOrders());
            db.SaveChanges();

            return db.Entities
                .GroupBy(o => o.Country)
                .Select(g => new { g.Key, Statuses = g.Select(e => e.Status).ToList() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, SortedList(x.Statuses.Select(s => s.ToString())))).ToList();
        });

        Assert.Equal([("FR", "New"), ("UK", "New,Shipped"), ("US", "New,Shipped")], result);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // A bare terminal aggregate on a GroupBy nested over a keyed GroupBy(key).Select(...). The nested GroupBy must bind
    // its own key: a stale prior Grouping once routed it through the post-GroupBy.Select aggregate path, which
    // re-emitted the first $group and dropped the nested key. Seed: countries A, B, C have one order each and D has
    // two, so N is {1, 1, 1, 2}.
    // ---------------------------------------------------------------------------------------------------------------

    private static Order[] SeedNestedGroups() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Country = "A", Year = 2020, Amount = 1 },
        new() { Id = ObjectId.GenerateNewId(), Country = "B", Year = 2020, Amount = 2 },
        new() { Id = ObjectId.GenerateNewId(), Country = "C", Year = 2020, Amount = 3 },
        new() { Id = ObjectId.GenerateNewId(), Country = "D", Year = 2020, Amount = 4 },
        new() { Id = ObjectId.GenerateNewId(), Country = "D", Year = 2021, Amount = 5 },
    ];

    private List<T> RunNestedGroups<T>(MongoQueryMode mode, string name, Func<IQueryable<Order>, List<T>> query)
    {
        using var db = CreateContext(SeedNestedGroups(), mode, name + mode);
        return query(db.Entities);
    }

    // Without the Where: nested groups N = 1 (A, B, C) and N = 2 (D). With Where(Land != "A"): N = 1 (B, C) and N = 2
    // (D). A stale Grouping would make Count/LongCount answer 1 in both forms and the predicate forms decline.
    [Theory]
    [InlineData("Count", false, 2L)]
    [InlineData("LongCount", false, 2L)]
    [InlineData("AnySize3", false, 1L)]
    [InlineData("CountSize1", false, 1L)]
    [InlineData("Count", true, 2L)]
    [InlineData("LongCount", true, 2L)]
    [InlineData("AnySize3", true, 0L)]
    [InlineData("CountSize1", true, 1L)]
    public void Nested_group_by_bare_terminal_aggregate_counts_the_nested_groups(string aggregate, bool postGroupWhere, long expected)
    {
        var name = nameof(Nested_group_by_bare_terminal_aggregate_counts_the_nested_groups) + aggregate + postGroupWhere;
        var result = NativeModeAssert.NativeAndParity(mode => RunNestedGroups(mode, name, q =>
        {
            var first = q.GroupBy(o => o.Country).Select(g => new { Land = g.Key, N = g.Count() });
            var nested = (postGroupWhere ? first.Where(x => x.Land != "A") : first).GroupBy(x => x.N);
            return new List<long>
            {
                aggregate switch
                {
                    "Count" => nested.Count(),
                    "LongCount" => nested.LongCount(),
                    "AnySize3" => nested.Any(g => g.Count() == 3) ? 1 : 0,
                    _ => nested.Count(g => g.Count() == 1)
                }
            };
        }));

        Assert.Equal([expected], result);
    }

    [Fact]
    public void Nested_group_by_over_a_distinct_then_where_counts_the_nested_groups()
    {
        // Distinct countries A, B, C, D; without A that leaves three, grouped by their length (all 1): one group.
        // (int?): a non-nullable Length key over a possibly-null string declines (see NativeStringNullPropagationTests).
        var result = NativeModeAssert.NativeAndParity(mode => RunNestedGroups(mode,
            nameof(Nested_group_by_over_a_distinct_then_where_counts_the_nested_groups), q =>
                new List<int>
                {
                    q.Select(o => new { o.Country }).Distinct().Where(x => x.Country != "A").GroupBy(x => (int?)x.Country.Length).Count()
                }));

        Assert.Equal([1], result);
    }

    [Fact]
    public void Nested_group_by_with_having_ordering_and_paging_on_the_nested_group()
    {
        // The nested GroupBy is pending (Grouping cleared at the snapshot), so its HAVING, ordering and paging use
        // the ungrouped-GroupBy arms and are emitted around the nested $group. Nested groups: N = 1 (3 members) and
        // N = 2 (1 member).
        var name = nameof(Nested_group_by_with_having_ordering_and_paging_on_the_nested_group);
        var result = NativeModeAssert.NativeAndParity(mode => RunNestedGroups<string>(mode, name, q =>
        {
            var first = q.GroupBy(o => o.Country).Select(g => new { Land = g.Key, N = g.Count() });
            var having = first.GroupBy(x => x.N).Where(g => g.Count() > 1).Select(g => new { g.Key, M = g.Count() })
                .AsEnumerable().Select(x => $"h{x.Key}:{x.M}");
            var ordered = first.GroupBy(x => x.N).OrderByDescending(g => g.Key).Take(1).Select(g => new { g.Key, M = g.Count() })
                .AsEnumerable().Select(x => $"o{x.Key}:{x.M}");
            var skipped = first.GroupBy(x => x.N).OrderBy(g => g.Key).Skip(1).Select(g => new { g.Key, M = g.Count() })
                .AsEnumerable().Select(x => $"s{x.Key}:{x.M}");
            return [.. having, .. ordered, .. skipped];
        }));

        Assert.Equal(["h1:3", "o2:1", "s2:1"], result);
    }

    [Theory]
    [InlineData("Count")]
    [InlineData("Select")]
    [InlineData("Having")]
    public void Third_level_group_by_declines_cleanly(string shape)
    {
        // There is one Prior* slot: a third GroupBy would overwrite it, and the first $group would silently vanish.
        // Levels: N = {A: 1, B: 1, C: 1, D: 2}; by N: {1: 3 members, 2: 1 member}, so M = {3, 1}; by M: {1: 1, 3: 1}.
        var name = nameof(Third_level_group_by_declines_cleanly) + shape;
        var result = NativeModeAssert.DeclinesCleanly(mode => RunNestedGroups<string>(mode, name, q =>
        {
            var third = q.GroupBy(o => o.Country).Select(g => new { Land = g.Key, N = g.Count() })
                .GroupBy(x => x.N).Select(g => new { K = g.Key, M = g.Count() })
                .GroupBy(x => x.M);
            return shape switch
            {
                "Count" => [third.Count().ToString()],
                "Select" => [.. third.Select(g => new { g.Key, C = g.Count() }).AsEnumerable().OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.C}")],
                _ => [.. third.Where(g => g.Key > 2).Select(g => new { g.Key, C = g.Count() }).AsEnumerable().Select(x => $"{x.Key}:{x.C}")]
            };
        }));

        Assert.Equal(shape switch { "Count" => ["2"], "Select" => ["1:1", "3:1"], _ => ["3:1"] }, result);
    }
}
