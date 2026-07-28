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
        // Contrast with the BARE embedded-collection projection, Select(b => b.Posts.Count): that is NOT
        // "deferred" in the usual sense of falling back to driver-LINQ and working — it hard-fails in EVERY
        // query mode (ArgumentException, from a MongoProjectionBindingExpressionVisitor gap), and it did so on
        // main long before the EF-322 native-query work began. Pinned by the companion test below.
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
    public void Bare_embedded_collection_Count_projection_is_a_known_preexisting_limitation()
    {
        // Select(b => b.Posts.Count) hard-fails in EVERY query mode — it is not a graceful fallback. Measured
        // to predate the entire EF-322 native-query work stream (reproduced on main), so it is neither caused
        // nor fixed by the .Count-in-a-predicate slice; this test exists so that if it ever starts working, or
        // starts failing differently, someone notices and updates the sibling test's comment and
        // Query/AGENTS.md along with it. Tracked as EF-357.
        //
        // The exception TYPE is not part of the provider's contract for an unsupported shape (see the
        // versioning rubric in AGENTS.md), so treat a type change here as a prompt to re-measure and
        // re-document, not as a regression in itself.
        var collection = SeedLengths(nameof(Bare_embedded_collection_Count_projection_is_a_known_preexisting_limitation));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            Assert.Throws<ArgumentException>(() => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList());
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
    public void A_predicated_Count_declines_and_falls_back_to_correct_rows()
    {
        // Count(pred) has no array-index form; it needs $expr over $filter — a separate slice.
        var collection = SeedWellFormed(nameof(A_predicated_Count_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(
            collection, q => q.Where(b => b.Posts.Count(p => p.Rank > 0) > 1));

        // len2 has ranks {0,1} → one passes; len3 has {0,1,2} → two pass.
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
}
