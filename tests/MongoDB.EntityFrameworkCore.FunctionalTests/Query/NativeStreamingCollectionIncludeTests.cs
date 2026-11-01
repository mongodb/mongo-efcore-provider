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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A non-owned collection Include backed by a plain <c>$lookup</c> array is read straight off the cursor, each array element into a whole
/// entity, instead of through a <see cref="BsonDocument"/>. The object graph, tracking, errors and the shapes that must stay on the DOM
/// shaper (filtered or paged includes, ThenInclude) must be exactly what the DOM path produced.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStreamingCollectionIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---- Entities and fixtures ---------------------------------------------------------------------

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
        public int? Rating { get; set; }
        public int CategoryId { get; set; }
        public Category? Category { get; set; }
        public int AuthorId { get; set; }
        public Author? Author { get; set; }
    }

    private class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class BlogContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Article> Articles => Set<Article>();
        public DbSet<Author> Authors => Set<Author>();

        public IMongoCollection<BsonDocument> Raw(string name)
            => Database.GetService<IMongoClientWrapper>().GetCollection<BsonDocument>(name + suffix);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Category>().ToCollection("Categories" + suffix);
            modelBuilder.Entity<Author>().ToCollection("Authors" + suffix);
            modelBuilder.Entity<Article>(b =>
            {
                b.ToCollection("Articles" + suffix);
                b.HasOne(a => a.Category).WithMany(c => c.Articles).HasForeignKey(a => a.CategoryId);
                b.HasOne(a => a.Author).WithMany().HasForeignKey(a => a.AuthorId);
            });
        }
    }

    private BlogContext Context(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<BlogContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    // Category 1: articles 1,2,3. Category 2: article 4. Category 3: none (an empty array). Authors 1 and 2 are shared across articles.
    private (string Suffix, BlogContext Seed) SeedBlog(MongoQueryMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using (var seed = Context(MongoQueryMode.Native, suffix))
        {
            seed.Authors.AddRange(new Author { Id = 1, Name = "ann" }, new Author { Id = 2, Name = "bob" });
            seed.Categories.AddRange(
                new Category { Id = 1, Name = "c1" }, new Category { Id = 2, Name = "c2" }, new Category { Id = 3, Name = "c3" });
            seed.SaveChanges();
            seed.Articles.AddRange(
                new Article { Id = 1, Title = "a1", Views = 10, Rating = 5, CategoryId = 1, AuthorId = 1 },
                new Article { Id = 2, Title = "a2", Views = 20, Rating = null, CategoryId = 1, AuthorId = 2 },
                new Article { Id = 3, Title = "a3", Views = 30, Rating = 3, CategoryId = 1, AuthorId = 1 },
                new Article { Id = 4, Title = "a4", Views = 40, Rating = 4, CategoryId = 2, AuthorId = 2 });
            seed.SaveChanges();
        }

        return (suffix, Context(mode, suffix));
    }

    private BlogContext Seed(MongoQueryMode mode) => SeedBlog(mode).Seed;

    // The cursor's row type: BsonDocument on the DOM shaper; the entity (untracked) or MongoMaterializedRow<entity> (tracked) when streaming.
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
        => Assert.NotEqual(typeof(BsonDocument), CursorRowType(query));

    private static void AssertDom<T>(IQueryable<T> query)
        => Assert.Equal(typeof(BsonDocument), CursorRowType(query));

    // A comparable description of the graph, including that each article's inverse points at the same category instance.
    private static string[] Describe(IEnumerable<Category> categories)
        => categories.OrderBy(c => c.Id)
            .Select(c => $"{c.Name}:[{string.Join(',', c.Articles.OrderBy(a => a.Id).Select(a => $"{a.Title}/{a.Views}/{a.Rating?.ToString() ?? "-"}/{(ReferenceEquals(a.Category, c) ? "inv" : a.Category is null ? "noinv" : "other")}"))}]")
            .ToArray();

    private static readonly string[] ExpectedGraph =
    [
        "c1:[a1/10/5/inv,a2/20/-/inv,a3/30/3/inv]",
        "c2:[a4/40/4/inv]",
        "c3:[]"
    ];

    private static string[] Tracked(DbContext db)
        => db.ChangeTracker.Entries()
            .Select(e => $"{e.Metadata.DisplayName()}:{e.Property("Id").CurrentValue}:{e.State}")
            .Order()
            .ToArray();

    // ---- Untracked ---------------------------------------------------------------------------------

    [Fact]
    public void Untracked_collection_include_streams_and_builds_the_same_graph_as_driver_linq()
    {
        using (var db = Seed(MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Categories.AsNoTracking().Include(c => c.Articles));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (suffix, db) = SeedBlog(mode);
            using (db)
            {
                return Describe(db.Categories.AsNoTracking().Include(c => c.Articles).ToList()).ToList();
            }
        });

        Assert.Equal(ExpectedGraph, results);
    }

    [Fact]
    public void Empty_collection_is_an_empty_list_not_null()
    {
        using var db = Seed(MongoQueryMode.NativeOnly);

        var empty = db.Categories.AsNoTracking().Include(c => c.Articles).OrderBy(c => c.Id).ToList().Single(c => c.Id == 3);

        Assert.NotNull(empty.Articles);
        Assert.Empty(empty.Articles);
    }

    [Fact]
    public void Collection_include_beside_root_filter_sort_and_paging_streams_and_matches_driver_linq()
    {
        using (var db = Seed(MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Categories.AsNoTracking().Where(c => c.Id > 0).OrderBy(c => c.Name).Skip(1).Take(2).Include(c => c.Articles));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return Describe(db.Categories.AsNoTracking().Where(c => c.Id > 0).OrderBy(c => c.Name).Skip(1).Take(2)
                    .Include(c => c.Articles).ToList()).ToList();
            }
        });

        Assert.Equal(ExpectedGraph[1..], results);
    }

    [Fact]
    public void Collection_include_and_the_inverse_reference_agree_for_untracked_identity_resolution()
    {
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                var categories = db.Categories.AsNoTrackingWithIdentityResolution().Include(c => c.Articles).ToList();
                return Describe(categories).ToList();
            }
        });

        Assert.Equal(ExpectedGraph, results);
    }

    [Fact]
    public async Task Async_collection_include_matches()
    {
        using var db = Seed(MongoQueryMode.NativeOnly);

        var categories = await db.Categories.AsNoTracking().Include(c => c.Articles).ToListAsync();

        Assert.Equal(ExpectedGraph, Describe(categories));
    }

    // ---- Tracked -----------------------------------------------------------------------------------

    [Fact]
    public void Tracked_collection_include_streams_wires_the_graph_and_marks_it_loaded()
    {
        using var db = Seed(MongoQueryMode.NativeOnly);
        AssertStreams(db.Categories.Include(c => c.Articles));

        var categories = db.Categories.Include(c => c.Articles).ToList();

        Assert.Equal(ExpectedGraph, Describe(categories));
        Assert.Equal(
            ["Article:1:Unchanged", "Article:2:Unchanged", "Article:3:Unchanged", "Article:4:Unchanged",
             "Category:1:Unchanged", "Category:2:Unchanged", "Category:3:Unchanged"],
            Tracked(db));

        // Loaded flags: set by the state manager (a non-empty collection) or by the include (an empty one).
        foreach (var category in categories)
        {
            Assert.True(db.Entry(category).Collection(c => c.Articles).IsLoaded, $"{category.Name} not marked loaded");
        }
    }

    [Fact]
    public void Tracked_collection_include_matches_driver_linq_and_resolves_to_already_tracked_instances()
    {
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                var tracked = db.Categories.Single(c => c.Id == 1);
                var loaded = db.Categories.Include(c => c.Articles).OrderBy(c => c.Id).ToList();

                Assert.Same(tracked, loaded[0]);
                Assert.Equal(7, db.ChangeTracker.Entries().Count());
                return Describe(loaded).ToList();
            }
        });

        Assert.Equal(ExpectedGraph, results);
    }

    [Fact]
    public void Unread_rows_of_the_cursor_batch_are_not_tracked_with_their_articles()
    {
        using var db = Seed(MongoQueryMode.NativeOnly);

        var first = db.Categories.Include(c => c.Articles).OrderBy(c => c.Id).AsEnumerable().Take(1).ToList();

        Assert.Equal("c1", Assert.Single(first).Name);
        Assert.Equal(
            ["Article:1:Unchanged", "Article:2:Unchanged", "Article:3:Unchanged", "Category:1:Unchanged"],
            Tracked(db));
    }

    [Fact]
    public void Saving_changes_to_a_streamed_tracked_graph_updates_only_what_changed()
    {
        var (suffix, db) = SeedBlog(MongoQueryMode.NativeOnly);
        using (db)
        {
            var category = db.Categories.Include(c => c.Articles).Single(c => c.Id == 1);
            category.Articles.Single(a => a.Id == 2).Views = 99;

            Assert.Equal(1, db.SaveChanges());
            Assert.Equal(99, db.Raw("Articles").Find(new BsonDocument("_id", 2)).Single()["Views"].AsInt32);
        }
    }

    // ---- Errors ------------------------------------------------------------------------------------

    [Fact]
    public void Malformed_article_in_a_later_category_does_not_fail_the_categories_before_it()
    {
        var (suffix, db) = SeedBlog(MongoQueryMode.NativeOnly);
        using (db)
        {
            db.Raw("Articles").UpdateOne(new BsonDocument("_id", 4), new BsonDocument("$set", new BsonDocument("Views", "not an int")));

            // Only the first category is read: the malformed article belongs to the second, which the driver's batch holds but the caller never reaches.
            var first = db.Categories.AsNoTracking().Include(c => c.Articles).OrderBy(c => c.Id).AsEnumerable().Take(1).ToList();
            Assert.Equal(3, Assert.Single(first).Articles.Count);

            using var enumerator = db.Categories.AsNoTracking().Include(c => c.Articles).OrderBy(c => c.Id).AsEnumerable().GetEnumerator();
            Assert.True(enumerator.MoveNext());
            Assert.Throws<FormatException>(() => enumerator.MoveNext());
        }
    }

    // ---- Shapes that stay on the DOM shaper ----------------------------------------------------------

    [Fact]
    public void Then_include_of_a_reference_off_a_collection_streams_and_builds_the_same_graph()
    {
        using (var db = Seed(MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Categories.AsNoTracking().Include(c => c.Articles).ThenInclude(a => a.Author));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return db.Categories.AsNoTracking().Include(c => c.Articles).ThenInclude(a => a.Author).OrderBy(c => c.Id).ToList()
                    .Select(c => $"{c.Name}:[{string.Join(',', c.Articles.OrderBy(a => a.Id).Select(a => $"{a.Title}/{a.Author?.Name}"))}]")
                    .ToList();
            }
        });

        Assert.Equal(["c1:[a1/ann,a2/bob,a3/ann]", "c2:[a4/bob]", "c3:[]"], results);
    }

    [Fact]
    public void Sorted_and_paged_include_streams_and_matches_driver_linq()
    {
        using (var db = Seed(MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Categories.AsNoTracking().Include(c => c.Articles.OrderByDescending(a => a.Views).Take(2)));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return db.Categories.AsNoTracking().Include(c => c.Articles.OrderByDescending(a => a.Views).Take(2)).OrderBy(c => c.Id).ToList()
                    .Select(c => $"{c.Name}:[{string.Join(',', c.Articles.Select(a => a.Title))}]")
                    .ToList();
            }
        });

        Assert.Equal(["c1:[a3,a2]", "c2:[a4]", "c3:[]"], results);
    }

    [Fact]
    public void Tracked_then_include_tracks_every_entity_once_and_shares_the_referenced_instance()
    {
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                var categories = db.Categories.Include(c => c.Articles).ThenInclude(a => a.Author).OrderBy(c => c.Id).ToList();
                var authors = categories.SelectMany(c => c.Articles).Where(a => a.Author != null).Select(a => a.Author!).Distinct().Count();
                return new List<string>
                {
                    $"entries={db.ChangeTracker.Entries().Count()}",
                    $"authors={authors}",
                    string.Join(
                        ';',
                        categories.Select(c => $"{c.Name}:[{string.Join(',', c.Articles.OrderBy(a => a.Id).Select(a => $"{a.Title}/{a.Author?.Name}"))}]")),
                };
            }
        });

        // Three categories, four articles, two distinct authors shared by the articles.
        Assert.Equal("entries=9", results[0]);
        Assert.Equal("authors=2", results[1]);
    }

    [Fact]
    public void Reference_includes_stream_although_the_principal_has_an_inverse_collection_navigation()
    {
        // Article -> Category -> Category.Articles: the inverse collection of the included principal is never read, so it must not disqualify the query.
        using var db = Seed(MongoQueryMode.NativeOnly);

        AssertStreams(db.Articles.AsNoTracking().Include(a => a.Author));
        AssertStreams(db.Articles.AsNoTracking().Include(a => a.Category));
        AssertStreams(db.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author));
        AssertStreams(db.Articles.Include(a => a.Author));
        AssertStreams(db.Categories.AsNoTracking());
    }

    [Fact]
    public void Entity_with_an_unincluded_collection_navigation_streams_and_leaves_it_unloaded()
    {
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return db.Categories.AsNoTracking().OrderBy(c => c.Id).ToList().Select(c => $"{c.Name}:{c.Articles.Count}").ToList();
            }
        });

        Assert.Equal(["c1:0", "c2:0", "c3:0"], results);
    }

    [Fact]
    public void Collection_include_with_a_reference_include_on_the_root_still_matches_driver_linq()
    {
        // Articles -> (Category, Author): two reference includes (no collection); a regression guard that the gate change did not alter them.
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return db.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author).OrderBy(a => a.Id).ToList()
                    .Select(a => $"{a.Title}/{a.Category?.Name}/{a.Author?.Name}")
                    .ToList();
            }
        });

        Assert.Equal(["a1/c1/ann", "a2/c1/bob", "a3/c1/ann", "a4/c2/bob"], results);
    }

    [Fact]
    public void Collection_lookup_used_only_by_a_predicate_is_skipped_not_read_as_an_include()
    {
        // The Any(..) registers a $lookup so its array is in every returned document, but the shaper does not include the collection:
        // the streaming reader must skip the array, and Articles stays unloaded (an empty list), exactly as on the DOM path.
        using (var db = Seed(MongoQueryMode.NativeOnly))
        {
            AssertStreams(db.Categories.AsNoTracking().Where(c => c.Articles.Any(a => a.Views > 15)));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return db.Categories.AsNoTracking().Where(c => c.Articles.Any(a => a.Views > 15)).OrderBy(c => c.Id).ToList()
                    .Select(c => $"{c.Name}:{c.Articles.Count}")
                    .ToList();
            }
        });

        Assert.Equal(["c1:0", "c2:0"], results);
    }

    [Fact]
    public void Collection_include_beside_a_predicate_on_the_same_collection_matches_driver_linq()
    {
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return Describe(db.Categories.AsNoTracking().Where(c => c.Articles.Any(a => a.Views > 15)).Include(c => c.Articles).ToList()).ToList();
            }
        });

        Assert.Equal(ExpectedGraph[..2], results);
    }

    [Fact]
    public void Include_on_a_query_that_projects_does_not_stream_through_the_entity_reader()
    {
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            var (_, db) = SeedBlog(mode);
            using (db)
            {
                return db.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => new { c.Name, N = c.Articles.Count })
                    .AsEnumerable().Select(r => $"{r.Name}:{r.N}").ToList();
            }
        });

        Assert.Equal(["c1:3", "c2:1", "c3:0"], results);
    }
}
