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
/// Native <c>Select(...).Distinct()</c>: a degenerate <c>$group</c> (group by the projected value(s), no
/// accumulators) plus a flattening <c>$project</c>, including bare-scalar projections, whole-entity Distinct,
/// and operators composed after it. Unsupported shapes (e.g. a value-converted key) fall back under
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
        // US/US/US, UK/UK, FR -> 3 distinct countries.
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

    [Fact]
    public void Anonymous_computed_projection_Distinct_then_OrderBy_on_member_goes_native()
    {
        // A computed projection member (A = o.Country + o.City) has no IProperty; the ordering key resolves against
        // the Distinct's flattened alias via a MongoElementRefExpression (as TryResolveDistinctAliasComputedField
        // does for Where).
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

        // Native: falls back to driver-LINQ and returns correct results.
        using (var nativeDb = Make(collection, MongoQueryMode.Native, configure))
        {
            var result = nativeDb.Entities.Select(o => new { o.Status }).Distinct()
                .AsEnumerable().OrderBy(r => r.Status).ToList();

            Assert.Equal(
                new[] { OrderStatus.New, OrderStatus.Shipped },
                result.Select(r => r.Status).ToArray());
        }

        // NativeOnly: fallback is disallowed, so it throws.
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
