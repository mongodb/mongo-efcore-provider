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
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Projection-binding fallbacks that must not crash:
///
/// Bare <c>First</c>/<c>FirstOrDefault</c>/<c>Single</c>/<c>SingleOrDefault</c>/<c>Any</c> over a materialized
/// owned-collection leaf (e.g. <c>Select(b => b.Posts.First().Heading)</c>) fold client-side under
/// <see cref="MongoQueryMode.Native"/>, like the Count/LongCount arms in <see cref="NativeOwnedCollectionCountTests"/>;
/// they are not natively representable.
///
/// A filtered <c>Count(pred)</c> under a pure arithmetic/cast spine (e.g.
/// <c>Select(b => b.Posts.Count(p => p.Rank > 0) * 2)</c>) is rebuilt via <c>IsReachableThroughArithmeticSpine</c>
/// rather than requiring the Count to be the selector body.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeProjectionReducerTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        public int? Rank { get; set; }
        public string? Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private static BsonDocument PostDoc(int? rank, string? heading)
        => new()
        {
            { "Rank", rank.HasValue ? rank.Value : BsonNull.Value },
            { "Heading", heading is null ? BsonNull.Value : heading }
        };

    private static BsonDocument Row(string title, params BsonDocument[] posts)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Posts", new BsonArray(posts) }
        };

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // One blog with exactly one post ("only") and one with two ("multi"), so First/FirstOrDefault, Single/
    // SingleOrDefault (behind Where(Count == 1)/Where(Count <= 1)) and Any all have results from the same seed.
    private IMongoCollection<Blog> SeedForReducers(string name)
        => Seed(
            name,
            Row("only", PostDoc(1, "solo")),
            Row("multi", PostDoc(1, "first"), PostDoc(2, "second")));

    [Theory]
    [InlineData("First")]
    [InlineData("FirstOrDefault")]
    [InlineData("Single")]
    [InlineData("SingleOrDefault")]
    public void Bare_collection_reducer_projection_leaf_no_longer_throws(string reducerName)
    {
        var collection = SeedForReducers(reducerName);
        // Native, not NativeOnly: a client-side fold, like the Count/LongCount arms.
        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var query = reducerName switch
        {
            "First" => db.Entities.AsNoTracking().Select(b => b.Posts.First().Heading),
            "FirstOrDefault" => db.Entities.AsNoTracking().Select(b => b.Posts.FirstOrDefault()!.Heading),
            "Single" => db.Entities.AsNoTracking().Where(b => b.Posts.Count == 1)
                .Select(b => b.Posts.Single().Heading),
            "SingleOrDefault" => db.Entities.AsNoTracking().Where(b => b.Posts.Count <= 1)
                .Select(b => b.Posts.SingleOrDefault()!.Heading),
            _ => throw new InvalidOperationException()
        };

        // Exact values, so a fold returning the wrong element (last, or null) fails. There is no $sort (whole-entity
        // fetch + client-side fold), so First sees insertion order: "only" ("solo") then "multi" ("first",
        // "second"). Single/SingleOrDefault are filtered to the one qualifying blog.
        var expected = reducerName switch
        {
            "First" or "FirstOrDefault" => new[] { "solo", "first" },
            "Single" or "SingleOrDefault" => new[] { "solo" },
            _ => throw new InvalidOperationException()
        };

        var result = query.ToList();
        Assert.Equal(expected, result);
    }

    // Any needs an empty collection in the fixture to distinguish a real Any() from a hardcoded `true` (First
    // would throw on an empty list, and Single is filtered to one row), so it gets its own seed.
    private IMongoCollection<Blog> SeedForAny(string name)
        => Seed(
            name,
            Row("only", PostDoc(1, "solo")),
            Row("multi", PostDoc(1, "first"), PostDoc(2, "second")),
            Row("empty"));

    [Fact]
    public void Bare_collection_Any_projection_leaf_no_longer_throws()
    {
        var collection = SeedForAny(nameof(Bare_collection_Any_projection_leaf_no_longer_throws));
        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var result = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Any()).ToList();

        // By Title: empty, multi, only. A hardcoded `true` fails on the "empty" row.
        Assert.Equal([false, true, true], result);
    }

    [Fact]
    public void Filtered_count_as_operand_of_arithmetic_no_longer_hard_fails()
    {
        // The Count call is an operand of `*`, not the selector body, so the fallback shaper must reach it through
        // the arithmetic spine. It still isn't native: NativeProjectionBinder's IsArrayFreeComputedSubtree declines
        // any arithmetic subtree containing a filtered count, so Route stays Fallback (NativeOnly throws), like the
        // unfiltered `b.Posts.Count * 2`.
        var collection = Seed(
            nameof(Filtered_count_as_operand_of_arithmetic_no_longer_hard_fails),
            Row("none", PostDoc(-1, "a")),
            Row("one", PostDoc(1, "a"), PostDoc(-1, "b")),
            Row("two", PostDoc(1, "a"), PostDoc(2, "b")));

        // By Title: none, one, two.
        int[] expected = [0, 2, 4];

        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var results = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => b.Posts.Count(p => p.Rank > 0) * 2)
                .ToList();
            Assert.Equal(expected, results);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            var results = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => b.Posts.Count(p => p.Rank > 0) * 2)
                .ToList();
            Assert.Equal(expected, results);
        }

        // Pins that this shape is a client-side fold, not natively $project-representable.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Rank > 0) * 2)
                    .ToList());
        }
    }
}
