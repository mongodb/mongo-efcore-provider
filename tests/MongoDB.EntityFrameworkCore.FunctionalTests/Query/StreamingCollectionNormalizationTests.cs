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
/// Missing/null owned-collection normalization to an empty collection on the streaming materializer
/// (<see cref="MongoStreamingEntityMaterializerRewriter"/>); <see cref="ProjectedCollectionNormalizationTests"/>
/// covers the DOM shaper.
/// <para>
/// Only a whole-entity <c>ToList()</c> over a flat owned collection streams: reducers (<c>First()</c>) compile
/// DOM-only, and an element navigation makes the shape streaming-ineligible. So <see cref="FlatBlog.Posts"/>
/// has no <c>= []</c> initializer (which would mask the fix) and <see cref="FlatPost"/> has no navigations.
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

        // No `= []` initializer on purpose: an empty collection here can only come from the provider.
        public List<FlatPost> Posts { get; set; }
    }

    public class FlatPost
    {
        // No navigations on purpose: an element navigation makes the shape streaming-ineligible.
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

    // A null `posts` means the field is absent; BsonNull.Value means present and explicitly null.
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

    // Guards against the model drifting (a FlatPost navigation, composite PK, TPH base) and silently routing
    // these queries to the DOM shaper, which would make every test here vacuous.
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
        // Pins the post-loop normalization in MongoStreamingEntityMaterializerRewriter.BuildFillLoop; without it
        // the `missing` and `null` rows come back with Posts == null. NativeOnly so a non-streamable shape
        // throws rather than silently using the DOM shaper.
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
        // Native (streaming), NativeOnly and DriverLinq must agree for a supported query.
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
        // ToList() streams while First() is compiled DOM-only (allowStreaming: false), so this checks the two
        // materialization paths agree on the same document.
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
