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
/// EF-322 slice A4 — the COMPUTED bare projection leaf (tier 2), over a deliberately RAGGED owned-collection
/// fixture.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole slice turns on the ragged states, so the fixture leads with them.</b> Tier 2 was built,
/// measured and REVERTED at EF-322 step 3a for exactly one reason: a bare <c>.Count</c> over a MISSING or
/// explicitly-BSON-null array aborts the whole aggregate with <c>MongoCommandException</c> under the DEFAULT
/// <c>Native</c> mode, because the un-stripped fallback is the driver's own push-down and the driver renders a
/// bare <c>{"$size": "$Posts"}</c> where native renders <c>{"$size": {"$ifNull": ["$Posts", []]}}</c>. A count
/// measurement seeded only with well-formed arrays cannot see any of that, so every seed here carries all FOUR
/// array states — present, empty, element ABSENT, explicit BSON null — and
/// <see cref="Ragged_seed_really_carries_all_four_array_states"/> asserts the stored documents really do,
/// against the raw BSON, before any query result in this file is trusted.
/// </para>
/// <para>
/// <b>What this file pins TODAY, and why every assertion here is a baseline rather than a feature.</b> The
/// A4-0 change that ships with it — <c>NativeProjectionBinder.NullCoalesceSyntheticBareCountBody</c> — is
/// deliberately INERT: it is gated on <see cref="Query.Expressions.ProjectionAliasTier"/>
/// <c>.Synthetic</c>, and nothing writes that tier yet. So every computed bare leaf below still DECLINES
/// (<c>NativeTranslationNotSupportedException</c> under <c>NativeOnly</c>) and still answers correctly by
/// falling back. That is the point of shipping the correctness fix first: when the tier-2 admission lands,
/// these same assertions must keep their values while their ROUTING flips, instead of the capability commit
/// opening a <c>MongoCommandException</c> under the default mode and closing it in the same diff.
/// </para>
/// <para>
/// <b>The parameterized-<c>Where</c> legs are mandatory, not decoration.</b> A captured local inside
/// <c>string.StartsWith</c> makes the native renderer decline LATE — after the alias-addressed shaper has
/// already been committed — and that late-decline route is the only thing in the suite that exercises the
/// fallback path where this slice's failures live. The explicit-<c>DriverLinq</c> leg is mandatory for a
/// second reason: the versioning rubric's carve-out for the native default is CONDITIONAL on
/// <c>UseQueryMode(DriverLinq)</c> restoring the previous path, and tier 2 as prototyped broke that for this
/// very shape (MEASURED — <c>Select(b =&gt; b.Posts.Count)</c> aborted under explicit <c>DriverLinq</c> with no
/// decline involved at all).
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
        // Nullable ON PURPOSE: a missing stored element field must materialize rather than throw, or the
        // ragged states cannot be exercised at all. Same reasoning as NativeOwnedCollectionCountTests.Post.
        public string? Heading { get; set; }
    }

    /// <summary>
    /// The same shape as <see cref="Blog"/>, but the owned collection navigation is declared as the INTERFACE
    /// <see cref="ICollection{T}"/> rather than <c>List&lt;Post&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Not an exotic model: EF's nav-expansion spells the navigation <c>EF.Property&lt;TNavClrType&gt;</c> with
    /// the DECLARED property type, so an interface-typed navigation reaches the A4-0 rewrite with an
    /// interface-typed node — and this suite already models one elsewhere
    /// (<c>OwnedEntityTests.PersonWithIEnumerableLocations</c>). The rewrite's first version declined every
    /// interface type, which would have left this whole family of models hitting the bare-<c>$size</c> abort the
    /// moment the tier-2 admission landed.
    /// </remarks>
    public class InterfaceBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public ICollection<Post> Posts { get; set; } = new List<Post>();
    }

    /// <summary>
    /// The same shape again, but the owned collection navigation is declared <see cref="ISet{T}"/> — an
    /// ordinary, EF-supported collection type that <c>List&lt;Post&gt;</c> is NOT assignable to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fixture the whole file was missing. <see cref="InterfaceBlog"/> models
    /// <c>ICollection&lt;Post&gt;</c>, and the unit theory beside it covered
    /// <c>ICollection&lt;T&gt;</c>/<c>IEnumerable&lt;T&gt;</c>/<c>IList&lt;T&gt;</c> — exactly the three
    /// interfaces <c>List&lt;T&gt;</c> IS assignable to, i.e. the three for which the A4-0 rewrite SUCCEEDS. No
    /// model anywhere in <c>src/</c> or <c>tests/</c> declared an <c>ISet&lt;T&gt;</c> or
    /// <c>IReadOnlySet&lt;T&gt;</c> navigation, so the rewrite's DECLINE branch had no coverage at all — and the
    /// spec suite could not see it either, Northwind having no owned collections.
    /// </para>
    /// </remarks>
    public class SetBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public ISet<Post> Posts { get; set; } = new HashSet<Post>();
    }

    /// <summary>
    /// A blog whose ragged collection is a PRIMITIVE one (<c>List&lt;string&gt;</c>) rather than an owned entity
    /// collection.
    /// </summary>
    /// <remarks>
    /// Its only consumer is
    /// <see cref="Primitive_collection_bare_count_is_NOT_admitted_and_still_aborts_on_a_ragged_array"/>, which
    /// pins the measured pre-existing behaviour of <c>b.Tags.Count</c>. It is a separate model rather than an
    /// extra property on <see cref="Blog"/> so that no other case in this file silently changes its stored
    /// documents.
    /// </remarks>
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

    // MQL-capture idiom mirrored from NativeBareProjectionTests: FunctionalTests has no TestMqlLoggerFactory /
    // AssertMql (those live in the SpecificationTests project), so MQL is captured through SpyLoggerProvider.
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

    // A null `posts` means the FIELD IS ABSENT; BsonNull.Value means the field is present and explicitly null.
    // Those two states are the entire reason this file exists, and they are NOT interchangeable — an
    // implementation can normalize one and abort on the other.
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

    // Titles sort in seed order, so every expectation below can be written positionally against
    // OrderBy(b => b.Title) without also projecting the title.
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
        // Re-created per seed: InsertMany stamps _id onto the documents it is handed, so a shared static array
        // would be re-inserted with duplicate keys on the second call.
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

    // The SAME ragged documents (all four array states) read through the ISet-typed model — the stored shape is
    // identical, only the DECLARED navigation type differs, which is precisely the axis under test.
    private IMongoCollection<SetBlog> SeedSet(string name)
        => database.MongoDatabase.GetCollection<SetBlog>(
            SeedRaw(name).CollectionNamespace.CollectionName);

    // The primitive-collection seed carries the SAME four array states under a `Tags` element: present-2,
    // empty, element ABSENT, explicitly BSON null, present-1.
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

    // The same model with NO ragged rows — the control that isolates "aborts on a ragged array" from
    // "aborts on a primitive collection".
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

    // ── The owned-reference-HOP fixture, ragged in the SAME four states ─────────────────────────────────
    //
    // ADDED AFTER A REVIEW FOUND A LIVE REGRESSION THIS FILE COULD NOT SEE. The only hop-collection fixture in
    // the suite (NativeOwnedCollectionCountTests' SeedLengths) writes `Home.Notes` as an EMPTY ARRAY on every
    // row, so it cannot exercise the missing and explicitly-null states — which are the entire hazard this
    // slice is about. A ragged fixture is mandatory for a count leaf, and that applies to the HOP navigation
    // and not only to the root-declared one.

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
    // The FILTERED vector, derived from the seed rather than from ExpectedCounts: only p1_two and p5_one hold a
    // post headed "h0", and p1_two's second post is headed "h1", so the filter drops it to 1.
    private static readonly int[] ExpectedFilteredCounts = [1, 0, 0, 0, 1];
    // Re-summed from ExpectedCounts, not restated: 2*2, 0*2, 0*2, 0*2, 1*2.
    private static readonly int[] ExpectedDoubledCounts = [4, 0, 0, 0, 2];
    private static readonly int[] ExpectedDoubledRanks = [2, 4, 6, 8, 10];
    // Ranks are 1..5 in seed order (see RaggedRows), so *3 is 3,6,9,12,15 — re-derived from that seed, not
    // scaled from ExpectedDoubledRanks.
    private static readonly int[] ExpectedTripledRanks = [3, 6, 9, 12, 15];
    private static readonly int[] ExpectedNarrowedWeights = [1, 2, 3, 4, 5];

    [Fact]
    public void Ragged_seed_really_carries_all_four_array_states()
    {
        // THE FIXTURE'S OWN SELF-CHECK, and it is not ceremony. "Element absent" and "explicitly BSON null" are
        // two different stored states that a driver, a serializer or a seed helper can silently collapse into
        // one; if they collapse, every count assertion in this file still passes while covering three states
        // instead of four. Asserting against the RAW BsonDocuments is the only way to tell them apart, because
        // both materialize identically once EF has read them.
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
        // States exercised: ALL FOUR (present-2, empty, absent, explicit null, present-1), and here they are
        // read INTO rather than read past — this is the leaf whose rendering the array state actually changes.
        //
        // THE SHAPE THE WHOLE SLICE IS FOR. Through A4-1 this DECLINED under NativeOnly and the assertion below
        // was an `Assert.Throws`; A4-2 admits it, so the same three values-assertions now cover a NATIVE route
        // under NativeOnly, the default Native route, and the rubric-mandated explicit-DriverLinq escape hatch.
        // NativeOnly SUCCEEDING is the only nativeness proof this suite accepts.
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
        // States exercised: ALL FOUR. THE ROUTE THAT MATTERS. A captured local inside string.StartsWith makes
        // the native renderer refuse a parameterized regex term, so TryBuildNativeFactory returns null AFTER
        // the alias-addressed shaper has been committed — the fallback then runs the captured chain through the
        // driver-LINQ bridge, which is where the driver's bare $size lives and where the missing/explicitly-null
        // array aborts once tier 2 admits this shape. Every title starts with "p", so the predicate selects all
        // five rows and the expectation is the full ragged vector rather than a filtered subset.
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
        // States exercised: ALL FOUR (they are read past, not read into — see below).
        //
        // The two OTHER computed bare leaves A4 admits, pinned on the same route and the same fixture. They are
        // MEASURED to be safe where the count is not, and the reason is worth keeping next to the count's:
        // the driver renders arithmetic as $multiply and a narrowing cast as $toInt, neither of which touches
        // an array, so neither can abort on a missing or explicitly-null one. Pinning their values HERE, while
        // they still fall back, is what makes the later routing flip a visible change rather than an invisible
        // one.
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

    // ── A4-1: the tier-2 admission for ARITHMETIC and CAST bare leaves ──────────────────────────────────

    [Fact]
    public void Bare_arithmetic_projection_leaf_goes_native_for_every_array_state()
    {
        // States exercised: ALL FOUR — the ragged rows are read PAST rather than read into (the leaf is
        // `Rank * 2`, and `Rank` is present on every row), which is exactly the point: the reason A4-1 is safe
        // independent of the A4-0 prerequisite is that arithmetic renders `$multiply`, which never touches an
        // array and therefore cannot abort on a missing or explicitly-null one. Running it over the ragged seed
        // is what turns that from an argument into a measurement.
        //
        // NativeOnly SUCCEEDING is the nativeness proof — the only one this suite accepts. The Native and
        // DriverLinq legs are not redundant with it: DriverLinq is the rubric-level escape hatch (populating
        // Projection flips ProjectionAnalyzer.CanPushDown, so the driver now renders this bare Select itself,
        // under its OWN `_v` alias — which is the reserved alias tier 2 deliberately chose so that the read hits
        // either way).
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
        // THE ONLY COVERAGE FOR `IsArrayFreeComputedSubtree`'s MongoParameterExpression ARM, and it exists
        // because nothing else in the suite reaches it. Every other computed bare leaf here is Field×Constant
        // (`b.Rank * 2`) or Convert(Field) (`(int)b.Weight`), and the parameterized legs elsewhere in this file
        // put their captured local in the WHERE clause, not in the projection leaf — so the allow-list's
        // parameter arm could be deleted with the whole suite still green, silently dropping this shape from
        // native to fallback. Mutation-verified in the OPPOSITE direction from the two subtree-check mutations:
        // deleting that arm turns this test red (see the task report for the count).
        //
        // States exercised: ALL FOUR — read past, like the other arithmetic and cast cases, since `Rank` is
        // present on every ragged row.
        var (collection, _) = Seed(nameof(Bare_arithmetic_leaf_over_a_CAPTURED_LOCAL_goes_native));
        var factor = 3;
        var prefix = "p";

        // NativeOnly SUCCEEDING is the routing proof; the other two modes pin the values on the routes a user
        // actually gets, including the rubric-mandated explicit-DriverLinq escape hatch.
        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedTripledRanks,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Rank * factor).ToList());
        }

        // And the LATE-DECLINE leg, which is NOT redundant with the sibling arithmetic case's: here the captured
        // local is in the LEAF as well as the predicate, so this is the only leg proving a parameter placeholder
        // inside a Synthetic-tier projection survives being handed to the driver to render on the un-stripped
        // fallback. An alias or placeholder miss on this route is silent.
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
        // States exercised: ALL FOUR, same "read past" reasoning as the arithmetic case — a narrowing cast
        // renders `$toInt`, which touches no array either. The values discriminate a truncating read from a
        // rounding one: Weight is Rank + 0.5, so (int)1.5 must be 1 and (int)4.5 must be 4, not 2 and 5.
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
        // NOT a routing proof (a driver-LINQ push-down emits a $project too, and for a bare body it emits `_v`
        // as well — that coincidence is the whole design). What this pins is that the emit side really chose the
        // RESERVED alias rather than some derived name: the Synthetic tier's entire safety story is that the
        // alias the shaper reads by is the same one the DRIVER would have written, because the late-fallback
        // strip is deliberately NOT applied to a Synthetic override.
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
        // States exercised: ALL FOUR, and here they are read INTO rather than read past — which is the entire
        // reason this case exists and why it could not be covered by the arithmetic case above.
        //
        // THE SHAPE THE TOP-NODE GATE ALONE LETS THROUGH. `b.Posts.Count * 2` is an arithmetic
        // MongoBinaryExpression (gate 1 admits it) over a MongoSizeExpression (gate 2 declines it). A4-1 shipped
        // without gate 2 and this shape re-opened the exact defect that got tier 2 reverted at step 3a: the
        // Synthetic tier is deliberately NOT stripped on a late fallback, so the DRIVER's own push-down renders
        // a bare {"$size": "$Posts"} where native renders $size over $ifNull, and a MISSING or explicitly-null
        // array aborts the whole aggregate. MEASURED with only gate 1:
        //   MongoCommandException : The argument to $size must be an array, but was of type: missing
        // under the DEFAULT Native mode on the late-decline route AND under explicit DriverLinq with no decline
        // involved at all — the second of which also breaks the versioning rubric's carve-out for the native
        // default, since that carve-out is conditional on UseQueryMode(DriverLinq) restoring the previous path.
        //
        // A4-0's NullCoalesceSyntheticBareCountBody cannot cover it: that rewrite matches only a pushed-down
        // bare Select whose body IS the count, never one that merely contains one. So the boundary has to be
        // enforced by declining the shape, which is what this pins.
        var (collection, _) = Seed(nameof(Bare_arithmetic_over_a_collection_count_is_declined_and_answers_correctly));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);

            // The DIRECT route — no decline involved. This is the leg that went red under explicit DriverLinq,
            // because populating Projection flips ProjectionAnalyzer.CanPushDown and hands the bare Select to
            // the driver to render.
            Assert.Equal(ExpectedDoubledCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count * 2).ToList());

            // And the LATE-DECLINE route, where the captured-local StartsWith makes the native renderer refuse
            // after the shaper is already committed. This is the leg that went red under the DEFAULT Native
            // mode — the one a DriverLinq-only escape hatch would not have saved.
            Assert.Equal(ExpectedDoubledCounts,
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count * 2).ToList());
        }

        // The routing pin: still declined, so the values above are the fallback's and the abort is unreachable.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count * 2).ToList());
        }
    }

    [Fact]
    public void Bare_constant_leaf_is_not_admitted_by_the_tier_2_node_kind_gate()
    {
        // THE MUTATION TEST FOR THE NODE-KIND GATE. Tier 2 gates on the resulting NODE KIND — an arithmetic
        // MongoBinaryExpression or a MongoConvertExpression, both of which render as aggregation-operator
        // DOCUMENTS — rather than on "the leaf translated successfully", which would additionally admit a bare
        // constant or captured parameter. The constant is FALSY on purpose, because that is where the two
        // spellings diverge.
        //
        // WHAT THE MUTATION ACTUALLY PRODUCES, MEASURED (gate relaxed to plain translation success, rebuilt,
        // re-run) — and it is NOT what the WRAPPED gate's own recorded measurement produces, so the two must not
        // be conflated. A wrapped `new { b.Title, X = 0 }` hard-FAILS under the default Native mode, because the
        // falsy leaf sits beside an inclusion and `$project` cannot mix exclusion with inclusion. A BARE `0` has
        // no sibling at all, so nothing is mixed and MongoDB ACCEPTS the pipeline; the shaper then folds the
        // constant client-side and the VALUES stay correct. So a values-only test would be VACUOUS here, and the
        // observable difference is the emitted pipeline:
        //
        //   declined (today)  { "$project" : { "_v" : { "$literal" : 0 }, "_id" : 0 } }   ← the DRIVER's rendering
        //   gate relaxed      { "$project" : { "_v" : 0, "_id" : 0 } }                    ← native's, a pure EXCLUSION
        //
        // Both were captured from this test. The first is a genuine value projection; the second is not a value
        // projection at all — `$project` reads a bare `0` as an exclusion FLAG — so the aggregate returns whole
        // documents minus two fields and the correct answers arrive only because the shaper never needed the
        // pipeline's output for a constant. That accident is not a contract, and it is what the node-kind gate
        // keeps out. Red counts for the mutation are in the task report.
        var (collection, _) = Seed(nameof(Bare_constant_leaf_is_not_admitted_by_the_tier_2_node_kind_gate));

        // (1) The discriminating leg, asserted in BOTH directions so neither a stray `$literal` elsewhere in the
        // pipeline nor a coincidental substring can carry it.
        using (var db = CreateContextWithLogging(collection, MongoQueryMode.Native, out var spy))
        {
            Assert.Equal([0, 0, 0, 0, 0],
                db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => 0).ToList());

            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
            Assert.Contains("\"_v\" : { \"$literal\" : 0 }", mql);
            Assert.DoesNotContain("\"_v\" : 0", mql);
        }

        // (2) And the values are unchanged on the escape hatch too, plus the clean decline that proves the
        // routing.
        using (var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            Assert.Equal([0, 0, 0, 0, 0],
                driverLinq.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => 0).ToList());
        }

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => 0).ToList());
        }
    }

    [Fact]
    public void Interface_typed_collection_navigation_count_answers_correctly_on_every_route()
    {
        // States exercised: ALL FOUR, on an ICollection<T>-declared navigation. This is the shape the A4-0
        // rewrite originally declined outright — see InterfaceBlog's remarks — so it is the coverage that
        // distinguishes "the prerequisite protects List<T>" from "the prerequisite protects a collection
        // navigation". Since A4-2 the LATE-DECLINE leg is the load-bearing one: the interface-typed navigation
        // now really does reach the driver's un-stripped push-down, and it answers 0 for the missing and
        // explicitly-null rows only because TryCreateEmptyCollection coalesced it against a List<Post>.
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
        // THE THIRD INSTANCE OF THE RECURRING DEFECT CLASS, and the regression net for the fix that closed it
        // structurally. States exercised: ALL FOUR, read INTO, on an ISet<Post>-declared navigation.
        //
        // `ISet<Post>` carries a perfectly NON-DOTTED array path, so the array-path-only version of the gate
        // admitted it — but the A4-0 rewrite declines it on a DIFFERENT dimension: List<Post> is not assignable
        // to ISet<Post>, so TryCreateEmptyCollection cannot build the `??` substitute, the pushed-down body is
        // never coalesced, the Synthetic tier suppresses the strip, and the driver renders a bare
        // {"$size": "$Posts"}. MEASURED over this fixture, base vs. that version: THREE routes went from
        // `2,0,0,0,1` to
        //   MongoCommandException : The argument to $size must be an array, but was of type: missing
        // — explicit DriverLinq, DriverLinq late-decline, and the DEFAULT Native late-decline. Same signature as
        // the `Count * 2` and owned-hop instances, through a door neither of them closed.
        //
        // The gate now ASKS the rewrite (IsFallbackSafeBareSizeLeaf -> TryMatchRewritableBareCountBody) instead
        // of describing its reach, so this shape declines and is back to its base behaviour: a graceful fallback
        // with correct values in every mode.
        var collection = SeedSet(nameof(Set_typed_collection_navigation_bare_count_is_declined_and_answers_correctly));
        var prefix = "p";

        // Collect-then-assert: the abort legs are the ones this test exists for, so an early regression must not
        // stop them running. See LegOutcome's remarks.
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
        // THE BOUND ON THE FINDING ABOVE, measured rather than assumed, and it is what keeps the fix from being
        // read as "an ISet navigation is not supported". A FILTERED count over the SAME navigation is admitted
        // by a different arm of IsFallbackSafeBareSizeLeaf — the one protected STRUCTURALLY, because the driver
        // renders it {$sum: {$map: …}} and $map tolerates a missing array where $size does not — so it never
        // consults the rewrite, never sees the constructibility dimension, and correctly stays native.
        //
        // States exercised: ALL FOUR, read INTO. The NativeOnly leg is the routing proof; the late-decline legs
        // are what prove the un-stripped driver push-down answers correctly for this shape too.
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
        // State exercised: element ABSENT — deliberately, because it is one of the two states a bare $size
        // aborts on, and the terminator narrows the result to exactly that row.
        //
        // `Select(…).First()` is the SECOND captured-chain shape the A4-0 rewrite navigates — the pushed-down
        // Select is not the outermost node — and it had no coverage at any level when the rewrite first
        // shipped. The predicate is a captured local so the native factory still declines LATE, which is the
        // route where the missing array actually reaches the driver. NOTE the terminator must sit DIRECTLY on
        // the Select: an operator composed between them (`.Skip(2).First()`) is the known composed-operator
        // gap, not this shape.
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

    // ── A4-2: the tier-2 admission for the two SIZE kinds ───────────────────────────────────────────────

    [Fact]
    public void Bare_filtered_count_leaf_goes_native_for_every_array_state()
    {
        // States exercised: ALL FOUR, read INTO. The filtered spelling is a SEPARATE node kind
        // (MongoFilteredSizeExpression, the "sibling, not a flag" decision in Query/AGENTS.md), so gate 1a names
        // it explicitly and this case is the only thing that pins that arm end to end.
        //
        // The predicate is chosen to DISCRIMINATE from the unfiltered vector: p1_two has headings h0 and h1, so
        // `Heading == "h0"` counts 1 there rather than 2 — [1,0,0,0,1] against the unfiltered [2,0,0,0,1]. A
        // `Heading != null` predicate would have produced the same vector as the unfiltered count and the test
        // could not have told a filtered rendering from an unfiltered one.
        var (collection, _) = Seed(nameof(Bare_filtered_count_leaf_goes_native_for_every_array_state));
        var prefix = "p";

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(ExpectedFilteredCounts,
                db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Heading == "h0")).ToList());
        }

        // THE LATE-DECLINE LEG, and for this shape it is doing something the unfiltered one's is not. The A4-0
        // rewrite does NOT cover a filtered count (TryRewriteSelect matches the one-argument Count only), so the
        // driver's un-stripped push-down renders it with a BARE `$Posts` input — see
        // Bare_count_leaves_render_size_over_ifNull_natively_and_on_the_fallback. It answers correctly anyway,
        // and that is the MEASURED fact that makes admitting the filtered kind safe without a rewrite of its
        // own: the driver renders `{$sum: {$map: …}}` rather than `$size`, and `$map` over a missing or
        // explicitly-null array yields missing instead of aborting the aggregate.
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
        // THE MQL PIN FOR THE PREREQUISITE, and it is the only assertion in this file that can distinguish
        // "answers correctly" from "answers correctly for the reason the design claims". The tier-2 revert was
        // caused by exactly one byte-level difference — the driver rendering a bare `{"$size": "$Posts"}` where
        // native renders `$size` over `$ifNull` — and a values-only test cannot see it on a seed whose arrays
        // all exist. Asserting it on BOTH the native route and the explicit-DriverLinq one is what proves the
        // A4-0 rewrite really reaches the fallback rather than only the native emit.
        var (collection, _) = Seed(nameof(Bare_count_leaves_render_size_over_ifNull_natively_and_on_the_fallback));

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContextWithLogging(collection, mode, out var spy);
            _ = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();
            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;

            Assert.Contains("\"_v\" : { \"$size\" : { \"$ifNull\" : [\"$Posts\", []] } }", mql);
            // Asserted in the NEGATIVE direction too: the bare form is the exact rendering that aborts, so a
            // test that only looked for the coalesced substring would stay green if both appeared.
            Assert.DoesNotContain("\"$size\" : \"$Posts\"", mql);
        }

        // The FILTERED kind, pinned as it actually renders rather than as a reader would assume. Native wraps
        // `$filter`'s input in `$ifNull`; the DRIVER renders `{$sum: {$map: {input: "$Posts", …}}}` with a bare
        // input and NO `$ifNull` — because the A4-0 rewrite is scoped to the unfiltered count. That asymmetry
        // is deliberate and is safe only because `$map` tolerates a missing array where `$size` does not, so it
        // is pinned here rather than left to be re-derived.
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
        // States exercised: ALL FOUR — Skip(1) drops p1_two, leaving the empty, absent, explicitly-null and
        // present-1 rows, which is the ragged half rather than the well-formed one.
        //
        // THE SHAPE A READER OF THE A4-0 REWRITE WILL EXPECT TO BE BROKEN, and it is not — for a reason worth
        // recording because it is not visible in the rewrite's own code. NullCoalesceSyntheticBareCountBody
        // navigates only two captured-chain shapes: the pushed-down Select as the OUTERMOST node, or one under a
        // single no-arg cardinality terminator. `Select(…).Skip(1)` looks like neither. MEASURED: EF's
        // nav-expansion carries the projection as a PENDING SELECTOR and applies it last, so the captured chain
        // really is `Skip(…).Select(…)` with the Select outermost, and the rewrite fires — the emitted fallback
        // MQL is `[$match, $sort, $skip, $project{_v: $size($ifNull(…))}]`, asserted below.
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

        // And on the LATE-DECLINE route, which is the one that would abort if the rewrite had not been reached.
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
        // States exercised: ALL FOUR. `b.Posts.Count()` and `b.Posts.LongCount()` are DIFFERENT captured
        // spellings from the `.Count` PROPERTY the rest of this file uses, and the A4-0 rewrite matches on the
        // captured `Queryable.Count`/`LongCount` call — so whether the property and the extension lower to the
        // same captured shape was UNVERIFIED until this case. They do: the late-decline leg answers 0 for the
        // missing and explicitly-null rows, which is only possible if the rewrite matched.
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

            // LongCount carries its own late-decline leg rather than relying on Count()'s. The A4-0 rewrite
            // matches Count and LongCount as two separate names, so a regression could plausibly reach one and
            // not the other, and this is the route where such a miss aborts rather than merely falling back.
            Assert.Equal(ExpectedCounts.Select(c => (long)c),
                db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.LongCount()).ToList());
        }
    }

    [Fact]
    public void Primitive_collection_bare_count_is_NOT_admitted_and_still_aborts_on_a_ragged_array()
    {
        // A PIN OF MEASURED BEHAVIOUR, NOT OF CORRECT BEHAVIOUR — the same convention as
        // Ef362OwnedHopArrayProjectionTests' dotted-scalar pin. `b.Tags.Count` over a PRIMITIVE collection is
        // not natively representable, so it never reaches the tier-2 gate at all; the whole projection declines
        // and the DRIVER renders a bare `{"$size": "$Tags"}`, which aborts on the missing and explicitly-null
        // rows under the default Native mode and under explicit DriverLinq alike.
        //
        // MEASURED byte-identical at this task's base and at HEAD — same exception, same emitted MQL — so A4-2
        // neither causes nor closes it, and the A4-0 rewrite was deliberately NOT widened to it (it would have
        // been unrequested scope for a shape the rewrite cannot even reach). This case exists so the next reader
        // finds the fact measured rather than re-deriving it, and so a future widening has to move it.
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

        // The same leaf over a seed with NO ragged rows answers correctly, which is what makes the line above a
        // statement about the ARRAY STATE rather than about primitive collections in general.
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
        // A SECOND PIN OF MEASURED BEHAVIOUR — and the one a reviewer of A4-2 is most likely to mistake for a
        // regression this task introduced, so it is measured on both sides rather than argued.
        //
        // `Select(b => b.Posts.Count)` followed by Distinct, Sum or an OrderBy over the projected value USED TO
        // abort with MongoCommandException on the missing and explicitly-null rows, under the default Native
        // mode and under explicit DriverLinq: the pushed-down Select was NEITHER outermost NOR under a no-arg
        // cardinality terminator, so NullCoalesceSyntheticBareCountBody didn't reach it and the driver rendered
        // a bare `$size`.
        //
        // EF-359's MongoEFToLinqTranslatingExpressionVisitor.TryRewriteEmbeddedCollectionNavigationCount closes
        // this independently of chain position — it coalesces the field access wherever
        // `Queryable.Count(AsQueryable(EF.Property(...)))` appears in the residual EF-to-driver-LINQ tree, not
        // just at the pushed-down Select's own body — so all three legs now answer correctly (missing/null rows
        // count as 0) in both modes instead of aborting.
        var (collection, _) = Seed(nameof(Bare_count_leaf_under_a_REDUCING_operator_no_longer_aborts_on_a_ragged_array));
        var prefix = "p";

        // Counts by title: p1_two=2, p2_empty=0, p3_missing=0, p4_null=0, p5_one=1.
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
        // The hop fixture's own self-check, for the same reason the root fixture has one: "Notes element
        // absent" and "Notes explicitly BSON null" are two stored states that collapse into one the moment EF
        // has read them, so only the raw BSON can tell them apart. Without this, the two tests below could pass
        // while covering three states instead of four — which is exactly how the pre-existing hop fixture
        // (every row's Home.Notes an empty array) hid a live regression.
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
        // THE C1 REGRESSION TEST. States exercised: ALL FOUR, read INTO, on the HOP navigation.
        //
        // `b.Home.Notes.Count` translates to a MongoSizeExpression just like `b.Posts.Count` does, so the first
        // version of arm 1a — which ran no check on the leaf at all — admitted it. But the A4-0 rewrite's
        // IsNavigationOnParameter requires the navigation to be rooted DIRECTLY at the selector parameter, so a
        // two-hop chain is never coalesced, the Synthetic tier suppresses the strip, and the driver's
        // un-stripped push-down renders a bare {"$size": "$Home.Notes"}. MEASURED on this fixture, base vs. that
        // version: three routes went from `2,0,0,0,1` to
        //   MongoCommandException : The argument to $size must be an array, but was of type: missing
        // — explicit DriverLinq, DriverLinq late-decline, and the DEFAULT Native late-decline leg.
        //
        // The gate now DECLINES a dotted array path, so this shape is back to its base behaviour: a graceful
        // fallback with correct values in every mode. The decline is the point of the test; the values are what
        // make it a regression net rather than a routing assertion.
        var (collection, _) = SeedHop(nameof(Bare_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly));
        var prefix = "h";

        // COLLECT-THEN-ASSERT: every leg runs, then the whole set is asserted. See LegOutcome's remarks — a
        // regression in an early leg must not hide the abort legs, which are the ones this test exists for.
        var expected = "[" + string.Join(",", ExpectedCounts) + "]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateHopContext(collection, mode);

            // The DIRECT route — no decline involved. This is the leg that went red under explicit DriverLinq.
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Home.Notes.Count).ToList())));

            // And the LATE-DECLINE route, which is the leg that went red under the DEFAULT Native mode — the
            // one a DriverLinq-only escape hatch would not have saved.
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
        // States exercised: ALL FOUR, read INTO, on the HOP navigation.
        //
        // The filtered kind through a hop is declined by the SAME dotted-path rule, and that is a DELIBERATE
        // uniformity rather than a necessity: a filtered count is structurally protected without any rewrite
        // (the driver renders `{$sum: {$map: …}}`, and `$map` tolerates a missing array where `$size` does
        // not), so this shape could have been admitted. It is not, because one rule over both size kinds is
        // checkable by reading one method, whereas "unfiltered must be non-dotted, filtered may be anything"
        // is two rules a later reader has to keep matched against two different protection mechanisms. Recorded
        // here so that widening it later is a deliberate choice rather than a repeat of C1 in reverse.
        var (collection, _) =
            SeedHop(nameof(Bare_FILTERED_count_leaf_through_an_owned_reference_HOP_is_declined_and_answers_correctly));
        var prefix = "h";

        // Collect-then-assert, for the reason the sibling case states.
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
        // THE SHAPE WHOSE MANDATORY explicit-DriverLinq LEG WAS UNMET AT THE FIRST COMMIT. A bare filtered count
        // whose ELEMENT predicate closes over a captured local is newly admitted by arm 1a, and the only test
        // that touched it was a Task-5 tripwire whose first `Assert.Throws` iteration aborted the whole test —
        // so its DriverLinq and NativeOnly legs never executed and the "zero MongoCommandException" claim did
        // not cover them. This case runs every leg, on its own fixture, for that shape specifically.
        //
        // States exercised: ALL FOUR, read INTO. The captured local also has to survive being handed to the
        // DRIVER to render on the un-stripped fallback, which the late-decline leg below is the only thing to
        // prove.
        var (collection, _) = Seed(nameof(Bare_filtered_count_leaf_with_a_CAPTURED_PARAMETER_goes_native));
        var heading = "h0";
        var prefix = "p";

        // Collect-then-assert: the NativeOnly leg runs FIRST here, so a regression in it would otherwise abort
        // the test before the mandatory explicit-DriverLinq leg ever ran — the very defect this case was added
        // to close. See LegOutcome's remarks.
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
    /// Runs <paramref name="query"/> and describes what it did as a short string, so a caller can COLLECT every
    /// leg's outcome and assert them together instead of aborting on the first.
    /// </summary>
    /// <remarks>
    /// The collect-then-assert shape is not a style preference. Written as a loop of direct assertions, a
    /// regression in the FIRST leg aborts the test and the remaining legs never execute — which is exactly how
    /// this slice's mandatory explicit-<c>DriverLinq</c> leg went unexercised while the slice claimed "zero
    /// MongoCommandException across all runs" (EF-405 A4-2 review, I2). Collecting first means a regression
    /// reports what EVERY mode did, and in particular cannot hide an abort behind an earlier failure.
    /// </remarks>
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
        // States exercised: ALL FOUR. The independent oracle for the count vector every other test in this file
        // asserts positionally: whole entities are materialized and the selector is evaluated CLIENT-side, in
        // memory, with no projection anywhere in the query. Deliberately NOT a projection query — comparing a
        // projection against a projection would be asserting this slice against itself.
        var (collection, _) = Seed(nameof(Whole_entity_materialization_is_the_oracle_for_every_array_state));

        using var db = CreateContext(collection, MongoQueryMode.Native);

        var oracle = db.Entities.AsNoTracking().ToList()
            .OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();

        Assert.Equal(ExpectedCounts, oracle);
    }
}
