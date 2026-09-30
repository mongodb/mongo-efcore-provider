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
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Computed bare projection leaves (the Synthetic <c>_v</c> alias tier) over a deliberately ragged
/// owned-collection fixture.
/// </summary>
/// <remarks>
/// <para>
/// A bare <c>.Count</c> over a missing or explicit-null array aborts the aggregate with
/// <c>MongoCommandException</c> if the driver renders it (bare <c>{"$size": "$Posts"}</c>) rather than native
/// (<c>$size</c> over <c>$ifNull</c>). So every seed carries all four array states — present, empty, absent,
/// explicit null — and <see cref="Ragged_seed_really_carries_all_four_array_states"/> verifies them in raw BSON.
/// </para>
/// <para>
/// The parameterized-<c>Where</c> legs are mandatory: a captured local in <c>string.StartsWith</c> makes the
/// native renderer decline late, after the alias-addressed shaper is committed, which is the fallback path where
/// these failures live. The explicit-<c>DriverLinq</c> legs are mandatory because <c>UseQueryMode(DriverLinq)</c>
/// must keep restoring the previous path.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeComputedBareProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public double Weight { get; set; }
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        // Nullable so a missing stored element field materializes rather than throws.
        public string? Heading { get; set; }
    }

    /// <summary>
    /// Same as <see cref="Blog"/>, but the navigation is declared as <see cref="ICollection{T}"/>. Nav-expansion
    /// uses the declared type, so the count rewrite sees an interface-typed node.
    /// </summary>
    public class InterfaceBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public ICollection<Post> Posts { get; set; } = new List<Post>();
    }

    /// <summary>
    /// Same as <see cref="Blog"/>, but the navigation is declared <see cref="ISet{T}"/>, which
    /// <c>List&lt;Post&gt;</c> is not assignable to — the only coverage of the count rewrite's decline branch.
    /// </summary>
    public class SetBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public ISet<Post> Posts { get; set; } = new HashSet<Post>();
    }

    /// <summary>
    /// A blog whose ragged collection is a primitive <c>List&lt;string&gt;</c>. Separate from <see cref="Blog"/>
    /// so no other test's stored documents change.
    /// </summary>
    public class TaggedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<string> Tags { get; set; } = [];
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private static readonly Action<ModelBuilder> InterfaceBlogModel =
        mb => mb.Entity<InterfaceBlog>().OwnsMany(b => b.Posts);

    private static SingleEntityDbContext<Blog> CreateContext(IMongoCollection<Blog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<InterfaceBlog> CreateInterfaceContext(
        IMongoCollection<InterfaceBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: InterfaceBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static readonly Action<ModelBuilder> SetBlogModel = mb => mb.Entity<SetBlog>().OwnsMany(b => b.Posts);

    private static SingleEntityDbContext<SetBlog> CreateSetContext(
        IMongoCollection<SetBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: SetBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // FunctionalTests has no AssertMql (that's in SpecificationTests), so MQL is captured via SpyLoggerProvider.
    private static SingleEntityDbContext<Blog> CreateContextWithLogging(
        IMongoCollection<Blog> collection, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // A null `posts` means the field is absent; BsonNull.Value writes an explicit null. The two are not
    // interchangeable — an implementation can normalize one and abort on the other.
    private static BsonDocument Row(string title, int rank, BsonValue? posts)
    {
        var doc = new BsonDocument
        {
            {"_id", ObjectId.GenerateNewId()}, {"Title", title}, {"Rank", rank}, {"Weight", rank + 0.5}
        };
        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    private static BsonArray PostsOf(int count)
        => new(Enumerable.Range(0, count).Select(i => new BsonDocument {{"Heading", "h" + i}}));

    // Titles sort in seed order, so expectations are positional against OrderBy(b => b.Title):
    //
    //   p1_two     Posts present, 2 elements    Rank 1   Weight 1.5
    //   p2_empty   Posts present, empty array   Rank 2   Weight 2.5
    //   p3_missing Posts element ABSENT         Rank 3   Weight 3.5
    //   p4_null    Posts explicitly BSON null   Rank 4   Weight 4.5
    //   p5_one     Posts present, 1 element     Rank 5   Weight 5.5
    private static readonly BsonDocument[] RaggedRows =
    [
        Row("p1_two", 1, PostsOf(2)),
        Row("p2_empty", 2, new BsonArray()),
        Row("p3_missing", 3, posts: null),
        Row("p4_null", 4, BsonNull.Value),
        Row("p5_one", 5, PostsOf(1))
    ];

    private IMongoCollection<BsonDocument> SeedRaw(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        // Re-created per seed: InsertMany stamps _id onto the documents, so a shared array would collide.
        raw.InsertMany(RaggedRows.Select(d => (BsonDocument)d.DeepClone()));
        return raw;
    }

    private (IMongoCollection<Blog> Typed, IMongoCollection<BsonDocument> Raw) Seed(string name)
    {
        var raw = SeedRaw(name);
        return (database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName), raw);
    }

    private IMongoCollection<InterfaceBlog> SeedInterface(string name)
        => database.MongoDatabase.GetCollection<InterfaceBlog>(
            SeedRaw(name).CollectionNamespace.CollectionName);

    // The same ragged documents, read through the ISet-typed model; only the declared navigation type differs.
    private IMongoCollection<SetBlog> SeedSet(string name)
        => database.MongoDatabase.GetCollection<SetBlog>(
            SeedRaw(name).CollectionNamespace.CollectionName);

    // The same four array states under a `Tags` element.
    private IMongoCollection<TaggedBlog> SeedTagged(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            TaggedRow("p1_two", new BsonArray(["t0", "t1"])),
            TaggedRow("p2_empty", new BsonArray()),
            TaggedRow("p3_missing", tags: null),
            TaggedRow("p4_null", BsonNull.Value),
            TaggedRow("p5_one", new BsonArray(["t0"]))
        ]);
        return database.MongoDatabase.GetCollection<TaggedBlog>(raw.CollectionNamespace.CollectionName);
    }

    // No ragged rows: the control separating "aborts on a ragged array" from "aborts on a primitive collection".
    private IMongoCollection<TaggedBlog> SeedTaggedWellFormed(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name + "_wf"));
        raw.InsertMany(
        [
            TaggedRow("q1_two", new BsonArray(["t0", "t1"])),
            TaggedRow("q2_empty", new BsonArray()),
            TaggedRow("q3_one", new BsonArray(["t0"]))
        ]);
        return database.MongoDatabase.GetCollection<TaggedBlog>(raw.CollectionNamespace.CollectionName);
    }

    private static BsonDocument TaggedRow(string title, BsonValue? tags)
    {
        var doc = new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", title}};
        if (tags is not null)
        {
            doc.Add("Tags", tags);
        }

        return doc;
    }

    private static SingleEntityDbContext<TaggedBlog> CreateTaggedContext(
        IMongoCollection<TaggedBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── The owned-reference-hop fixture, ragged in the same four states ─────────────────────────────────
    //
    // The hop fixture in NativeOwnedCollectionCountTests writes `Home.Notes` as [] on every row, so it can't see
    // the missing/null hazard for a hop navigation.

    public class HopBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public HomeAddress Home { get; set; } = new();
    }

    public class HomeAddress
    {
        public string? City { get; set; }
        public List<Note> Notes { get; set; } = [];
    }

    public class Note
    {
        public string? Text { get; set; }
    }

    private static readonly Action<ModelBuilder> HopBlogModel =
        mb => mb.Entity<HopBlog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));

    private static SingleEntityDbContext<HopBlog> CreateHopContext(
        IMongoCollection<HopBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: HopBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static BsonDocument HopRow(string title, BsonValue? notes)
    {
        var home = new BsonDocument {{"City", title + "_city"}};
        if (notes is not null)
        {
            home.Add("Notes", notes);
        }

        return new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", title}, {"Home", home}};
    }

    private static BsonArray NotesOf(int count)
        => new(Enumerable.Range(0, count).Select(i => new BsonDocument {{"Text", "n" + i}}));

    private (IMongoCollection<HopBlog> Typed, IMongoCollection<BsonDocument> Raw) SeedHop(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            HopRow("h1_two", NotesOf(2)),
            HopRow("h2_empty", new BsonArray()),
            HopRow("h3_missing", notes: null),
            HopRow("h4_null", BsonNull.Value),
            HopRow("h5_one", NotesOf(1))
        ]);
        return (database.MongoDatabase.GetCollection<HopBlog>(raw.CollectionNamespace.CollectionName), raw);
    }

    private static readonly int[] ExpectedCounts = [2, 0, 0, 0, 1];
    // Filtered vector: only p1_two and p5_one have a post headed "h0" (p1_two's second is "h1").
    private static readonly int[] ExpectedFilteredCounts = [1, 0, 0, 0, 1];
    // 2*2, 0*2, 0*2, 0*2, 1*2.
    private static readonly int[] ExpectedDoubledCounts = [4, 0, 0, 0, 2];
    private static readonly int[] ExpectedDoubledRanks = [2, 4, 6, 8, 10];
    // Ranks are 1..5 in seed order, so *3 is 3,6,9,12,15.
    private static readonly int[] ExpectedTripledRanks = [3, 6, 9, 12, 15];
    private static readonly int[] ExpectedNarrowedWeights = [1, 2, 3, 4, 5];

    [Fact]
    public void Ragged_seed_really_carries_all_four_array_states()
    {
        // "Absent" and "explicit null" materialize identically, so only the raw BSON can prove both were seeded;
        // otherwise every count test could pass while covering three states instead of four.
        var (_, raw) = Seed(nameof(Ragged_seed_really_carries_all_four_array_states));

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty)
            .SortBy(d => d["Title"]).ToList();

        Assert.Equal(["p1_two", "p2_empty", "p3_missing", "p4_null", "p5_one"],
            stored.Select(d => d["Title"].AsString).ToArray());

        Assert.Equal(2, stored[0]["Posts"].AsBsonArray.Count);
        Assert.Empty(stored[1]["Posts"].AsBsonArray);
        Assert.False(stored[2].Contains("Posts"));
        Assert.True(stored[3].Contains("Posts"));
        Assert.Equal(BsonNull.Value, stored[3]["Posts"]);
        Assert.Single(stored[4]["Posts"].AsBsonArray);
    }

    [Fact]
    public void Bare_count_projection_goes_native_for_every_array_state()
    {
        // All four states, read into. NativeOnly succeeding is the nativeness proof; Native and explicit
        // DriverLinq must give the same values.
        var (collection, _) = Seed(nameof(Bare_count_projection_goes_native_for_every_array_state));

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Bare_count_projection_answers_correctly_on_the_LATE_decline_route()
    {
        // Late-decline route: the captured local in StartsWith makes the native renderer decline after the shaper
        // is committed, so the fallback runs through the driver-LINQ bridge — where a bare $size would abort on
        // missing/null arrays. Every title starts with "p", so all five rows are selected.
        var (collection, _) = Seed(nameof(Bare_count_projection_answers_correctly_on_the_LATE_decline_route));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedCounts,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Bare_arithmetic_and_cast_projections_answer_correctly_on_the_LATE_decline_route()
    {
        // Arithmetic ($multiply) and narrowing cast ($toInt) touch no array, so they can't abort on a ragged
        // one; pinned on the late-decline route over the ragged fixture.
        var (collection, _) =
            Seed(nameof(Bare_arithmetic_and_cast_projections_answer_correctly_on_the_LATE_decline_route));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);

            Assert.Equal(ExpectedDoubledRanks,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Rank * 2).ToList());

            Assert.Equal(ExpectedNarrowedWeights,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => (int)b.Weight).ToList());
        }
    }

    // ── Arithmetic and cast bare leaves ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Bare_arithmetic_projection_leaf_goes_native_for_every_array_state()
    {
        // Ragged rows are read past (`Rank` exists on every row): $multiply never touches an array. NativeOnly
        // succeeding is the nativeness proof. Under DriverLinq, populating Projection flips
        // ProjectionAnalyzer.CanPushDown and the driver renders the Select under its own `_v` alias — the same
        // reserved alias the Synthetic tier uses, so the shaper's read hits either way.
        var (collection, _) = Seed(nameof(Bare_arithmetic_projection_leaf_goes_native_for_every_array_state));

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedDoubledRanks,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Rank * 2).ToList());
        }
    }

    [Fact]
    public void Bare_arithmetic_leaf_over_a_CAPTURED_LOCAL_goes_native()
    {
        // The only coverage of IsArrayFreeComputedSubtree's MongoParameterExpression arm: every other computed leaf
        // is Field×Constant or Convert(Field), and other parameterized legs put the local in the Where clause.
        // Without this, deleting that arm would silently drop the shape to fallback with the suite green.
        var (collection, _) = Seed(nameof(Bare_arithmetic_leaf_over_a_CAPTURED_LOCAL_goes_native));
        var factor = 3;
        var prefix = "p";

        // NativeOnly succeeding is the routing proof; the other modes pin values on the routes users get.
        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedTripledRanks,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Rank * factor).ToList());
        }

        // Late-decline leg with the captured local in the leaf as well as the predicate: proves a parameter
        // placeholder inside a Synthetic-tier projection survives being rendered by the driver. A miss is silent.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedTripledRanks,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Rank * factor).ToList());
        }
    }

    [Fact]
    public void Bare_cast_projection_leaf_goes_native_for_every_array_state()
    {
        // $toInt touches no array. Weight is Rank + 0.5, so truncation gives 1 and 4 for 1.5 and 4.5, not 2 and 5.
        var (collection, _) = Seed(nameof(Bare_cast_projection_leaf_goes_native_for_every_array_state));

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedNarrowedWeights,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => (int)b.Weight).ToList());
        }
    }

    [Fact]
    public void Bare_computed_leaves_emit_the_reserved_underscore_v_alias()
    {
        // Not a routing proof (a driver push-down also emits `_v`). Pins that the emit side chose the reserved
        // alias: the late-fallback strip is not applied to a Synthetic override, so the shaper's alias must be the
        // one the driver would write.
        var (collection, _) = Seed(nameof(Bare_computed_leaves_emit_the_reserved_underscore_v_alias));

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, out var arithmeticSpy))
        {
            _ = db.Entities.AsNoTracking().Select(b => b.Rank * 2).ToList();
            var mql = arithmeticSpy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"_v\" : { \"$multiply\"", mql);
        }

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, out var castSpy))
        {
            _ = db.Entities.AsNoTracking().Select(b => (int)b.Weight).ToList();
            var mql = castSpy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"_v\" : { \"$toInt\"", mql);
        }
    }

    [Fact]
    public void Bare_arithmetic_over_a_collection_count_is_declined_and_answers_correctly()
    {
        // States read into. `b.Posts.Count * 2` is an arithmetic node (admitted at the top) over a
        // MongoSizeExpression, which the subtree check declines. Admitted, the un-stripped Synthetic tier lets the
        // driver render a bare {"$size": "$Posts"}, which aborts with
        //   MongoCommandException : The argument to $size must be an array, but was of type: missing
        // under default Native (late decline) and explicit DriverLinq. NullCoalesceSyntheticBareCountBody only
        // rewrites a body that is the count itself, so the shape must be declined.
        var (collection, _) = Seed(nameof(Bare_arithmetic_over_a_collection_count_is_declined_and_answers_correctly));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);

            // Direct route: populating Projection would flip CanPushDown and hand the Select to the driver, so this
            // is the leg that aborts under explicit DriverLinq if admitted.
            Assert.Equal(ExpectedDoubledCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count * 2).ToList());

            // Late-decline route: the leg that aborts under default Native if admitted.
            Assert.Equal(ExpectedDoubledCounts,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count * 2).ToList());
        }

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count * 2).ToList());
        }
    }

    [Fact]
    public void Bare_constant_leaf_now_goes_native_via_the_project_literal_wrap()
    {
        // A bare constant goes native. $project reads a bare 0/1 as an exclusion/inclusion flag, so
        // MongoPipelineFactory.RenderProject $literal-wraps bare constant/parameter values (as RenderAddFields does
        // for $set), matching the driver's `{ "_v" : { "$literal" : 0 }, "_id" : 0 }`. Unwrapped, `0` would render
        // as a pure exclusion returning whole documents.
        var (collection, _) = Seed(nameof(Bare_constant_leaf_now_goes_native_via_the_project_literal_wrap));

        // (1) Native, with the `$literal`-wrapped shape, asserted in both directions.
        using (var db = CreateContextWithLogging(collection, MongoQueryMode.Native, out var spy))
        {
            Assert.Equal([0, 0, 0, 0, 0],
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => 0).ToList());

            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"_v\" : { \"$literal\" : 0 }", mql);
            Assert.DoesNotContain("\"_v\" : 0", mql);
        }

        using (var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            Assert.Equal([0, 0, 0, 0, 0],
                driverLinq.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => 0).ToList());
        }

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal([0, 0, 0, 0, 0],
                nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => 0).ToList());
        }
    }

    [Fact]
    public void Interface_typed_collection_navigation_count_answers_correctly_on_every_route()
    {
        // ICollection<T>-declared navigation. On the late-decline leg it reaches the driver's un-stripped
        // push-down and answers 0 for missing/null rows only because TryCreateEmptyCollection coalesced it
        // against a List<Post>.
        var collection = SeedInterface(nameof(Interface_typed_collection_navigation_count_answers_correctly_on_every_route));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateInterfaceContext(collection, mode);

            Assert.Equal(ExpectedCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList());
        }

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateInterfaceContext(collection, mode);

            Assert.Equal(ExpectedCounts,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly()
    {
        // ISet<Post> navigation, states read into. List<Post> isn't assignable to ISet<Post>, so
        // TryCreateEmptyCollection can't build the `??` substitute and the driver would render a bare
        // {"$size": "$Posts"} — aborting with "The argument to $size must be an array, but was of type: missing"
        // under explicit DriverLinq and on both late-decline routes. The gate asks the rewrite
        // (IsFallbackSafeBareSizeLeaf -> TryMatchRewritableBareCountBody), so this declines and falls back.
        var collection = SeedSet(nameof(Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly));
        var prefix = "p";

        // Collect-then-assert so an early regression can't hide the abort legs; see LegOutcome.
        var expected = "[" + string.Join(",", ExpectedCounts) + "]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateSetContext(collection, mode);

            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList())));

            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList())));
        }

        using (var nativeOnly = CreateSetContext(collection, MongoQueryMode.NativeOnly))
        {
            legs.Add(("NativeOnly", LegOutcome(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList())));
        }

        Assert.Equal(
            [
                ("Native direct", expected),
                ("Native late-decline", expected),
                ("DriverLinq direct", expected),
                ("DriverLinq late-decline", expected),
                ("NativeOnly", "declined")
            ],
            legs);
    }

    [Fact]
    public void Set_typed_collection_navigation_FILTERED_count_is_unaffected_and_still_goes_native()
    {
        // A filtered count over the same ISet navigation stays native: it's admitted by a different arm of
        // IsFallbackSafeBareSizeLeaf because the driver renders it {$sum: {$map: …}}, and $map tolerates a
        // missing array. NativeOnly is the routing proof; the late-decline legs prove the driver push-down.
        var collection =
            SeedSet(nameof(Set_typed_collection_navigation_FILTERED_count_is_unaffected_and_still_goes_native));
        var prefix = "p";

        var expected = "[" + string.Join(",", ExpectedFilteredCounts) + "]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateSetContext(collection, mode);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList())));
        }

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateSetContext(collection, mode);
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList())));
        }

        Assert.Equal(
            [
                ("NativeOnly direct", expected),
                ("Native direct", expected),
                ("DriverLinq direct", expected),
                ("Native late-decline", expected),
                ("DriverLinq late-decline", expected)
            ],
            legs);
    }

    [Fact]
    public void Bare_count_projection_under_a_cardinality_terminator_answers_correctly()
    {
        // `Select(…).First()`: the pushed-down Select sits under a cardinality terminator, the second chain shape
        // the count rewrite handles. The captured-local predicate forces a late decline and narrows to the
        // absent-array row. The terminator must sit directly on the Select; `.Skip(2).First()` is a known gap.
        var (collection, _) = Seed(nameof(Bare_count_projection_under_a_cardinality_terminator_answers_correctly));
        var missingRowPrefix = "p3";

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);

            Assert.Equal(0,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(missingRowPrefix))
                    .Select(b => b.Posts.Count).First());
        }
    }

    // ── The two size kinds ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bare_filtered_count_leaf_goes_native_for_every_array_state()
    {
        // The filtered count is a separate node kind (MongoFilteredSizeExpression), so this pins its gate arm end
        // to end. `Heading == "h0"` gives [1,0,0,0,1], distinguishable from the unfiltered [2,0,0,0,1].
        var (collection, _) = Seed(nameof(Bare_filtered_count_leaf_goes_native_for_every_array_state));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedFilteredCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList());
        }

        // Late-decline leg: the count rewrite doesn't cover a filtered count, so the driver renders it with a bare
        // `$Posts` input — but as {$sum: {$map: …}}, and $map over a missing/null array yields missing rather than
        // aborting. That is why the filtered kind is safe without a rewrite of its own.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedFilteredCounts,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList());
        }
    }

    [Fact]
    public void Bare_count_leaves_render_size_over_ifNull_natively_and_on_the_fallback()
    {
        // The MQL pin: values alone can't distinguish bare `{"$size": "$Posts"}` from `$size` over `$ifNull`.
        // Asserting on both native and explicit DriverLinq proves the count rewrite reaches the fallback.
        var (collection, _) = Seed(nameof(Bare_count_leaves_render_size_over_ifNull_natively_and_on_the_fallback));

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContextWithLogging(collection, mode, out var spy);
            _ = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();
            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;

            Assert.Contains("\"_v\" : { \"$size\" : { \"$ifNull\" : [\"$Posts\", []] } }", mql);
            // Negative too: the bare form is the one that aborts, and both could appear.
            Assert.DoesNotContain("\"$size\" : \"$Posts\"", mql);
        }

        // Filtered kind: native wraps `$filter`'s input in `$ifNull`; the driver renders
        // `{$sum: {$map: {input: "$Posts", …}}}` with no `$ifNull`. Safe only because $map tolerates a missing array.
        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, out var nativeSpy))
        {
            _ = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList();
            var mql = nativeSpy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"$filter\" : { \"input\" : { \"$ifNull\" : [\"$Posts\", []] }", mql);
        }

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.DriverLinq, out var driverSpy))
        {
            _ = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList();
            var mql = driverSpy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"$map\" : { \"input\" : \"$Posts\"", mql);
        }
    }

    [Fact]
    public void Bare_count_leaf_under_a_COMPOSED_slot_operator_answers_correctly()
    {
        // Skip(1) drops p1_two, leaving the ragged rows. The count rewrite handles only an outermost Select or one
        // under a no-arg terminator, but nav-expansion applies the pending selector last, so the chain is
        // `Skip(…).Select(…)` and the rewrite fires: `[$match, $sort, $skip, $project{_v: $size($ifNull(…))}]`.
        var (collection, _) =
            Seed(nameof(Bare_count_leaf_under_a_COMPOSED_slot_operator_answers_correctly));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedCounts.Skip(1),
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).Skip(1).ToList());
            Assert.Equal(ExpectedCounts.Take(3),
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).Take(3).ToList());
        }

        // Late-decline route, which would abort if the rewrite were not reached.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContextWithLogging(collection, mode, out var spy);
            Assert.Equal(ExpectedCounts.Skip(1),
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).Skip(1).ToList());

            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"$skip\" : 1", mql);
            Assert.Contains("\"$ifNull\" : [\"$Posts\", []]", mql);
        }
    }

    [Fact]
    public void Bare_count_leaf_spelled_as_the_Enumerable_extension_method_goes_native()
    {
        // `Count()`/`LongCount()` extension spellings (vs. the `.Count` property). The late-decline leg answering 0
        // for missing/null rows proves the rewrite matched them.
        var (collection, _) = Seed(nameof(Bare_count_leaf_spelled_as_the_Enumerable_extension_method_goes_native));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count()).ToList());
            Assert.Equal(ExpectedCounts.Select(c => (long)c),
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.LongCount()).ToList());
        }

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedCounts,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count()).ToList());

            // LongCount gets its own late-decline leg: the rewrite matches Count and LongCount by separate names.
            Assert.Equal(ExpectedCounts.Select(c => (long)c),
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.LongCount()).ToList());
        }
    }

    [Fact]
    public void Primitive_collection_bare_count_is_NOT_admitted_and_still_aborts_on_a_ragged_array()
    {
        // Pins measured, not correct, behavior. `b.Tags.Count` over a primitive collection isn't natively
        // representable, so the projection declines and the driver renders a bare `{"$size": "$Tags"}`, which
        // aborts on missing/null rows under Native and DriverLinq. The count rewrite doesn't cover it; a future
        // widening should update this test.
        var collection = SeedTagged(nameof(Primitive_collection_bare_count_is_NOT_admitted_and_still_aborts_on_a_ragged_array));

        using (var nativeOnly = CreateTaggedContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Tags.Count).ToList());
        }

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateTaggedContext(collection, mode);
            var ex = Assert.Throws<MongoCommandException>(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Tags.Count).ToList());
            Assert.Contains("The argument to $size must be an array", ex.Message);
        }

        // The same leaf over non-ragged rows succeeds, so the abort is about array state, not primitive collections.
        var wellFormed = SeedTaggedWellFormed(nameof(Primitive_collection_bare_count_is_NOT_admitted_and_still_aborts_on_a_ragged_array));
        using (var db = CreateTaggedContext(wellFormed, MongoQueryMode.Native))
        {
            Assert.Equal([2, 0, 1],
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Tags.Count).ToList());
        }
    }

    [Fact]
    public void Bare_count_leaf_under_a_REDUCING_operator_no_longer_aborts_on_a_ragged_array()
    {
        // `Select(b => b.Posts.Count)` followed by Distinct, Sum or OrderBy: the Select is neither outermost nor
        // under a terminator, so NullCoalesceSyntheticBareCountBody doesn't reach it. It still answers correctly
        // because MongoEFToLinqTranslatingExpressionVisitor.TryRewriteEmbeddedCollectionNavigationCount coalesces
        // the field wherever `Queryable.Count(AsQueryable(EF.Property(...)))` appears in the driver-LINQ tree.
        var (collection, _) = Seed(nameof(Bare_count_leaf_under_a_REDUCING_operator_no_longer_aborts_on_a_ragged_array));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);

            var distinct = db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix))
                .Select(b => b.Posts.Count).Distinct().ToList();
            Assert.Equal([0, 1, 2], distinct.OrderBy(c => c).ToList());

            var sum = db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix))
                .Select(b => b.Posts.Count).Sum();
            Assert.Equal(3, sum);

            var ordered = db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix))
                .Select(b => b.Posts.Count).OrderBy(c => c).ToList();
            Assert.Equal([0, 0, 0, 1, 2], ordered);
        }
    }

    [Fact]
    public void Ragged_HOP_seed_really_carries_all_four_array_states()
    {
        // Hop-fixture self-check: absent and explicit-null Notes are only distinguishable in raw BSON.
        var (_, raw) = SeedHop(nameof(Ragged_HOP_seed_really_carries_all_four_array_states));

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).SortBy(d => d["Title"]).ToList();

        Assert.Equal(["h1_two", "h2_empty", "h3_missing", "h4_null", "h5_one"],
            stored.Select(d => d["Title"].AsString).ToArray());

        Assert.Equal(2, stored[0]["Home"]["Notes"].AsBsonArray.Count);
        Assert.Empty(stored[1]["Home"]["Notes"].AsBsonArray);
        Assert.False(stored[2]["Home"].AsBsonDocument.Contains("Notes"));
        Assert.True(stored[3]["Home"].AsBsonDocument.Contains("Notes"));
        Assert.Equal(BsonNull.Value, stored[3]["Home"]["Notes"]);
        Assert.Single(stored[4]["Home"]["Notes"].AsBsonArray);
    }

    [Fact]
    public void Bare_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly()
    {
        // Hop navigation, states read into. The count rewrite's IsNavigationOnParameter requires the navigation to
        // be rooted directly at the selector parameter, so `b.Home.Notes.Count` is never coalesced and the driver
        // would render a bare {"$size": "$Home.Notes"}, aborting with "The argument to $size must be an array, but
        // was of type: missing" under explicit DriverLinq and both late-decline routes. The gate declines a dotted
        // array path, so this falls back with correct values.
        var (collection, _) = SeedHop(nameof(Bare_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly));
        var prefix = "h";

        // Collect-then-assert so an early regression can't hide the abort legs; see LegOutcome.
        var expected = "[" + string.Join(",", ExpectedCounts) + "]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateHopContext(collection, mode);

            // Direct route: the leg that aborts under explicit DriverLinq if admitted.
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Home.Notes.Count).ToList())));

            // Late-decline route: the leg that aborts under default Native if admitted.
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Home.Notes.Count).ToList())));
        }

        using (var nativeOnly = CreateHopContext(collection, MongoQueryMode.NativeOnly))
        {
            legs.Add(("NativeOnly", LegOutcome(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Home.Notes.Count).ToList())));
        }

        Assert.Equal(
            [
                ("Native direct", expected),
                ("Native late-decline", expected),
                ("DriverLinq direct", expected),
                ("DriverLinq late-decline", expected),
                ("NativeOnly", "declined")
            ],
            legs);
    }

    [Fact]
    public void Bare_FILTERED_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly()
    {
        // Hop navigation, filtered kind. It would be safe (the driver's $map tolerates a missing array), but the
        // dotted-path rule declines both size kinds so there is one rule to check. Widening it should be deliberate.
        var (collection, _) =
            SeedHop(nameof(Bare_FILTERED_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly));
        var prefix = "h";

        // Collect-then-assert, as above.
        var expected = "[" + string.Join(",", ExpectedFilteredCounts) + "]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateHopContext(collection, mode);

            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Home.Notes.Count(n => n.Text == "n0")).ToList())));

            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Home.Notes.Count(n => n.Text == "n0")).ToList())));
        }

        using (var nativeOnly = CreateHopContext(collection, MongoQueryMode.NativeOnly))
        {
            legs.Add(("NativeOnly", LegOutcome(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Home.Notes.Count(n => n.Text == "n0")).ToList())));
        }

        Assert.Equal(
            [
                ("Native direct", expected),
                ("Native late-decline", expected),
                ("DriverLinq direct", expected),
                ("DriverLinq late-decline", expected),
                ("NativeOnly", "declined")
            ],
            legs);
    }

    [Fact]
    public void Bare_filtered_count_leaf_with_a_CAPTURED_PARAMETER_goes_native()
    {
        // A filtered count whose element predicate closes over a captured local, through every leg. The late-decline
        // leg proves the captured local survives being rendered by the driver on the un-stripped fallback.
        var (collection, _) = Seed(nameof(Bare_filtered_count_leaf_with_a_CAPTURED_PARAMETER_goes_native));
        var heading = "h0";
        var prefix = "p";

        // Collect-then-assert: NativeOnly runs first, and must not stop the explicit-DriverLinq leg running.
        var expected = "[" + string.Join(",", ExpectedFilteredCounts) + "]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == heading)).ToList())));
        }

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == heading)).ToList())));
        }

        Assert.Equal(
            [
                ("NativeOnly direct", expected),
                ("Native direct", expected),
                ("DriverLinq direct", expected),
                ("Native late-decline", expected),
                ("DriverLinq late-decline", expected)
            ],
            legs);
    }

    /// <summary>
    /// Runs <paramref name="query"/> and describes the outcome as a short string, so a test can collect every leg's
    /// outcome and assert them together; a failing early leg must not stop later legs (e.g. an abort) from running.
    /// </summary>
    private static string LegOutcome(Func<object?> query)
    {
        try
        {
            var result = query();
            return result is System.Collections.IEnumerable values and not string
                ? "[" + string.Join(",", values.Cast<object>()) + "]"
                : result?.ToString() ?? "null";
        }
        catch (NativeTranslationNotSupportedException)
        {
            return "declined";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }
    }

    [Fact]
    public void Whole_entity_materialization_is_the_oracle_for_every_array_state()
    {
        // The independent oracle: whole entities materialized and the selector evaluated client-side, with no
        // projection in the query (a projection-vs-projection comparison would test this code against itself).
        var (collection, _) = Seed(nameof(Whole_entity_materialization_is_the_oracle_for_every_array_state));

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var oracle = db.Entities.AsNoTracking().ToList()
            .OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();

        Assert.Equal(ExpectedCounts, oracle);
    }

    // ── Negate and widening-cast numeric leaves ──────────────────────────────────────────────────────────
    //
    // `-x` renders as {$subtract: [0, x]} and a widening cast is dropped by TryTranslateValue, so the server computes
    // the whole leaf. The read side must then read `_v` (or the member alias) once: re-applying Negate client-side
    // over the already-negated value answers +Rank. Every assertion pins the sign, not just the row count.

    private static readonly int[] ExpectedNegatedRanks = [-1, -2, -3, -4, -5];
    private static readonly long[] ExpectedNegatedRanksAsLong = [-1L, -2L, -3L, -4L, -5L];

    [Fact]
    public void Negate_leaf_goes_native_bare_and_wrapped()
    {
        var (collection, _) = Seed(nameof(Negate_leaf_goes_native_bare_and_wrapped));

        var bare = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => -b.Rank).ToList();
        });
        Assert.Equal(ExpectedNegatedRanks, bare);

        var wrapped = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => new { N = -b.Rank }).ToList();
        });
        Assert.Equal(ExpectedNegatedRanks, wrapped.Select(r => r.N).ToArray());

        // A double operand, beside a plain member so the wrapped read is not the only leaf.
        var doubles = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new { b.Title, W = -b.Weight }).ToList();
        });
        Assert.Equal([-1.5, -2.5, -3.5, -4.5, -5.5], doubles.Select(r => r.W).ToArray());
        Assert.Equal(["p1_two", "p2_empty", "p3_missing", "p4_null", "p5_one"], doubles.Select(r => r.Title).ToArray());
    }

    [Fact]
    public void Widening_cast_over_negate_goes_native()
    {
        var (collection, _) = Seed(nameof(Widening_cast_over_negate_goes_native));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => (long)-b.Rank).ToList();
        });
        Assert.Equal(ExpectedNegatedRanksAsLong, results);
    }

    [Fact]
    public void Negate_over_widening_cast_reads_the_server_value_once()
    {
        var (collection, _) = Seed(nameof(Negate_over_widening_cast_reads_the_server_value_once));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => -(long)b.Rank).ToList();
        });
        Assert.All(results, r => Assert.True(r < 0)); // double negation would make these positive
        Assert.Equal(ExpectedNegatedRanksAsLong, results);

        var wrapped = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => new { N = -(long)b.Rank }).ToList();
        });
        Assert.Equal(ExpectedNegatedRanksAsLong, wrapped.Select(r => r.N).ToArray());
    }

    [Fact]
    public void Widening_cast_over_arithmetic_goes_native()
    {
        var (collection, _) = Seed(nameof(Widening_cast_over_arithmetic_goes_native));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => (long)(b.Rank + b.Rank * 10)).ToList();
        });
        Assert.Equal([11L, 22L, 33L, 44L, 55L], results);
    }

    [Fact]
    public void Narrowing_cast_over_arithmetic_stays_declined()
    {
        // A narrowing cast is outside IsNumericComputedLeafShape, and `(short)` has no $toX ($toInt semantics differ
        // from C#'s unchecked truncation), so it must stay declined. DeclinesCleanly can't be used: the driver has no
        // oracle either (it throws "conversion to System.Int16 is not supported"), so every mode must fail loudly
        // rather than return a value.
        var (collection, _) = Seed(nameof(Narrowing_cast_over_arithmetic_stays_declined));

        List<short> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => (short)(b.Rank + (long)b.Rank)).ToList();
        }

        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => Run(MongoQueryMode.Native));
        Assert.Throws<MongoDB.Driver.Linq.ExpressionNotSupportedException>(() => Run(MongoQueryMode.DriverLinq));
    }

    [Fact]
    public void Negate_over_a_collection_count_goes_native_wrapped_and_declines_bare()
    {
        // The read side keys on structural equality with the staged leaf, not on the operand's shape: an earlier
        // operand-shape heuristic mis-bound `new { N = -b.Posts.Count }`. Wrapped, the count renders as $size over
        // $ifNull, so missing/null arrays read 0.
        var (collection, _) = Seed(nameof(Negate_over_a_collection_count_goes_native_wrapped_and_declines_bare));

        var wrapped = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new { b.Title, N = -b.Posts.Count }).ToList();
        });
        Assert.Equal([-2, 0, 0, 0, -1], wrapped.Select(r => r.N).ToArray());

        // Bare, the Synthetic `_v` tier's IsArrayFreeComputedSubtree declines the nested $size (its un-stripped
        // fallback would render a bare $size that aborts on a missing array); the fallback answers correctly.
        var bare = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => -b.Posts.Count).ToList();
        });
        Assert.Equal([-2, 0, 0, 0, -1], bare);
    }

    [Fact]
    public void Negate_leaf_answers_correctly_beside_a_whole_entity_and_on_the_LATE_decline_route()
    {
        var (collection, _) =
            Seed(nameof(Negate_leaf_answers_correctly_beside_a_whole_entity_and_on_the_LATE_decline_route));
        var prefix = "p";

        // Beside a whole entity: the mixed shaper evaluates the client form over whole documents.
        var mixed = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => new { b, N = -b.Rank }).ToList()
                .Select(r => (r.b.Title, r.N)).ToList();
        });
        Assert.Equal(ExpectedNegatedRanks, mixed.Select(r => r.N).ToArray());

        // Late decline: the captured local in StartsWith declines after the shaper is committed, so the driver renders
        // the projection and the shaper must still read it once.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedNegatedRanks,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => -b.Rank).ToList());
            Assert.Equal(ExpectedNegatedRanksAsLong,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => new { N = -(long)b.Rank }).ToList().Select(r => r.N).ToArray());
        }
    }
}
