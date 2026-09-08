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
/// EF-347 (Task 2) native <c>Select(new {...}).Distinct()</c> → a degenerate <c>$group</c> (group by the
/// projected value(s), zero accumulators) followed by the flattening <c>$project</c>. Proves that a
/// supported projected-Distinct shape executes as a native aggregation pipeline and dedups correctly, and
/// that unsupported shapes (whole-entity source, a value-converted/represented projection key, an operator
/// applied after Distinct) fall back to driver-LINQ under <see cref="MongoQueryMode.Native"/> yet throw
/// <see cref="NativeTranslationNotSupportedException"/> under <see cref="MongoQueryMode.NativeOnly"/> — the
/// "went native" signal (the emitted MQL is otherwise indistinguishable from the driver-LINQ fallback for
/// filter/sort/paging shapes). EF-395 additionally admits a BARE-scalar projection (<c>Select(o =>
/// o.Country).Distinct()</c>) into the same native path — see
/// <see cref="Bare_scalar_projection_Distinct_goes_native"/>.
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
    }

    // City is constant per country, so distinct {Country, City} collapses to distinct countries.
    private static Order[] SeedOrders() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Country = "US", City = "NYC", Year = 2020, Amount = 100, Status = OrderStatus.New },
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
        // EF-395: TryBindDistinctFromProjection no longer declines on select.IsBareProjection, so this now
        // goes native (NativeOnly succeeding is the "went native" signal). US/US/US, UK/UK, FR -> 3 distinct
        // countries.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Bare_scalar_projection_Distinct_goes_native));

        var result = db.Entities.Select(o => o.Country).Distinct().ToList().OrderBy(c => c).ToList();
        Assert.Equal(["FR", "UK", "US"], result);
    }

    [Fact]
    public void Whole_entity_Distinct_falls_back_under_native_only()
    {
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Whole_entity_Distinct_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Distinct().ToList());
    }

    [Fact]
    public void Distinct_then_Where_goes_native()
    {
        // EF-322: Where composed AFTER a projected Distinct now resolves its predicate against the Distinct's
        // OWN flattened output alias (MongoExpressionTranslator.DistinctAliasScope), not the root entity —
        // succeeding under NativeOnly is the "went native" signal.
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
        // Select(o => new { Country = o.City }) deliberately reuses the entity's real "Country" property name
        // for a DIFFERENT source field (City). If Where(x => x.Country == "NYC") resolved by name against the
        // root entity (as the ordinary MongoExpressionTranslator does), it would silently filter on the
        // entity's real Country field ("US"/"UK"/"FR") instead of the projected City value — wrong data.
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
        // A computed predicate member (not one of the Distinct's own key part aliases) must still decline
        // rather than silently resolving against the entity — the whole point of DistinctAliasScope is that a
        // member name outside the key parts is out of scope, not a fallthrough.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Where_on_unrelated_computed_key_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.Select(o => new { o.Country }).Distinct().Where(r => r.Country.Length == 2).ToList());
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
        // A projected key with a non-default BsonRepresentation (enum stored as string) must NOT go native:
        // the flattening $project would read the group _id back through a generic CLR-type serializer,
        // which cannot reproduce the string-stored enum — diverging from DriverLinq. Mirrors
        // NativeGroupByTests.GroupBy_bson_represented_key_falls_back (shared HasDefaultKeySerialization guard).
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

        // Native: falls back to driver-LINQ and returns correct results (parity with DriverLinq).
        using (var nativeDb = Make(collection, MongoQueryMode.Native, configure))
        {
            var result = nativeDb.Entities.Select(o => new { o.Status }).Distinct()
                .AsEnumerable().OrderBy(r => r.Status).ToList();

            Assert.Equal(
                new[] { OrderStatus.New, OrderStatus.Shipped },
                result.Select(r => r.Status).ToArray());
        }

        // NativeOnly: the represented key forbids native execution and fallback is disallowed → throws.
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly, configure);
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Entities.Select(o => new { o.Status }).Distinct().ToList());
    }

    [Fact]
    public void Distinct_then_Count_goes_native()
    {
        // EF-322: a bare Count() applied AFTER a projected Distinct now goes native too — the terminal $count
        // stage is emitted right after the $group + flattening $project (and after any PostGroupOps already
        // recorded) instead of being dropped. Succeeding under NativeOnly is the "went native" signal (3
        // distinct countries: US, UK, FR).
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
        // EF-322: Count(pred) composed directly after a projected Distinct resolves its predicate against the
        // Distinct's own flattened output alias (the SAME MongoExpressionTranslator.DistinctAliasScope
        // mechanism Where uses) — succeeding under NativeOnly is the "went native" signal.
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
        // Select(o => new { Country = o.City }) deliberately reuses the entity's real "Country" property name
        // for a DIFFERENT source field (City). If Count(x => x.Country != "NYC") resolved by name against the
        // root entity, it would silently count against the entity's real Country field instead of City.
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
        // A computed predicate member (not one of the Distinct's own key part aliases) must still decline
        // rather than silently resolving against the entity.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Count_with_unrelated_computed_predicate_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.Select(o => new { o.Country }).Distinct().Count(r => r.Country.Length == 2));
    }

    [Fact]
    public void Distinct_then_Where_then_Count_goes_native()
    {
        // A Where composed before a bare Count() both land natively after the $group: the Where's $match into
        // PostGroupOps (EF-322, previous commit), the Count's terminal $count stage after it (this commit's
        // lowerer fix — PostGroupOps must still be emitted even when a trailing aggregate also sets
        // Cardinality, or the Where's $match would be silently dropped).
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
        // EF-453: Sum/Min/Max/Average terminating directly on a bare-scalar-projected Distinct() now go
        // native (NativeOnly succeeding is the "went native" signal). Distinct years: {2020, 2021}.
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
        // EF-322: Sum(selector) over a WRAPPED (named-member) projected Distinct now goes native too — the
        // selector resolves against the Distinct's own flattened output alias (the SAME
        // MongoExpressionTranslator.DistinctAliasScope mechanism Where/Count(pred) use), reusing the ordinary
        // Sum/Min/Max/Average operand-resolution machinery in NativeCardinalityBinder.TryBindAggregate.
        // Distinct {Country, Year} pairs: (US,2020),(US,2021),(UK,2020),(FR,2021) — sum of Year = 8082.
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
        // Proves composition with an already-native post-Distinct Where (previous commit): the Where's $match
        // lands in PostGroupOps, and the Sum's terminal accumulator stage still follows it correctly (the SAME
        // MongoSelectLowerer fix that made Where-then-Count work). Distinct {Country, Year} pairs excluding
        // FR: (US,2020),(US,2021),(UK,2020) — sum of Year = 6061.
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
    public void Wrapped_projection_Distinct_then_Sum_with_computed_selector_still_falls_back_under_native_only()
    {
        // A computed selector (not a bare member access naming one of the Distinct's own key parts) must still
        // decline — the pre-existing "computed selectors fall back" guard in NativeCardinalityBinder
        // .TryBindAggregate, unaffected by the EF-322 alias-scope carve-out.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Wrapped_projection_Distinct_then_Sum_with_computed_selector_still_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.Select(o => new { o.Country, o.Year }).Distinct().Sum(r => r.Year * 2));
    }

    [Fact]
    public void Distinct_then_GroupBy_goes_native_and_dedups()
    {
        // EF-322: GroupBy(key).Select(aggregate) composed AFTER a projected Distinct now goes native too — a
        // SECOND $group (+ flattening $project) is emitted after the Distinct's own $group/$project rather
        // than overwriting it, so the Distinct's dedup still applies before the GroupBy's own aggregation.
        // The seed has DUPLICATE (Country, Year) rows so Distinct is NOT a no-op: distinct {Country,Year} =
        // {US2020, US2021, UK2020, FR2021}, so grouping by Country and counting yields US=2, UK=1, FR=1. If
        // the Distinct were dropped/overwritten (the historical wrong-data hazard this feature guards
        // against), the raw 6 rows would give US=3, UK=2, FR=1 instead — this test is load-bearing proof that
        // does NOT happen. Succeeding under NativeOnly is the "went native" signal.
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
        // Proves composition with an already-native post-Distinct Where: the Where's $match lands in
        // PostGroupOps (previous commit) BETWEEN the Distinct's $group and the GroupBy's own $group — not
        // after it, since nothing can route into PostGroupOps once IsGroupBy flips true.
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
        // Select(o => new { Country = o.City }) deliberately reuses the entity's real "Country" property name
        // for a DIFFERENT source field (City). If GroupBy(x => x.Country) resolved by name against the root
        // entity, it would silently group by the entity's real Country field instead of City.
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
    public void Distinct_then_GroupBy_with_computed_key_still_falls_back_under_native_only()
    {
        // A computed group-by key member (not one of the Distinct's own key part aliases) must still decline
        // rather than silently resolving against the entity.
        using var db = CreateContext(SeedOrders(), MongoQueryMode.NativeOnly,
            nameof(Distinct_then_GroupBy_with_computed_key_still_falls_back_under_native_only));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities
                .Select(o => new { o.Country, o.Year })
                .Distinct()
                .GroupBy(x => x.Country + x.Year)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList());
    }

    [Fact]
    public void Select_after_Distinct_is_unsupported_and_never_returns_silent_null_data()
    {
        // A second projected Select applied AFTER a native projected Distinct must NEVER silently go native and
        // return null-valued rows. Structural hazard (guarded in TranslateSelect's non-grouped projection
        // branch): this Select reaches that branch (the shaper is no longer a GroupByShaperExpression), so it
        // bypasses the IsDistinct slot/cardinality guards; without the guard TryPopulateNativeProjection would
        // APPEND this Select's field-ref onto the Distinct's Projection while Grouping is still set, and the
        // lowerer would emit a flatten $project over fields gone after the $group → nulls.
        //
        // In practice this provider cannot build a shaper that reads a prior anonymous projection's members
        // (MongoProjectionBindingExpressionVisitor throws on the nested ProjectionBindingExpression BEFORE the
        // gate), so the shape is UNSUPPORTED and throws during translation in EVERY mode — Native, DriverLinq,
        // and NativeOnly alike. The property this locks in: Native does NOT diverge from DriverLinq by silently
        // returning null rows — both fail identically (no wrong/null data).
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
        // EF-322: First()/Single() (an entity-REDUCER, not a scalar aggregate) composed directly after a
        // projected Distinct now goes native too. It has no field reference of its own to get wrong — the
        // reducer's synthesized $limit just needs to land in the right place (PostGroupOps, via the SAME
        // ActiveOps routing Where/OrderBy/Skip/Take already use) and Cardinality needs the SAME
        // SetGroupedTerminalAggregate sanctioned exception Count/Sum use to coexist with Grouping.
        // A bare (unordered) Distinct().First() is not deterministic in general (no guaranteed row order
        // without a $sort), so an explicit OrderBy is chained between Distinct and First to stabilize the
        // "first" row for a meaningful equality assertion.
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
        // Proves composition with an already-native post-Distinct Where: the Where's $match and the
        // reducer's own $limit both land in PostGroupOps, in arrival order.
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
        // EF-322: Take(n) composed directly after a projected Distinct now goes native too (it has no field
        // reference to get wrong, unlike Where/OrderBy) — succeeding under NativeOnly is the "went native"
        // signal. An OrderBy between Distinct and Take stabilizes which 2 of the 3 distinct countries come
        // back (otherwise Take(n) over an unordered Distinct has no guaranteed subset).
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
        // EF-322: OrderBy composed AFTER a projected Distinct now resolves its key selector against the
        // Distinct's OWN flattened output alias (NativeGroupByBinder.TryResolveDistinctOrderingKey), not the
        // root entity — succeeding under NativeOnly is the "went native" signal.
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
    public void Distinct_then_OrderBy_on_renamed_member_sorts_by_the_projected_source_not_the_colliding_entity_property()
    {
        // Select(o => new { Country = o.City }) deliberately reuses the entity's real "Country" property name
        // for a DIFFERENT source field (City). If OrderBy(x => x.Country) resolved by name against the root
        // entity (as the ordinary MongoExpressionTranslator does), it would silently sort by the entity's real
        // Country field instead of the projected City value — wrong data. It must sort by City's value instead.
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
        // Ordering the SOURCE before the projection/Distinct (as opposed to Operator_after_Distinct_*, which
        // orders/filters/reduces the Distinct's OUTPUT) is a different composition seam: the $sort applies to
        // the pre-group documents, so EMPIRICALLY it does not interfere with the degenerate-$group Distinct
        // translation — succeeding under NativeOnly is the "went native" signal.
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
        // A Join over a projected-Distinct source falls back GRACEFULLY (unlike a genuine GroupBy+Join, which
        // hard-declines): Distinct produces a flat set of rows the driver-LINQ path joins correctly, so under
        // Native it must NOT throw and must equal DriverLinq. Before the IsDistinct/IsGroupBy split this
        // reused IsGroupBy and therefore HARD-THREW under Native (MarkGroupByFallbackUnsafe) — a
        // correct-results→throw regression. This test is the load-bearing proof of the graceful path: it
        // asserts no throw under Native AND parity with DriverLinq.
        using var nativeDb = CreateDistinctJoinContext(MongoQueryMode.Native,
            nameof(Distinct_then_Join_falls_back_gracefully_and_matches_driver_linq) + "N");
        using var driverDb = CreateDistinctJoinContext(MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Join_falls_back_gracefully_and_matches_driver_linq) + "D");

        // The result selector projects fields of the INNER entity r (a proper entity shaper), not the outer
        // distinct-projection-bound `a` — referencing `a.Country` in the output is a separate, pre-existing
        // projection-binding limitation (unrelated to this fix) that fails QMTEV translation in ALL modes.
        // Mirrors NativeGroupByTests' GroupBy+Join shape (which projects the inner entity), isolating exactly
        // the IsDistinct-vs-IsGroupBy fallback-mode difference this fix is about.
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
        // The graceful fallback becomes a clean decline under NativeOnly (fallback disallowed) — NOT a hard
        // GroupBy-style decline, but the same NativeTranslationNotSupportedException surface.
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
