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
/// Shapes with a lookup or a document inside another one, read straight off the cursor: an entity beside lookups only its predicate uses, a
/// correlated list whose elements include a collection, an owned collection under an owned reference, a collection Include on a join's inner
/// entity, and the unwound elements of an owned collection.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeNestedShapesStreamingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Article> Articles { get; set; } = [];
    }

    private class Article
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int Views { get; set; }
        public int CategoryId { get; set; }
        public Category? Category { get; set; }
        public List<Comment> Comments { get; set; } = [];
    }

    private class Comment
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int ArticleId { get; set; }
    }

    private class Place
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
        public List<Visit> Visits { get; set; } = [];
    }

    private class Home
    {
        public string City { get; set; } = "";
        public List<Note> Notes { get; set; } = [];
    }

    private class Note
    {
        public string Text { get; set; } = "";
    }

    private class Visit
    {
        public string Who { get; set; } = "";
        public int Days { get; set; }
    }

    private sealed class ShopContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Article> Articles => Set<Article>();
        public DbSet<Comment> Comments => Set<Comment>();
        public DbSet<Place> Places => Set<Place>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Category>().ToCollection("NestedCategories" + suffix);
            mb.Entity<Article>(b =>
            {
                b.ToCollection("NestedArticles" + suffix);
                b.HasOne(a => a.Category).WithMany(c => c.Articles).HasForeignKey(a => a.CategoryId);
                b.HasMany(a => a.Comments).WithOne().HasForeignKey(c => c.ArticleId);
            });
            mb.Entity<Comment>().ToCollection("NestedComments" + suffix);
            mb.Entity<Place>(b =>
            {
                b.ToCollection("NestedPlaces" + suffix);
                b.OwnsOne(p => p.Home, h => h.OwnsMany(x => x.Notes));
                b.OwnsMany(p => p.Visits);
            });
        }
    }

    private ShopContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<ShopContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options,
            suffix);

    // Category 1 has articles 1 (two comments) and 2 (none), category 2 has article 3 (one comment), category 3 has none.
    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var db = database.MongoDatabase;
        db.GetCollection<BsonDocument>("NestedCategories" + suffix).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Name", "c1" } },
            new BsonDocument { { "_id", 2 }, { "Name", "c2" } },
            new BsonDocument { { "_id", 3 }, { "Name", "c3" } }
        ]);
        db.GetCollection<BsonDocument>("NestedArticles" + suffix).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Title", "t1" }, { "Views", 20 }, { "CategoryId", 1 } },
            new BsonDocument { { "_id", 2 }, { "Title", "t2" }, { "Views", 5 }, { "CategoryId", 1 } },
            new BsonDocument { { "_id", 3 }, { "Title", "t3" }, { "Views", 30 }, { "CategoryId", 2 } }
        ]);
        db.GetCollection<BsonDocument>("NestedComments" + suffix).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Text", "x" }, { "ArticleId", 1 } },
            new BsonDocument { { "_id", 2 }, { "Text", "y" }, { "ArticleId", 1 } },
            new BsonDocument { { "_id", 3 }, { "Text", "z" }, { "ArticleId", 3 } }
        ]);
        db.GetCollection<BsonDocument>("NestedPlaces" + suffix).InsertMany(
        [
            new BsonDocument
            {
                { "_id", 1 }, { "Title", "p1" },
                { "Home", new BsonDocument { { "City", "Oslo" }, { "Notes", new BsonArray { new BsonDocument("Text", "n1"), new BsonDocument("Text", "n2") } } } },
                { "Visits", new BsonArray { new BsonDocument { { "Who", "a" }, { "Days", 2 } }, new BsonDocument { { "Who", "b" }, { "Days", 5 } } } }
            },
            new BsonDocument
            {
                { "_id", 2 }, { "Title", "p2" },
                { "Home", new BsonDocument { { "City", "Rome" } } },
                { "Visits", new BsonArray() }
            },
            new BsonDocument
            {
                { "_id", 3 }, { "Title", "p3" },
                { "Home", new BsonDocument { { "City", "Bern" }, { "Notes", new BsonArray { new BsonDocument("Text", "n3") } } } },
                { "Visits", new BsonArray { new BsonDocument { { "Who", "c" }, { "Days", 1 } } } }
            }
        ]);
        return suffix;
    }

    private static void AssertStreams<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            var rowType = enumerator.GetType().GetGenericArguments()[0];
            Assert.True(
                MongoProjectedRowState.IsRowType(rowType) || rowType != typeof(BsonDocument),
                $"Expected the streaming reader, but the cursor yields {rowType}.");
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_correlated_list_whose_elements_include_a_collection_streams(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var articles = tracked ? db.Articles.AsTracking() : db.Articles.AsNoTracking();
            var rows = categories.OrderBy(c => c.Id)
                .Select(c => new { c.Id, Articles = articles.Include(a => a.Comments).Where(a => a.CategoryId == c.Id).ToList() }).ToList();
            return new List<string>
            {
                string.Join(
                    ",", rows.Select(
                        r => r.Id + ":" + string.Join("+", r.Articles.OrderBy(a => a.Id).Select(a => a.Title + "/" + string.Join("", a.Comments.OrderBy(m => m.Id).Select(m => m.Text)))))),
                $"entries={db.ChangeTracker.Entries().Count()}",
            };
        });

        Assert.Equal("1:t1/xy+t2/,2:t3/z,3:", results[0]);
        Assert.Equal(tracked ? "entries=6" : "entries=0", results[1]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(
            streaming.Categories.AsNoTracking().Select(
                c => new { c.Id, Articles = streaming.Articles.Include(a => a.Comments).Where(a => a.CategoryId == c.Id).ToList() }));
    }

    [Fact]
    public void A_whole_entity_beside_a_lookup_only_its_predicate_uses_streams()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Categories.Where(c => db.Articles.Where(a => a.CategoryId == c.Id).Any(a => a.Views > 15)).OrderBy(c => c.Id)
                .ToList().Select(c => c.Name).ToList();
        });

        Assert.Equal(["c1", "c2"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.Where(c => streaming.Articles.Where(a => a.CategoryId == c.Id).Any(a => a.Views > 15)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_collection_Include_on_the_inner_side_of_a_join_streams(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var articles = tracked ? db.Articles.AsTracking() : db.Articles.AsNoTracking();
            var rows = categories.Join(articles.Include(a => a.Comments), c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .OrderBy(x => x.a.Id).ToList();
            return rows.Select(r => r.c.Name + "/" + r.a.Title + "/" + string.Join("", r.a.Comments.OrderBy(m => m.Id).Select(m => m.Text))).ToList();
        });

        Assert.Equal(["c1/t1/xy", "c1/t2/", "c2/t3/z"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.Join(streaming.Articles.Include(a => a.Comments), c => c.Id, a => a.CategoryId, (c, a) => new { c, a }));
    }

    [Fact]
    public void An_owned_collection_under_an_owned_reference_streams_beside_scalars()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Places.AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new { p.Title, p.Home.City, p.Home.Notes }).ToList()
                .Select(r => r.Title + ":" + r.City + ":" + string.Join("+", r.Notes.Select(n => n.Text))).ToList();
        });

        Assert.Equal(["p1:Oslo:n1+n2", "p2:Rome:", "p3:Bern:n3"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Places.AsNoTracking().Select(p => new { p.Title, p.Home.City, p.Home.Notes }));
    }

    [Fact]
    public void An_owned_collection_read_whole_and_through_an_element_Select_streams()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Places.AsNoTracking().OrderBy(p => p.Id)
                .Select(p => new { Days = p.Visits.Select(v => v.Days).ToList(), p.Visits }).ToList()
                .Select(r => string.Join("+", r.Days) + ":" + string.Join("+", r.Visits.Select(v => v.Who))).ToList();
        });

        Assert.Equal(["2+5:a+b", ":", "1:c"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Places.AsNoTracking().Select(p => new { Days = p.Visits.Select(v => v.Days).ToList(), p.Visits }));
    }

    [Fact]
    public void The_unwound_elements_of_an_owned_collection_stream()
    {
        var suffix = Seed();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = Db(mode, suffix);
            var results = db.Places.AsNoTracking().SelectMany(p => p.Visits).ToList().OrderBy(v => v.Who).Select(v => v.Who + v.Days).ToList();
            Assert.Equal(["a2", "b5", "c1"], results);
        }

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Places.AsNoTracking().SelectMany(p => p.Visits));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reference_Include_on_the_inner_side_of_a_join_streams(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var articles = tracked ? db.Articles.AsTracking() : db.Articles.AsNoTracking();
            var rows = categories.Join(articles.Include(a => a.Category), c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .OrderBy(x => x.a.Id).ToList();
            return new List<string>
            {
                string.Join(",", rows.Select(r => r.c.Name + "/" + r.a.Title + "/" + r.a.Category?.Name)),
                $"entries={db.ChangeTracker.Entries().Count()}",
            };
        });

        Assert.Equal("c1/t1/c1,c1/t2/c1,c2/t3/c2", results[0]);
        Assert.Equal(tracked ? "entries=5" : "entries=0", results[1]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(
            streaming.Categories.Join(streaming.Articles.Include(a => a.Category), c => c.Id, a => a.CategoryId, (c, a) => new { c, a }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_whole_entity_with_an_Include_beside_a_joined_scalar_streams(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var rows = categories.Include(c => c.Articles).Join(db.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a.Title })
                .OrderBy(x => x.Title).ToList();
            return new List<string>
            {
                string.Join(",", rows.Select(r => r.c.Name + "/" + r.Title + "/" + r.c.Articles.Count)),
                $"entries={db.ChangeTracker.Entries().Count()}",
            };
        });

        Assert.Equal("c1/t1/2,c1/t2/2,c2/t3/1", results[0]);
        Assert.Equal(tracked ? "entries=5" : "entries=0", results[1]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.Include(c => c.Articles).Join(streaming.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a.Title }));
    }

    [Fact]
    public void A_whole_entity_Include_beside_a_predicate_on_the_same_collection_streams()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Categories.Include(c => c.Articles).Where(c => db.Articles.Where(a => a.CategoryId == c.Id).Any(a => a.Views > 15))
                .OrderBy(c => c.Id).ToList().Select(c => c.Name + ":" + c.Articles.Count).ToList();
        });

        Assert.Equal(["c1:2", "c2:1"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(
            streaming.Categories.Include(c => c.Articles).Where(c => streaming.Articles.Where(a => a.CategoryId == c.Id).Any(a => a.Views > 15)));
    }

    [Fact]
    public void An_entity_used_twice_in_an_untracked_projection_is_read_into_two_instances()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var rows = db.Categories.AsNoTracking().Join(db.Articles.AsNoTracking(), c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .Select(x => new { First = x.a, Second = x.a }).OrderBy(x => x.First.Id).ToList();
            return rows.Select(r => r.First.Title + r.Second.Title + ReferenceEquals(r.First, r.Second)).ToList();
        });

        Assert.Equal(["t1t1False", "t2t2False", "t3t3False"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(
            streaming.Categories.AsNoTracking().Join(streaming.Articles.AsNoTracking(), c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .Select(x => new { First = x.a, Second = x.a }));
    }

    private static string Describe(Category category) => category.Name + "#" + category.Id;

    [Fact]
    public void A_client_method_over_a_whole_entity_streams()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Categories.OrderBy(c => c.Id).Select(c => Describe(c)).ToList();
        });

        Assert.Equal(["c1#1", "c2#2", "c3#3"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.Select(c => Describe(c)));
    }

    [Fact]
    public void A_grouping_projected_into_a_nested_construction_streams()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Articles.GroupBy(a => a.CategoryId)
                .Select(g => new Wrapper { Container = new Inner { Key = g.Key, Total = g.Sum(a => a.Views), Count = g.Count() } })
                .ToList().OrderBy(w => w.Container.Key).Select(w => $"{w.Container.Key}:{w.Container.Total}:{w.Container.Count}").ToList();
        });

        Assert.Equal(["1:25:2", "2:30:1"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(
            streaming.Articles.GroupBy(a => a.CategoryId)
                .Select(g => new Wrapper { Container = new Inner { Key = g.Key, Total = g.Sum(a => a.Views), Count = g.Count() } }));
    }

    private class Wrapper
    {
        public Inner Container { get; set; } = null!;
    }

    private class Inner
    {
        public int Key { get; set; }
        public int Total { get; set; }
        public int Count { get; set; }
    }
}
