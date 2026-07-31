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
using System.Linq.Expressions;
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
/// EF-322: an element COUNT over an OWNED (embedded) collection navigation, compared against a value, translates
/// natively — as an array-index existence test for an integer-constant threshold, and as $expr over a null-safe
/// $size otherwise. Each admitted shape asserts a NativeOnly routing proof; each excluded shape asserts a clean
/// decline.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionCountTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

    // MQL-capture idiom copied from NativeSelectManyTests.cs (the sibling NativeOwnedCollectionPredicateTests.cs
    // this file otherwise mirrors has NO MQL-asserting test to copy from — TestMqlLoggerFactory/AssertMql live
    // only in the SpecificationTests project; FunctionalTests captures MQL via SpyLoggerProvider instead).
    private SingleEntityDbContext<T> CreateContextWithLogging<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction,
        out SpyLoggerProvider spyLogger)
        where T : class
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    // Full-message equality would also have to match the "Executed MQL query\n<namespace>.aggregate([...])"
    // wrapper NativeSelectManyTests.cs's idiom leaves out — Assert.Contains against the captured pipeline
    // fragment (the actual idiom that file uses) pins the pipeline shape without coupling to that wrapper.
    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    public class Post
    {
        // Nullable ON PURPOSE: a missing or explicitly-null stored field must MATERIALIZE (as null) rather
        // than throw, or the missing-field state cannot be exercised at all. A required non-nullable element
        // property with a missing field is a separate, pre-existing materialization concern (it throws in
        // every mode) and is deliberately out of this file's scope.
        public int? Rank { get; set; }
        public string? Heading { get; set; }
        public int? Other { get; set; }

        // DELIBERATELY COLLIDES with Blog.Title so the correlated-element-predicate guard is exercised on an
        // input that would otherwise be ACCEPTED — the element-scoped translator resolves members by NAME.
        public string Title { get; set; } = "";

        public List<Comment> Comments { get; set; } = [];
    }

    public class Comment
    {
        public int? Age { get; set; }
    }

    public class Home
    {
        public List<Note> Notes { get; set; } = [];
    }

    public class Note
    {
        public int? Length { get; set; }
    }

    // A named DTO, not an anonymous type, so the SAME Expression<Func<Blog, TitleCount>> can be sent to the
    // server AND compiled for the in-memory oracle. It also exercises NativeProjectionBinder's MemberInit
    // branch, which the anonymous-type tests do not reach.
    public class TitleCount
    {
        public string Title { get; set; } = "";
        public int N { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
    {
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));
        mb.Entity<Blog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));
    };

    // Rows differ only in ARRAY LENGTH (0-3) plus the three "no elements" states, because that is the entire
    // input space a cardinality predicate is sensitive to. Element FIELD values are irrelevant here — unlike
    // the Any/All slices, where the element predicate was the thing under test.
    private static BsonDocument LenRow(string title, int length)
    {
        var posts = new BsonArray();
        for (var i = 0; i < length; i++)
            posts.Add(PostDoc(rank: i, heading: "h" + i));
        return Row(title, posts);
    }

    private static BsonDocument PostDoc(int? rank, string? heading)
        => new()
        {
            { "Rank", rank.HasValue ? rank.Value : BsonNull.Value },
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", 0 }, { "Title", "p" }, { "Comments", new BsonArray() }
        };

    private static BsonDocument PostWithComments(string heading, int commentCount)
    {
        var comments = new BsonArray();
        for (var i = 0; i < commentCount; i++)
            comments.Add(new BsonDocument { { "Age", i } });
        return new BsonDocument
        {
            { "Rank", 0 }, { "Heading", heading }, { "Other", 0 }, { "Title", "p" }, { "Comments", comments }
        };
    }

    // Home/Tags are always seeded present-but-empty: both are separate required properties on Blog, and a
    // document missing them fails materialization with an unrelated error the moment a predicate returns the
    // row as a full Blog.
    private static BsonDocument Row(string title, BsonValue? posts)
    {
        var doc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray() }
        };
        if (posts is not null)
            doc.Add("Posts", posts);
        return doc;
    }

    private static BsonDocument RowWithNotes(string title, int noteCount)
    {
        var notes = new BsonArray();
        for (var i = 0; i < noteCount; i++)
            notes.Add(new BsonDocument { { "Length", i } });
        return new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", notes } } },
            { "Posts", new BsonArray() }, { "Tags", new BsonArray() }
        };
    }

    // Combines LenRow's Posts-length control with RowWithNotes's Notes-length control on a SINGLE row — neither
    // helper alone can give one row a non-empty Posts AND a non-empty, DIFFERENT-length Home.Notes, which is
    // exactly what Count_projection_alongside_sibling_leaves_goes_native needs to make its third leaf
    // load-bearing. Built from the same element-document shapes those two helpers already use (PostDoc; the
    // {"Length": i} note literal), not a new document shape.
    private static BsonDocument LenRowWithNotes(string title, int postLength, int noteCount)
    {
        var posts = new BsonArray();
        for (var i = 0; i < postLength; i++)
            posts.Add(PostDoc(rank: i, heading: "h" + i));
        var notes = new BsonArray();
        for (var i = 0; i < noteCount; i++)
            notes.Add(new BsonDocument { { "Length", i } });
        return new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", notes } } },
            { "Posts", posts }, { "Tags", new BsonArray() }
        };
    }

    private static BsonDocument RowWithTags(string title, params string[] tags)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Posts", new BsonArray() },
            { "Tags", new BsonArray(tags) }
        };

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Every array LENGTH state a cardinality predicate can distinguish, plus the three "no elements" states.
    // "missing" and "null" are the rows a query-dialect $size form would get WRONG (neither matches $size: 0,
    // but LINQ's Count is 0 for both).
    private IMongoCollection<Blog> SeedLengths(string name)
        => Seed(name,
            LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3),
            Row("missing", posts: null), Row("null", BsonNull.Value));

    // Rows whose Posts is a real, non-null ARRAY. The driver's own count translation renders $size under $expr
    // and ABORTS the aggregate on a missing or explicitly-null array, so the DriverLinq oracle leg can only run
    // against these rows — the same two-seed split the Any/All slices established.
    private IMongoCollection<Blog> SeedWellFormed(string name)
        => Seed(name, LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3));

    // Runs the query under NativeOnly (routing proof) and under DriverLinq (value oracle), asserts the two
    // agree on the matched set, and returns the matched titles.
    private List<string> AssertNativeAndParity(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        List<string> nativeOnly;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            nativeOnly = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, nativeOnly);
        return nativeOnly;
    }

    // Asserts a shape is NOT native: it throws NativeTranslationNotSupportedException under NativeOnly
    // (a clean decline, not a crash), AND that the fallback it relies on actually delivers correct,
    // independently-cross-checked results — Native == DriverLinq, both returned to the caller to assert
    // against a hand-verified expected value.
    private List<string> AssertDeclinesCleanly(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => query(db.Entities.AsNoTracking()).ToList());
        }

        List<string> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            native = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, native);
        return native;
    }

    // Proves a shape goes native (NativeOnly succeeds) without a driver-LINQ oracle leg — used for the
    // full-matrix seed, whose missing/null Posts rows abort the driver's own translation.
    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    // NOTE: `threshold` is a [Theory] parameter captured into the lambda, so EF parameterizes it and every row
    // here routes to the $expr tier, NOT the array-index constant form. That is the same trap the matrix comment
    // below documents. Kept as genuine $expr-tier coverage across thresholds 0-3; the constant tier's GreaterThan
    // arm is covered by Count_comparison_emits_the_array_index_form and the const-gt* matrix rows.
    [Theory]
    [InlineData(0, new[] { "len1", "len2", "len3" })]
    [InlineData(1, new[] { "len2", "len3" })]
    [InlineData(2, new[] { "len3" })]
    [InlineData(3, new string[0])]
    public void Count_greater_than_with_a_parameterized_threshold_goes_native(int threshold, string[] expected)
    {
        var collection = SeedLengths($"gt{threshold}");
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count > threshold));
        Assert.Equal(expected, titles);
    }

    [Fact]
    public void Count_equal_zero_matches_empty_missing_and_null_arrays()
    {
        // The decisive correctness row: LINQ's Count == 0 is TRUE for an empty, a MISSING and an explicitly-null
        // array (EF materializes a missing embedded array as an empty list). A query-dialect { $size: 0 } form
        // would match only "len0" — which is why $size was rejected as the primary rendering.
        var collection = SeedLengths(nameof(Count_equal_zero_matches_empty_missing_and_null_arrays));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count == 0));

        Assert.Equal(new[] { "len0", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_equal_nonzero_goes_native()
    {
        var collection = SeedLengths(nameof(Count_equal_nonzero_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count == 2));
        Assert.Equal(new[] { "len2" }, titles);
    }

    [Fact]
    public void Count_not_equal_goes_native()
    {
        var collection = SeedLengths(nameof(Count_not_equal_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count != 2));
        Assert.Equal(new[] { "len0", "len1", "len3", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_less_than_goes_native()
    {
        var collection = SeedLengths(nameof(Count_less_than_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count < 2));
        Assert.Equal(new[] { "len0", "len1", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_less_than_or_equal_goes_native()
    {
        var collection = SeedLengths(nameof(Count_less_than_or_equal_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count <= 1));
        Assert.Equal(new[] { "len0", "len1", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_greater_than_or_equal_goes_native()
    {
        var collection = SeedLengths(nameof(Count_greater_than_or_equal_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count >= 2));
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void Count_call_form_and_LongCount_go_native()
    {
        var collection = SeedLengths(nameof(Count_call_form_and_LongCount_go_native));

        Assert.Equal(new[] { "len2", "len3" },
            AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count() > 1)));
        Assert.Equal(new[] { "len2", "len3" },
            AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.LongCount() > 1L)));
    }

    [Fact]
    public void Reversed_operand_order_goes_native()
    {
        var collection = SeedLengths(nameof(Reversed_operand_order_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => 1 < b.Posts.Count));
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void A_parameterized_threshold_goes_native_via_the_expr_tier()
    {
        var collection = SeedLengths(nameof(A_parameterized_threshold_goes_native_via_the_expr_tier));
        var threshold = 1;

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count > threshold));

        // $ifNull is what keeps the missing/null rows from aborting the aggregate — without it this query
        // throws instead of returning rows.
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void Negated_count_comparison_goes_native()
    {
        var collection = SeedLengths(nameof(Negated_count_comparison_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => !(b.Posts.Count > 1)));
        Assert.Equal(new[] { "len0", "len1", "missing", "null" }, titles);
    }

    [Fact]
    public void Count_through_an_owned_reference_hop_goes_native()
    {
        var collection = Seed(nameof(Count_through_an_owned_reference_hop_goes_native),
            RowWithNotes("notes0", 0), RowWithNotes("notes2", 2));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Home.Notes.Count > 1));

        Assert.Equal(new[] { "notes2" }, titles);
    }

    [Fact]
    public void Count_inside_a_quantifier_goes_native()
    {
        // The constant tier is pure query dialect, so it is legal inside $elemMatch — where $expr is a hard
        // server error. This is the shape that would fail at EXECUTION time if the tier choice were wrong.
        var collection = Seed(nameof(Count_inside_a_quantifier_goes_native),
            Row("few", new BsonArray { PostWithComments("a", 1) }),
            Row("many", new BsonArray { PostWithComments("a", 3) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.Count > 2)));

        Assert.Equal(new[] { "many" }, titles);
    }

    [Fact]
    public void Arithmetic_projection_leaf_containing_a_count_goes_native()
    {
        // AN UNPLANNED INCIDENTAL WIDENING, surfaced by the Task 6 review and pinned here so a future change
        // cannot silently withdraw it. Because the count is recognized as an ORDINARY OPERAND in
        // TranslateOperand, an arithmetic projection leaf containing one now reaches
        // NativeProjectionBinder.TryTranslateLeaf's arithmetic branch and goes native. The count renders in
        // the $expr/aggregation dialect here (a $project leaf is not a $match), so the null-safe $size applies
        // and a missing/null array yields 0 rather than aborting the aggregate.
        //
        // Contrast with the BARE embedded-collection projection, Select(b => b.Posts.Count): unlike this
        // arithmetic leaf, it does NOT go native — a bare-scalar terminal projection never populates
        // Select.Projection, so Route stays Fallback and the count is folded CLIENT-SIDE instead (over an
        // aggregate([]) pipeline, no $size). EF-357 used to make it hard-fail in EVERY query mode
        // (ArgumentException, from a MongoProjectionBindingExpressionVisitor gap) long before the EF-322
        // native-query work began; that translation-time crash is now fixed, and TRANSLATION now succeeds with
        // CORRECT values for a document whose array is present — see
        // Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays below. EF-357 is
        // now FULLY closed: a missing or explicitly-null array used to throw ArgumentNullException at
        // MATERIALIZATION (Enumerable.Count(null)) — EF-358 fixed that by normalizing the projection path's
        // missing/null array to an empty collection — see
        // Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array below.
        var collection = SeedLengths(nameof(Arithmetic_projection_leaf_containing_a_count_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var doubled = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, X = b.Posts.Count * 2 })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0), ("len1", 2), ("len2", 4), ("len3", 6), ("missing", 0), ("null", 0)],
            doubled.Select(r => (r.Title, r.X)).ToArray());
    }

    [Fact]
    public void Owned_collection_count_projection_leaf_goes_native()
    {
        // The plain sibling of Arithmetic_projection_leaf_containing_a_count_goes_native above: `Count` on its
        // own, not wrapped in arithmetic. Before this slice the arithmetic form was native while the plain form
        // was not — the count reached TranslateOperand only as an operand of something else.
        var collection = SeedLengths(nameof(Owned_collection_count_projection_leaf_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0), ("len1", 1), ("len2", 2), ("len3", 3), ("missing", 0), ("null", 0)],
            rows.Select(r => (r.Title, r.N)).ToArray());
    }

    [Fact]
    public void Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_aborts_on_a_missing_array()
    {
        // The THIRD leg of the wrapped count's disposition, which the rest of this file leaves unmeasured: every
        // other wrapped-count test here runs under NativeOnly (or Native, for the oracle). Two reviewers of this
        // slice independently measured DriverLinq and found something worth pinning, and it was re-measured here
        // before this test was written rather than taken on trust.
        //
        // Before this slice the wrapped form threw ArgumentException in ALL THREE modes (Task 1 spike Q1), so
        // DriverLinq had no behaviour to preserve. It has one now, and it is NOT the same as Native's:
        //
        //   * present arrays  -> DriverLinq agrees with Native (0,1,2,3).
        //   * missing / explicitly-null array -> DriverLinq raises MongoCommandException, because the driver's
        //     LINQ provider renders a BARE server-side $size with no $ifNull, and $size against a missing or null
        //     value is a hard server error that aborts the whole aggregate. Native renders
        //     {$size: {$ifNull: ["$Posts", []]}} and answers 0, matching LINQ's whole-entity semantics.
        //
        // So for THIS shape, UseQueryMode(DriverLinq) does not restore equivalent RESULTS on ragged data — native
        // is strictly more correct. That is a divergence worth recording deliberately, not rediscovering: it is
        // also the rendering that a projection MIXING a count leaf with a binder-declined leaf would fall back to
        // under the default Native mode.
        //
        // This is not a regression introduced by the slice (there was no working DriverLinq behaviour to regress
        // from), and the exception TYPE is not part of the provider's contract here — the test exists so that a
        // change in either direction is noticed and re-documented.
        var wellFormed = SeedWellFormed(
            nameof(Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_aborts_on_a_missing_array));

        using (var db = CreateContext(wellFormed, MongoQueryMode.DriverLinq, BlogModel))
        {
            var rows = db.Entities.AsNoTracking()
                .Select(b => new { b.Title, N = b.Posts.Count })
                .ToList().OrderBy(r => r.Title).ToList();

            Assert.Equal(
                [("len0", 0), ("len1", 1), ("len2", 2), ("len3", 3)],
                rows.Select(r => (r.Title, r.N)).ToArray());
        }

        var ragged = SeedLengths(
            nameof(Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_aborts_on_a_missing_array)
            + "ragged");

        using (var db = CreateContext(ragged, MongoQueryMode.DriverLinq, BlogModel))
        {
            var ex = Assert.Throws<MongoCommandException>(
                () => db.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count })
                    .ToList());

            Assert.Contains("$size", ex.Message);
        }

        // The contrast leg, on the SAME ragged seed: native answers 0 for both the missing and the explicit-null
        // row. Without this the test would pin the abort without showing that native does better.
        using (var db = CreateContext(ragged, MongoQueryMode.Native, BlogModel))
        {
            var rows = db.Entities.AsNoTracking()
                .Select(b => new { b.Title, N = b.Posts.Count })
                .ToList().OrderBy(r => r.Title).ToList();

            Assert.Equal(
                [("len0", 0), ("len1", 1), ("len2", 2), ("len3", 3), ("missing", 0), ("null", 0)],
                rows.Select(r => (r.Title, r.N)).ToArray());
        }
    }

    [Theory]
    [InlineData("constant-5")]
    [InlineData("constant-0")]
    [InlineData("constant-false")]
    [InlineData("captured-parameter")]
    public void Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate(string leafKind)
    {
        // THE MUTATION GUARD for NativeProjectionBinder's node-kind gate — the `value is MongoSizeExpression`
        // test on the count leaf branch. Before this test, NOTHING protected that line: relaxing the gate to
        // plain `TryTranslateValue(...)` success left the entire functional Query namespace green, 0 failed
        // (measured during the branch review, under "Debug EF10"). No pass COUNT is quoted: three counts recorded
        // at different points in this branch's life were irreconcilable to a later reader, and a subsequent run of
        // the same filter gave another — see the same note on NativeProjectionBinder's count-leaf branch.
        //
        // Why the gate must stay narrow, as MEASURED rather than assumed: under a widened gate a bare constant
        // leaf renders as a BARE VALUE inside $project, and $project reads a bare value as an inclusion/exclusion
        // FLAG, not a literal. `X = 5` survives that (junk `X: 5` in the pipeline, values still correct via
        // Visit's own client-side constant fold), but `X = 0` and `X = false` make the server reject the whole
        // command: "Invalid $project :: caused by :: Cannot do exclusion on field X in inclusion projection".
        //
        // WHAT THIS TEST DISCRIMINATES, and why it asserts routing rather than only values: today these shapes
        // are NOT native — measured, not expected. NativeOnly throws NativeTranslationNotSupportedException
        // ("Query projects a non-entity result..."), and Native/DriverLinq return correct values through the
        // driver-LINQ fallback, which renders a constant safely as {$literal: 5} rather than a bare value. So a
        // values-only assertion would NOT catch the widening for `X = 5` — the values are correct either way.
        // The NativeOnly leg is what fails if the gate is widened (the shape starts going native and stops
        // throwing); the Native leg is what fails for the 0/false rows (the command aborts).
        var collection = SeedWellFormed(
            nameof(Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate) + leafKind);

        var captured = 7;

        // Each selector pairs a REAL member leaf with the constant/parameter leaf under test, so the projection
        // is exactly the "one admissible leaf + one bare-value leaf" shape a widened gate would wrongly accept.
        Expression<Func<Blog, string>> render = leafKind switch
        {
            "constant-5" => b => b.Title + "=" + 5,
            "constant-0" => b => b.Title + "=" + 0,
            "constant-false" => b => b.Title + "=" + false,
            _ => b => b.Title + "=" + 7
        };

        Func<SingleEntityDbContext<Blog>, List<string>> run = leafKind switch
        {
            "constant-5" => db => db.Entities.AsNoTracking().Select(b => new { b.Title, X = 5 })
                .ToList().Select(r => r.Title + "=" + r.X).OrderBy(v => v).ToList(),
            "constant-0" => db => db.Entities.AsNoTracking().Select(b => new { b.Title, X = 0 })
                .ToList().Select(r => r.Title + "=" + r.X).OrderBy(v => v).ToList(),
            "constant-false" => db => db.Entities.AsNoTracking().Select(b => new { b.Title, X = false })
                .ToList().Select(r => r.Title + "=" + r.X).OrderBy(v => v).ToList(),
            _ => db => db.Entities.AsNoTracking().Select(b => new { b.Title, X = captured })
                .ToList().Select(r => r.Title + "=" + r.X).OrderBy(v => v).ToList()
        };

        // The oracle is in-memory LINQ over the SAME rendering, so the expected values cannot silently drift.
        List<string> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var compiled = render.Compile();
            expected = db.Entities.AsNoTracking().ToList().Select(compiled).OrderBy(v => v).ToList();
        }

        // Native: correct values. Reddens if a widened gate emits a bare 0/false and the server aborts the
        // command, and reddens if a widened gate ever produced a wrong value.
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            Assert.Equal(expected, run(db));
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            Assert.Equal(expected, run(db));
        }

        // NativeOnly: the shape DECLINES. This is the leg that reddens the moment the node-kind gate is widened,
        // for every row of this theory including `X = 5`.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => run(db));
        }
    }

    [Fact]
    public void Owned_collection_count_projection_emits_a_null_safe_size()
    {
        // $ifNull is MANDATORY, not defensive: $size against a missing or explicitly-null array is a hard server
        // error that aborts the whole aggregate, not merely a wrong answer. The "missing" and "null" rows in this
        // seed are what would abort without it.
        var collection = SeedLengths(nameof(Owned_collection_count_projection_emits_a_null_safe_size));

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count }).ToList();

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$project", mql);
        Assert.Contains("$size", mql);
        Assert.Contains("$ifNull", mql);
        Assert.Contains("Posts", mql);
    }

    [Fact]
    public void Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays()
    {
        // EF-357 is now FULLY closed — see
        // Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array below, which pins
        // the second half of that closure (EF-358's materialization-time fix).
        //
        // This shape used to throw ArgumentException in EVERY query mode — not a graceful fallback, no data at
        // all — because the projection-binding shaper fold (which runs at TRANSLATION time, before
        // MongoQueryMode is read) rebuilt Queryable.Count over a CollectionShaperExpression typed List<T> and BCL
        // expression validation rejected the mismatch. It predates the whole EF-322 native work stream.
        //
        // It is deliberately NOT native: a bare-scalar terminal projection never populates Select.Projection — a
        // pre-existing SP3-wide boundary, not a count-specific one — so Route stays Fallback and NativeOnly still
        // declines cleanly. Closing EF-357 was about correct results, not about routing.
        //
        // MEASURED (Task 1 spike, not assumed): the count is folded CLIENT-SIDE over the fetched document — the
        // emitted pipeline is aggregate([]), with no $size and no $project. The driver's LINQ provider is never
        // asked to render the count, because the rebuilt Enumerable.Count runs over an already-materialized
        // collection shaper. This seed is deliberately SeedWellFormed for the reason the companion test explains.
        var collection = SeedWellFormed(
            nameof(Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays));

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.Native, BlogModel, out var spyLogger))
        {
            var counts = db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList();
            Assert.Equal(new[] { 0, 1, 2, 3 }, counts);

            // Pins the "client-side, over aggregate([])" claim made above: the emitted pipeline for the
            // Native-mode run IS aggregate([]) exactly, with no $project and no $size stage anywhere in it.
            var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
            Assert.Contains("aggregate([])", mql);
            Assert.DoesNotContain("$project", mql);
            Assert.DoesNotContain("$size", mql);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            var counts = db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList();
            Assert.Equal(new[] { 0, 1, 2, 3 }, counts);
        }

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array()
    {
        // EF-357 is now FULLY closed, and this test records the second half of that closure. Owned-data slice 7
        // removed the TRANSLATION-time ArgumentException; EF-358 removed the MATERIALIZATION-time
        // ArgumentNullException this test used to assert, by making EVERY path — projection included —
        // normalize a missing or explicitly-null stored array to an empty collection. NOT "matching whole-entity,
        // which always did" — measured false: pre-fix nothing normalized on any path, including whole-entity;
        // see ProjectedCollectionNormalizationTests' class doc comment and the src/ EF-358 comments for the
        // corrected mechanism (a CLR field-initializer artifact, not a provider guarantee, was what made
        // whole-entity APPEAR to already normalize in some fixtures).
        //
        // The shape is still NOT native: a bare-scalar projection body never populates Select.Projection, which
        // is the SP3-wide bare-scalar boundary rather than anything count-specific, so the count is still folded
        // client-side over aggregate([]) — see
        // Bare_embedded_collection_Count_projection_returns_correct_counts_for_present_arrays, which pins that
        // MQL. What changed is only that the client-side fold now receives an empty collection instead of null.
        //
        // The native WRAPPED form was always correct for all three states via $ifNull and is unaffected.
        var collection = SeedLengths(
            nameof(Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);

            var counts = db.Entities.AsNoTracking()
                .Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList();

            Assert.Equal([0, 0, 0, 1, 2, 3], counts);
        }
    }

    [Fact]
    public void Count_inside_an_owned_SelectMany_inner_filter_goes_native()
    {
        // The MongoFieldPrefixRewriter case added in Task 2 is LOAD-BEARING, not defensive: an owned
        // SelectMany's inner filter reaches Rewrite, and the count's array path is ELEMENT-relative
        // ("Comments"), which the rewriter must prefix to "Posts.Comments" to address the $unwind-ed element.
        // Without that case this shape THROWS inside pre-existing code instead of working — the same emergent
        // capability (and the same ordering hazard) the Any slice recorded for its own $elemMatch case.
        var collection = Seed(nameof(Count_inside_an_owned_SelectMany_inner_filter_goes_native),
            Row("blog", new BsonArray { PostWithComments("few", 1), PostWithComments("many", 3) }));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var headings = db.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => p.Comments.Count > 2), (b, p) => new { p.Heading })
            .ToList().Select(x => x.Heading).OrderBy(h => h).ToList();

        Assert.Equal(new[] { "many" }, headings);
    }

    [Fact]
    public void Count_predicate_matches_driver_linq_on_well_formed_rows()
    {
        var collection = SeedWellFormed(nameof(Count_predicate_matches_driver_linq_on_well_formed_rows));
        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.Count > 1));
        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Fact]
    public void Count_predicate_is_correct_for_a_tracking_query()
    {
        var collection = SeedLengths(nameof(Count_predicate_is_correct_for_a_tracking_query));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.Where(b => b.Posts.Count > 1).ToList()
            .Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(new[] { "len2", "len3" }, titles);
    }

    [Theory]
    [InlineData(">", "{ \"Posts.2\" : { \"$exists\" : true } }")]
    [InlineData(">=", "{ \"Posts.1\" : { \"$exists\" : true } }")]
    [InlineData("<", "{ \"Posts.1\" : { \"$exists\" : false } }")]
    [InlineData("<=", "{ \"Posts.2\" : { \"$exists\" : false } }")]
    public void Count_comparison_emits_the_array_index_form(string op, string expectedMatch)
    {
        var collection = SeedWellFormed($"mql{op.Length}{op[0]}");
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        var query = op switch
        {
            ">" => db.Entities.AsNoTracking().Where(b => b.Posts.Count > 2),
            ">=" => db.Entities.AsNoTracking().Where(b => b.Posts.Count >= 2),
            "<" => db.Entities.AsNoTracking().Where(b => b.Posts.Count < 2),
            _ => db.Entities.AsNoTracking().Where(b => b.Posts.Count <= 2)
        };
        query.ToList();

        AssertMql(spy, expectedMatch);
    }

    [Fact]
    public void Count_equality_emits_a_merged_two_key_match()
    {
        var collection = SeedWellFormed(nameof(Count_equality_emits_a_merged_two_key_match));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => b.Posts.Count == 2).ToList();

        AssertMql(spy, "{ \"Posts.1\" : { \"$exists\" : true }, \"Posts.2\" : { \"$exists\" : false } }");
    }

    [Fact]
    public void A_parameterized_threshold_emits_expr_with_a_null_safe_size()
    {
        var collection = SeedWellFormed(nameof(A_parameterized_threshold_emits_expr_with_a_null_safe_size));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);
        var threshold = 1;

        db.Entities.AsNoTracking().Where(b => b.Posts.Count > threshold).ToList();

        // A single pinned fragment, not three independent Assert.Contains checks — three separate substring
        // assertions ($ifNull, $size, $expr) would all still pass even if $ifNull/$size were nested in the
        // wrong order, since Assert.Contains says nothing about their relative position or nesting.
        AssertMql(spy,
            "{ \"$expr\" : { \"$gt\" : [{ \"$size\" : { \"$ifNull\" : [\"$Posts\", []] } }, 1] } }");
    }

    [Fact]
    public void Bare_Any_still_emits_the_index_zero_form()
    {
        // The bare-Any() unification's byte-identity bar, asserted from the user-facing side.
        var collection = SeedWellFormed(nameof(Bare_Any_still_emits_the_index_zero_form));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => b.Posts.Any()).ToList();

        AssertMql(spy, "{ \"Posts.0\" : { \"$exists\" : true } }");
    }

    [Fact]
    public void Negated_bare_Any_still_emits_the_index_zero_absent_form()
    {
        var collection = SeedWellFormed(nameof(Negated_bare_Any_still_emits_the_index_zero_absent_form));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => !b.Posts.Any()).ToList();

        AssertMql(spy, "{ \"Posts.0\" : { \"$exists\" : false } }");
    }

    [Fact]
    public void A_predicated_Count_now_goes_native()
    {
        // USED TO PIN a decline: "Count(pred) has no array-index form; it needs $expr over $filter — a separate
        // slice." EF-359 Task 2 is that separate slice — the PREDICATE spelling (this shape) now goes native via
        // $expr over a null-safe $size of a $filter (MongoFilteredSizeExpression, from EF-359 Task 1). Results are
        // unchanged; only the routing flipped from fallback to native. See
        // NativeOwnedCollectionFilteredCountTests for the full breadth (thresholds, MQL shape, correlated/regex/
        // primitive-collection/nested-quantifier declines). The PROJECTION spelling (shape A,
        // Filtered_count_projection_is_a_known_preexisting_hard_fail_in_every_mode below) is untouched by this
        // task and still hard-fails in every mode.
        //
        // PARITY (fix round 1): this is the ONE task in the EF-359 slice where translated results could actually
        // change (a wrong $filter/$size composition returns wrong rows, not a decline), so AssertNativeAndParity —
        // NativeOnly succeeds AND agrees with DriverLinq — replaces the routing-only AssertNativeOnlyMatches the
        // original flip used. The seed is SeedLengths, not SeedWellFormed, so parity is asserted across the
        // RAGGED matrix too (missing/explicitly-null Posts), not just well-formed arrays: the Task 0 spike measured
        // the driver-LINQ fallback for this shape ($sum over $map) tolerates a missing/null array exactly like the
        // native $ifNull-wrapped form does, so there is no ragged-row caveat to restrict the seed for.
        var collection = SeedLengths(nameof(A_predicated_Count_now_goes_native));

        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.Count(p => p.Rank > 0) > 1));

        // len2 has ranks {0,1} → one passes; len3 has {0,1,2} → two pass; missing/null have zero elements → 0 matches.
        Assert.Equal(new[] { "len3" }, titles);
    }

    [Fact]
    public void A_primitive_collection_Count_declines_and_falls_back_to_correct_rows()
    {
        // TryResolveOwnedCollectionPath requires an embedded collection NAVIGATION; Tags is a mapped
        // primitive-collection PROPERTY. Deferred deliberately — the right slice lights up Any/All/.Count for
        // primitive collections together.
        var collection = Seed(nameof(A_primitive_collection_Count_declines_and_falls_back_to_correct_rows),
            RowWithTags("notags"), RowWithTags("twotags", "a", "b"));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Tags.Count > 1));

        Assert.Equal(new[] { "twotags" }, titles);
    }

    [Fact]
    public void A_parameterized_count_inside_a_quantifier_declines_and_falls_back_to_correct_rows()
    {
        // $expr is a HARD SERVER ERROR inside $elemMatch, so the parameterized tier must decline there rather
        // than emit an unrunnable query. IsQueryDialectRenderable does that with no dedicated guard.
        var collection = Seed(
            nameof(A_parameterized_count_inside_a_quantifier_declines_and_falls_back_to_correct_rows),
            Row("few", new BsonArray { PostWithComments("a", 1) }),
            Row("many", new BsonArray { PostWithComments("a", 3) }));
        var threshold = 2;

        var titles = AssertDeclinesCleanly(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.Count > threshold)));

        Assert.Equal(new[] { "many" }, titles);
    }

    [Fact]
    public void A_negated_parameterized_count_declines_and_falls_back_to_correct_rows()
    {
        // The accepted asymmetry: Count <= @param is native, but !(Count > @param) declines, because the
        // negator is gated on query-dialect renderability. A coverage gap, not a correctness one.
        var collection = SeedWellFormed(
            nameof(A_negated_parameterized_count_declines_and_falls_back_to_correct_rows));
        var threshold = 1;

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => !(b.Posts.Count > threshold)));

        Assert.Equal(new[] { "len0", "len1" }, titles);
    }

    // ------------------------------------------------------------------
    // Differential matrix — the primary correctness bar for the index arithmetic
    // ------------------------------------------------------------------
    //
    // An off-by-one in the index arithmetic, or a wrong negation direction, returns WRONG ROWS rather than
    // declining — and the driver-LINQ oracle cannot cover the missing/null-array rows (its own count
    // translation aborts the aggregate on such a document). So the oracle here is IN-MEMORY LINQ over the
    // materialized entities: the SAME expression is sent to the server and, compiled, evaluated client-side.
    // Using one expression for both legs is what makes this a real differential test rather than two
    // hand-written predicates that can silently disagree.

    public static TheoryData<string, Expression<Func<Blog, bool>>> CountMatrixCases()
    {
        var data = new TheoryData<string, Expression<Func<Blog, bool>>>();

        // ---- CONSTANT tier: literal thresholds, written out because a literal cannot come from a loop ----
        //
        // These MUST be inline literals. A captured loop variable becomes an EF query PARAMETER, which routes
        // to the $expr tier — so a loop here would exercise the index arithmetic in ZERO rows, leaving the
        // off-by-one risk (the whole reason this matrix exists) completely untested. Thresholds 0/1/2 cover
        // every boundary the arithmetic distinguishes: 0 is the degenerate/upper-bound-only case, 1 is the
        // bare-Any() equivalence point, 2 is a generic interior value.
        //
        // MEASURED CAVEAT — three of the rows below do NOT exercise the arithmetic their predicate text implies,
        // because EF Core rewrites `Count() > 0` into `Any()` upstream of this provider's translator (confirmed
        // by instrumenting MongoExpressionTranslator.TryTranslate: the incoming expression for
        // `b.Posts.Count > 0` is literally `Property(b, "Posts").AsQueryable().Any()`). Those rows therefore take
        // the bare-Any()/GreaterThanOrEqual path, not TryRenderSizeComparison's GreaterThan arm:
        //   const-gt0     -> Any()
        //   and           -> (Any() AndAlso (Count() < 3))
        //   nested-count  -> Posts.Any(o => Comments.Any())
        // They are kept because they still validate real, reachable user shapes end-to-end — but do NOT rely on
        // them for GreaterThan-arm coverage at n = 0. The rewrite is narrow and syntactic: `>= 1`, `!= 0`, `== 0`,
        // `< 0` and `<= 0` all arrive unrewritten, so every other constant-tier row does reach the arm its text
        // implies. Consequence worth knowing: TryRenderSizeComparison's GreaterThan arm at n = 0 is reachable
        // only from a hand-built expression tree, not from ordinary LINQ.
        data.Add("const-gt0", b => b.Posts.Count > 0);
        data.Add("const-gt1", b => b.Posts.Count > 1);
        data.Add("const-gt2", b => b.Posts.Count > 2);
        data.Add("const-gte0", b => b.Posts.Count >= 0);   // tautology → $expr tier
        data.Add("const-gte1", b => b.Posts.Count >= 1);
        data.Add("const-gte2", b => b.Posts.Count >= 2);
        data.Add("const-lt0", b => b.Posts.Count < 0);     // contradiction → $expr tier
        data.Add("const-lt1", b => b.Posts.Count < 1);
        data.Add("const-lt2", b => b.Posts.Count < 2);
        data.Add("const-lte0", b => b.Posts.Count <= 0);
        data.Add("const-lte1", b => b.Posts.Count <= 1);
        data.Add("const-lte2", b => b.Posts.Count <= 2);
        data.Add("const-eq0", b => b.Posts.Count == 0);
        data.Add("const-eq1", b => b.Posts.Count == 1);
        data.Add("const-eq2", b => b.Posts.Count == 2);
        data.Add("const-eq4", b => b.Posts.Count == 4);    // above every seeded length → empty result
        data.Add("const-ne0", b => b.Posts.Count != 0);
        data.Add("const-ne1", b => b.Posts.Count != 1);
        data.Add("const-ne2", b => b.Posts.Count != 2);

        // ---- PARAMETERIZED tier: a captured local per iteration, exercising $expr + $ifNull ----
        for (var n = 0; n <= 3; n++)
        {
            var t = n;   // captured ⇒ an EF query parameter ⇒ the $expr tier
            data.Add($"param-gt{t}", b => b.Posts.Count > t);
            data.Add($"param-gte{t}", b => b.Posts.Count >= t);
            data.Add($"param-lt{t}", b => b.Posts.Count < t);
            data.Add($"param-lte{t}", b => b.Posts.Count <= t);
            data.Add($"param-eq{t}", b => b.Posts.Count == t);
            data.Add($"param-ne{t}", b => b.Posts.Count != t);
        }

        // Negations, the call forms, a nested count, and a reversed order.
        data.Add("not-gt1", b => !(b.Posts.Count > 1));
        data.Add("not-eq2", b => !(b.Posts.Count == 2));
        data.Add("call-gt1", b => b.Posts.Count() > 1);
        data.Add("longcount-gt1", b => b.Posts.LongCount() > 1L);
        data.Add("reversed", b => 1 < b.Posts.Count);
        data.Add("nested-count", b => b.Posts.Any(p => p.Comments.Count > 0));
        data.Add("and", b => b.Posts.Count > 0 && b.Posts.Count < 3);
        data.Add("or", b => b.Posts.Count == 0 || b.Posts.Count == 3);

        // Any/All regression rows: these paths must be COMPLETELY unaffected by the slice.
        data.Add("any-bare", b => b.Posts.Any());
        data.Add("negated-any-bare", b => !b.Posts.Any());
        data.Add("any-pred", b => b.Posts.Any(p => p.Rank > 0));
        data.Add("all-pred", b => b.Posts.All(p => p.Rank >= 0));

        return data;
    }

    [Theory]
    [MemberData(nameof(CountMatrixCases))]
    public void Count_result_equals_the_in_memory_oracle_for_every_array_length_and_state(
        string name, Expression<Func<Blog, bool>> predicate)
    {
        var collection = Seed($"diff_{name}", DifferentialRows());

        // Oracle: materialize every row, then evaluate the SAME predicate in memory.
        List<string> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Where(predicate.Compile()).Select(b => b.Title).OrderBy(t => t).ToList();
        }

        // Server: the query must go NATIVE (NativeOnly is the only reliable signal) and agree exactly.
        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // Every array LENGTH boundary crossed with the three "no elements" states, plus a row carrying comments so
    // the nested-count case can discriminate.
    private static BsonDocument[] DifferentialRows() =>
    [
        LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3),
        Row("missing", posts: null),
        Row("null", BsonNull.Value),
        Row("withComments", new BsonArray { PostWithComments("a", 2) }),
        Row("emptyComments", new BsonArray { PostWithComments("a", 0) }),
    ];

    public static TheoryData<string, Expression<Func<Blog, TitleCount>>> CountProjectionShapes() => new()
    {
        { "property", b => new TitleCount { Title = b.Title, N = b.Posts.Count } },
        { "call", b => new TitleCount { Title = b.Title, N = b.Posts.Count() } },
        { "arithmetic", b => new TitleCount { Title = b.Title, N = b.Posts.Count * 2 } },
    };

    [Theory]
    [MemberData(nameof(CountProjectionShapes))]
    public void Count_projection_equals_the_in_memory_oracle_for_every_array_length_and_state(
        string name, Expression<Func<Blog, TitleCount>> selector)
    {
        // The differential gate, mirroring Count_result_equals_the_in_memory_oracle_for_every_array_length_and_
        // state for the predicate half: the SAME Expression object is sent to the server and compiled for
        // client-side evaluation, so the two sides cannot silently diverge the way two hand-written projections
        // can. The seed's missing / explicitly-null Posts rows are the ones a bare $size would abort on.
        var collection = Seed($"projdiff_{name}", DifferentialRows());

        List<(string Title, int N)> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var compiled = selector.Compile();
            expected = db.Entities.AsNoTracking().ToList()
                .Select(compiled).Select(r => (r.Title, r.N)).OrderBy(r => r.Title).ToList();
        }

        List<(string Title, int N)> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Select(selector)
                .ToList().Select(r => (r.Title, r.N)).OrderBy(r => r.Title).ToList();
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LongCount_projection_leaf_goes_native()
    {
        var collection = SeedLengths(nameof(LongCount_projection_leaf_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.LongCount() })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0L), ("len1", 1L), ("len2", 2L), ("len3", 3L), ("missing", 0L), ("null", 0L)],
            rows.Select(r => (r.Title, r.N)).ToArray());
    }

    [Fact]
    public void Count_projection_through_an_owned_reference_hop_goes_native()
    {
        // b.Home.Notes.Count — TryResolveOwnedCollectionPath walks the owned single-reference hop to reach the
        // collection, the same breadth the predicate half covers.
        var collection = Seed(nameof(Count_projection_through_an_owned_reference_hop_goes_native),
            RowWithNotes("none", 0), RowWithNotes("one", 1), RowWithNotes("three", 3));

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Home.Notes.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("none", 0), ("one", 1), ("three", 3)],
            rows.Select(r => (r.Title, r.N)).ToArray());

        // MEASURED: the resolved array path is specifically "Home.Notes" — the values-only assertion above
        // cannot distinguish the correct path from a wrong-but-coincidentally-same-shaped one (e.g. a path
        // that happens to also be empty/short enough to produce the same counts on this seed).
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("Home.Notes", mql);
    }

    [Fact]
    public void Count_projection_alongside_sibling_leaves_goes_native()
    {
        // len2 carries a non-empty Home.Notes (3 elements, deliberately DIFFERENT from its own Posts.Count of
        // 2) so the third leaf, Notes = b.Home.Notes.Count, actually discriminates in both directions: plain
        // SeedLengths seeds every row's Home.Notes as an empty array, so a Notes leaf that silently clobbered
        // onto the wrong projection slot (e.g. reading Posts's own size, or always reading 0) would still show
        // 0 on every row and the assertion below would not catch it.
        var collection = Seed(nameof(Count_projection_alongside_sibling_leaves_goes_native),
            LenRow("len0", 0), LenRow("len1", 1), LenRowWithNotes("len2", postLength: 2, noteCount: 3),
            LenRow("len3", 3), Row("missing", posts: null), Row("null", BsonNull.Value));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count, Doubled = b.Posts.Count * 2, Notes = b.Home.Notes.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0, 0, 0), ("len1", 1, 2, 0), ("len2", 2, 4, 3), ("len3", 3, 6, 0),
                ("missing", 0, 0, 0), ("null", 0, 0, 0)],
            rows.Select(r => (r.Title, r.N, r.Doubled, r.Notes)).ToArray());
    }

    [Fact]
    public void Bare_and_wrapped_count_projections_take_different_paths_from_the_same_model()
    {
        // The (I)/(II) disjointness proof. The two halves of this slice fire on the same LINQ construct in the
        // same model and must not collide: the WRAPPED form populates Select.Projection (Route == Projection) and
        // is pushed into $project, so NativeOnly succeeds; the BARE form is a bare-scalar projection that never
        // populates Projection (Route == Fallback), so NativeOnly declines — and only the EF-357 Enumerable.Count
        // rebuild applies. The split is disjoint BY CONSTRUCTION: MongoProjectionBindingExpressionVisitor's
        // Count/LongCount arm returns unconditionally whenever Route == Projection, so the switch arm the
        // EF-357 rebuild lives in only ever sees Route != Projection. Cited by quoted text rather than line
        // number, because the last round of line-number citations here rotted when the target block was rewritten:
        // see the "POSITION, precisely" comment on the count-leaf registration in
        // MongoProjectionBindingExpressionVisitor.VisitMethodCall, the bullet ending "the two arms are disjoint by
        // construction, not by luck". This test asserts the observable ROUTING outcome (NativeOnly succeeds for
        // the wrapped shape, throws for the bare one) — it does not exercise or depend on the relative ORDER of
        // that visitor's internal blocks, which was measured NOT load-bearing for this split (same comment, the
        // bullet beginning "It must come AFTER TryBindProjectedCollectionNavigationCount").
        var collection = SeedLengths(nameof(Bare_and_wrapped_count_projections_take_different_paths_from_the_same_model));

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            var wrapped = db.Entities.AsNoTracking()
                .Select(b => new { N = b.Posts.Count }).ToList().Select(r => r.N).OrderBy(n => n).ToList();
            Assert.Equal(new[] { 0, 0, 0, 1, 2, 3 }, wrapped);

            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList());
        }
    }

    [Fact]
    public void Filtered_count_projection_now_goes_native_EF359()
    {
        // This test USED TO PIN the EF-359 bug: Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })
        // threw System.InvalidOperationException ("The LINQ expression 'o' could not be translated...")
        // identically under Native, DriverLinq AND NativeOnly — a translation-time crash inside
        // MongoProjectionBindingExpressionVisitor.Translate, reached unconditionally from
        // MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect before MongoQueryMode was ever read
        // by the compile-time gate, so the mode had no bearing on whether it crashed. EF-359 Task 3 fixed it by
        // widening NativeProjectionBinder's node-kind gate (MongoSizeExpression -> also MongoFilteredSizeExpression)
        // and MongoProjectionBindingExpressionVisitor's IsCanonicalCount (both arities, both Queryable/Enumerable)
        // in lockstep. The shape now emits { $project: { ..., N: { $size: { $filter: ... } } } } and returns
        // correct values in every mode. Full breadth (LongCount, named-DTO, sibling leaves, owned-reference hop,
        // arithmetic wrapping) lives in NativeOwnedCollectionFilteredCountTests; this case stays HERE, under its
        // original name's ticket, so the file that documented the bug also records its closure.
        //
        // SeedLengths' LenRow gives element ranks 0..n-1, so "Rank > 0" counts (length - 1) elements for a
        // non-empty row (rank 0 never matches): len0 -> 0, len1 -> 0 (only rank 0 present), len2 -> 1 (rank 1),
        // len3 -> 2 (ranks 1, 2); missing/null rows have no Posts array at all -> 0.
        var collection = SeedLengths(nameof(Filtered_count_projection_now_goes_native_EF359));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("len0", 0), ("len1", 0), ("len2", 1), ("len3", 2), ("missing", 0), ("null", 0)],
            rows.Select(r => (r.Title, r.N)).ToList());
    }
}
