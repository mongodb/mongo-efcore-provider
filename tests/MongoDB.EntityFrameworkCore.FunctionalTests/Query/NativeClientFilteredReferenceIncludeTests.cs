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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A document database has no referential constraint, so a <em>required</em> reference means "not null if present", and a document can
/// point at one that is not there. An <c>Include</c> of it returns only the documents whose reference resolved. The cursor does that
/// filtering itself, reading the <c>$lookup</c>'s array as it is, rather than have the server <c>$match</c> on it (which costs about as much
/// as the lookup): the pipeline has neither the <c>$match</c> nor the <c>$set</c> then, unless a stage after the lookups depends on the rows
/// that would have been dropped (paging, a count), in which case the server keeps filtering. The rows returned are the same either way.
/// </summary>
public class NativeClientFilteredReferenceIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Article
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int CategoryId { get; set; }
        public Category Category { get; set; } = null!;
        public int AuthorId { get; set; }
        public Author Author { get; set; } = null!;
    }

    private class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Author
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class ArticleContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Article> Articles => Set<Article>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Article>(b =>
            {
                b.ToCollection("ClientFilterArticles" + suffix);
                b.HasOne(a => a.Category).WithMany().HasForeignKey(a => a.CategoryId);
                b.HasOne(a => a.Author).WithMany().HasForeignKey(a => a.AuthorId);
            });
            mb.Entity<Category>().ToCollection("ClientFilterCategories" + suffix);
            mb.Entity<Author>().ToCollection("ClientFilterAuthors" + suffix);
        }
    }

    private ArticleContext Db(MongoQueryMode mode, string suffix, ILoggerFactory? loggerFactory = null)
    {
        var builder = new DbContextOptionsBuilder<ArticleContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        if (loggerFactory != null)
        {
            builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
        }

        return new ArticleContext(builder.Options, suffix);
    }

    // Articles 3, 4, 5 and 7 point at a category or an author that does not exist; category 3 is used only by article 7, which is dropped.
    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var categories = database.MongoDatabase.GetCollection<BsonDocument>("ClientFilterCategories" + suffix);
        var authors = database.MongoDatabase.GetCollection<BsonDocument>("ClientFilterAuthors" + suffix);
        var articles = database.MongoDatabase.GetCollection<BsonDocument>("ClientFilterArticles" + suffix);

        categories.InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Name", "c1" } },
            new BsonDocument { { "_id", 2 }, { "Name", "c2" } },
            new BsonDocument { { "_id", 3 }, { "Name", "c3" } }
        ]);
        authors.InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Name", "a1" } },
            new BsonDocument { { "_id", 2 }, { "Name", "a2" } }
        ]);
        articles.InsertMany(
        [
            Article(1, 1, 1), Article(2, 2, 2), Article(3, 99, 1), Article(4, 1, 99), Article(5, 99, 99), Article(6, 1, 2), Article(7, 3, 99)
        ]);
        return suffix;

        static BsonDocument Article(int id, int categoryId, int authorId)
            => new() { { "_id", id }, { "Title", "t" + id }, { "CategoryId", categoryId }, { "AuthorId", authorId } };
    }


    private static string Describe(Article a) => $"{a.Id}:{a.Category.Name}:{a.Author.Name}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Documents_whose_required_reference_is_missing_are_not_returned(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var query = tracked ? db.Articles.AsTracking() : db.Articles.AsNoTracking();
            return query.OrderBy(a => a.Id).Include(a => a.Category).Include(a => a.Author).ToList().Select(Describe).ToList();
        });

        Assert.Equal(["1:c1:a1", "2:c2:a2", "6:c1:a2"], results);
    }

    [Fact]
    public async Task The_async_query_drops_them_too()
    {
        var suffix = Seed();
        await using var db = Db(MongoQueryMode.NativeOnly, suffix);

        var results = await db.Articles.AsNoTracking().OrderBy(a => a.Id).Include(a => a.Category).Include(a => a.Author).ToListAsync();

        Assert.Equal(["1:c1:a1", "2:c2:a2", "6:c1:a2"], results.Select(Describe));
    }

    [Fact]
    public void A_tracked_query_tracks_nothing_of_a_dropped_document()
    {
        var suffix = Seed();
        using var db = Db(MongoQueryMode.NativeOnly, suffix);

        var results = db.Articles.AsTracking().OrderBy(a => a.Id).Include(a => a.Category).Include(a => a.Author).ToList();

        Assert.Equal(3, results.Count);
        Assert.Same(results[0].Category, results[2].Category);

        // Three articles, categories 1 and 2, authors 1 and 2: category 3 belongs to a dropped article and is not tracked.
        Assert.Equal(7, db.ChangeTracker.Entries().Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries<Category>(), e => e.Entity.Id == 3);
    }

    [Fact]
    public void The_pipeline_leaves_the_filtering_to_the_cursor()
    {
        var suffix = Seed();
        var (loggerFactory, spy) = SpyLoggerProvider.Create();
        using var db = Db(MongoQueryMode.NativeOnly, suffix, loggerFactory);

        var results = db.Articles.AsNoTracking().OrderBy(a => a.Id).Include(a => a.Category).Include(a => a.Author).ToList();

        Assert.Equal(3, results.Count);
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$lookup", mql);
        Assert.DoesNotContain("$match", mql);
        Assert.DoesNotContain("$arrayElemAt", mql);
    }

    [Fact]
    public void Paging_runs_before_the_lookups_and_so_does_not_count_a_dropped_document()
    {
        var suffix = Seed();

        // Skip(1).Take(2) pages over all seven documents (2 and 3), and document 3 then has no category.
        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author).OrderBy(a => a.Id).Skip(1).Take(2)
                .ToList().Select(Describe).ToList();
        });

        Assert.Equal(["2:c2:a2"], results);
    }

    [Fact]
    public void A_stage_that_reads_the_lookup_keeps_the_filtering_on_the_server()
    {
        var suffix = Seed();
        var (loggerFactory, spy) = SpyLoggerProvider.Create();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix, mode == MongoQueryMode.NativeOnly ? loggerFactory : null);
            return db.Articles.AsNoTracking().Include(a => a.Category).OrderBy(a => a.Category.Name).ThenBy(a => a.Id)
                .ToList().Select(a => $"{a.Id}:{a.Category.Name}").ToList();
        });

        Assert.Equal(["1:c1", "4:c1", "6:c1", "2:c2", "7:c3"], results);
        Assert.Contains("$exists", spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));
    }

    [Fact]
    public void A_first_that_would_be_a_dropped_document_finds_none()
    {
        var suffix = Seed();
        using var db = Db(MongoQueryMode.NativeOnly, suffix);

        Assert.Null(db.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author).FirstOrDefault(a => a.Id == 3));
        Assert.Throws<InvalidOperationException>(
            () => db.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author).First(a => a.Id == 4));
        Assert.Equal(
            "6:c1:a2",
            Describe(db.Articles.AsNoTracking().Include(a => a.Category).Include(a => a.Author).Single(a => a.Id == 6)));
    }

    [Fact]
    public void One_reference_include_drops_only_what_that_reference_misses()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Articles.AsNoTracking().OrderBy(a => a.Id).Include(a => a.Category).ToList()
                .Select(a => $"{a.Id}:{a.Category.Name}").ToList();
        });

        Assert.Equal(["1:c1", "2:c2", "4:c1", "6:c1", "7:c3"], results);
    }
}
