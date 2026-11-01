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
/// A list of another collection's entities beside other values of a projection (<c>new { c.Id, Articles = c.Articles.Where(…).ToList() }</c>) is
/// the array of a correlated <c>$lookup</c>, read straight off the cursor. Its entities are tracked once like any other, and one the context already
/// tracks comes back as that instance.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCorrelatedCollectionProjectionTests(TemporaryDatabaseFixture database)
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
            mb.Entity<Category>().ToCollection("CorrCategories" + suffix);
            mb.Entity<Article>(b =>
            {
                b.ToCollection("CorrArticles" + suffix);
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
        database.MongoDatabase.GetCollection<BsonDocument>("CorrCategories" + suffix).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Name", "c1" } },
            new BsonDocument { { "_id", 2 }, { "Name", "c2" } },
            new BsonDocument { { "_id", 3 }, { "Name", "c3" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>("CorrArticles" + suffix).InsertMany(
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
            var rowType = enumerator.GetType().GetGenericArguments()[0];
            Assert.True(
                MongoProjectedRowState.IsRowType(rowType),
                $"Expected the streaming projection reader, but the cursor yields {rowType}.");
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_filtered_list_beside_a_scalar_streams_and_tracks_each_entity_once(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var categories = tracked ? db.Categories.AsTracking() : db.Categories.AsNoTracking();
            var rows = categories.OrderBy(c => c.Id)
                .Select(c => new { c.Id, Articles = c.Articles.Where(a => a.Title != "t2").ToList() }).ToList();
            return new List<string>
            {
                string.Join(",", rows.Select(r => r.Id + ":" + string.Join("+", r.Articles.OrderBy(a => a.Id).Select(a => a.Title)))),
                $"entries={db.ChangeTracker.Entries().Count()}",
            };
        });

        Assert.Equal("1:t1,2:t3,3:", results[0]);
        Assert.Equal(tracked ? "entries=2" : "entries=0", results[1]);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        var query = tracked ? streaming.Categories.AsTracking() : streaming.Categories.AsNoTracking();
        AssertStreams(query.Select(c => new { c.Id, Articles = c.Articles.Where(a => a.Title != "t2").ToList() }));
    }

    [Fact]
    public void An_entity_the_context_already_tracks_comes_back_as_that_instance()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var first = db.Articles.AsTracking().Single(a => a.Id == 1);
            var rows = db.Categories.AsTracking().OrderBy(c => c.Id)
                .Select(c => new { c.Id, Articles = c.Articles.Where(a => a.Id < 3).ToList() }).ToList();
            return new List<string>
            {
                $"tracked={ReferenceEquals(first, rows[0].Articles.Single(a => a.Id == 1))}",
                $"entries={db.ChangeTracker.Entries().Count()}",
            };
        });

        Assert.Equal(["tracked=True", "entries=2"], results);
    }
}
