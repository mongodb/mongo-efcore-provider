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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// The inner entity of an explicit <c>Join</c> / <c>LeftJoin</c>, projected beside the outer one (<c>new { a, c }</c>) or a scalar, is read by the
/// cursor straight into the entity. The entities, an unmatched left-join inner, and the tracking of both sides must read exactly as the DOM shaper
/// read them.
/// </summary>
public class NativeJoinInnerEntityProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Article
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int CategoryId { get; set; }
    }

    private class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class JoinContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Article> Articles => Set<Article>();
        public DbSet<Category> Categories => Set<Category>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Article>().ToCollection("JoinArticles" + suffix);
            mb.Entity<Category>().ToCollection("JoinCategories" + suffix);
        }
    }

    private JoinContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<JoinContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options,
            suffix);

    // Articles 1, 2 and 4 share categories 1 and 2; article 3 points at a category that does not exist.
    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>("JoinCategories" + suffix).InsertMany(
            [new BsonDocument { { "_id", 1 }, { "Name", "c1" } }, new BsonDocument { { "_id", 2 }, { "Name", "c2" } }]);
        database.MongoDatabase.GetCollection<BsonDocument>("JoinArticles" + suffix).InsertMany(
        [
            Article(1, 1), Article(2, 2), Article(3, 9), Article(4, 1)
        ]);
        return suffix;

        static BsonDocument Article(int id, int categoryId)
            => new() { { "_id", id }, { "Title", "t" + id }, { "CategoryId", categoryId } };
    }

    private static void AssertStreams<T>(IQueryable<T> query)
    {
        var enumerator = ((System.Collections.IEnumerable)query).GetEnumerator();
        try
        {
            Assert.True(
                MongoProjectedRowState.IsRowType(enumerator.GetType().GetGenericArguments()[0]),
                $"Expected the streaming projection reader, but the cursor yields {enumerator.GetType().GetGenericArguments()[0]}.");
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Join_pair_of_entities_streams_and_shares_the_tracked_inner(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var articles = tracked ? db.Articles.AsTracking() : db.Articles.AsNoTracking();
            var rows = articles.Join(db.Categories, a => a.CategoryId, c => c.Id, (a, c) => new { a, c }).OrderBy(x => x.a.Id).ToList();
            return new List<string>
            {
                $"shared={ReferenceEquals(rows[0].c, rows[2].c)}",
                $"entries={db.ChangeTracker.Entries().Count()}",
                string.Join(",", rows.Select(r => $"{r.a.Id}:{r.c.Name}")),
            };
        });

        Assert.Equal(tracked ? "shared=True" : "shared=False", results[0]);
        Assert.Equal(tracked ? "entries=5" : "entries=0", results[1]);
        Assert.Equal("1:c1,2:c2,4:c1", results[2]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Articles.Join(streaming.Categories, a => a.CategoryId, c => c.Id, (a, c) => new { a, c }));
    }

#if !EF8 && !EF9
    [Fact]
    public void Left_join_pair_has_a_null_inner_for_an_unmatched_row()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Articles.LeftJoin(db.Categories, a => a.CategoryId, c => c.Id, (a, c) => new { a, c }).OrderBy(x => x.a.Id).ToList()
                .Select(r => $"{r.a.Id}:{r.c?.Name ?? "-"}").ToList();
        });

        Assert.Equal(["1:c1", "2:c2", "3:-", "4:c1"], results);
    }
#endif

    [Fact]
    public void Join_scalar_beside_the_inner_entity_and_a_later_where_match_driver_linq()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var mixed = db.Articles.AsNoTracking().Join(db.Categories, a => a.CategoryId, c => c.Id, (a, c) => new { a.Title, c })
                .OrderBy(x => x.Title).ToList().Select(r => $"{r.Title}:{r.c.Name}");
            var filtered = db.Articles.AsNoTracking().Join(db.Categories, a => a.CategoryId, c => c.Id, (a, c) => new { a, c })
                .Where(x => x.c.Name == "c1").OrderBy(x => x.a.Id).ToList().Select(r => $"{r.a.Id}:{r.c.Name}");
            return mixed.Concat(filtered).ToList();
        });

        Assert.Equal(["t1:c1", "t2:c2", "t4:c1", "1:c1", "4:c1"], results);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Bare_inner_entity_streams_and_tracks_each_category_once(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var articles = tracked ? db.Articles.AsTracking() : db.Articles.AsNoTracking();
            var rows = articles.OrderBy(a => a.Id).Join(db.Categories, a => a.CategoryId, c => c.Id, (a, c) => c).ToList();
            return new List<string>
            {
                $"shared={ReferenceEquals(rows[0], rows[2])}",
                $"entries={db.ChangeTracker.Entries().Count()}",
                string.Join(",", rows.Select(c => c.Name)),
            };
        });

        Assert.Equal(tracked ? "shared=True" : "shared=False", results[0]);
        Assert.Equal(tracked ? "entries=2" : "entries=0", results[1]);
        Assert.Equal("c1,c2,c1", results[2]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Articles.Join(streaming.Categories, a => a.CategoryId, c => c.Id, (a, c) => c));
    }

#if !EF8 && !EF9
    [Fact]
    public void Bare_inner_entity_of_a_left_join_is_null_when_unmatched()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Articles.AsNoTracking().OrderBy(a => a.Id).LeftJoin(db.Categories, a => a.CategoryId, c => c.Id, (a, c) => c).ToList()
                .Select(c => c?.Name ?? "-").ToList();
        });

        Assert.Equal(["c1", "c2", "-", "c1"], results);
    }
#endif
}
