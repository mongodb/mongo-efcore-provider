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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// The native one-pass materializer constructs each row while the driver deserializes its whole cursor batch, so it must leave
/// tracking, and any failure, to the moment the enumerator reaches the row: rows the caller never reads are neither tracked nor able
/// to fail the rows before them.
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef479LazyBatchMaterializationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---- Malformed rows ----------------------------------------------------------------------------

    [Fact]
    public void Unconsumed_rows_of_the_cursor_batch_are_not_tracked()
    {
        using var db = SeedItems();

        var result = db.Entities.OrderBy(e => e.Value).AsEnumerable().Take(1).ToList();

        Assert.Equal([1], result.Select(e => e.Value));
        Assert.Single(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Malformed_row_after_the_consumed_rows_does_not_throw()
    {
        using var db = SeedItems(malformedLast: true);

        var result = db.Entities.OrderBy(e => e.Value).AsEnumerable().Take(1).ToList();

        Assert.Equal([1], result.Select(e => e.Value));
    }

    [Fact]
    public void Malformed_row_throws_when_it_is_reached_and_not_before()
    {
        using var db = SeedItems(malformedLast: true);

        using var enumerator = db.Entities.OrderBy(e => e.Value).AsEnumerable().GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current.Value);
        Assert.True(enumerator.MoveNext());
        Assert.Equal(2, enumerator.Current.Value);

        var exception = Assert.ThrowsAny<Exception>(() => enumerator.MoveNext());
        Assert.Contains("not an int", exception.Message);

        // The two rows before the failure were tracked as they were read; nothing else was.
        Assert.Equal(2, db.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void Malformed_row_in_the_middle_of_a_batch_does_not_stop_the_rows_before_it()
    {
        using var db = SeedItems(malformedMiddle: true);

        using var enumerator = db.Entities.OrderBy(e => e.Id).AsEnumerable().GetEnumerator();

        Assert.True(enumerator.MoveNext());
        Assert.Equal(1, enumerator.Current.Value);

        // The failure is the row's own (raised from its stored value), not a corrupted-reader error from the rows after it.
        var exception = Assert.ThrowsAny<Exception>(() => enumerator.MoveNext());
        Assert.Contains("not an int", exception.Message);
    }

    [Fact]
    public void Malformed_row_does_not_fail_an_untracked_query_before_it_is_reached()
    {
        using var db = SeedItems(malformedLast: true);

        var result = db.Entities.AsNoTracking().OrderBy(e => e.Value).AsEnumerable().Take(2).ToList();

        Assert.Equal([1, 2], result.Select(e => e.Value));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    // ---- Tracking ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Take_one_of_a_tracked_include_tracks_only_that_row_and_its_related_entity(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var result = db.Reviews.Include(r => r.Product).OrderBy(r => r.Id).AsEnumerable().Take(1).ToList();

        Assert.Equal("R1", Assert.Single(result).Text);
        Assert.Equal("P1", result[0].Product!.Name);
        Assert.Equal(["Product:1:Unchanged", "Review:1:Unchanged"], Tracked(db));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Tracked_include_resolves_a_shared_related_entity_to_one_instance(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var reviews = db.Reviews.Include(r => r.Product).OrderBy(r => r.Id).ToList();

        Assert.Equal(
            ["Product:1:Unchanged", "Product:2:Unchanged", "Review:1:Unchanged", "Review:2:Unchanged", "Review:3:Unchanged", "Review:4:Unchanged"],
            Tracked(db));
        Assert.Same(reviews[0].Product, reviews[1].Product);
        Assert.Same(reviews[0].Product, reviews[3].Product);
        Assert.NotSame(reviews[0].Product, reviews[2].Product);
        Assert.Equal(["P1", "P1", "P2", "P1"], reviews.Select(r => r.Product!.Name));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Tracked_query_returns_an_entity_that_is_already_tracked(MongoQueryMode mode)
    {
        using var db = Seed(mode);
        var product = db.Products.Single(p => p.Id == 1);

        var reviews = db.Reviews.Include(r => r.Product).OrderBy(r => r.Id).ToList();

        Assert.Same(product, reviews[0].Product);
        Assert.Same(product, reviews[1].Product);
        Assert.Equal(6, db.ChangeTracker.Entries().Count());

        var again = db.Products.OrderBy(p => p.Id).ToList();
        Assert.Same(product, again[0]);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Tracked_include_keeps_the_shadow_foreign_key_so_saving_does_not_clear_it(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var reviews = db.Reviews.Include(r => r.Product).OrderBy(r => r.Id).ToList();

        Assert.Equal(
            [1, 1, 2, 1],
            reviews.Select(r => db.Entry(r).Property<int?>("ProductId").CurrentValue!.Value));

        reviews[2].Text = "changed";
        Assert.Equal(1, db.SaveChanges());

        var stored = db.GetRawDocument("Reviews", 3);
        Assert.Equal("changed", stored["Text"].AsString);
        Assert.Equal(2, stored["ProductId"].AsInt32);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Tracked_query_marks_an_include_that_found_nothing_as_loaded(MongoQueryMode mode)
    {
        using var db = Seed(mode);
        db.RawCollection("Reviews").InsertOne(new BsonDocument { { "_id", 5 }, { "Text", "R5" } });

        var review = db.Reviews.Include(r => r.Product).Single(r => r.Id == 5);

        Assert.Null(review.Product);
        Assert.True(db.Entry(review).Reference(r => r.Product).IsLoaded);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void No_tracking_include_tracks_nothing_but_sets_the_navigation(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var reviews = db.Reviews.AsNoTracking().Include(r => r.Product).OrderBy(r => r.Id).ToList();

        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(["P1", "P1", "P2", "P1"], reviews.Select(r => r.Product!.Name));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void No_tracking_with_identity_resolution_shares_the_related_entity_without_tracking_it(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var reviews = db.Reviews.AsNoTrackingWithIdentityResolution().Include(r => r.Product).OrderBy(r => r.Id).ToList();

        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Same(reviews[0].Product, reviews[1].Product);
        Assert.Same(reviews[0].Product, reviews[3].Product);
        Assert.NotSame(reviews[0].Product, reviews[2].Product);
    }

    // ---- Owned collections -------------------------------------------------------------------------

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Take_one_of_an_owned_collection_tracks_only_that_row_and_its_owned_entities(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var parcel = Assert.Single(db.Parcels.OrderBy(p => p.Id).AsEnumerable().Take(1).ToList());

        Assert.Equal(["a", "b"], parcel.Items.Select(i => i.Label));
        Assert.Equal(3, db.ChangeTracker.Entries().Count());
        Assert.Equal(2, db.ChangeTracker.Entries().Count(e => e.Entity is ParcelItem));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Tracked_owned_collections_are_complete_when_every_row_is_read(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var parcels = db.Parcels.OrderBy(p => p.Id).ToList();

        Assert.Equal([2, 1, 3], parcels.Select(p => p.Items.Count));
        Assert.Equal(3 + 6, db.ChangeTracker.Entries().Count());
        Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));
    }

    // ---- Plain single-entity rows (no shadow values, no graph): keys and types come from the instance ------

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Plain_tracked_rows_track_lazily_and_resolve_to_already_tracked_entities(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var first = Assert.Single(db.Products.OrderBy(p => p.Id).AsEnumerable().Take(1).ToList());
        Assert.Equal(["Product:1:Unchanged"], Tracked(db));

        var all = db.Products.OrderBy(p => p.Id).ToList();
        Assert.Same(first, all[0]);
        Assert.Equal(["Product:1:Unchanged", "Product:2:Unchanged"], Tracked(db));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Plain_tracked_rows_of_a_hierarchy_keep_their_concrete_types(MongoQueryMode mode)
    {
        using var db = Seed(mode);

        var first = Assert.Single(db.Animals.OrderBy(a => a.Id).AsEnumerable().Take(1).ToList());
        Assert.IsType<Dog>(first);
        Assert.Equal(["Dog:1:Unchanged"], Tracked(db));

        var all = db.Animals.OrderBy(a => a.Id).ToList();
        Assert.Same(first, all[0]);
        Assert.Equal([typeof(Dog), typeof(Cat), typeof(Dog)], all.Select(a => a.GetType()));
        Assert.Equal(["Cat:2:Unchanged", "Dog:1:Unchanged", "Dog:3:Unchanged"], Tracked(db));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Plain_tracked_rows_with_a_composite_key_resolve_to_already_tracked_entities(MongoQueryMode mode)
    {
        using var db = Seed(mode);
        var tracked = db.Pairs.Find(1, 2)!;

        var all = db.Pairs.OrderBy(p => p.A).ThenBy(p => p.B).ToList();

        Assert.Same(tracked, all[1]);
        Assert.Equal(3, db.ChangeTracker.Entries().Count());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Plain_tracked_rows_with_a_converted_key_resolve_to_already_tracked_entities(MongoQueryMode mode)
    {
        using var db = Seed(mode);
        var tracked = db.Shades.Find(Color.Green)!;

        var all = db.Shades.OrderBy(s => s.Name).ToList();

        Assert.Same(tracked, all.Single(s => s.Id == Color.Green));
        Assert.Equal(2, db.ChangeTracker.Entries().Count());
    }

    // ---- Fixtures ----------------------------------------------------------------------------------

    private SingleEntityDbContext<Item> SeedItems(
        bool malformedLast = false, bool malformedMiddle = false, [CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Item>(name);
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument { { "_id", 1 }, { "Value", 1 } },
            new BsonDocument { { "_id", 2 }, { "Value", malformedMiddle ? new BsonString("not an int") : 2 } },
            new BsonDocument { { "_id", 3 }, { "Value", malformedLast ? new BsonString("not an int") : 3 } }
        ]);
        return SingleEntityDbContext.Create(collection);
    }

    private Ef479Context Seed(MongoQueryMode mode, [CallerMemberName] string? name = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seed = Context(MongoQueryMode.Native, suffix);

        seed.Products.AddRange(new Product { Id = 1, Name = "P1" }, new Product { Id = 2, Name = "P2" });
        seed.SaveChanges();
        seed.Reviews.AddRange(
            new Review { Id = 1, Text = "R1", Product = seed.Products.Local.Single(p => p.Id == 1) },
            new Review { Id = 2, Text = "R2", Product = seed.Products.Local.Single(p => p.Id == 1) },
            new Review { Id = 3, Text = "R3", Product = seed.Products.Local.Single(p => p.Id == 2) },
            new Review { Id = 4, Text = "R4", Product = seed.Products.Local.Single(p => p.Id == 1) });
        seed.Parcels.AddRange(
            new Parcel { Id = 1, Items = [new ParcelItem { Label = "a" }, new ParcelItem { Label = "b" }] },
            new Parcel { Id = 2, Items = [new ParcelItem { Label = "c" }] },
            new Parcel { Id = 3, Items = [new ParcelItem { Label = "d" }, new ParcelItem { Label = "e" }, new ParcelItem { Label = "f" }] });
        seed.Animals.AddRange(
            new Dog { Id = 1, Name = "Rex" }, new Cat { Id = 2, Name = "Tom" }, new Dog { Id = 3, Name = "Fido" });
        seed.Pairs.AddRange(
            new Pair { A = 1, B = 1, Value = "x" }, new Pair { A = 1, B = 2, Value = "y" }, new Pair { A = 2, B = 1, Value = "z" });
        seed.Shades.AddRange(new Shade { Id = Color.Red, Name = "red" }, new Shade { Id = Color.Green, Name = "green" });
        seed.SaveChanges();
        seed.Dispose();

        return Context(mode, suffix);
    }

    private Ef479Context Context(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<Ef479Context>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    private static string[] Tracked(DbContext db)
        => db.ChangeTracker.Entries()
            .Select(e => $"{e.Metadata.DisplayName()}:"
                         + $"{string.Join(',', e.Metadata.FindPrimaryKey()!.Properties.Select(p => e.Property(p.Name).CurrentValue))}:{e.State}")
            .Order()
            .ToArray();

    private sealed class Ef479Context(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
        public DbSet<Review> Reviews => Set<Review>();
        public DbSet<Parcel> Parcels => Set<Parcel>();
        public DbSet<Animal> Animals => Set<Animal>();
        public DbSet<Pair> Pairs => Set<Pair>();
        public DbSet<Shade> Shades => Set<Shade>();

        public IMongoCollection<BsonDocument> RawCollection(string name)
            => Database.GetService<IMongoClientWrapper>().GetCollection<BsonDocument>(name + suffix);

        public BsonDocument GetRawDocument(string name, int id)
            => RawCollection(name).Find(new BsonDocument("_id", id)).Single();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().ToCollection("Products" + suffix);
            modelBuilder.Entity<Review>(b =>
            {
                b.ToCollection("Reviews" + suffix);
                b.HasOne(r => r.Product).WithMany().HasForeignKey("ProductId");
            });
            modelBuilder.Entity<Parcel>(b =>
            {
                b.ToCollection("Parcels" + suffix);
                b.OwnsMany(p => p.Items);
            });
            modelBuilder.Entity<Animal>().ToCollection("Animals" + suffix);
            modelBuilder.Entity<Dog>();
            modelBuilder.Entity<Cat>();
            modelBuilder.Entity<Pair>(b =>
            {
                b.ToCollection("Pairs" + suffix);
                b.HasKey(p => new { p.A, p.B });
            });
            modelBuilder.Entity<Shade>(b =>
            {
                b.ToCollection("Shades" + suffix);
                b.Property(s => s.Id).HasConversion<string>();
            });
        }
    }

    private class Item
    {
        public int Id { get; set; }
        public int Value { get; set; }
    }

    private class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Review
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public Product? Product { get; set; }
    }

    private class Parcel
    {
        public int Id { get; set; }
        public List<ParcelItem> Items { get; set; } = [];
    }

    private class ParcelItem
    {
        public string Label { get; set; } = "";
    }

    private class Animal
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Dog : Animal;

    private class Cat : Animal;

    private class Pair
    {
        public int A { get; set; }
        public int B { get; set; }
        public string Value { get; set; } = "";
    }

    private enum Color
    {
        Red,
        Green
    }

    private class Shade
    {
        public Color Id { get; set; }
        public string Name { get; set; } = "";
    }
}
