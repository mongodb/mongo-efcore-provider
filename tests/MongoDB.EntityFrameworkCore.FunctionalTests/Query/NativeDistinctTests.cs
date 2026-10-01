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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native <c>Select(...).Distinct()</c>: a degenerate <c>$group</c> (group by the projected value(s), no
/// accumulators) plus a flattening <c>$project</c>, including bare-scalar projections, whole-entity Distinct,
/// and operators composed after it. Unsupported shapes (e.g. a BsonRepresentation key) fall back under
/// <see cref="MongoQueryMode.Native"/> and throw <see cref="NativeTranslationNotSupportedException"/> under
/// <see cref="MongoQueryMode.NativeOnly"/>, the only reliable "went native" signal.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeDistinctTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private enum OrderStatus { New, Shipped, Cancelled }

    private class Order
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string City { get; set; } = "";
        public int Year { get; set; }
        public decimal Amount { get; set; }
        public OrderStatus Status { get; set; }
        public int? Rank { get; set; }
    }

    // City is constant per country, so distinct {Country, City} collapses to distinct countries.
    private static Order[] SeedOrders() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Country = "US", City = "NYC", Year = 2020, Amount = 100, Status = OrderStatus.New, Rank = 7 },
        new() { Id = ObjectId.GenerateNewId(), Country = "US", City = "NYC", Year = 2020, Amount = 150, Status = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Country = "US", City = "NYC", Year = 2021, Amount = 200, Status = OrderStatus.Shipped },
        new() { Id = ObjectId.GenerateNewId(), Country = "UK", City = "London", Year = 2020, Amount = 50, Status = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Country = "UK", City = "London", Year = 2020, Amount = 25, Status = OrderStatus.Shipped },
        new() { Id = ObjectId.GenerateNewId(), Country = "FR", City = "Paris", Year = 2021, Amount = 300, Status = OrderStatus.New },
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
    public void Distinct_anonymous_projection_goes_native_and_dedups()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_anonymous_projection_goes_native_and_dedups));

        var result = db.Entities.Select(o => new { o.Country }).Distinct()
            .AsEnumerable().OrderBy(r => r.Country).ToList();

        Assert.Equal(new[] { "FR", "UK", "US" }, result.Select(r => r.Country).ToArray()); // deduped, went native
    }

    [Fact]
    public void Distinct_composite_projection_matches_driver_linq()
    {
        using var nativeDb = CreateContext(SeedOrders(), MongoQueryMode.Native,
            nameof(Distinct_composite_projection_matches_driver_linq) + "N");
        using var driverDb = CreateContext(SeedOrders(), MongoQueryMode.DriverLinq,
            nameof(Distinct_composite_projection_matches_driver_linq) + "D");

        Func<SingleEntityDbContext<Order>, object[]> run = db => db.Entities.Select(o => new { o.Country, o.Year }).Distinct()
            .AsEnumerable().OrderBy(r => r.Country).ThenBy(r => r.Year).Select(r => (object)(r.Country, r.Year)).ToArray();

        Assert.Equal(run(driverDb), run(nativeDb));
    }

    [Fact]
    public void Bare_scalar_projection_Distinct_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_scalar_projection_Distinct_goes_native));

        var result = db.Entities.Select(o => o.Country).Distinct().ToList().OrderBy(c => c).ToList();
        Assert.Equal(["FR", "UK", "US"], result);
    }

    [Fact]
    public void Bare_scalar_projection_Distinct_then_OrderBy_with_identity_selector_goes_native()
    {
        // A bare-scalar Distinct's OrderBy key is the identity (c => c); it resolves against the Distinct's sole
        // key part.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_scalar_projection_Distinct_then_OrderBy_with_identity_selector_goes_native));

        var result = db.Entities.Select(o => o.Country).Distinct().OrderBy(c => c).ToList();
        Assert.Equal(["FR", "UK", "US"], result);
    }

    [Fact]
    public void Bare_scalar_projection_Distinct_then_OrderBy_with_identity_selector_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Bare_scalar_projection_Distinct_then_OrderBy_with_identity_selector_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Bare_scalar_projection_Distinct_then_OrderBy_with_identity_selector_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => o.Country).Distinct().OrderBy(c => c).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(["FR", "UK", "US"], native);
        Assert.Equal(Run(driverDb), native);
    }

    private List<T> RunDistinct<T>(string name, Func<SingleEntityDbContext<Order>, List<T>> query)
    {
        var seed = SeedOrders();
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, name + mode);
            return query(db);
        });
    }

    [Fact]
    public void Computed_sole_key_Distinct_then_Where_on_bare_parameter_goes_native()
    {
        // Select(o => o.Year * 2) is a computed sole key (no IProperty), so the bare lambda parameter of the Where
        // resolves to the key's alias via TryResolveFlattenedAlias. Distinct values: {4040, 4042}.
        var result = RunDistinct(nameof(Computed_sole_key_Distinct_then_Where_on_bare_parameter_goes_native),
            db => db.Entities.Select(o => o.Year * 2).Distinct().Where(v => v > 4040).ToList());

        Assert.Equal([4042], result);
    }

    [Fact]
    public void Computed_sole_string_key_Distinct_then_Where_on_bare_parameter_goes_native()
    {
        var result = RunDistinct(nameof(Computed_sole_string_key_Distinct_then_Where_on_bare_parameter_goes_native),
            db => db.Entities.Select(o => o.Country + o.City).Distinct().Where(v => v != "UKLondon").ToList()
                .OrderBy(v => v).ToList());

        Assert.Equal(["FRParis", "USNYC"], result);
    }

    [Fact]
    public void Computed_sole_key_Distinct_then_OrderBy_on_bare_parameter_goes_native()
    {
        var result = RunDistinct(nameof(Computed_sole_key_Distinct_then_OrderBy_on_bare_parameter_goes_native),
            db => db.Entities.Select(o => o.Year * 2).Distinct().OrderByDescending(v => v).ToList());

        Assert.Equal([4042, 4040], result);
    }

    [Fact]
    public void Grouped_count_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity()
    {
        // Control: pins decline (NativeOnly throws) plus Native/DriverLinq fallback parity for a grouped-aggregate
        // Distinct with a bare-parameter Where. Counts: US 3, UK 2, FR 1. (It does not itself discriminate the
        // Accumulators.Count clause, which is defensive parity; see TryResolveFlattenedAlias.)
        var seed = SeedOrders();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Grouped_count_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity) + mode);
            return db.Entities.GroupBy(o => o.Country).Select(g => g.Count()).Distinct().Where(c => c > 1).ToList()
                .OrderBy(c => c).ToList();
        });

        Assert.Equal([2, 3], result);
    }

    [Fact]
    public void One_member_anonymous_computed_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity()
    {
        // Regression: Select(o => new { S = o.Year * 2 }).Distinct() has the same single computed key part as a bare
        // scalar Distinct, but its parameter is the anonymous type, not the scalar. It must not bind to the scalar
        // alias (which matched 0 rows natively); it declines and falls back to driver-LINQ (1 row).
        var seed = SeedOrders();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode,
                nameof(One_member_anonymous_computed_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity) + mode);
            return db.Entities.Select(o => new { S = o.Year * 2 }).Distinct().Where(v => v.Equals(new { S = 4042 })).ToList();
        });

        Assert.Single(result);
        Assert.Equal(4042, result[0].S);
    }

    [Fact]
    public void One_member_anonymous_field_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity()
    {
        // Regression (field-backed sibling of the computed case above): Select(o => new { o.Country }).Distinct() has
        // one field-backed key part, but its parameter is the anonymous type, not the scalar. It must not bind to the
        // scalar field (0 rows natively); it declines and falls back to driver-LINQ (1 row).
        var seed = SeedOrders();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode,
                nameof(One_member_anonymous_field_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity) + mode);
            return db.Entities.Select(o => new { o.Country }).Distinct().Where(v => v.Equals(new { Country = "US" })).ToList();
        });

        Assert.Single(result);
        Assert.Equal("US", result[0].Country);
    }

    [Fact]
    public void One_member_ValueTuple_field_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity()
    {
        var seed = SeedOrders();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode,
                nameof(One_member_ValueTuple_field_Distinct_then_Where_on_bare_parameter_declines_with_fallback_parity) + mode);
            return db.Entities.Select(o => new ValueTuple<string>(o.Country)).Distinct()
                .Where(v => v.Equals(new ValueTuple<string>("US"))).ToList();
        });

        Assert.Single(result);
        Assert.Equal("US", result[0].Item1);
    }

    [Fact]
    public void One_member_anonymous_Distinct_then_bare_parameter_consumers_match_driver_linq()
    {
        // Audit probe for the other bare-parameter resolution sites over a one-member anonymous Distinct:
        // OrderBy(v => v) (TryResolveDistinctOrderingKey identity arm), Any/Count(predicate) (NativeCardinalityBinder
        // via the DistinctAliasScope translator), Contains(item). Each must equal driver-LINQ (native or fallback).
        var seed = SeedOrders();

        object Run(MongoQueryMode mode, int which)
        {
            using var db = CreateContext(seed, mode, nameof(One_member_anonymous_Distinct_then_bare_parameter_consumers_match_driver_linq) + mode + which);
            var q = db.Entities.Select(o => new { o.Country }).Distinct();
            return which switch
            {
                0 => q.OrderBy(v => v).AsEnumerable().Select(v => v.Country).ToArray(),
                1 => q.Any(v => v.Equals(new { Country = "US" })),
                2 => q.Count(v => v.Equals(new { Country = "US" })),
                3 => q.Contains(new { Country = "US" }),
                4 => q.Any(v => v.Equals(new { Country = "ZZ" })),
                _ => q.Where(v => !v.Equals(new { Country = "US" })).AsEnumerable().Select(v => v.Country).OrderBy(c => c).ToArray(),
            };
        }

        for (var which = 0; which <= 5; which++)
        {
            // No catch: a throw on either path fails the probe outright (a same-type throw on both sides must not
            // compare equal and mask a divergence).
            var driver = Run(MongoQueryMode.DriverLinq, which);
            var native = Run(MongoQueryMode.Native, which);

            Assert.True(
                driver is Array da && native is Array na ? da.Cast<object>().SequenceEqual(na.Cast<object>()) : Equals(driver, native),
                $"probe {which}: driver={Describe(driver)} native={Describe(native)}");
        }

        static string Describe(object o) => o is Array a ? string.Join(",", a.Cast<object>()) : o.ToString()!;
    }

    [Fact]
    public void Bare_scalar_nullable_Distinct_then_Where_on_bare_parameter_goes_native()
    {
        // Control: the type guard must not reject a legitimate nullable bare-scalar key (int? field).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_scalar_nullable_Distinct_then_Where_on_bare_parameter_goes_native));

        var result = db.Entities.Select(o => o.Rank).Distinct().Where(v => v == 7).ToList();
        Assert.Equal([7], result);
    }

    [Fact]
    public void Bare_scalar_enum_Distinct_then_Where_on_bare_parameter_goes_native()
    {
        // Control: the type guard must not reject a legitimate enum bare-scalar key.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_scalar_enum_Distinct_then_Where_on_bare_parameter_goes_native));

        var result = db.Entities.Select(o => o.Status).Distinct().Where(v => v == OrderStatus.Shipped).ToList();
        Assert.Equal([OrderStatus.Shipped], result);
    }

    // --- Bare-scalar Distinct over a value-converted property (IsBareValueConvertedDistinctKey) ---
    //
    // The $group dedups the STORED values (as driver-LINQ does) and the shaper reads them back through the property's
    // converter. Comparisons serialize their constant/parameter through the converter; ordering and relational
    // comparisons act on the stored value, as for the same property in an ordinary native Where/OrderBy and on
    // driver-LINQ (enum-as-string sorts Cancelled < New < Shipped; int-as-string sorts "2020" < "2021" < "999").

    private class ConvertedOrder
    {
        public ObjectId Id { get; set; }
        public OrderStatus Status { get; set; }
        public int Year { get; set; }
        public int? Rank { get; set; }
        public bool Paid { get; set; }

        // Set-op operand partners for Status: PlainStatus has no converter (stored as an int), PrevStatus has the same
        // enum->string converter, AltStatus and UpperStatus lambda converters of the same converter type but different
        // conversions (lower-case / upper-case string).
        public OrderStatus PlainStatus { get; set; }
        public OrderStatus PrevStatus { get; set; }
        public OrderStatus AltStatus { get; set; }
        public OrderStatus UpperStatus { get; set; }
    }

    private static ConvertedOrder[] SeedConvertedOrders() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Status = OrderStatus.New, Year = 2020, Rank = 7, Paid = true, PlainStatus = OrderStatus.New, PrevStatus = OrderStatus.New, AltStatus = OrderStatus.Cancelled, UpperStatus = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Status = OrderStatus.New, Year = 2020, Paid = false, PlainStatus = OrderStatus.Shipped, PrevStatus = OrderStatus.New, AltStatus = OrderStatus.Cancelled, UpperStatus = OrderStatus.Cancelled },
        new() { Id = ObjectId.GenerateNewId(), Status = OrderStatus.Shipped, Year = 2021, Rank = 3, Paid = true, PlainStatus = OrderStatus.New, PrevStatus = OrderStatus.New, AltStatus = OrderStatus.New, UpperStatus = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Status = OrderStatus.Shipped, Year = 999, Rank = 7, Paid = true, PlainStatus = OrderStatus.New, PrevStatus = OrderStatus.Shipped, AltStatus = OrderStatus.New, UpperStatus = OrderStatus.New },
        new() { Id = ObjectId.GenerateNewId(), Status = OrderStatus.Cancelled, Year = 2021, Paid = false, PlainStatus = OrderStatus.New, PrevStatus = OrderStatus.Shipped, AltStatus = OrderStatus.Shipped, UpperStatus = OrderStatus.New },
    ];

    private static void ConfigureConverters(ModelBuilder mb)
    {
        var entity = mb.Entity<ConvertedOrder>();
        entity.Property(o => o.Status).HasConversion<string>();
        entity.Property(o => o.PrevStatus).HasConversion<string>();
        entity.Property(o => o.AltStatus).HasConversion(
            v => v.ToString().ToLowerInvariant(), v => Enum.Parse<OrderStatus>(v, true));
        entity.Property(o => o.UpperStatus).HasConversion(
            v => v.ToString().ToUpperInvariant(), v => Enum.Parse<OrderStatus>(v, true));
        entity.Property(o => o.Year).HasConversion<string>();
        entity.Property(o => o.Rank).HasConversion<string>();
        entity.Property(o => o.Paid).HasConversion(v => v ? "Y" : "N", v => v == "Y");
    }

    // One EF-seeded collection (so the stored form is the converted one) queried under each mode.
    private Func<MongoQueryMode, List<T>> ConvertedRunner<T>(
        string name, Func<IQueryable<ConvertedOrder>, List<T>> query, Action<ModelBuilder>? configure = null)
    {
        configure ??= ConfigureConverters;
        var collection = database.MongoDatabase.GetCollection<ConvertedOrder>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);

        using (var seedDb = MakeConverted(collection, MongoQueryMode.Native, configure))
        {
            seedDb.Entities.AddRange(SeedConvertedOrders());
            seedDb.SaveChanges();
        }

        return mode =>
        {
            using var db = MakeConverted(collection, mode, configure);
            return query(db.Entities);
        };
    }

    private static SingleEntityDbContext<ConvertedOrder> MakeConverted(
        IMongoCollection<ConvertedOrder> collection, MongoQueryMode mode, Action<ModelBuilder> configure)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: configure,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });


    // A second entity type in its own collection, so a projected set op can combine two properties with the same
    // name (hence the same projected alias) but different stored forms. Driver-LINQ rejects every cross-DbSet query
    // ("Unsupported cross-DbSet query"), so these shapes have no driver oracle: NativeOnly against a hand oracle.
    private class OtherOrder
    {
        public ObjectId Id { get; set; }
        public OrderStatus Status { get; set; }
    }

    private sealed class CrossCollectionContext(
        TemporaryDatabaseFixture database, string prefix, MongoQueryMode mode, Action<ModelBuilder> configureOther)
        : DbContext(new DbContextOptionsBuilder<CrossCollectionContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, NoModelCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<ConvertedOrder> Converted { get; set; } = null!;
        public DbSet<OtherOrder> Others { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureConverters(modelBuilder);
            modelBuilder.Entity<ConvertedOrder>().ToCollection(prefix + "c");
            modelBuilder.Entity<OtherOrder>().ToCollection(prefix + "o");
            configureOther(modelBuilder);
        }

        // Each context's OtherOrder configuration differs, so never reuse a cached model.
        private sealed class NoModelCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    // Converted: Status New, New, Shipped, Shipped, Cancelled (stored "New", ...). Others: Status New, New, Shipped,
    // stored however configureOther says. Both EF-seeded.
    private Func<MongoQueryMode, CrossCollectionContext> CrossCollection(string name, Action<ModelBuilder> configureOther)
    {
        var prefix = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        using (var seed = new CrossCollectionContext(database, prefix, MongoQueryMode.Native, configureOther))
        {
            seed.Converted.AddRange(SeedConvertedOrders());
            seed.Others.AddRange(
                new OtherOrder { Id = ObjectId.GenerateNewId(), Status = OrderStatus.New },
                new OtherOrder { Id = ObjectId.GenerateNewId(), Status = OrderStatus.New },
                new OtherOrder { Id = ObjectId.GenerateNewId(), Status = OrderStatus.Shipped });
            seed.SaveChanges();
        }

        return mode => new CrossCollectionContext(database, prefix, mode, configureOther);
    }

    private static List<OrderStatus> Sorted(IQueryable<OrderStatus> query) => query.ToList().OrderBy(v => v).ToList();

    // EF-337: an order-sensitive operator over a converted Distinct key declines natively, and Native and DriverLinq both
    // get the driver-LINQ bridge's refusal naming the property; never an answer computed from the stored form. The same
    // query evaluated in memory over the materialized entities (the client-side workaround the message suggests)
    // documents the correct answer.
    private void AssertStoredOrderingRefusal<T>(
        string name, Func<IQueryable<ConvertedOrder>, List<T>> query, string property, List<T> expected)
    {
        var run = ConvertedRunner(name, query);
        Assert.Equal(typeof(NotSupportedException), DeclinesToSameFailure(run));
        Assert.Contains($"'{property}'", Assert.Throws<NotSupportedException>(() => run(MongoQueryMode.Native)).Message);

        Assert.Equal(expected, ConvertedRunner(name + "_oracle", q => query(q.ToList().AsQueryable()))(MongoQueryMode.Native));
    }

    // For a shape whose driver-LINQ fallback throws: NativeOnly declines, and Native (falling back) throws the same
    // exception type as DriverLinq rather than answering natively. Returns that exception type.
    private static Type DeclinesToSameFailure<T>(Func<MongoQueryMode, List<T>> run)
    {
        Assert.Throws<NativeTranslationNotSupportedException>(() => run(MongoQueryMode.NativeOnly));
        var driver = Record.Exception(() => run(MongoQueryMode.DriverLinq));
        var native = Record.Exception(() => run(MongoQueryMode.Native));
        Assert.NotNull(driver);
        Assert.NotNull(native);
        Assert.Equal(driver.GetType(), native.GetType());
        return native.GetType();
    }

    [Fact]
    public void Bare_scalar_value_converted_enum_Distinct_materializes_converted_values()
    {
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Bare_scalar_value_converted_enum_Distinct_materializes_converted_values),
            q => q.Select(o => o.Status).Distinct().ToList().OrderBy(v => v).ToList()));

        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_Where_on_bare_parameter_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Bare_scalar_value_converted_Distinct_then_Where_on_bare_parameter_goes_native),
            q => q.Select(o => o.Status).Distinct().Where(v => v == OrderStatus.Shipped).ToList()));

        Assert.Equal([OrderStatus.Shipped], result);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_Where_not_equal_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Bare_scalar_value_converted_Distinct_then_Where_not_equal_goes_native),
            q => q.Select(o => o.Status).Distinct().Where(v => v != OrderStatus.Shipped).ToList()
                .OrderBy(v => v).ToList()));

        Assert.Equal([OrderStatus.New, OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_Where_on_captured_parameter_goes_native()
    {
        var status = OrderStatus.Cancelled;
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Bare_scalar_value_converted_Distinct_then_Where_on_captured_parameter_goes_native),
            q => q.Select(o => o.Status).Distinct().Where(v => v == status).ToList()));

        Assert.Equal([OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_OrderBy_is_refused()
    {
        // Sorting the stored (string) form would give Cancelled, New, Shipped instead of the enum order (EF-337): native
        // declines and the driver-LINQ fallback refuses, naming the property.
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_value_converted_Distinct_then_OrderBy_is_refused),
            q => q.Select(o => o.Status).Distinct().OrderBy(v => v).ToList(),
            "ConvertedOrder.Status",
            [OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled]);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_OrderByDescending_Take_is_refused()
    {
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_value_converted_Distinct_then_OrderByDescending_Take_is_refused),
            q => q.Select(o => o.Status).Distinct().OrderByDescending(v => v).Take(2).ToList(),
            "ConvertedOrder.Status",
            [OrderStatus.Cancelled, OrderStatus.Shipped]);
    }

    [Fact]
    public void Bare_scalar_order_preserving_converted_Distinct_then_OrderBy_goes_native()
    {
        // The refusal above is only for storage that doesn't order like the CLR value. EF's CastingConverter int -> long
        // is on the StoredOrdering allow-list, so a post-Distinct sort over it stays native and sorts correctly.
        var run = ConvertedRunner<int[]>(
            nameof(Bare_scalar_order_preserving_converted_Distinct_then_OrderBy_goes_native),
            q =>
            {
                var distinct = q.Select(o => o.Year).Distinct();
                return [distinct.OrderBy(v => v).ToArray(), distinct.OrderByDescending(v => v).Take(2).ToArray()];
            },
            mb =>
            {
                ConfigureConverters(mb);
                mb.Entity<ConvertedOrder>().Property(o => o.Year).HasConversion<long>();
            });

        var result = NativeModeAssert.NativeAndParity(run);

        Assert.Equal([999, 2020, 2021], result[0]);
        Assert.Equal([2021, 2020], result[1]);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_Count_and_Any_go_native()
    {
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner<int>(
            nameof(Bare_scalar_value_converted_Distinct_then_Count_and_Any_go_native),
            q =>
            {
                var distinct = q.Select(o => o.Status).Distinct();
                return
                [
                    distinct.Count(),
                    distinct.Count(v => v != OrderStatus.New),
                    distinct.Any() ? 1 : 0,
                    distinct.Any(v => v == OrderStatus.Cancelled) ? 1 : 0,
                    distinct.Where(v => v == OrderStatus.Shipped).Count()
                ];
            }));

        Assert.Equal([3, 2, 1, 1, 1], result);
    }

    [Fact]
    public void Bare_scalar_int_to_string_converted_Distinct_materializes_and_compares_goes_native()
    {
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner<int[]>(
            nameof(Bare_scalar_int_to_string_converted_Distinct_materializes_and_compares_goes_native),
            q =>
            {
                var distinct = q.Select(o => o.Year).Distinct();
                return
                [
                    distinct.ToList().OrderBy(v => v).ToArray(),
                    distinct.Where(v => v == 2021).ToArray(),
                    distinct.Where(v => v != 2021).ToList().OrderBy(v => v).ToArray()
                ];
            }));

        Assert.Equal([999, 2020, 2021], result[0]);
        Assert.Equal([2021], result[1]);
        Assert.Equal([999, 2020], result[2]);

        // Stored-string order and comparison ("999" > "2021") are refused (EF-337).
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_int_to_string_converted_Distinct_materializes_and_compares_goes_native) + "_OrderBy",
            q => q.Select(o => o.Year).Distinct().OrderBy(v => v).ToList(),
            "ConvertedOrder.Year",
            [999, 2020, 2021]);
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_int_to_string_converted_Distinct_materializes_and_compares_goes_native) + "_Where",
            q => q.Select(o => o.Year).Distinct().Where(v => v > 2020).ToList(),
            "ConvertedOrder.Year",
            [2021]);
    }

    [Fact]
    public void Bare_scalar_bool_to_string_converted_Distinct_goes_native()
    {
        // Paid is stored as "Y"/"N". Materialization has a driver-LINQ oracle; ordering by the stored form is refused
        // (EF-337).
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Bare_scalar_bool_to_string_converted_Distinct_goes_native),
            q => q.Select(o => o.Paid).Distinct().ToList().OrderBy(v => v).ToList()));

        Assert.Equal([false, true], result);

        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_bool_to_string_converted_Distinct_goes_native) + "_OrderByDescending",
            q => q.Select(o => o.Paid).Distinct().OrderByDescending(v => v).ToList(),
            "ConvertedOrder.Paid",
            [true, false]);
    }

    [Fact]
    public void Bare_scalar_bool_to_string_converted_Distinct_then_bool_predicates_go_native_with_hand_oracle()
    {
        // NativeOnly against a hand oracle: driver-LINQ is WRONG here. It renders `v`/`v == true` against the raw
        // BSON true instead of the converted "Y", so Where(v => v) returns [] and Where(v => !v) returns both values.
        // Native serializes the comparison through the converter (as Where(o => o.Paid) over the entity does).
        var run = ConvertedRunner<bool[]>(
            nameof(Bare_scalar_bool_to_string_converted_Distinct_then_bool_predicates_go_native_with_hand_oracle),
            q =>
            {
                var distinct = q.Select(o => o.Paid).Distinct();
                return
                [
                    distinct.Where(v => v).ToArray(),
                    distinct.Where(v => !v).ToArray(),
                    distinct.Where(v => v == true).ToArray(),
                    [distinct.Count(v => v) == 1],
                ];
            });

        var result = run(MongoQueryMode.NativeOnly);

        Assert.Equal([true], result[0]);
        Assert.Equal([false], result[1]);
        Assert.Equal([true], result[2]);
        Assert.Equal([true], result[3]);
    }

    [Fact]
    public void Bare_scalar_nullable_converted_Distinct_goes_native_with_hand_oracle()
    {
        // NativeOnly against a hand oracle: driver-LINQ can't run this shape at all (it throws "Serializer value type
        // IQueryable<Int32> is incompatible with expression value type IQueryable<Nullable<Int32>>" for a converted
        // int? projection). Distinct stored values: "7", null, "3".
        var run = ConvertedRunner<int?[]>(
            nameof(Bare_scalar_nullable_converted_Distinct_goes_native_with_hand_oracle),
            q =>
            {
                var distinct = q.Select(o => o.Rank).Distinct();
                return
                [
                    distinct.ToList().OrderBy(v => v).ToArray(),
                    distinct.Where(v => v == null).ToArray(),
                    distinct.Where(v => v == 7).ToArray(),
                    distinct.Where(v => v != null).ToList().OrderBy(v => v).ToArray(),
                ];
            });

        var result = run(MongoQueryMode.NativeOnly);

        Assert.Equal([null, 3, 7], result[0]);
        Assert.Equal([null], result[1]);
        Assert.Equal([7], result[2]);
        Assert.Equal([3, 7], result[3]);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_selectorless_aggregate_is_refused()
    {
        // A selector-less Max/Min/Sum/Average would reduce the stored values and read the result through a generic
        // CLR serializer (Max over the enum stored as a string throws InvalidCastException natively); it declines,
        // like the ungrouped Select(o => o.Status).Max(), and the driver-LINQ fallback refuses it (EF-337) instead of
        // reducing the stored strings ("Shipped").
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_value_converted_Distinct_then_selectorless_aggregate_is_refused),
            q => new List<OrderStatus> { q.Select(o => o.Status).Distinct().Max() },
            "ConvertedOrder.Status",
            [OrderStatus.Cancelled]);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_arithmetic_predicate_declines_cleanly()
    {
        // Arithmetic over the stored string can't honour the converter; the existing AllFieldsDefaultSerialized guard
        // declines it, as it does over the entity field.
        var result = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(Bare_scalar_value_converted_Distinct_then_arithmetic_predicate_declines_cleanly),
            q => q.Select(o => o.Year).Distinct().Where(v => v % 2 == 1).ToList()));

        Assert.Empty(result); // "$mod" over the stored strings matches nothing on the fallback either.
    }

    [Fact]
    public void One_member_anonymous_value_converted_Distinct_declines_cleanly()
    {
        // Scope pin: only a bare projection is admitted (IsBareValueConvertedDistinctKey). A wrapper's member-level
        // post-Distinct consumers haven't been audited for converters, so it stays on the fallback.
        var result = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(One_member_anonymous_value_converted_Distinct_declines_cleanly),
            q => q.Select(o => new { o.Status }).Distinct().ToList().Select(r => r.Status).OrderBy(v => v).ToList()));

        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Bare_scalar_bson_represented_Distinct_declines_cleanly()
    {
        // Scope pin: a BsonRepresentation key (no converter) stays on the fallback, bare or wrapped
        // (Distinct_bson_represented_projection_key_falls_back covers the wrapped form).
        var result = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(Bare_scalar_bson_represented_Distinct_declines_cleanly),
            q => q.Select(o => o.Status).Distinct().Where(v => v != OrderStatus.New).ToList().OrderBy(v => v).ToList(),
            mb => mb.Entity<ConvertedOrder>().Property(o => o.Status).HasBsonRepresentation(BsonType.String)));

        Assert.Equal([OrderStatus.Shipped, OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Bare_scalar_value_converted_Distinct_then_Contains_declines_cleanly()
    {
        // Contains(item) over the converted key stays on the fallback (no native Contains-over-Distinct for a
        // non-default-serialized key). NB: the driver-LINQ fallback answers false here although Shipped is present
        // (it compares the stored "Shipped" against the unconverted enum), so only parity is pinned, not the value.
        var result = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(Bare_scalar_value_converted_Distinct_then_Contains_declines_cleanly),
            q => new List<bool> { q.Select(o => o.Status).Distinct().Contains(OrderStatus.Shipped) }));

        Assert.Single(result);
    }

    [Fact]
    public void Bare_scalar_int_to_string_converted_Distinct_then_selectorless_Average_is_refused()
    {
        // Native once answered 0 here (a $avg over the stored strings, read back as a number). It declines
        // (TryBindDistinctTerminalAggregate's non-default-serialized key guard), and the driver-LINQ fallback refuses it
        // (EF-337) rather than running a $avg over strings.
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_int_to_string_converted_Distinct_then_selectorless_Average_is_refused),
            q => new List<double> { q.Select(o => o.Year).Distinct().Average() },
            "ConvertedOrder.Year",
            [1680.0]);
    }

    [Fact]
    public void Bare_scalar_int_to_string_converted_Distinct_then_selectorless_Sum_is_refused()
    {
        // Declines like Average; the driver-LINQ fallback refuses it (EF-337) instead of a $sum that skips the stored
        // strings and answers 0 (the true sum is 5040).
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_int_to_string_converted_Distinct_then_selectorless_Sum_is_refused),
            q => new List<int> { q.Select(o => o.Year).Distinct().Sum() },
            "ConvertedOrder.Year",
            [5040]);
    }

    [Fact]
    public void Bare_scalar_value_converted_enum_Distinct_then_relational_Where_is_refused()
    {
        // A stored-string comparison ("Shipped" > "New", "Cancelled" < "New") disagrees with the enum order, so it is
        // refused (EF-337) rather than answered.
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_value_converted_enum_Distinct_then_relational_Where_is_refused) + "_gt",
            q => q.Select(o => o.Status).Distinct().Where(v => v > OrderStatus.New).ToList(),
            "ConvertedOrder.Status",
            [OrderStatus.Shipped, OrderStatus.Cancelled]);
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_value_converted_enum_Distinct_then_relational_Where_is_refused) + "_lt",
            q => q.Select(o => o.Status).Distinct().Where(v => v < OrderStatus.Shipped).ToList(),
            "ConvertedOrder.Status",
            [OrderStatus.New]);
        AssertStoredOrderingRefusal(
            nameof(Bare_scalar_value_converted_enum_Distinct_then_relational_Where_is_refused) + "_ge",
            q => q.Select(o => o.Status).Distinct().Where(v => v >= OrderStatus.New).ToList(),
            "ConvertedOrder.Status",
            [OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled]);
    }

    [Fact]
    public void Bare_scalar_nullable_converted_Distinct_then_Count_counts_null_group_with_hand_oracle()
    {
        // NativeOnly against a hand oracle: driver-LINQ throws for a converted int? projection (see
        // Bare_scalar_nullable_converted_Distinct_goes_native_with_hand_oracle). Distinct stored values: "7", null, "3".
        var run = ConvertedRunner<long>(
            nameof(Bare_scalar_nullable_converted_Distinct_then_Count_counts_null_group_with_hand_oracle),
            q =>
            {
                var distinct = q.Select(o => o.Rank).Distinct();
                return [distinct.Count(), distinct.LongCount(), distinct.Count(v => v == null), distinct.Count(v => v != null)];
            });

        Assert.Equal([3L, 3L, 1L, 2L], run(MongoQueryMode.NativeOnly));
    }

    // --- Projected set ops over value-converted operands (OperandSerializationsMatch) ---
    //
    // Dedup and Intersect/Except compare the STORED values and source1's shaper reads every combined row, so each
    // alias must be stored the same way on both operands: the same property, both default-serialized, or equivalent
    // converters. Otherwise the set op declines (Union/Concat fall back; Intersect/Except hard-fail).

    [Fact]
    public void Converted_Distinct_set_ops_over_the_same_property_go_native()
    {
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner<OrderStatus[]>(
            nameof(Converted_Distinct_set_ops_over_the_same_property_go_native),
            q =>
            [
                Sorted(q.Select(o => o.Status).Distinct().Union(q.Where(o => o.Year == 2020).Select(o => o.Status).Distinct())).ToArray(),
                Sorted(q.Select(o => o.Status).Distinct().Concat(q.Where(o => o.Year == 2020).Select(o => o.Status))).ToArray(),
            ]));

        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], result[0]);
        Assert.Equal([OrderStatus.New, OrderStatus.New, OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], result[1]);

        var years = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Converted_Distinct_set_ops_over_the_same_property_go_native) + "Y",
            q => q.Select(o => o.Year).Distinct().Union(q.Where(o => o.Status == OrderStatus.New).Select(o => o.Year).Distinct())
                .ToList().OrderBy(v => v).ToList()));

        Assert.Equal([999, 2020, 2021], years);
    }

    [Fact]
    public void Converted_Distinct_Intersect_Except_over_the_same_property_go_native_with_hand_oracle()
    {
        // Intersect/Except have no driver-LINQ oracle. Year == 2020 rows have Status New only.
        var run = ConvertedRunner<OrderStatus[]>(
            nameof(Converted_Distinct_Intersect_Except_over_the_same_property_go_native_with_hand_oracle),
            q =>
            [
                Sorted(q.Select(o => o.Status).Distinct().Intersect(q.Where(o => o.Year == 2020).Select(o => o.Status).Distinct())).ToArray(),
                Sorted(q.Select(o => o.Status).Distinct().Except(q.Where(o => o.Year == 2020).Select(o => o.Status).Distinct())).ToArray(),
            ]);

        var result = run(MongoQueryMode.NativeOnly);

        Assert.Equal([OrderStatus.New], result[0]);
        Assert.Equal([OrderStatus.Shipped, OrderStatus.Cancelled], result[1]);
    }

    [Fact]
    public void Converted_Distinct_set_op_with_a_differently_named_plain_operand_declines()
    {
        // Status.Distinct() against PlainStatus (no converter, stored as an int): the aliases differ, so
        // ProjectionShapesMatch already declines. The fallback fails reading the int through Status's string converter
        // (both modes), or with PlainStatus first dedups nothing across the stored forms (both modes; wrong, since
        // the true Union is New, Shipped, Cancelled).
        DeclinesToSameFailure(ConvertedRunner(
            nameof(Converted_Distinct_set_op_with_a_differently_named_plain_operand_declines),
            q => Sorted(q.Select(o => o.Status).Distinct().Union(q.Select(o => o.PlainStatus)))));

        var reversed = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(Converted_Distinct_set_op_with_a_differently_named_plain_operand_declines) + "R",
            q => Sorted(q.Select(o => o.PlainStatus).Distinct().Union(q.Select(o => o.Status).Distinct()))));

        Assert.Equal(5, reversed.Count);
    }

    [Fact]
    public void Wrapped_set_op_mixing_converted_and_plain_properties_under_one_alias_declines_cleanly()
    {
        // Base hazard (not Distinct-specific): new { S = o.Status } (stored "New") and new { S = o.PlainStatus }
        // (stored 0) share the alias S. Natively this threw reading 0 through Status's converter; driver-LINQ reads
        // each side through its own member serializer and returns all ten values.
        var result = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(Wrapped_set_op_mixing_converted_and_plain_properties_under_one_alias_declines_cleanly),
            q => q.Select(o => new { S = o.Status }).Concat(q.Select(o => new { S = o.PlainStatus }))
                .ToList().Select(r => r.S).OrderBy(v => v).ToList()));

        Assert.Equal(10, result.Count);
    }

    [Fact]
    public void Wrapped_set_op_mixing_different_converters_of_one_converter_type_declines_cleanly()
    {
        // AltStatus ("cancelled") and UpperStatus ("CANCELLED") are both ValueConverter<OrderStatus, string> with the
        // same provider type; only the conversion expressions differ, so this pins the expression comparison.
        var result = NativeModeAssert.DeclinesCleanly(ConvertedRunner(
            nameof(Wrapped_set_op_mixing_different_converters_of_one_converter_type_declines_cleanly),
            q => q.Select(o => new { S = o.AltStatus }).Union(q.Select(o => new { S = o.UpperStatus }))
                .ToList().Select(r => r.S).OrderBy(v => v).ToList()));

        Assert.NotEmpty(result);
    }

    [Fact]
    public void Wrapped_set_op_over_two_properties_with_equivalent_converters_goes_native()
    {
        // Status and PrevStatus both HasConversion<string>(): different properties, equivalent converters (same type,
        // structurally equal conversions), so dedup over the stored strings is right.
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Wrapped_set_op_over_two_properties_with_equivalent_converters_goes_native),
            q => q.Select(o => new { S = o.Status }).Union(q.Select(o => new { S = o.PrevStatus }))
                .ToList().Select(r => r.S).OrderBy(v => v).ToList()));

        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Bare_set_op_over_the_same_bson_represented_property_goes_native()
    {
        // The same property on both sides is always compatible, including a BsonRepresentation (which the converter
        // equivalence doesn't cover).
        var result = NativeModeAssert.NativeAndParity(ConvertedRunner(
            nameof(Bare_set_op_over_the_same_bson_represented_property_goes_native),
            q => Sorted(q.Select(o => o.Status).Union(q.Where(o => o.Year == 2020).Select(o => o.Status))),
            mb => mb.Entity<ConvertedOrder>().Property(o => o.Status).HasBsonRepresentation(BsonType.String)));

        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], result);
    }

    [Fact]
    public void Cross_collection_converted_set_ops_with_the_same_converter_go_native_with_hand_oracle()
    {
        var context = CrossCollection(
            nameof(Cross_collection_converted_set_ops_with_the_same_converter_go_native_with_hand_oracle),
            mb => mb.Entity<OtherOrder>().Property(o => o.Status).HasConversion<string>());

        using var db = context(MongoQueryMode.NativeOnly);
        var converted = db.Converted.Select(o => o.Status);
        var others = db.Others.Select(o => o.Status);

        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], Sorted(converted.Distinct().Union(others.Distinct())));
        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], Sorted(others.Union(converted.Distinct())));
        Assert.Equal(
            [OrderStatus.New, OrderStatus.New, OrderStatus.Shipped, OrderStatus.Shipped, OrderStatus.Cancelled],
            Sorted(converted.Distinct().Concat(others.Distinct())));
        Assert.Equal([OrderStatus.New, OrderStatus.Shipped], Sorted(converted.Distinct().Intersect(others.Distinct())));
        Assert.Equal([OrderStatus.Cancelled], Sorted(converted.Distinct().Except(others)));
        Assert.Equal([OrderStatus.New, OrderStatus.Shipped, OrderStatus.Cancelled], Sorted(converted.Union(others)));
    }

    public static TheoryData<string> MismatchedOtherStatusConfigurations => ["plain", "lowercase", "bsonRepresentation"];

    [Theory]
    [MemberData(nameof(MismatchedOtherStatusConfigurations))]
    public void Cross_collection_set_ops_with_mismatched_stored_forms_decline(string otherConfiguration)
    {
        // Converted.Status is stored "Shipped"; Others.Status is stored 1 (plain), "shipped" (lowercase converter) or
        // "Shipped" via a BsonRepresentation (same bytes by coincidence, still declined: not the same property and no
        // converter to compare). Before OperandSerializationsMatch these went native and dedup'd nothing
        // (Union: New, New, Shipped, Shipped, Cancelled), intersected to empty, or threw reading 1 as a string, for
        // bare, bare-Distinct and mixed operands alike. There is no driver-LINQ oracle (cross-DbSet), so Union/Concat
        // decline and Intersect/Except hard-fail.
        Action<ModelBuilder> configure = otherConfiguration switch
        {
            "plain" => _ => { },
            "lowercase" => mb => mb.Entity<OtherOrder>().Property(o => o.Status).HasConversion(
                v => v.ToString().ToLowerInvariant(), v => Enum.Parse<OrderStatus>(v, true)),
            _ => mb => mb.Entity<OtherOrder>().Property(o => o.Status).HasBsonRepresentation(BsonType.String),
        };
        var context = CrossCollection(
            nameof(Cross_collection_set_ops_with_mismatched_stored_forms_decline) + otherConfiguration, configure);

        using var db = context(MongoQueryMode.NativeOnly);
        var converted = db.Converted.Select(o => o.Status);
        var others = db.Others.Select(o => o.Status);

        Assert.Throws<NativeTranslationNotSupportedException>(() => Sorted(converted.Union(others)));
        Assert.Throws<NativeTranslationNotSupportedException>(() => Sorted(others.Concat(converted)));
        Assert.Throws<InvalidOperationException>(() => Sorted(converted.Intersect(others)));
        if (otherConfiguration != "bsonRepresentation") // a BsonRepresentation Distinct key already declines
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => Sorted(converted.Distinct().Union(others.Distinct())));
            Assert.Throws<NativeTranslationNotSupportedException>(() => Sorted(others.Distinct().Union(converted.Distinct())));
            Assert.Throws<InvalidOperationException>(() => Sorted(converted.Distinct().Except(others.Distinct())));
        }

        Assert.Throws<NativeTranslationNotSupportedException>(() => Sorted(converted.Distinct().Concat(others)));
        Assert.Throws<NativeTranslationNotSupportedException>(() => Sorted(others.Union(converted.Distinct())));
    }

    [Fact]
    public void Anonymous_computed_projection_Distinct_then_OrderBy_on_member_goes_native()
    {
        // A computed projection member (A = o.Country + o.City) has no IProperty; the ordering key resolves against
        // the Distinct's flattened alias via a MongoElementRefExpression (as TryResolveFlattenedAlias does for
        // Where).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Anonymous_computed_projection_Distinct_then_OrderBy_on_member_goes_native));

        var result = db.Entities.Select(o => new { A = o.Country + o.City }).Distinct()
            .OrderBy(n => n.A).ToList();

        Assert.Equal(["FRParis", "UKLondon", "USNYC"], result.Select(r => r.A).ToArray());
    }

    [Fact]
    public void Anonymous_computed_projection_Distinct_then_OrderBy_on_member_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Anonymous_computed_projection_Distinct_then_OrderBy_on_member_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Anonymous_computed_projection_Distinct_then_OrderBy_on_member_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { A = o.Country + o.City }).Distinct()
                .OrderBy(n => n.A).AsEnumerable().Select(n => n.A).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(["FRParis", "UKLondon", "USNYC"], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Whole_entity_Distinct_goes_native()
    {
        // Whole-entity Distinct appends a MongoDistinctOp, lowering to the $group{_id:"$$ROOT"}/$replaceRoot dedup
        // that Union uses. Ids are unique, so this proves routing and rows, not collapsing.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Whole_entity_Distinct_goes_native));

        var result = db.Entities.Distinct().ToList();

        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void Whole_entity_Distinct_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Whole_entity_Distinct_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Whole_entity_Distinct_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Distinct().AsEnumerable().Select(o => o.Country).OrderBy(c => c).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(6, native.Length);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Whole_entity_Distinct_composes_with_where_orderby_skip_take_goes_native()
    {
        // Whole-entity Distinct is an ordinary PipelineOps entry, so operators around it need no special-casing.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Whole_entity_Distinct_composes_with_where_orderby_skip_take_goes_native));

        var result = db.Entities.Where(o => o.Country != "FR").Distinct()
            .OrderBy(o => o.Country).Skip(1).Take(2).ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, o => Assert.NotEqual("FR", o.Country));
    }

    [Fact]
    public void Whole_entity_Distinct_composes_with_where_orderby_skip_take_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Whole_entity_Distinct_composes_with_where_orderby_skip_take_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Whole_entity_Distinct_composes_with_where_orderby_skip_take_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Where(o => o.Country != "FR").Distinct()
                .OrderBy(o => o.Country).Skip(1).Take(2)
                .AsEnumerable().Select(o => o.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Whole_entity_Distinct_then_Select_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Whole_entity_Distinct_then_Select_goes_native));

        var result = db.Entities.Distinct().Select(o => o.Country).OrderBy(c => c).ToList();

        Assert.Equal(["FR", "UK", "UK", "US", "US", "US"], result);
    }

    [Fact]
    public void Distinct_then_Where_goes_native()
    {
        // Where after a projected Distinct resolves against the Distinct's flattened alias (DistinctAliasScope), not
        // the root entity.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_goes_native));

        var result = db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country == "US").ToList();

        Assert.Equal(["US"], result.Select(r => r.Country).ToArray());
    }

    [Fact]
    public void Distinct_then_Where_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Where_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country == "US")
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(["US"], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Where_on_renamed_member_filters_by_the_projected_source_not_the_colliding_entity_property()
    {
        // `new { Country = o.City }` reuses the entity's "Country" name for City. Resolving by name against the root
        // entity would silently filter on the real Country field.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Where_on_renamed_member_filters_by_the_projected_source_not_the_colliding_entity_property) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_on_renamed_member_filters_by_the_projected_source_not_the_colliding_entity_property) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { Country = o.City }).Distinct().Where(r => r.Country == "NYC")
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(["NYC"], native); // filtered by City's value, not the real Country field (which is never "NYC")
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Where_on_unrelated_computed_key_falls_back_under_native_only()
    {
        // A computed predicate over the alias with no native translation (ToUpper over Trim) must decline, not resolve
        // against the entity. (.Length is translatable, so it wouldn't exercise this.)
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_on_unrelated_computed_key_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country.Trim().ToUpper() == "US").ToList());
    }

    [Fact]
    public void Distinct_then_Where_with_ToUpper_on_renamed_member_resolves_against_the_distinct_alias()
    {
        // ToUpper() == constant is a native regex; it must address the projected City under its "Country" alias, not
        // the entity's real Country field (never "NYC").
        var seed = SeedOrders();
        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_with_ToUpper_on_renamed_member_resolves_against_the_distinct_alias) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_with_ToUpper_on_renamed_member_resolves_against_the_distinct_alias) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { Country = o.City }).Distinct().Where(r => r.Country.ToUpper() == "NYC")
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeOnlyDb);
        Assert.Equal(["NYC"], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Skip_goes_native_and_dedups()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Skip_goes_native_and_dedups));

        var result = db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).Skip(1).ToList();

        Assert.Equal(new[] { "UK", "US" }, result.Select(r => r.Country).ToArray());
    }

    [Fact]
    public void Distinct_then_Skip_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Skip_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Skip_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).Skip(1)
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "UK", "US" }, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_bson_represented_projection_key_falls_back()
    {
        // A non-default BsonRepresentation key (enum as string) can't be read back through a generic CLR serializer
        // by the flattening $project, so it must not go native (shared HasDefaultKeySerialization guard; see
        // NativeGroupByTests.GroupBy_bson_represented_key_falls_back).
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Distinct_bson_represented_projection_key_falls_back)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Order>(collectionName);
        Action<ModelBuilder> configure = mb =>
            mb.Entity<Order>().Property(o => o.Status).HasBsonRepresentation(BsonType.String);

        // Seed via EF so the stored representation matches the configured (string) representation.
        using (var seedDb = Make(collection, MongoQueryMode.Native, configure))
        {
            seedDb.Entities.AddRange(SeedOrders());
            seedDb.SaveChanges();
        }

        using (var nativeDb = Make(collection, MongoQueryMode.Native, configure))
        {
            var result = nativeDb.Entities.Select(o => new { o.Status }).Distinct()
                .AsEnumerable().OrderBy(r => r.Status).ToList();

            Assert.Equal(
                new[] { OrderStatus.New, OrderStatus.Shipped },
                result.Select(r => r.Status).ToArray());
        }

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly, configure);
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.Select(o => new { o.Status }).Distinct().ToList());
    }

    [Fact]
    public void Distinct_then_Count_goes_native()
    {
        // $count follows the $group + flattening $project (and any PostGroupOps). 3 distinct countries.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Count_goes_native));

        Assert.Equal(3, db.Entities.Select(o => new { o.Country }).Distinct().Count());
    }

    [Fact]
    public void Distinct_then_Count_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Count_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Count_matches_driver_linq) + "D");

        int Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().Count();

        var native = Run(nativeDb);
        Assert.Equal(3, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Count_with_predicate_goes_native()
    {
        // Count(pred) resolves against the Distinct's flattened alias, as Where does.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Count_with_predicate_goes_native));

        Assert.Equal(2, db.Entities.Select(o => new { o.Country }).Distinct().Count(r => r.Country != "FR"));
    }

    [Fact]
    public void Distinct_then_Count_with_predicate_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Count_with_predicate_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Count_with_predicate_matches_driver_linq) + "D");

        int Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().Count(r => r.Country != "FR");

        var native = Run(nativeDb);
        Assert.Equal(2, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Count_with_predicate_on_renamed_member_counts_the_projected_source_not_the_colliding_entity_property()
    {
        // `new { Country = o.City }` reuses the entity's "Country" name; resolving by name would count against the
        // real Country field instead of City.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Count_with_predicate_on_renamed_member_counts_the_projected_source_not_the_colliding_entity_property) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Count_with_predicate_on_renamed_member_counts_the_projected_source_not_the_colliding_entity_property) + "D");

        int Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { Country = o.City }).Distinct().Count(r => r.Country != "NYC");

        var native = Run(nativeDb);
        Assert.Equal(2, native); // London, Paris — NOT filtered against the real (always non-"NYC") Country field
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Any_with_predicate_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Any_with_predicate_goes_native));

        Assert.True(db.Entities.Select(o => new { o.Country }).Distinct().Any(r => r.Country == "FR"));
        Assert.False(db.Entities.Select(o => new { o.Country }).Distinct().Any(r => r.Country == "DE"));
    }

    [Fact]
    public void Distinct_then_All_with_predicate_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_All_with_predicate_goes_native));

        Assert.False(db.Entities.Select(o => new { o.Country }).Distinct().All(r => r.Country == "FR"));
        Assert.True(db.Entities.Select(o => new { o.Country }).Distinct().All(r => r.Country != "XX"));
    }

    [Fact]
    public void Distinct_then_Any_All_match_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Any_All_match_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Any_All_match_driver_linq) + "D");

        (bool Any, bool All) Run(SingleEntityDbContext<Order> db) =>
            (db.Entities.Select(o => new { o.Country }).Distinct().Any(r => r.Country == "FR"),
             db.Entities.Select(o => new { o.Country }).Distinct().All(r => r.Country != "XX"));

        var native = Run(nativeDb);
        Assert.Equal((true, true), native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Count_with_unrelated_computed_predicate_falls_back_under_native_only()
    {
        // As the Where test above: ToUpper() over Trim() has no native translation.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Count_with_unrelated_computed_predicate_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.Select(o => new { o.Country }).Distinct().Count(r => r.Country.Trim().ToUpper() == "US"));
    }

    [Fact]
    public void Distinct_then_Count_with_predicate_over_computed_projection_key_goes_native()
    {
        // A computed Distinct key (Country + City) yields the same 3 combinations as the plain-field tests:
        // "USNYC", "UKLondon", "FRParis", 2 starting with "U".
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Count_with_predicate_over_computed_projection_key_goes_native));

        var count = db.Entities.Select(o => new { Combined = o.Country + o.City }).Distinct()
            .Count(r => r.Combined.StartsWith("U"));

        Assert.Equal(2, count);
    }

    [Fact]
    public void Distinct_then_Where_then_Count_goes_native()
    {
        // The Where's $match lands in PostGroupOps and the $count after it; PostGroupOps must still be emitted when a
        // trailing aggregate sets Cardinality, or the $match is silently dropped.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_then_Count_goes_native));

        Assert.Equal(1, db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country == "FR").Count());
    }

    [Fact]
    public void Distinct_then_Where_then_Count_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Where_then_Count_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_then_Count_matches_driver_linq) + "D");

        int Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country != "FR").Count();

        var native = Run(nativeDb);
        Assert.Equal(2, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_scalar_Distinct_then_Max_goes_native(MongoQueryMode mode)
    {
        // Sum/Min/Max/Average on a bare-scalar Distinct. Distinct years: {2020, 2021}.
        using var db = CreateContext(SeedOrders(), mode, nameof(Bare_scalar_Distinct_then_Max_goes_native) + mode);

        Assert.Equal(2021, db.Entities.Select(o => o.Year).Distinct().Max());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_scalar_Distinct_then_Min_goes_native(MongoQueryMode mode)
    {
        using var db = CreateContext(SeedOrders(), mode, nameof(Bare_scalar_Distinct_then_Min_goes_native) + mode);

        Assert.Equal(2020, db.Entities.Select(o => o.Year).Distinct().Min());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_scalar_Distinct_then_Sum_goes_native(MongoQueryMode mode)
    {
        using var db = CreateContext(SeedOrders(), mode, nameof(Bare_scalar_Distinct_then_Sum_goes_native) + mode);

        Assert.Equal(4041, db.Entities.Select(o => o.Year).Distinct().Sum());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_scalar_Distinct_then_Average_goes_native(MongoQueryMode mode)
    {
        using var db = CreateContext(SeedOrders(), mode, nameof(Bare_scalar_Distinct_then_Average_goes_native) + mode);

        Assert.Equal(2020.5, db.Entities.Select(o => o.Year).Distinct().Average());
    }

    [Fact]
    public void Wrapped_projection_Distinct_then_Sum_goes_native()
    {
        // Sum(selector) over a wrapped Distinct resolves against the flattened alias, reusing
        // NativeCardinalityBinder.TryBindAggregate. Distinct {Country, Year}: (US,2020),(US,2021),(UK,2020),(FR,2021);
        // sum of Year = 8082.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Wrapped_projection_Distinct_then_Sum_goes_native));

        Assert.Equal(8082, db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Sum(r => r.Year));
    }

    [Fact]
    public void Wrapped_projection_Distinct_then_Min_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Wrapped_projection_Distinct_then_Min_goes_native));

        Assert.Equal(2020, db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Min(r => r.Year));
    }

    [Fact]
    public void Wrapped_projection_Distinct_then_Max_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Wrapped_projection_Distinct_then_Max_goes_native));

        Assert.Equal(2021, db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Max(r => r.Year));
    }

    [Fact]
    public void Wrapped_projection_Distinct_then_Average_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Wrapped_projection_Distinct_then_Average_goes_native));

        Assert.Equal(2020.5, db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Average(r => r.Year));
    }

    [Fact]
    public void Wrapped_projection_Distinct_then_Sum_Min_Max_Average_match_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Wrapped_projection_Distinct_then_Sum_Min_Max_Average_match_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Wrapped_projection_Distinct_then_Sum_Min_Max_Average_match_driver_linq) + "D");

        (int Sum, int Min, int Max, double Average) Run(SingleEntityDbContext<Order> db) =>
            (db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Sum(r => r.Year),
             db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Min(r => r.Year),
             db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Max(r => r.Year),
             db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Average(r => r.Year));

        var native = Run(nativeDb);
        Assert.Equal((8082, 2020, 2021, 2020.5), native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Where_then_Sum_with_selector_goes_native()
    {
        // The Where's $match lands in PostGroupOps and the Sum's accumulator stage follows it. Pairs excluding FR:
        // (US,2020),(US,2021),(UK,2020); sum of Year = 6061.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_then_Sum_with_selector_goes_native));

        Assert.Equal(
            6061,
            db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Where(r => r.Country != "FR").Sum(r => r.Year));
    }

    [Fact]
    public void Distinct_then_Where_then_Sum_with_selector_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Where_then_Sum_with_selector_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_then_Sum_with_selector_matches_driver_linq) + "D");

        int Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Where(r => r.Country != "FR").Sum(r => r.Year);

        var native = Run(nativeDb);
        Assert.Equal(6061, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Wrapped_projection_Distinct_then_Sum_with_computed_selector_goes_native()
    {
        // A computed selector resolves via TryTranslateValue within the alias scope. Sum of Year*2 = 16164.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Wrapped_projection_Distinct_then_Sum_with_computed_selector_goes_native));

        Assert.Equal(
            16164,
            db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Sum(r => r.Year * 2));
    }

    [Fact]
    public void Distinct_then_GroupBy_goes_native_and_dedups()
    {
        // GroupBy after a Distinct emits a second $group (+ $project) rather than overwriting the Distinct's, so dedup
        // applies first. The seed has duplicate (Country, Year) rows: expected US=2, UK=1, FR=1; dropping the Distinct
        // would give US=3, UK=2, FR=1.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_GroupBy_goes_native_and_dedups));

        var result = db.Entities
            .Select(o => new { o.Country, o.Year })
            .Distinct()
            .GroupBy(x => x.Country)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Count))
            .ToArray();

        Assert.Equal([("FR", 1), ("UK", 1), ("US", 2)], result); // distinct-then-group counts (NOT US=3, UK=2)
    }

    [Fact]
    public void Distinct_then_GroupBy_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_GroupBy_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_GroupBy_matches_driver_linq) + "D");

        (string Country, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .Select(o => new { o.Country, o.Year })
                .Distinct()
                .GroupBy(x => x.Country)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Count))
                .ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("FR", 1), ("UK", 1), ("US", 2)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Where_then_GroupBy_goes_native()
    {
        // The Where's $match lands in PostGroupOps between the two $groups; nothing can route there once IsGroupBy
        // flips true.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_then_GroupBy_goes_native));

        var result = db.Entities
            .Select(o => new { o.Country, o.Year })
            .Distinct()
            .Where(r => r.Country != "FR")
            .GroupBy(x => x.Country)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Count))
            .ToArray();

        Assert.Equal([("UK", 1), ("US", 2)], result);
    }

    [Fact]
    public void Distinct_then_Where_then_GroupBy_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Where_then_GroupBy_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_then_GroupBy_matches_driver_linq) + "D");

        (string Country, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .Select(o => new { o.Country, o.Year })
                .Distinct()
                .Where(r => r.Country != "FR")
                .GroupBy(x => x.Country)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Count))
                .ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("UK", 1), ("US", 2)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_GroupBy_on_renamed_member_groups_by_the_projected_source_not_the_colliding_entity_property()
    {
        // `new { Country = o.City }` reuses the entity's "Country" name; resolving by name would group by the real
        // Country field instead of City.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_GroupBy_on_renamed_member_groups_by_the_projected_source_not_the_colliding_entity_property) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_GroupBy_on_renamed_member_groups_by_the_projected_source_not_the_colliding_entity_property) + "D");

        (string City, int Count)[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities
                .Select(o => new { Country = o.City, o.Year })
                .Distinct()
                .GroupBy(x => x.Country)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable()
                .OrderBy(r => r.Key)
                .Select(r => (r.Key, r.Count))
                .ToArray();

        var native = Run(nativeDb);
        // Grouped by City (NYC/London/Paris), never by the real (always US/UK/FR) Country field.
        Assert.Equal([("London", 1), ("NYC", 2), ("Paris", 1)], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_GroupBy_with_computed_key_goes_native_under_native_only()
    {
        // A computed group key (concat over the Distinct's alias) routes through TryTranslateValue, resolving within
        // the DistinctAliasScope set from Select.PriorGrouping.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_GroupBy_with_computed_key_goes_native_under_native_only));

        var result = db.Entities
            .Select(o => new { o.Country, o.Year })
            .Distinct()
            .GroupBy(x => x.Country + x.Year)
            .Select(g => new { g.Key, Count = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToList();

        // Distinct (Country, Year) pairs are unique, so each concatenated key ("US2020" etc.) groups one row.
        Assert.Equal(
            [("FR2021", 1), ("UK2020", 1), ("US2020", 1), ("US2021", 1)],
            result.Select(r => (r.Key, r.Count)).ToArray());
    }

    [Fact]
    public void Select_after_Distinct_is_unsupported_and_never_returns_silent_null_data()
    {
        // A second Select after a projected Distinct must never go native and return null rows: it bypasses the
        // IsDistinct guards, and appending its field-ref to the Distinct's Projection would flatten fields gone after
        // the $group (guarded in TranslateSelect's non-grouped branch). In practice the shaper can't read a prior
        // anonymous projection's members, so it throws in every mode; the point is Native doesn't diverge.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Select_after_Distinct_is_unsupported_and_never_returns_silent_null_data) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Select_after_Distinct_is_unsupported_and_never_returns_silent_null_data) + "D");

        Exception? Run(SingleEntityDbContext<Order> db) => Record.Exception(() =>
            db.Entities.Select(o => new { o.Country, o.City }).Distinct().Select(x => new { Nation = x.Country }).ToList());

        Assert.NotNull(Run(nativeDb));   // Native throws — NOT a silent null-data success
        Assert.NotNull(Run(driverDb));   // DriverLinq throws the same way — no Native-vs-DriverLinq divergence
    }

    [Fact]
    public void Distinct_then_First_goes_native()
    {
        // First()/Single() after a projected Distinct: the reducer's $limit lands in PostGroupOps and Cardinality uses
        // the SetGroupedTerminalAggregate exception. OrderBy makes "first" deterministic.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_First_goes_native));

        var result = db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).First();

        Assert.Equal("FR", result.Country); // alphabetically first of the 3 distinct countries (FR, UK, US)
    }

    [Fact]
    public void Distinct_then_First_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_First_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_First_matches_driver_linq) + "D");

        string Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).First().Country;

        var native = Run(nativeDb);
        Assert.Equal("FR", native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Single_goes_native()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Single_goes_native));

        var result = db.Entities.Select(o => new { o.Country }).Distinct().Single(r => r.Country == "FR");

        Assert.Equal("FR", result.Country);
    }

    [Fact]
    public void Distinct_then_Where_then_First_goes_native()
    {
        // The Where's $match and the reducer's $limit both land in PostGroupOps, in arrival order.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_then_First_goes_native));

        var result = db.Entities.Select(o => new { o.Country }).Distinct()
            .Where(r => r.Country != "FR").OrderBy(r => r.Country).First();

        Assert.Equal("UK", result.Country);
    }

    [Fact]
    public void Distinct_then_Where_then_First_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Where_then_First_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Where_then_First_matches_driver_linq) + "D");

        string Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct()
                .Where(r => r.Country != "FR").OrderBy(r => r.Country).First().Country;

        var native = Run(nativeDb);
        Assert.Equal("UK", native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Take_goes_native_and_dedups()
    {
        // Take(n) after a Distinct; OrderBy stabilizes which 2 of the 3 countries come back.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Take_goes_native_and_dedups));

        var result = db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).Take(2).ToList();

        Assert.Equal(new[] { "FR", "UK" }, result.Select(r => r.Country).ToArray()); // alphabetically first 2 of (FR, UK, US)
    }

    [Fact]
    public void Distinct_then_Take_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_Take_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Take_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).Take(2)
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "FR", "UK" }, native); // alphabetically first 2 of (FR, UK, US)
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_OrderBy_goes_native_and_dedups()
    {
        // OrderBy after a Distinct resolves against the flattened alias (TryResolveDistinctOrderingKey).
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_OrderBy_goes_native_and_dedups));

        var result = db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country).ToList();

        Assert.Equal(new[] { "FR", "UK", "US" }, result.Select(r => r.Country).ToArray());
    }

    [Fact]
    public void Distinct_then_OrderBy_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_OrderBy_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_OrderBy_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { o.Country }).Distinct().OrderBy(r => r.Country)
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "FR", "UK", "US" }, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_OrderBy_with_computed_key_over_bare_scalar_goes_native()
    {
        // A computed OrderBy key (x.IndexOf(term)) over a bare-scalar Distinct, mirroring EF's
        // Distinct_followed_by_ordering_on_condition. IndexOf("U"): FR=-1, UK=0, US=0; ThenBy(x => x) gives FR, UK, US.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_OrderBy_with_computed_key_over_bare_scalar_goes_native));

        var result = db.Entities.Select(o => o.Country).Distinct()
            .OrderBy(x => x.IndexOf("U")).ThenBy(x => x).ToList();

        Assert.Equal(["FR", "UK", "US"], result);
    }

    [Fact]
    public void Distinct_then_OrderBy_with_computed_key_over_bare_scalar_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_OrderBy_with_computed_key_over_bare_scalar_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_OrderBy_with_computed_key_over_bare_scalar_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => o.Country).Distinct()
                .OrderBy(x => x.IndexOf("U")).ThenBy(x => x).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(["FR", "UK", "US"], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_OrderBy_on_renamed_member_sorts_by_the_projected_source_not_the_colliding_entity_property()
    {
        // `new { Country = o.City }` reuses the entity's "Country" name; resolving by name would silently sort by the
        // real Country field instead of City.
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Distinct_then_OrderBy_on_renamed_member_sorts_by_the_projected_source_not_the_colliding_entity_property) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Distinct_then_OrderBy_on_renamed_member_sorts_by_the_projected_source_not_the_colliding_entity_property) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.Select(o => new { Country = o.City }).Distinct().OrderBy(r => r.Country)
                .AsEnumerable().Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "London", "NYC", "Paris" }, native); // ordered by City, not by the real Country field
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void OrderBy_before_Distinct_goes_native_and_dedups()
    {
        // Ordering the source before the Distinct sorts pre-group documents and doesn't interfere with the $group.
        var seed = SeedOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(OrderBy_before_Distinct_goes_native_and_dedups));

        var result = db.Entities.OrderBy(o => o.Year).Select(o => new { o.Country }).Distinct()
            .AsEnumerable().OrderBy(r => r.Country).ToList();

        Assert.Equal(new[] { "FR", "UK", "US" }, result.Select(r => r.Country).ToArray());
    }

    [Fact]
    public void OrderBy_before_Distinct_matches_driver_linq()
    {
        var seed = SeedOrders();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(OrderBy_before_Distinct_matches_driver_linq) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(OrderBy_before_Distinct_matches_driver_linq) + "D");

        string[] Run(SingleEntityDbContext<Order> db) =>
            db.Entities.OrderBy(o => o.Year).Select(o => new { o.Country }).Distinct()
                .AsEnumerable().OrderBy(r => r.Country).Select(r => r.Country).ToArray();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "FR", "UK", "US" }, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Join_falls_back_gracefully_and_matches_driver_linq()
    {
        // A Join over a projected Distinct falls back gracefully (unlike GroupBy+Join, which hard-declines): under
        // Native it must not throw and must equal DriverLinq.
        using var nativeDb = CreateDistinctJoinContext(MongoQueryMode.Native,
            nameof(Distinct_then_Join_falls_back_gracefully_and_matches_driver_linq) + "N");
        using var driverDb = CreateDistinctJoinContext(MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Join_falls_back_gracefully_and_matches_driver_linq) + "D");

        // The result selector projects the inner entity r; referencing the outer `a` fails translation in all modes
        // (a separate projection-binding limitation). Mirrors NativeGroupByTests' GroupBy+Join shape.
        (string Country, string Continent)[] Run(DistinctJoinDbContext db) =>
            db.Orders
                .Select(o => new { o.Country })
                .Distinct()
                .Join(db.Regions, a => a.Country, r => r.Country, (a, r) => new { r.Country, r.Continent })
                .AsEnumerable()
                .OrderBy(x => x.Country)
                .Select(x => (x.Country, x.Continent))
                .ToArray();

        var native = Run(nativeDb);
        Assert.Equal([("FR", "EU"), ("UK", "EU"), ("US", "NA")], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Distinct_then_Join_throws_under_native_only()
    {
        // Under NativeOnly the graceful fallback becomes a NativeTranslationNotSupportedException.
        using var db = CreateDistinctJoinContext(MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Join_throws_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders
                .Select(o => new { o.Country })
                .Distinct()
                .Join(db.Regions, a => a.Country, r => r.Country, (a, r) => new { r.Country, r.Continent })
                .ToList());
    }

    private DistinctJoinDbContext CreateDistinctJoinContext(MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "O" + suffix;
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "R" + suffix;

        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(SeedOrders());
        database.MongoDatabase.GetCollection<Region>(regionsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), Country = "US", Continent = "NA" },
            new() { Id = ObjectId.GenerateNewId(), Country = "UK", Continent = "EU" },
            new() { Id = ObjectId.GenerateNewId(), Country = "FR", Continent = "EU" },
        ]);

        return new DistinctJoinDbContext(database, ordersName, regionsName, mode);
    }

    private class Region
    {
        public ObjectId Id { get; set; }
        public string Country { get; set; } = "";
        public string Continent { get; set; } = "";
    }

    private class DistinctJoinDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _regionsCollection;

        public DistinctJoinDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string regionsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<DistinctJoinDbContext>()
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
}
