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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A grouping's result documents are flat (the key parts and accumulators under their aliases), so a grouping whose shaper reads them
/// only through simple scalar reads streams like a scalar projection: the cursor yields typed rows and the shaper runs per row. Values,
/// null and missing keys, and the groupings that must stay on the DOM shaper (list-valued elements) must be exactly what the DOM produced.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStreamingGroupByTests(TemporaryDatabaseFixture database)
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
        public int? Rating { get; set; }
        public int CategoryId { get; set; }
        public Category? Category { get; set; }
        public int AuthorId { get; set; }
    }

    private sealed class BlogContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Article> Articles => Set<Article>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Category>().ToCollection("Categories" + suffix);
            modelBuilder.Entity<Article>(b =>
            {
                b.ToCollection("Articles" + suffix);
                b.HasOne(a => a.Category).WithMany(c => c.Articles).HasForeignKey(a => a.CategoryId);
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

    // Categories 1-3; twelve articles with ragged ratings (several null) so a nullable key has a null group.
    private BlogContext Seed(MongoQueryMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using (var seed = Context(MongoQueryMode.Native, suffix))
        {
            seed.Categories.AddRange(
                new Category { Id = 1, Name = "news" }, new Category { Id = 2, Name = "tech" }, new Category { Id = 3, Name = "misc" });
            seed.SaveChanges();
            for (var i = 1; i <= 12; i++)
            {
                seed.Articles.Add(new Article
                {
                    Id = i,
                    Title = "t" + i,
                    Views = i * 10,
                    Rating = i % 4 == 0 ? null : i % 4 + 2,
                    CategoryId = i % 3 + 1,
                    AuthorId = i % 2 + 1,
                });
            }

            seed.SaveChanges();
        }

        return Context(mode, suffix);
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
            rowType.IsGenericType && rowType.GetGenericTypeDefinition() == typeof(MongoProjectedRow<>),
            $"Expected the streaming reader, but the cursor yields {rowType}.");
    }

    private static void AssertDom<T>(IQueryable<T> query)
        => Assert.Equal(typeof(BsonDocument), CursorRowType(query));

    // The grouping's values, in a form both modes can be compared in (rows are ordered by the caller).
    private List<string> Run<T>(MongoQueryMode mode, Func<BlogContext, IQueryable<T>> query, Func<T, string> format, out Type rowType)
    {
        using var db = Seed(mode);
        var q = query(db);
        rowType = CursorRowType(q);
        return q.AsEnumerable().Select(format).ToList();
    }

    private List<string> NativeAndParity<T>(
        Func<BlogContext, IQueryable<T>> query, Func<T, string> format, bool streams = true, bool unordered = false)
    {
        var native = Run(MongoQueryMode.NativeOnly, query, format, out var rowType);
        var driver = Run(MongoQueryMode.DriverLinq, query, format, out _);

        Assert.Equal(unordered ? driver.Order().ToList() : driver, unordered ? native.Order().ToList() : native);
        Assert.NotEmpty(native);

        var streamed = rowType.IsGenericType && rowType.GetGenericTypeDefinition() == typeof(MongoProjectedRow<>);
        Assert.True(streamed == streams, streams ? $"Expected streaming, cursor yields {rowType}." : $"Expected the DOM shaper, cursor yields {rowType}.");
        return native;
    }

    [Fact]
    public void Count_and_accumulators_by_a_scalar_key_stream_and_match_driver_linq()
    {
        var results = NativeAndParity(
            db => db.Articles.AsNoTracking().GroupBy(a => a.CategoryId)
                .Select(g => new { g.Key, N = g.Count(), Sum = g.Sum(a => a.Views), Min = g.Min(a => a.Views), Max = g.Max(a => a.Views), Avg = g.Average(a => a.Views) })
                .OrderBy(r => r.Key),
            r => $"{r.Key}|{r.N}|{r.Sum}|{r.Min}|{r.Max}|{r.Avg}");

        // CategoryId = i % 3 + 1 over Views = 10 * i: category 1 holds i = 3, 6, 9, 12; category 2 holds 1, 4, 7, 10; category 3 holds 2, 5, 8, 11.
        Assert.Equal(["1|4|300|30|120|75", "2|4|220|10|100|55", "3|4|260|20|110|65"], results);
    }

    [Fact]
    public void Composite_key_streams_and_matches_driver_linq()
    {
        var results = NativeAndParity(
            db => db.Articles.AsNoTracking().GroupBy(a => new { a.CategoryId, a.AuthorId })
                .Select(g => new { g.Key.CategoryId, g.Key.AuthorId, N = g.Count() })
                .OrderBy(r => r.CategoryId).ThenBy(r => r.AuthorId),
            r => $"{r.CategoryId}/{r.AuthorId}:{r.N}");

        Assert.Equal(6, results.Count);
        Assert.Equal(12, results.Sum(r => int.Parse(r[(r.IndexOf(':') + 1)..])));
    }

    [Fact]
    public void Nullable_key_has_a_null_group_that_reads_back_as_null_in_both_modes()
    {
        var results = NativeAndParity(
            db => db.Articles.AsNoTracking().GroupBy(a => a.Rating).Select(g => new { g.Key, N = g.Count() }).OrderBy(r => r.Key),
            r => $"{r.Key?.ToString() ?? "<null>"}:{r.N}");

        Assert.Contains("<null>:3", results);
    }

    [Fact]
    public void Having_filter_over_the_groups_streams_and_matches_driver_linq()
    {
        var results = NativeAndParity(
            db => db.Articles.AsNoTracking().GroupBy(a => a.CategoryId).Where(g => g.Sum(a => a.Views) > 250)
                .Select(g => new { g.Key, Total = g.Sum(a => a.Views) }).OrderBy(r => r.Key),
            r => $"{r.Key}:{r.Total}");

        Assert.Equal(["1:300", "3:260"], results);
    }

    [Fact]
    public void Filter_before_the_grouping_streams_and_matches_driver_linq()
    {
        var results = NativeAndParity(
            db => db.Articles.AsNoTracking().Where(a => a.Views > 40).GroupBy(a => a.AuthorId).Select(g => new { g.Key, N = g.Count() }).OrderBy(r => r.Key),
            r => $"{r.Key}:{r.N}");

        Assert.Equal(["1:4", "2:4"], results);
    }

    [Fact]
    public void Ordering_and_paging_over_the_groups_stream_and_match_driver_linq()
    {
        NativeAndParity(
            db => db.Articles.AsNoTracking().GroupBy(a => a.Id % 6).Select(g => new { g.Key, Total = g.Sum(a => a.Views) })
                .OrderByDescending(r => r.Total).ThenBy(r => r.Key).Skip(1).Take(3),
            r => $"{r.Key}:{r.Total}");
    }

    [Fact]
    public void Joined_key_streams_and_matches_driver_linq()
    {
        var results = NativeAndParity(
            // No server-side ordering: an OrderBy after a grouping over a join scope is not translated natively.
            db => db.Articles.AsNoTracking().GroupBy(a => a.Category!.Name).Select(g => new { g.Key, N = g.Count(), Views = g.Sum(a => a.Views) }),
            r => $"{r.Key}:{r.N}:{r.Views}",
            unordered: true);

        Assert.Equal(["misc:4:260", "news:4:300", "tech:4:220"], results.Order().ToList());
    }

    [Fact]
    public void List_valued_group_element_streams_as_a_list_slot_and_matches_driver_linq()
    {
        var results = NativeAndParity(
            db => db.Articles.AsNoTracking().Where(a => a.Id <= 6).GroupBy(a => a.AuthorId).Select(g => new { g.Key, Titles = g.Select(a => a.Title).ToList() }).OrderBy(r => r.Key),
            r => $"{r.Key}:{string.Join(',', r.Titles.Order())}");

        Assert.Equal(["1:t2,t4,t6", "2:t1,t3,t5"], results);
    }

    [Fact]
    public void Empty_grouping_yields_no_rows_and_a_scalar_count_of_groups_still_works()
    {
        using var db = Seed(MongoQueryMode.NativeOnly);

        Assert.Empty(db.Articles.AsNoTracking().Where(a => a.Views > 1000).GroupBy(a => a.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToList());
        Assert.Equal(3, db.Articles.AsNoTracking().GroupBy(a => a.CategoryId).Count());
    }

    [Fact]
    public async Task Async_read_of_a_streaming_grouping_matches_and_first_works_in_native_mode()
    {
        using (var db = Seed(MongoQueryMode.NativeOnly))
        {
            var grouped = db.Articles.AsNoTracking().GroupBy(a => a.CategoryId).Select(g => new { g.Key, N = g.Count() }).OrderBy(r => r.Key);
            Assert.Equal(["1:4", "2:4", "3:4"], (await grouped.ToListAsync()).Select(r => $"{r.Key}:{r.N}"));
        }

        // First() over a grouping is not translated natively (NativeOnly refuses it), so Native runs it through the driver.
        using (var db = Seed(MongoQueryMode.Native))
        {
            Assert.Equal(1, db.Articles.AsNoTracking().GroupBy(a => a.CategoryId).Select(g => new { g.Key, N = g.Count() }).OrderBy(r => r.Key).First().Key);
        }
    }

    [Fact]
    public void Malformed_accumulator_input_fails_only_its_own_group_row_when_it_is_reached()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using (var seed = Context(MongoQueryMode.Native, suffix))
        {
            seed.Articles.AddRange(
                new Article { Id = 1, Views = 1, CategoryId = 1 }, new Article { Id = 2, Views = 2, CategoryId = 2 }, new Article { Id = 3, Views = 3, CategoryId = 3 });
            seed.SaveChanges();
        }

        using var db = Context(MongoQueryMode.NativeOnly, suffix);
        var articles = db.Database.GetService<MongoDB.EntityFrameworkCore.Storage.IMongoClientWrapper>().GetCollection<BsonDocument>("Articles" + suffix);
        articles.UpdateOne(new BsonDocument("_id", 3), new BsonDocument("$set", new BsonDocument("CategoryId", "not an int")));

        using var enumerator = db.Articles.AsNoTracking().GroupBy(a => a.CategoryId).Select(g => new { g.Key, N = g.Count() }).AsEnumerable().GetEnumerator();

        // Group order is the server's, so only assert that reading never fails before the malformed key's own row, and that it does fail there.
        var read = 0;
        Assert.ThrowsAny<Exception>(() =>
        {
            while (enumerator.MoveNext())
            {
                read++;
            }
        });
        Assert.InRange(read, 0, 2);
    }
}
