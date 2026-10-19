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
/// EF-427: two independent gaps in the native projection-binding visitor.
///
/// Item 1 — the bare (no-predicate) <c>First</c>/<c>FirstOrDefault</c>/<c>Single</c>/<c>SingleOrDefault</c>/
/// <c>Any</c> reducers over a materialized owned-collection navigation leaf (e.g.
/// <c>Select(b => b.Posts.First().Heading)</c>) used to hard-fail with a raw BCL <see cref="ArgumentException"/>
/// in every <see cref="MongoQueryMode"/> — the same stranded-rebuild gap the sibling Count/LongCount arms in
/// <see cref="NativeOwnedCollectionCountTests"/> already fixed for those two methods, but never extended to
/// these five siblings. This is a graceful CLIENT-SIDE fold once fixed (Native, not NativeOnly — this shape
/// does not become natively $project-representable, it just stops crashing).
///
/// Item 2 — a filtered <c>Count(pred)</c> reachable from the projection root only through a pure
/// arithmetic/cast spine (e.g. <c>Select(b => b.Posts.Count(p => p.Rank > 0) * 2)</c>) used to hard-fail with
/// <see cref="InvalidOperationException"/> in every mode, because the rebuild arm required the Count call to
/// BE the selector body via <c>ReferenceEquals</c>. Widened via <c>IsReachableThroughArithmeticSpine</c>.
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

    // Every blog carries at least one post, and one blog carries EXACTLY one post ("only"), the other carries
    // two ("multi") — so First/FirstOrDefault (any post count > 0), Single/SingleOrDefault (behind a
    // Where(Count == 1)/Where(Count <= 1) filter, per the ticket's own repro) and Any all have a non-empty
    // result set to assert against from the SAME seed.
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
        // Native, not NativeOnly: this shape stays a graceful client-side fold, like the adjacent
        // Count/LongCount arms this task's rebuild arms sit next to.
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

        // Previously threw ArgumentException from Expression.Call's own BCL argument-assignability
        // validation (List<Post> is not assignable to IQueryable<Post>) before this fix.
        //
        // EXACT expected values, not just non-emptiness: a fold that returns the WRONG element (e.g. last
        // instead of first, or null) must fail this. The seed rows are inserted "only" then "multi" and read
        // back via a plain collection scan with no $sort (this shape is a native whole-entity fetch + a
        // CLIENT-SIDE fold, not a sorted query), so natural/insertion order is what First/FirstOrDefault see:
        // "only" (single post "solo") then "multi" (posts "first","second"). Single/SingleOrDefault are
        // filtered down to the ONE qualifying blog ("only") regardless of order.
        var expected = reducerName switch
        {
            "First" or "FirstOrDefault" => new[] { "solo", "first" },
            "Single" or "SingleOrDefault" => new[] { "solo" },
            _ => throw new InvalidOperationException()
        };

        var result = query.ToList();
        Assert.Equal(expected, result);
    }

    // A dedicated seed for Any: unlike First/FirstOrDefault (which would throw InvalidOperationException on
    // Enumerable.First over an EMPTY materialized list) and Single/SingleOrDefault (filtered to exactly one
    // qualifying row by the test's own Where clause), Any() is well-defined over an empty collection and
    // needs one in the fixture to actually discriminate a real Any() from a hardcoded `true` — mirroring how
    // Ef425InterposedCollectionOperatorTests deliberately seeds an empty array for the same reason.
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

        // Alphabetical order by Title: empty, multi, only. A hardcoded `true` (instead of a real Any() fold)
        // would pass a Contains(true, result) check against this same seed but fails THIS exact-array
        // assertion on the "empty" row.
        Assert.Equal([false, true, true], result);
    }

    [Fact]
    public void Filtered_count_as_operand_of_arithmetic_no_longer_hard_fails()
    {
        // b.Posts.Count(p => p.Rank > 0) * 2 — the Count call is an OPERAND of `*`, not the selector body
        // itself, so ReferenceEquals(methodCallExpression, _translatedRootExpression) used to fail and this
        // arm declined with no graceful fallback (EF-427 item 2), hard-failing with InvalidOperationException
        // in every mode.
        //
        // MEASURED DISPOSITION AFTER THE FIX, corrected from this task's own brief (which predicted a
        // NativeOnly PASS): NativeProjectionBinder's own arithmetic-leaf gate
        // (IsArrayFreeComputedSubtree) is a SEPARATE, untouched component that already declines any
        // arithmetic subtree containing a filtered count — this task only fixes the FALLBACK shaper crash in
        // MongoProjectionBindingExpressionVisitor, not that native-$project admission gate. So this shape's
        // Route stays Fallback (measured: NativeOnly still throws NativeTranslationNotSupportedException
        // below), exactly mirroring the sibling UNFILTERED arithmetic form's own documented asymmetry
        // ("Select(b => b.Posts.Count * 2) is a graceful decline with correct values in the two fallback
        // modes") — the filtered spelling now behaves the same way, just no longer a hard crash.
        var collection = Seed(
            nameof(Filtered_count_as_operand_of_arithmetic_no_longer_hard_fails),
            Row("none", PostDoc(-1, "a")),
            Row("one", PostDoc(1, "a"), PostDoc(-1, "b")),
            Row("two", PostDoc(1, "a"), PostDoc(2, "b")));

        // Alphabetical order by Title: none, one, two.
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

        // Positively pins that this shape is NOT natively $project-representable — it is a graceful
        // CLIENT-SIDE fold, not a native-execution proof (see the disposition note above).
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Rank > 0) * 2)
                    .ToList());
        }
    }
}
