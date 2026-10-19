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
/// EF-358, the STREAMING half. <see cref="ProjectedCollectionNormalizationTests"/> covers the DOM shaper;
/// this class covers the one-pass streaming materializer
/// (<see cref="MongoStreamingEntityMaterializerRewriter"/>), which is a completely separate materialization
/// path and needed its own fix for the same bug.
/// <para>
/// Every other fixture on the EF-358 branch is MASKED against this path, which is how the gap survived nine
/// review rounds: <see cref="ProjectedCollectionNormalizationTests"/>' Blog declares
/// <c>Posts { get; set; } = []</c> AND its Post owns Comments (a navigation on the collection element, which
/// makes the shape streaming-INELIGIBLE, so it routes to the DOM shaper); the OwnedEntityTests cases all use
/// <c>First()</c>, and a reducer is compiled DOM-only (<c>allowStreaming: false</c>); StoredDataStillReadableTests
/// likewise uses <c>First(...)</c>. The ONE shape that actually streams is a whole-entity <c>ToList()</c> over a
/// FLAT owned collection — hence <see cref="FlatBlog"/> below.
/// </para>
/// <para>
/// The model is written to be un-masked in both ways that matter, and each is load-bearing rather than
/// stylistic: <see cref="FlatBlog.Posts"/> carries NO <c>= []</c> field initializer (with one, the CLR default
/// is already an empty collection and the test cannot tell provider normalization from initializer masking),
/// and <see cref="FlatPost"/> carries NO navigation of its own (with one, StreamingEligibility routes the
/// query to the DOM shaper and the test would be vacuously green — asserted explicitly in each test rather
/// than assumed).
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class StreamingCollectionNormalizationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class FlatBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";

        // NO `= []` initializer, ON PURPOSE — see the class doc comment. The CLR default is null, so an
        // empty collection here can only have come from the provider.
        public List<FlatPost> Posts { get; set; }
    }

    public class FlatPost
    {
        // NO navigation of its own, ON PURPOSE — an element navigation makes the shape streaming-ineligible.
        public string Heading { get; set; }
    }

    private static readonly Action<ModelBuilder> FlatBlogModel = mb => mb.Entity<FlatBlog>().OwnsMany(b => b.Posts);

    private static SingleEntityDbContext<FlatBlog> CreateContext(
        IMongoCollection<FlatBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: FlatBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // A null `posts` means the FIELD IS ABSENT; BsonNull.Value means the field is present and explicitly null.
    private static BsonDocument Row(string title, BsonValue? posts)
    {
        var doc = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", title } };
        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    // Titles are chosen so alphabetical order is deterministic: empty < missing < null < two.
    private IMongoCollection<FlatBlog> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            Row("two", new BsonArray { new BsonDocument("Heading", "a"), new BsonDocument("Heading", "b") }),
            Row("empty", new BsonArray()),
            Row("missing", posts: null),
            Row("null", BsonNull.Value)
        ]);
        return database.MongoDatabase.GetCollection<FlatBlog>(coll.CollectionNamespace.CollectionName);
    }

    // Proves the shape this class relies on genuinely takes the streaming path. Asserted, not assumed: a
    // navigation added to FlatPost (or a composite PK, or a TPH base type) would silently route these queries
    // to the DOM shaper and make every test below vacuous — exactly how the streaming gap survived nine
    // review rounds on this branch.
    private static void AssertStreamingEligible(DbContext db)
    {
        var entityType = db.Model.FindEntityType(typeof(FlatBlog))!;
        Assert.True(
            StreamingEligibility.IsEligible(entityType),
            "FlatBlog must be streaming-eligible or these tests do not exercise the streaming materializer.");
    }

    [Fact]
    public void Streamed_whole_entity_normalizes_a_missing_or_null_array_to_an_empty_collection()
    {
        // THE MUTATION PIN for the streaming half of EF-358. Revert the post-loop normalization in
        // MongoStreamingEntityMaterializerRewriter.BuildFillLoop and the `missing` and `null` rows come back
        // with Posts == null. NativeOnly is used deliberately: it is the only reliable "went native" signal,
        // AND it makes an un-streamable shape throw instead of silently degrading to the DOM shaper (which
        // would hide a regression in the very path under test).
        var collection = Seed(nameof(Streamed_whole_entity_normalizes_a_missing_or_null_array_to_an_empty_collection));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        AssertStreamingEligible(db);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();

        Assert.Equal(["empty", "missing", "null", "two"], blogs.Select(b => b.Title).ToArray());
        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Streamed_whole_entity_agrees_with_driver_linq_for_every_array_state()
    {
        // Restoring this agreement is the point of the fix: at branch base every path answered null, and the
        // first two EF-358 edits moved only the DOM path, so Native (streaming) and DriverLinq diverged for a
        // SUPPORTED query. Asserted across all three modes so a future change to either path is caught.
        var collection = Seed(nameof(Streamed_whole_entity_agrees_with_driver_linq_for_every_array_state));

        int?[] Counts(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            if (mode != MongoQueryMode.DriverLinq)
            {
                AssertStreamingEligible(db);
            }

            return db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList()
                .Select(b => b.Posts?.Count).ToArray();
        }

        var driverLinq = Counts(MongoQueryMode.DriverLinq);

        Assert.Equal(new int?[] { 0, 0, 0, 2 }, driverLinq);
        Assert.Equal(driverLinq, Counts(MongoQueryMode.Native));
        Assert.Equal(driverLinq, Counts(MongoQueryMode.NativeOnly));
    }

    [Fact]
    public void Streamed_and_reduced_paths_agree_for_a_missing_or_null_array()
    {
        // ToList() streams; First() is compiled DOM-only (allowStreaming: false), so the same document read
        // through the two paths is the cheapest cross-path check that the streaming fix and the DOM fix agree.
        // Pre-fix these disagreed: ToList() answered null and First() answered an empty collection.
        var collection = Seed(nameof(Streamed_and_reduced_paths_agree_for_a_missing_or_null_array));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        AssertStreamingEligible(db);

        foreach (var title in new[] { "missing", "null", "empty", "two" })
        {
            var streamed = db.Entities.AsNoTracking().Where(b => b.Title == title).ToList().Single();
            var reduced = db.Entities.AsNoTracking().First(b => b.Title == title);

            Assert.NotNull(streamed.Posts);
            Assert.NotNull(reduced.Posts);
            Assert.Equal(reduced.Posts.Count, streamed.Posts.Count);
        }
    }
}
