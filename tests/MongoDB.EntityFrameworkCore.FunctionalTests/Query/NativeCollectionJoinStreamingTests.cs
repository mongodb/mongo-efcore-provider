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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A join onto a collection navigation (<c>Categories.Join(Articles, …)</c>) repeats the outer entity once per match. Its result, whether the outer
/// entity, the inner one or both, is read straight off the cursor; the entity that repeats must come back as one tracked instance.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCollectionJoinStreamingTests(TemporaryDatabaseFixture database)
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
        public int CategoryId { get; set; }
        public Category? Category { get; set; }
    }

    private sealed class ShopContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Article> Articles => Set<Article>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Category>().ToCollection("JoinedCategories" + suffix);
            mb.Entity<Article>(b =>
            {
                b.ToCollection("JoinedArticles" + suffix);
                b.HasOne(a => a.Category).WithMany(c => c.Articles).HasForeignKey(a => a.CategoryId);
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

    // Category 1 has articles 1 and 2, category 2 has article 3, category 3 has none.
    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>("JoinedCategories" + suffix).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Name", "c1" } },
            new BsonDocument { { "_id", 2 }, { "Name", "c2" } },
            new BsonDocument { { "_id", 3 }, { "Name", "c3" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>("JoinedArticles" + suffix).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Title", "t1" }, { "CategoryId", 1 } },
            new BsonDocument { { "_id", 2 }, { "Title", "t2" }, { "CategoryId", 1 } },
            new BsonDocument { { "_id", 3 }, { "Title", "t3" }, { "CategoryId", 2 } }
        ]);
        return suffix;
    }

    private static void AssertStreams<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            Assert.NotEqual(typeof(BsonDocument), enumerator.GetType().GetGenericArguments()[0]);
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pair_of_entities_streams_and_the_repeated_outer_is_one_instance(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var rows = categories.Join(db.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a }).OrderBy(x => x.a.Id).ToList();
            return new List<string>
            {
                string.Join(",", rows.Select(r => r.c.Name + "/" + r.a.Title)),
                $"shared={ReferenceEquals(rows[0].c, rows[1].c)}",
                $"entries={db.ChangeTracker.Entries().Count()}",
            };
        });

        Assert.Equal("c1/t1,c1/t2,c2/t3", results[0]);
        Assert.Equal(tracked ? "shared=True" : "shared=False", results[1]);
        Assert.Equal(tracked ? "entries=5" : "entries=0", results[2]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.Join(streaming.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_the_inner_or_only_the_outer_streams(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var inner = categories.Join(db.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .Where(x => x.c.Name == "c1").Select(x => x.a).OrderBy(a => a.Id).ToList();
            var outer = categories.Join(db.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .Where(x => x.a.Title != "t2").Select(x => x.c).OrderBy(c => c.Id).ToList();
            return new List<string>
            {
                string.Join(",", inner.Select(a => a.Title)),
                string.Join(",", outer.Select(c => c.Name)),
            };
        });

        Assert.Equal("t1,t2", results[0]);
        Assert.Equal("c1,c2", results[1]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.Join(streaming.Articles, c => c.Id, a => a.CategoryId, (c, a) => a));
        AssertStreams(streaming.Categories.Join(streaming.Articles, c => c.Id, a => a.CategoryId, (c, a) => c));
    }

#if !EF8 && !EF9
    [Fact]
    public void A_left_join_pair_has_a_null_inner_for_an_unmatched_outer()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Categories.LeftJoin(db.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a })
                .OrderBy(x => x.c.Id).ThenBy(x => x.a!.Id).ToList()
                .Select(r => r.c.Name + "/" + (r.a?.Title ?? "-")).ToList();
        });

        Assert.Equal(["c1/t1", "c1/t2", "c2/t3", "c3/-"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Categories.LeftJoin(streaming.Articles, c => c.Id, a => a.CategoryId, (c, a) => new { c, a }));
    }
#endif
}
