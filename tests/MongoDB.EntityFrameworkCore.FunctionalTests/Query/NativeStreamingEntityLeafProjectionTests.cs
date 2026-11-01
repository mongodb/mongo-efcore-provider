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

using System.Collections;
using System.Runtime.CompilerServices;
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
/// A whole entity inside an untracked projection (<c>new { o.Code, o.Shipping }</c>: an owned reference, or a joined entity) is read by the
/// cursor straight into the entity instead of through a <see cref="BsonDocument"/> per row. The entity, an absent or null sub-document and a
/// missing required one must read exactly as the DOM shaper read them.
/// </summary>
public class NativeStreamingEntityLeafProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? model = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private class Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public Shipping? Shipping { get; set; }
    }

    private class Shipping
    {
        public string Carrier { get; set; } = "";
        public Address? Address { get; set; }
    }

    private class Address
    {
        public string City { get; set; } = "";
        public int Zip { get; set; }
    }

    private static readonly Action<ModelBuilder> OrderModel = mb =>
        mb.Entity<Order>().OwnsOne(o => o.Shipping, s => s.OwnsOne(x => x.Address));

    private IMongoCollection<Order> SeedOrders([CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Order>(name);
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument
            {
                { "_id", 1 }, { "Code", "one" },
                { "Shipping", new BsonDocument { { "Carrier", "air" }, { "Address", new BsonDocument { { "City", "Oslo" }, { "Zip", 10 } } } } }
            },
            new BsonDocument { { "_id", 2 }, { "Code", "two" }, { "Shipping", BsonNull.Value } },
            new BsonDocument { { "_id", 3 }, { "Code", "three" } },
            new BsonDocument { { "_id", 4 }, { "Code", "four" }, { "Shipping", new BsonDocument { { "Carrier", "sea" } } } },
            new BsonDocument
            {
                { "_id", 5 }, { "Code", "five" },
                { "Shipping", new BsonDocument { { "Carrier", "rail" }, { "Address", new BsonDocument { { "City", "Rome" }, { "Zip", 50 } } } } }
            },
        ]);
        return collection;
    }

    private static Type CursorRowType<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            return enumerator.GetType().GetGenericArguments()[0];
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static void AssertStreams<T>(IQueryable<T> query)
    {
        var rowType = CursorRowType(query);
        Assert.True(
            MongoProjectedRowState.IsRowType(rowType),
            $"Expected the streaming projection reader, but the cursor yields {rowType}.");
    }

    private static void AssertCursorIsNotDocument<T>(IQueryable<T> query)
    {
        var rowType = CursorRowType(query);
        Assert.NotEqual(typeof(BsonDocument), rowType);
    }

    private static string Describe(Shipping? s)
        => s == null ? "<null>" : $"{s.Carrier}:{(s.Address == null ? "<null>" : s.Address.City + "/" + s.Address.Zip)}";

    [Fact]
    public void Owned_reference_in_a_projection_streams()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);

        AssertStreams(db.Entities.AsNoTracking().Select(o => new { o.Code, o.Shipping }));
    }

    [Fact]
    public void Owned_reference_with_a_nested_reference_matches_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return db.Entities.AsNoTracking().OrderBy(o => o.Id)
                .Select(o => new { o.Code, o.Shipping })
                .AsEnumerable()
                .Select(o => $"{o.Code}|{Describe(o.Shipping)}")
                .ToList();
        });

        Assert.Equal(
            ["one|air:Oslo/10", "two|<null>", "three|<null>", "four|sea:<null>", "five|rail:Rome/50"],
            results);
    }

    // ---- the whole root entity beside other values -----------------------------------------------------

    [Fact]
    public void Whole_entity_beside_a_scalar_streams()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);

        AssertStreams(db.Entities.AsNoTracking().Select(o => new { Item = o, o.Code.Length }));
    }

    [Fact]
    public void Whole_entity_beside_a_scalar_matches_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return db.Entities.AsNoTracking().OrderBy(o => o.Id)
                .Select(o => new { Item = o, o.Code.Length })
                .AsEnumerable()
                .Select(r => $"{r.Item.Id}|{r.Item.Code}|{Describe(r.Item.Shipping)}|{r.Length}")
                .ToList();
        });

        Assert.Equal(
            [
                "1|one|air:Oslo/10|3", "2|two|<null>|3", "3|three|<null>|5", "4|four|sea:<null>|4", "5|five|rail:Rome/50|4"
            ],
            results);
    }

    // ---- a whole entity wrapped in client code -----------------------------------------------------------

    private class OrderDto(Order order)
    {
        public Order Entity { get; } = order;
        public string Summary { get; } = order.Code + "/" + (order.Shipping?.Address?.Zip.ToString() ?? "-");
    }

    private static string Describe(Order order) => order.Code + ":" + (order.Shipping?.Carrier ?? "-");

    [Fact]
    public void Constructor_wrapped_entity_streams()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);

        // The cursor yields the entity (untracked) or the entity with its deferred tracking (tracked), never a BsonDocument.
        AssertCursorIsNotDocument(db.Entities.AsNoTracking().Select(o => new OrderDto(o)));
        AssertCursorIsNotDocument(db.Entities.Select(o => new OrderDto(o)));
        AssertCursorIsNotDocument(db.Entities.AsNoTracking().Select(o => Describe(o)));
    }

    [Fact]
    public void Constructor_wrapped_and_client_method_entities_match_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var wrapped = db.Entities.AsNoTracking().OrderBy(o => o.Id).Select(o => new OrderDto(o)).ToList()
                .Select(d => d.Summary + "|" + Describe(d.Entity)).ToList();
            var described = db.Entities.AsNoTracking().OrderBy(o => o.Id).Select(o => Describe(o)).ToList();
            return wrapped.Concat(described).ToList();
        });

        Assert.Equal(
            [
                "one/10|one:air", "two/-|two:-", "three/-|three:-", "four/-|four:sea", "five/50|five:rail",
                "one:air", "two:-", "three:-", "four:sea", "five:rail"
            ],
            results);
    }

    [Fact]
    public void Tracked_constructor_wrapped_entity_is_tracked_and_resolves_to_the_tracked_instance()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var tracked = db.Entities.Single(o => o.Id == 4);
            var dtos = db.Entities.Select(o => new OrderDto(o)).AsEnumerable().OrderBy(d => d.Entity.Id).ToList();
            return new List<string>
            {
                $"same={ReferenceEquals(dtos[3].Entity, tracked)}",
                $"entries={db.ChangeTracker.Entries<Order>().Count()}",
                string.Join(",", dtos.Select(d => d.Summary)),
            };
        });

        Assert.Equal("same=True", results[0]);
        Assert.Equal("entries=5", results[1]);
    }

    // ---- tracked queries ---------------------------------------------------------------------------------

    [Fact]
    public void Tracked_whole_entity_beside_a_scalar_streams_and_tracks_each_entity()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);

        AssertStreams(db.Entities.Select(o => new { Item = o, o.Code.Length }));

        var rows = db.Entities.Select(o => new { Item = o, o.Code.Length }).OrderBy(r => r.Item.Id).ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(5, db.ChangeTracker.Entries<Order>().Count());
        Assert.All(rows, r => Assert.Equal(EntityState.Unchanged, db.Entry(r.Item).State));
    }

    [Fact]
    public void Tracked_whole_entity_beside_a_scalar_returns_an_entity_that_is_already_tracked()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var tracked = db.Entities.Single(o => o.Id == 3);
            var rows = db.Entities.Select(o => new { Item = o, o.Code.Length }).AsEnumerable().OrderBy(r => r.Item.Id).ToList();
            return new List<string>
            {
                $"same={ReferenceEquals(rows[2].Item, tracked)}",
                $"entries={db.ChangeTracker.Entries<Order>().Count()}",
                string.Join(",", rows.Select(r => r.Item.Code + r.Length)),
            };
        });

        Assert.Equal("same=True", results[0]);
        Assert.Equal("entries=5", results[1]);
    }

    [Fact]
    public void A_computed_projection_streams_and_reads_a_missing_or_null_value_as_the_dom_did()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return db.Entities.AsNoTracking().OrderBy(o => o.Id)
                .Select(o => new { Pick = o.Code == "one" ? -1 : o.Id, Carrier = o.Shipping!.Carrier ?? "none" })
                .ToList().Select(r => $"{r.Pick}:{r.Carrier}").ToList();
        });

        Assert.Equal(["-1:air", "2:none", "3:none", "4:sea", "5:rail"], results);

        using var streaming = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);
        AssertStreams(streaming.Entities.AsNoTracking().Select(o => o.Code == "one" ? -1 : o.Id));
    }

    private class Wrapped(Order order)
    {
        public Order Order { get; } = order;
        public string? Code { get; set; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_wrapper_that_uses_its_entity_more_than_once_streams_and_tracks_each_row_once(bool tracked)
    {
        var collection = SeedOrders("Wrapped" + tracked);

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var first = tracked ? db.Entities.AsTracking().Single(o => o.Id == 2) : null;
            var query = tracked ? db.Entities.AsTracking() : db.Entities.AsNoTracking();
            var rows = query.OrderBy(o => o.Id).Select(o => new Wrapped(o) { Code = o.Code }).ToList();
            return new List<string>
            {
                $"same={(first == null || ReferenceEquals(first, rows[1].Order))}",
                $"entries={db.ChangeTracker.Entries<Order>().Count()}",
                string.Join(",", rows.Select(r => r.Order.Id + r.Code)),
            };
        });

        Assert.Equal("same=True", results[0]);
        Assert.Equal(tracked ? "entries=5" : "entries=0", results[1]);
        Assert.Equal("1one,2two,3three,4four,5five", results[2]);

        using var streaming = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);
        AssertCursorIsNotDocument(streaming.Entities.AsNoTracking().Select(o => new Wrapped(o) { Code = o.Code }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_constructed_entity_with_a_computed_member_streams(bool tracked)
    {
        var collection = SeedOrders("Constructed" + tracked);

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var query = tracked ? db.Entities.AsTracking() : db.Entities.AsNoTracking();
            var rows = query.OrderBy(o => o.Id).Select(o => new Order { Code = o.Code + "!" }).ToList();
            return new List<string>
            {
                string.Join(",", rows.Select(r => r.Code + (r.Shipping == null ? "-" : "S"))),
                $"entries={db.ChangeTracker.Entries<Order>().Count()}",
            };
        });

        Assert.Equal("one!-,two!-,three!-,four!-,five!-", results[0]);
        Assert.Equal("entries=0", results[1]);

        using var streaming = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);
        AssertStreams(streaming.Entities.AsNoTracking().Select(o => new Order { Code = o.Code + "!" }));
    }

    private class Marker
    {
        public int Tag { get; set; } = 7;
    }

    [Fact]
    public void A_projection_that_reads_nothing_from_the_document_streams_one_row_per_document()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return new List<string>
            {
                string.Join(",", db.Entities.AsNoTracking().Select(o => new Marker()).ToList().Select(m => m.Tag)),
                string.Join(",", db.Entities.AsNoTracking().Where(o => o.Id > 3).Select(o => 1).ToList()),
                string.Join(",", db.Entities.AsNoTracking().Select(o => new Marker()).Distinct().ToList().Select(m => m.Tag)),
            };
        });

        Assert.Equal("7,7,7,7,7", results[0]);
        Assert.Equal("1,1", results[1]);
        Assert.Equal("7", results[2]);

        using var streaming = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);
        AssertStreams(streaming.Entities.AsNoTracking().Select(o => new Marker()));
    }

    [Fact]
    public void A_tracked_whole_entity_beside_a_scalar_resolves_to_the_entity_already_tracked()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var first = db.Entities.AsTracking().Single(o => o.Id == 1);
            var rows = db.Entities.AsTracking().OrderBy(o => o.Id).Select(o => new { Item = o, o.Code.Length }).ToList();
            return new List<string>
            {
                $"same={ReferenceEquals(first, rows[0].Item)}",
                $"entries={db.ChangeTracker.Entries<Order>().Count()}",
                string.Join(",", rows.Select(r => r.Item.Code + ":" + r.Length)),
            };
        });

        Assert.Equal("same=True", results[0]);
        Assert.Equal("entries=5", results[1]);
        Assert.Equal("one:3,two:3,three:5,four:4,five:4", results[2]);
    }

    [Fact]
    public void No_tracking_with_identity_resolution_shares_one_instance_per_key_and_tracks_nothing()
    {
        var suffix = SeedBlog();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = BlogDb(mode, suffix);
            var rows = db.Articles.AsNoTrackingWithIdentityResolution().OrderBy(a => a.Id)
                .Select(a => new { a.Title, a.Author })
                .ToList();
            return new List<string>
            {
                $"shared={ReferenceEquals(rows[0].Author, rows[2].Author)}",
                $"entries={db.ChangeTracker.Entries().Count()}",
                string.Join(",", rows.Select(r => r.Title + ":" + (r.Author?.Name ?? "-"))),
            };
        });

        Assert.Equal("shared=True", results[0]);
        Assert.Equal("entries=0", results[1]);
    }

    [Fact]
    public void Tracked_joined_entity_is_tracked_once_and_shared_between_rows()
    {
        var suffix = SeedBlog();

        using (var db = BlogDb(MongoQueryMode.NativeOnly, suffix))
        {
            AssertStreams(db.Articles.Select(a => new { a.Title, a.Author }));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = BlogDb(mode, suffix);
            var rows = db.Articles.OrderBy(a => a.Id).Select(a => new { a.Title, a.Author }).ToList();
            return new List<string>
            {
                $"shared={ReferenceEquals(rows[0].Author, rows[2].Author)}",
                $"entries={db.ChangeTracker.Entries<Author>().Count()}",
                string.Join(",", rows.Select(r => r.Title + ":" + (r.Author?.Name ?? "-"))),
            };
        });

        Assert.Equal("shared=True", results[0]);
        Assert.Equal("entries=2", results[1]);
    }

    // ---- joined entities ------------------------------------------------------------------------------

    private class Article
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int? AuthorId { get; set; }
        public Author? Author { get; set; }
    }

    private class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Bio { get; set; }
    }

    private sealed class BlogContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Article> Articles => Set<Article>();
        public DbSet<Author> Authors => Set<Author>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Author>().ToCollection("Authors" + suffix);
            modelBuilder.Entity<Article>(b =>
            {
                b.ToCollection("Articles" + suffix);
                b.HasOne(a => a.Author).WithMany().HasForeignKey(a => a.AuthorId);
            });
        }
    }

    private BlogContext BlogDb(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<BlogContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    // Articles 1 and 3 share author 1; article 2 has author 2 (no bio); article 4 has no author; article 5 points at an author that does not exist.
    private string SeedBlog()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var seed = BlogDb(MongoQueryMode.Native, suffix);
        seed.Authors.AddRange(new Author { Id = 1, Name = "ann", Bio = "bio" }, new Author { Id = 2, Name = "bob" });
        seed.SaveChanges();
        seed.Articles.AddRange(
            new Article { Id = 1, Title = "a1", AuthorId = 1 },
            new Article { Id = 2, Title = "a2", AuthorId = 2 },
            new Article { Id = 3, Title = "a3", AuthorId = 1 },
            new Article { Id = 4, Title = "a4", AuthorId = null },
            new Article { Id = 5, Title = "a5", AuthorId = 99 });
        seed.SaveChanges();
        return suffix;
    }

    [Fact]
    public void Joined_entity_in_a_projection_streams()
    {
        var suffix = SeedBlog();
        using var db = BlogDb(MongoQueryMode.NativeOnly, suffix);

        AssertStreams(db.Articles.AsNoTracking().Select(a => new { a.Title, a.Author }));
    }

    [Fact]
    public void Joined_entity_in_a_projection_matches_driver_linq()
    {
        var suffix = SeedBlog();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = BlogDb(mode, suffix);
            return db.Articles.AsNoTracking().OrderBy(a => a.Id)
                .Select(a => new { a.Title, a.Author })
                .AsEnumerable()
                .Select(a => $"{a.Title}|{(a.Author == null ? "<null>" : a.Author.Id + ":" + a.Author.Name + ":" + (a.Author.Bio ?? "<null>"))}")
                .ToList();
        });

        Assert.Equal(
            ["a1|1:ann:bio", "a2|2:bob:<null>", "a3|1:ann:bio", "a4|<null>", "a5|<null>"],
            results);
    }
}
