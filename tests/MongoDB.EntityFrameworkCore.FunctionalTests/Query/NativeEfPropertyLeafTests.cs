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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A top-level <c>EF.Property&lt;T&gt;(param, "Name")</c> leaf resolves as a field in predicate, sort-key and
/// projection positions. Routing is proven via <see cref="MongoQueryMode.NativeOnly"/>, not MQL (both paths emit
/// identical pipelines here).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeEfPropertyLeafTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public string? Note { get; set; }
        public int? Score { get; set; }
    }

    // Composite PK: KeyA/KeyB are stored under "_id", not addressable by their own names (test 7).
    public class CompositeItem
    {
        public int KeyA { get; set; }
        public int KeyB { get; set; }
        public string Label { get; set; } = "";
    }

    public class ShadowItem
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
    }

    // ── 1. Predicate position ──────────────────────────────────────────────────────

    [Fact]
    public void Predicate_goes_native()
    {
        var collection = Seed(nameof(Predicate_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var titles = db.Entities.AsNoTracking()
            .Where(c => EF.Property<int>(c, "Rank") > 1)
            .OrderBy(c => c.Title)
            .Select(c => c.Title)
            .ToList();

        Assert.Equal(["b_two", "c_three"], titles);
    }

    // ── 2. Sort key position ───────────────────────────────────────────────────────

    [Fact]
    public void Sort_key_goes_native()
    {
        var collection = Seed(nameof(Sort_key_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var titles = db.Entities.AsNoTracking()
            .OrderBy(c => EF.Property<string>(c, "Title"))
            .Select(c => c.Title)
            .ToList();

        // Order proves the sort key was honored; a fallback would return the same rows.
        Assert.Equal(["a_one", "b_two", "c_three"], titles);
    }

    // ── 3. Projection leaf position ────────────────────────────────────────────────

    [Fact]
    public void Projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Projection_leaf_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var results = db.Entities.AsNoTracking()
            .OrderBy(c => c.Title)
            .Select(c => new {c.Title, R = EF.Property<int>(c, "Rank")})
            .ToList();

        Assert.Equal(["a_one", "b_two", "c_three"], results.Select(r => r.Title));
        Assert.Equal([1, 2, 3], results.Select(r => r.R));
    }

    // ── 3b. Bare projection leaf position ──────────────────────────────────────────
    //
    // The bare branch derives its $project alias from the leaf's document path, not a member name. An alias miss is
    // silent (nullable leaves read back null), so this needs its own parameterized-Where leg.

    [Fact]
    public void Bare_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Bare_projection_leaf_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var titles = db.Entities.AsNoTracking()
            .OrderBy(c => c.Title)
            .Select(c => EF.Property<string>(c, "Title"))
            .ToList();

        Assert.Equal(["a_one", "b_two", "c_three"], titles);
    }

    [Fact]
    public void Bare_projection_leaf_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection = Seed(nameof(Bare_projection_leaf_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "a"; // captured local — the mandatory late-decline leg, mirroring test 4 below.

        // Native mode: the parameterized StartsWith forces a late decline to driver-LINQ, proving the bare leaf's
        // alias survives the fallback too.
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var titles = db.Entities.AsNoTracking()
            .Where(c => c.Title.StartsWith(prefix))
            .OrderBy(c => c.Title)
            .Select(c => EF.Property<string>(c, "Title"))
            .ToList();

        Assert.Equal(["a_one"], titles);
    }

    // ── 4. Parameterized-Where leg ─────────────────────────────────────────────────

    [Fact]
    public void Parameterized_where_leg()
    {
        var collection = Seed(nameof(Parameterized_where_leg));

        // "b", not "a": "a_one" leaves both nullable columns unset, so an alias miss (null) would pass unnoticed.
        // "b_two" has Note = "n2" and Score = 20 (cf. NativeNullableMemberTests.Parameterized_where_leg).
        var prefix = "b"; // captured local — the mandatory late-decline leg for this test family.

        // Native mode: a parameterized StartsWith has no native regex rendering, so the native factory declines late
        // and the query runs on driver-LINQ. NativeOnly would throw at the gate instead.
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var results = db.Entities.AsNoTracking()
            .Where(c => c.Title.StartsWith(prefix))
            .OrderBy(c => c.Title)
            .Select(c => new
            {
                Note = EF.Property<string?>(c, "Note"),
                Score = EF.Property<int?>(c, "Score"),
                Rank = EF.Property<int>(c, "Rank")
            })
            .ToList();

        Assert.Single(results);
        // Nullable leaves first: an alias miss there reads back null rather than throwing.
        Assert.Equal("n2", results[0].Note);
        Assert.Equal(20, results[0].Score);
        Assert.Equal(2, results[0].Rank);
    }

    // ── 5. Parity with driver-LINQ ─────────────────────────────────────────────────

    [Fact]
    public void Parity_with_driver_linq()
    {
        var collection = Seed(nameof(Parity_with_driver_linq));

        static object Run(SingleEntityDbContext<Item> db)
            => db.Entities.AsNoTracking()
                .Where(c => EF.Property<int>(c, "Rank") > 1)
                .OrderBy(c => EF.Property<string>(c, "Title"))
                .Select(c => new {c.Title, R = EF.Property<int>(c, "Rank")})
                .ToList()
                .Select(r => (r.Title, r.R))
                .ToList();

        using var native = CreateContext(collection, MongoQueryMode.Native);
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);

        Assert.Equal(Run(driverLinq), Run(native));
    }

    // ── 6. Shadow property ─────────────────────────────────────────────────────────

    [Fact]
    public void Shadow_property_goes_native()
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(nameof(Shadow_property_goes_native)));
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "s_one"}, {"Shadow", 10}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "s_two"}, {"Shadow", 20}}
        ]);
        var collection = database.MongoDatabase.GetCollection<ShadowItem>(raw.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<ShadowItem>().Property<int>("Shadow"),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var results = db.Entities.AsNoTracking()
            .OrderBy(c => c.Title)
            .Where(c => EF.Property<int>(c, "Shadow") > 5)
            .Select(c => new {c.Title, Shadow = EF.Property<int>(c, "Shadow")})
            .ToList();

        Assert.Equal(["s_one", "s_two"], results.Select(r => r.Title));
        Assert.Equal([10, 20], results.Select(r => r.Shadow));
    }

    // ── 7. Composite-key component: the tripwire ───────────────────────────────────

    [Fact]
    public void Composite_key_component_resolves_natively()
    {
        var collection = SeedComposite(nameof(Composite_key_component_resolves_natively));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly})
        {
            using var db = CreateCompositeContext(collection, mode);

            var labels = db.Entities.AsNoTracking()
                .Where(c => EF.Property<int>(c, "KeyA") == 1)
                .Select(c => c.Label)
                .ToList();

            Assert.Equal(["one"], labels);
        }
    }

    // ── Seeds and helpers ───────────────────────────────────────────────────────────

    private IMongoCollection<Item> Seed(string name)
    {
        var collection = database.CreateCollection<Item>(UniqueCollectionName(name));

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.AddRange(
                new Item {Title = "a_one", Rank = 1},
                new Item {Title = "b_two", Rank = 2, Note = "n2", Score = 20},
                new Item {Title = "c_three", Rank = 3, Note = "n3", Score = 30});
            db.SaveChanges();
        }

        return collection;
    }

    private IMongoCollection<CompositeItem> SeedComposite(string name)
    {
        var collection = database.CreateCollection<CompositeItem>(UniqueCollectionName(name));

        using (var db = SingleEntityDbContext.Create(collection, ConfigureComposite))
        {
            db.Entities.AddRange(
                new CompositeItem {KeyA = 1, KeyB = 1, Label = "one"},
                new CompositeItem {KeyA = 2, KeyB = 1, Label = "two"});
            db.SaveChanges();
        }

        return collection;
    }

    private static void ConfigureComposite(ModelBuilder mb)
        => mb.Entity<CompositeItem>().HasKey(x => new {x.KeyA, x.KeyB});

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<CompositeItem> CreateCompositeContext(
        IMongoCollection<CompositeItem> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: ConfigureComposite,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
}
