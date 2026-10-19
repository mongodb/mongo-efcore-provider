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
/// A predicated element count over an owned (embedded) collection navigation, in both the <c>Where</c> spelling
/// (<c>$expr</c> over a null-safe <c>$size</c> of a <c>$filter</c>) and the projection spelling (a <c>$project</c>
/// leaf of the same shape).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionFilteredCountTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

/// <summary>
/// Whether the reserved Synthetic alias <c>_v</c> appears as a field name in the emitted <c>$project</c> stage.
/// </summary>
/// <remarks>
/// Scoped to the <c>$project</c> stage and the quoted key: the logged command includes the collection name,
/// which is derived from the test name, so a bare <c>Contains("_v")</c> could match unrelated text.
/// </remarks>
    private static bool ProjectStageCommitsTheSyntheticAlias(SpyLoggerProvider spyLogger)
    {
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var start = mql.IndexOf("$project", StringComparison.Ordinal);
        return start >= 0 && mql[start..].Contains("\"_v\"", StringComparison.Ordinal);
    }

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
        // Nullable so a missing or explicitly-null stored field materializes as null instead of throwing.
        public int? Rank { get; set; }
        public string? Heading { get; set; }
        public int? Other { get; set; }

        // Pinned is non-nullable: `!p.Pinned` is the only spelling that reaches MongoUnaryExpression (a
        // nullable bool's Not is declined earlier, at TranslateNode). Flagged is nullable: `p.Flagged!.Value` is
        // the bare-nullable-bool spelling, also declined at TranslateNode. Both are always seeded.
        public bool Pinned { get; set; }
        public bool? Flagged { get; set; }

        // Collides with Blog.Title on purpose: the element-scoped translator resolves members by name, so this
        // exercises the correlated-element-predicate guard on an input that would otherwise be accepted.
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

    // Named DTO: reaches NativeProjectionBinder's MemberInit branch rather than the anonymous-type (New) branch.
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

    private static BsonDocument LenRow(string title, int length)
    {
        var posts = new BsonArray();
        for (var i = 0; i < length; i++)
            posts.Add(PostDoc(rank: i, heading: "h" + i));
        return Row(title, posts);
    }

    // Pinned defaults to `Rank > 0`, so `!p.Pinned` counts exactly the complement of the canonical predicate.
    private static BsonDocument PostDoc(int? rank, string? heading, bool? pinned = null, bool? flagged = null)
        => new()
        {
            { "Rank", rank.HasValue ? rank.Value : BsonNull.Value },
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", 0 }, { "Title", "p" }, { "Comments", new BsonArray() },
            { "Pinned", pinned ?? rank > 0 },
            { "Flagged", flagged.HasValue ? flagged.Value : BsonNull.Value }
        };

    private static BsonDocument PostWithComments(string heading, int commentCount)
    {
        var comments = new BsonArray();
        for (var i = 0; i < commentCount; i++)
            comments.Add(new BsonDocument { { "Age", i } });
        return new BsonDocument
        {
            { "Rank", 0 }, { "Heading", heading }, { "Other", 0 }, { "Title", "p" }, { "Comments", comments },
            { "Pinned", false }, { "Flagged", BsonNull.Value }
        };
    }

    // Home/Tags are always seeded present-but-empty: both are separate required properties on Blog, and a
    // document missing them fails materialization with an unrelated error the moment a predicate returns the row.
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

    // Rows differ in how many elements satisfy the predicate — the axis LenRow cannot control (its ranks are
    // 0..n-1, correlating "Rank > 0" with length). `matching` elements have Rank = 5, `nonMatching` Rank = -5.
    private static BsonDocument MatchRow(string title, int matching, int nonMatching)
    {
        var posts = new BsonArray();
        for (var i = 0; i < matching; i++) posts.Add(PostDoc(rank: 5, heading: "m" + i));
        for (var i = 0; i < nonMatching; i++) posts.Add(PostDoc(rank: -5, heading: "n" + i));
        return Row(title, posts);
    }

    private static BsonDocument[] MatchRows() =>
    [
        MatchRow("none", 0, 3), MatchRow("one", 1, 2), MatchRow("three", 3, 0),
        Row("empty", new BsonArray()), Row("missing", null), Row("null", BsonNull.Value)
    ];

    // MatchRows() with an "m_" title prefix, for the late-decline legs: a captured-local StartsWith makes the
    // native factory decline after the alias-addressed shaper is committed — the one route where a bare
    // projection's alias miss is silent. The prefix keeps the ragged rows (the only ones that discriminate a bare
    // $size from $size over $ifNull) in the result. Same title order, so [0,0,0,0,1,3] is expected for both seeds.
    private static BsonDocument[] PrefixedMatchRows() =>
    [
        MatchRow("m_none", 0, 3), MatchRow("m_one", 1, 2), MatchRow("m_three", 3, 0),
        Row("m_empty", new BsonArray()), Row("m_missing", null), Row("m_null", BsonNull.Value)
    ];

    /// <summary>
    /// Runs <paramref name="query"/> and describes the outcome as a string, so callers collect every leg's
    /// outcome and assert them together.
    /// </summary>
    /// <remarks>
    /// Direct assertions in a loop abort on the first failing leg, leaving later legs (e.g. explicit
    /// <c>DriverLinq</c>) silently unexercised. Mirrors <c>NativeComputedBareProjectionTests.LegOutcome</c>.
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
    public void Filtered_count_predicate_goes_native()
    {
        var collection = Seed(nameof(Filtered_count_predicate_goes_native), MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Materialize entities first and project titles client-side: a server-side bare-scalar Select would fall
        // back to driver-LINQ for an unrelated reason and fail under NativeOnly.
        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Count(p => p.Rank > 0) > 1)
            .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(["three"], titles);
    }

    [Theory]
    [InlineData(0, new[] { "empty", "missing", "none", "null", "one", "three" })]
    [InlineData(1, new[] { "one", "three" })]
    [InlineData(3, new[] { "three" })]
    public void Filtered_count_predicate_is_correct_for_every_threshold(int threshold, string[] expected)
    {
        var collection = Seed($"thresh_{threshold}", MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // ToList() before Select: see Filtered_count_predicate_goes_native.
        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Count(p => p.Rank > 0) >= threshold)
            .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(expected, titles);
    }

    [Fact]
    public void Filtered_count_predicate_emits_expr_over_size_over_filter_never_an_array_index_test()
    {
        var collection = Seed(
            nameof(Filtered_count_predicate_emits_expr_over_size_over_filter_never_an_array_index_test), MatchRows());
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        _ = db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Rank > 0) > 2).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$filter", mql);
        // Fail-closed tripwire: an array-index existence test answers the unfiltered count's question, so
        // collapsing MongoFilteredSizeExpression into MongoSizeExpression would return wrong rows silently.
        Assert.DoesNotContain("Posts.2", mql);
        Assert.DoesNotContain("$exists", mql);
    }

    [Fact]
    public void Filtered_count_predicate_with_a_parameterized_threshold_goes_native()
    {
        var collection = Seed(nameof(Filtered_count_predicate_with_a_parameterized_threshold_goes_native), MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        var threshold = 1;

        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Count(p => p.Rank > 0) > threshold)
            .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(["three"], titles);
    }

    [Fact]
    public void Filtered_count_predicate_through_an_owned_reference_hop_goes_native()
    {
        var collection = Seed(
            nameof(Filtered_count_predicate_through_an_owned_reference_hop_goes_native),
            RowWithNotes("two", 2), RowWithNotes("none", 0));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Home.Notes.Count(n => n.Length > 0) > 0)
            .ToList().Select(b => b.Title).ToList();

        Assert.Equal(["two"], titles);
    }

    [Fact]
    public void Correlated_element_predicate_now_goes_native_via_two_scope_translator_and_returns_correct_rows()
    {
        // Post.Title collides with Blog.Title: a name-based resolution of b.Title would silently retarget it at
        // the element and return the wrong rows. See also NativeOwnedCollectionCorrelatedTests.
        var collection = Seed(
            nameof(Correlated_element_predicate_now_goes_native_via_two_scope_translator_and_returns_correct_rows),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "h") }));

        // Wrong-rows check runs first, in its own block, so it is independently load-bearing: a by-name
        // resolution compares the element's Title to itself and returns the row.
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            // "p" (PostDoc's Title) != "x", so the correct answer is no rows.
            Assert.Empty(db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Title == b.Title) > 0).ToList());
        }

        // Correlation to the immediate enclosing scope goes native; still returns the correct (empty) result.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Empty(db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Title == b.Title) > 0).ToList());
        }
    }

    // A constant-term regex renders natively inside $expr (no query-dialect $regularExpression fallback exists
    // there). The second row ("z") must not come back, so a regex rendered as match-everything fails.
    [Fact]
    public void Regex_element_predicate_in_a_Where_goes_native_EF322()
    {
        var collection = Seed(
            nameof(Regex_element_predicate_in_a_Where_goes_native_EF322),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }),
            Row("z", new BsonArray { PostDoc(rank: 1, heading: "world") }));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var titles = db.Entities.AsNoTracking()
                .Where(b => b.Posts.Count(p => p.Heading!.StartsWith("h")) > 0)
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

            Assert.Equal(["x"], titles);
        }
    }

    [Fact]
    public void Primitive_element_collection_filtered_count_declines()
    {
        var collection = Seed(nameof(Primitive_element_collection_filtered_count_declines), RowWithTags("x", "a", "bb"));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Tags is a primitive-collection property, not a navigation — TryResolveOwnedCollectionPath's final-hop
        // check declines it.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Tags.Count(t => t.Length > 1) > 0).ToList());
    }

    [Fact]
    public void Filtered_count_nested_inside_a_quantifier_declines_and_the_unfiltered_form_still_goes_native()
    {
        var collection = Seed(
            nameof(Filtered_count_nested_inside_a_quantifier_declines_and_the_unfiltered_form_still_goes_native),
            Row("x", new BsonArray { PostWithComments("h", 2) }));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // $expr is a server error inside $elemMatch, so a filtered count there must decline at translate time
        // rather than throw at execution under Native.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking()
                .Where(b => b.Posts.Any(p => p.Comments.Count(c => c.Age > 0) > 1)).ToList());

        // Regression tripwire: the unfiltered count in the same position renders as an array-index test and must
        // stay native.
        Assert.Single(db.Entities.AsNoTracking().Where(b => b.Posts.Any(p => p.Comments.Count > 1)).ToList());
    }

    // MongoExpressionTranslator's Not arm only negates `MongoBinaryExpression { Left: MongoSizeExpression }`,
    // so this shape becomes MongoUnaryExpression(Not, ...). RenderUnary asks
    // MongoAggregationExpressionRenderer.CanRender, which admits the filtered count (its element predicate is a
    // plain comparison), so it renders as `{ $expr: { $not: [ { $gt: [ { $size: { $filter: ... } }, 1 ] } ] } }`.
    [Fact]
    public void Negated_filtered_count_comparison_now_goes_native_with_correct_rows()
    {
        var collection = Seed(
            nameof(Negated_filtered_count_comparison_now_goes_native_with_correct_rows),
            MatchRow("none", 0, 3), MatchRow("one", 1, 2), MatchRow("three", 3, 0), Row("empty", new BsonArray()));

        // Must return the exact complement of the un-negated predicate ("three"), asserted as real rows so an
        // all-or-nothing bug fails.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            var titles = db.Entities.AsNoTracking()
                .Where(b => !(b.Posts.Count(p => p.Rank > 0) > 1))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

            Assert.Equal(["empty", "none", "one"], titles);
        }

        // Native and driver-LINQ must agree on the well-formed subset. The empty-array row is excluded because
        // driver-LINQ renders a bare $size with no $ifNull and aborts on it (see
        // NativeOwnedCollectionCountTests.Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_
        // aborts_on_a_missing_array).
        using (var native = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            var nativeTitles = native.Entities.AsNoTracking()
                .Where(b => !(b.Posts.Count(p => p.Rank > 0) > 1) && b.Title != "empty")
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
            var driverTitles = driver.Entities.AsNoTracking()
                .Where(b => !(b.Posts.Count(p => p.Rank > 0) > 1) && b.Title != "empty")
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

            Assert.Equal(driverTitles, nativeTitles);
            Assert.Equal(["none", "one"], nativeTitles);
        }
    }

    // NativeSelectManyBinder.TryBuildOwnedInnerFilter translates an inner-only owned SelectMany filter with a
    // single-scope element-scoped translator, so the filtered count is an ordinary operand. After
    // `$unwind: "$Posts"` it emits a top-level `$match` (legal, unlike inside $elemMatch) over
    // `{$size: {$filter: {input: {$ifNull: ["$Posts.Comments", []]}, ...}}}`. The count's array path is
    // element-relative and MongoFieldPrefixRewriter must prefix it — the filtered analogue of
    // NativeOwnedCollectionCountTests.Count_inside_an_owned_SelectMany_inner_filter_goes_native.
    //
    // No driver-LINQ oracle: this shape throws "could not be translated" there. The threshold makes the filter
    // load-bearing: "mid" has 2 comments but only 1 with Age > 0, so dropping the $filter returns ["many", "mid"].
    [Fact]
    public void Filtered_count_inside_an_owned_SelectMany_inner_filter_goes_native()
    {
        var collection = Seed(nameof(Filtered_count_inside_an_owned_SelectMany_inner_filter_goes_native),
            Row("blog", new BsonArray
            {
                PostWithComments("few", 1), PostWithComments("mid", 2), PostWithComments("many", 3)
            }));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var headings = db.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => p.Comments.Count(c => c.Age > 0) > 1), (b, p) => new { p.Heading })
            .ToList().Select(x => x.Heading).OrderBy(h => h).ToList();

        Assert.Equal(new[] { "many" }, headings);
    }

    // These predicated-count projections still throw InvalidOperationException ("could not be translated") in
    // every mode; none ever worked.
    //
    // Primitive (b.Tags.Count(pred)): Tags is a primitive-collection property, so
    // TryResolveOwnedCollectionPath declines, the whole leaf fails to translate, Route = Fallback, and
    // MongoProjectionBindingExpressionVisitor's predicated-Count arm declines too (the source is not a
    // CollectionShaperExpression), reaching the generic `methodCallExpression.Update(...)` rebuild that crashes.
    //
    // Posts.Where(pred).Count(): EF does not fuse this into Count(pred), so it takes the predicate-less
    // CountWithoutPredicate arm and reaches the same crash as a structurally distinct case.
    //
    // The correlated spelling is native; see Correlated_count_filtered_projection_goes_native_EF421.
    [Fact]
    public void Primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode()
    {
        var collection = Seed(
            nameof(Primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }),
            RowWithTags("y", "a", "bb"));

        void AssertHardFailsEverywhere(Func<IQueryable<Blog>, IQueryable<object>> query)
        {
            foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
            {
                using var db = CreateContext(collection, mode, BlogModel);
                var ex = Assert.Throws<InvalidOperationException>(() => query(db.Entities.AsNoTracking()).ToList());
                Assert.Contains("could not be translated", ex.Message);
            }
        }

        // Primitive collection: declined by TryResolveOwnedCollectionPath's final-hop check.
        AssertHardFailsEverywhere(
            q => q.Select(b => new { b.Title, N = b.Tags.Count(t => t.Length > 1) }).Cast<object>());

        // Posts.Where(pred).Count(): a different method-call shape from Count(pred), not fused upstream by EF.
        AssertHardFailsEverywhere(
            q => q.Select(b => new { b.Title, N = b.Posts.Where(p => p.Rank > 0).Count() }).Cast<object>());
    }

    // Post.Title collides with Blog.Title, so this exercises real correlation resolution: PostDoc writes
    // Title = "p", which never equals "x" or "y", so the correct count is 0 for both rows.
    [Fact]
    public void Correlated_count_filtered_projection_goes_native_EF421()
    {
        var collection = Seed(
            nameof(Correlated_count_filtered_projection_goes_native_EF421),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }),
            Row("y", new BsonArray { PostDoc(rank: 2, heading: "world") }));

        AssertElementPredicateGoesNative(
            collection,
            q => ProjectTitleAndCount(
                q.Select(b => new { b.Title, N = b.Posts.Count(p => p.Title == b.Title) }),
                r => (r.Title, r.N)),
            ["x=0", "y=0"]);
    }

    // Deliberately without ragged (empty/missing/null Posts) rows: on a late-fallback route the pipeline is the
    // driver's, whose ragged-array behavior is out of scope. Native $ifNull-over-$filter is covered elsewhere.
    private static BsonDocument[] MatchRowsNoRagged() =>
        [MatchRow("none", 0, 3), MatchRow("one", 1, 2), MatchRow("three", 3, 0)];

    private static List<string> ProjectTitleAndCount<T>(IQueryable<T> rows, Func<T, (string Title, int N)> read)
        => rows.ToList().Select(read).OrderBy(r => r.Title).Select(r => $"{r.Title}={r.N}").ToList();

    [Fact]
    public void Regex_element_predicate_filtered_projection_goes_native_EF365()
    {
        // A StartsWith regex, which has no aggregation-dialect form of its own.
        var collection = Seed(nameof(Regex_element_predicate_filtered_projection_goes_native_EF365),
            MatchRowsNoRagged());

        AssertElementPredicateGoesNative(
            collection,
            q => ProjectTitleAndCount(
                q.Select(b => new { b.Title, N = b.Posts.Count(p => p.Heading!.StartsWith("m")) }),
                r => (r.Title, r.N)),
            ["none=0", "one=1", "three=3"]);
    }

    // Contains (MongoInExpression) and unary Not (MongoUnaryExpression) element predicates render via
    // RenderIn/RenderUnary. The projection-side filtered-count branch has no separate gate, so a render arm is
    // sufficient to make them native (see AssertElementPredicateGoesNative).

    [Fact]
    public void Contains_element_predicate_filtered_projection_goes_native_EF413()
    {
        // Contains over a captured collection translates to MongoInExpression, rendered by RenderIn.
        var collection = Seed(nameof(Contains_element_predicate_filtered_projection_goes_native_EF413),
            MatchRowsNoRagged());
        var wanted = new[] { "m0", "m1" };

        AssertElementPredicateGoesNative(
            collection,
            q => ProjectTitleAndCount(
                q.Select(b => new { b.Title, N = b.Posts.Count(p => wanted.Contains(p.Heading)) }),
                r => (r.Title, r.N)),
            ["none=0", "one=1", "three=2"]);
    }

    [Fact]
    public void Unary_not_element_predicate_filtered_projection_goes_native_EF413()
    {
        // Not over a non-nullable bool is the only spelling that builds a MongoUnaryExpression (a nullable
        // bool's Not is declined earlier). Pinned = (Rank > 0), so `!p.Pinned` counts the complement:
        // none => 3, one => 2, three => 0.
        var collection = Seed(nameof(Unary_not_element_predicate_filtered_projection_goes_native_EF413),
            MatchRowsNoRagged());

        AssertElementPredicateGoesNative(
            collection,
            q => ProjectTitleAndCount(
                q.Select(b => new { b.Title, N = b.Posts.Count(p => !p.Pinned) }),
                r => (r.Title, r.N)),
            ["none=3", "one=2", "three=0"]);
    }

    // Asserts identical results in every mode, including NativeOnly (which must succeed).
    //
    // The projection-side filtered-count branch is deliberately gate-free: the element predicate is not checked
    // at translate time, because a translate-time check hard-fails the whole leaf with InvalidOperationException
    // in every mode (including DriverLinq) instead of declining (see MongoExpressionTranslator's count-branch
    // remarks and NativeComputedSortTests.Filtered_owned_collection_count_sort_key_over_a_string_represented_operand_is_refused). The renderer
    // decides; a render-time throw is caught in TryBuildPipeline and becomes a fallback.
    private void AssertElementPredicateGoesNative(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, List<string>> run, string[] expected)
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            Assert.Equal(expected, run(db.Entities.AsNoTracking()));
        }
    }

    // A filtered count's element predicate with Not over a value-converted bool must decline rather than answer
    // wrong. Because the element predicate is not checked at translate time (see
    // AssertElementPredicateGoesNative), the guard lives in MongoAggregationExpressionRenderer.RenderUnary, which
    // throws for a non-default-serialized bare field.
    public class ConvertedFlagOwner
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<ConvertedFlagPost> Posts { get; set; } = [];
    }

    public class ConvertedFlagPost
    {
        // Same converter as NativeComputedSortTests.ConvertedFlagModel: both stored values are non-empty strings,
        // so a raw-field $not is wrong for every Flag == false element.
        public bool Flag { get; set; }
    }

    private static readonly Action<ModelBuilder> ConvertedFlagOwnerModel =
        mb => mb.Entity<ConvertedFlagOwner>().OwnsMany(b => b.Posts, p =>
            p.Property(x => x.Flag).HasConversion(v => v ? "Y" : "N", v => v == "Y"));

    [Fact]
    public void Filtered_count_element_predicate_using_Not_over_a_value_converted_bool_declines_instead_of_answering_wrong()
    {
        var name = UniqueCollectionName(
            nameof(Filtered_count_element_predicate_using_Not_over_a_value_converted_bool_declines_instead_of_answering_wrong));
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "two-false" },
                { "Posts", new BsonArray { new BsonDocument { { "Flag", "N" } }, new BsonDocument { { "Flag", "N" } }, new BsonDocument { { "Flag", "Y" } } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "one-false" },
                { "Posts", new BsonArray { new BsonDocument { { "Flag", "N" } }, new BsonDocument { { "Flag", "Y" } }, new BsonDocument { { "Flag", "Y" } } } }
            }
        ]);
        var collection = database.MongoDatabase.GetCollection<ConvertedFlagOwner>(name);

        // The CLR-correct answer is two-false=2, one-false=1, but the driver's own LINQ provider renders `!p.Flag`
        // as raw-field truthiness too and answers 0 for every row — a driver limitation out of scope here. What is
        // guaranteed: NativeOnly never succeeds with wrong data, and Native agrees with DriverLinq.
        List<string> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode, ConvertedFlagOwnerModel);
            return db.Entities.AsNoTracking()
                .Select(b => new { b.Title, N = b.Posts.Count(p => !p.Flag) })
                .ToList().OrderBy(r => r.Title).Select(r => $"{r.Title}={r.N}").ToList();
        }

        // Load-bearing: NativeOnly declines cleanly rather than returning 0 for every count (raw-field $not over
        // "Y"/"N" is always false).
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, ConvertedFlagOwnerModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count(p => !p.Flag) })
                    .ToList());
        }

        // Native must agree with the DriverLinq fallback it declines to.
        Assert.Equal(Run(MongoQueryMode.DriverLinq), Run(MongoQueryMode.Native));
    }

    [Fact]
    public void Mixed_projection_with_a_regex_filtered_count_goes_native_EF365()
    {
        // Risk case (see Query/AGENTS.md, "alias-agreement and sibling-readability for mixed projections"): the
        // shaper is built alias-addressed before native-vs-fallback is decided. No alias override is registered
        // (every alias is the member name), so a late fallback keeps the driver's $project and names match. Three
        // leaves so a member/alias mis-pairing shows up as swapped values.
        var collection = Seed(nameof(Mixed_projection_with_a_regex_filtered_count_goes_native_EF365),
            MatchRowsNoRagged());

        AssertElementPredicateGoesNative(
            collection,
            q => q.Select(b => new
                {
                    b.Title,
                    N = b.Posts.Count(p => p.Heading!.StartsWith("m")),
                    Echo = b.Title
                })
                .ToList()
                .OrderBy(r => r.Title)
                .Select(r => $"{r.Title}={r.N}/{r.Echo}")
                .ToList(),
            ["none=0/none", "one=1/one", "three=3/three"]);
    }

    [Fact]
    public void Bare_nullable_bool_element_predicate_filtered_projection_still_hard_fails_EF365()
    {
        // A bare nullable bool (`p.Flagged!.Value`) is declined by TranslateNode's bare-boolean-member arm
        // (non-nullable bools only), so the leaf never translates and hits the generic fall-through crash.
        // Widening that arm is a correctness decision (native-vs-driver divergence on a missing/null element).
        var collection = Seed(nameof(Bare_nullable_bool_element_predicate_filtered_projection_still_hard_fails_EF365),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello", flagged: true) }));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count(p => p.Flagged!.Value) }).ToList());
            Assert.Contains("could not be translated", ex.Message);
        }
    }

    // Rows with both a populated Tags collection and Posts elements that differ on the element predicate —
    // needed by the two array-sibling tests below.
    private static BsonDocument TagsAndPostsRow(string title, string[] tags, int matching, int nonMatching)
    {
        var posts = new BsonArray();
        for (var i = 0; i < matching; i++) posts.Add(PostDoc(rank: 5, heading: "m" + i));
        for (var i = 0; i < nonMatching; i++) posts.Add(PostDoc(rank: -5, heading: "n" + i));
        return new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray(tags) }, { "Posts", posts }
        };
    }

    [Fact]
    public void Primitive_collection_sibling_of_a_regex_filtered_count_goes_native_EF365()
    {
        // A primitive-collection leaf (`b.Tags`) is a MongoFieldExpression, not a MongoElementRefExpression, so
        // the sibling whole-document-readability gate never engages and the projection is admitted alongside a
        // computed count. Asserts the data. Safe because no leaf registers an alias override, so a late fallback
        // keeps the driver's $project and its member-name aliases.
        var collection = Seed(
            nameof(Primitive_collection_sibling_of_a_regex_filtered_count_goes_native_EF365),
            TagsAndPostsRow("x", ["a", "bb"], matching: 2, nonMatching: 1),
            TagsAndPostsRow("y", ["c"], matching: 0, nonMatching: 1));

        AssertElementPredicateGoesNative(
            collection,
            q => q.Select(b => new { b.Title, b.Tags, N = b.Posts.Count(p => p.Heading!.StartsWith("m")) })
                .ToList()
                .OrderBy(r => r.Title)
                .Select(r => $"{r.Title}=[{string.Join("|", r.Tags)}]/{r.N}")
                .ToList(),
            ["x=[a|bb]/2", "y=[c]/0"]);
    }

    [Fact]
    public void Owned_collection_array_leaf_sibling_of_a_filtered_count_still_declines_before_mutating_EF365()
    {
        // An owned collection navigation (`b.Posts`) is an array leaf, which forces IsWholeDocumentReadableLeaf
        // on every sibling; a computed count is backed by no document element, so the whole projection declines
        // before mutating anything and hard-fails. Tripwire: if this stops throwing, the alias-agreement
        // invariant has been reopened (a stripped fallback would read the count's alias off a raw document).
        var collection = Seed(
            nameof(Owned_collection_array_leaf_sibling_of_a_filtered_count_still_declines_before_mutating_EF365),
            TagsAndPostsRow("x", ["a"], matching: 2, nonMatching: 1));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking()
                    .Select(b => new { b.Posts, N = b.Posts.Count(p => p.Heading!.StartsWith("m")) }).ToList());
        }
    }

    [Fact]
    public void Filtered_count_projection_goes_native()
    {
        // Select(b => new { b.Title, N = b.Posts.Count(pred) }). See also
        // NativeOwnedCollectionCountTests.Filtered_count_projection_now_goes_native_EF359.
        var collection = Seed(nameof(Filtered_count_projection_goes_native), MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("empty", 0), ("missing", 0), ("none", 0), ("null", 0), ("one", 1), ("three", 3)],
            rows.Select(r => (r.Title, r.N)).ToList());
    }

    [Fact]
    public void Filtered_count_projection_emits_size_over_filter_in_project()
    {
        var collection = Seed(nameof(Filtered_count_projection_emits_size_over_filter_in_project), MatchRows());
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        _ = db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count(p => p.Rank > 0) }).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$project", mql);
        Assert.Contains("$filter", mql);
        Assert.Contains("$ifNull", mql);
    }

    [Fact]
    public void Filtered_LongCount_projection_goes_native()
    {
        var collection = Seed(nameof(Filtered_LongCount_projection_goes_native), MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.LongCount(p => p.Rank > 0) })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal([0L, 0L, 0L, 0L, 1L, 3L], rows.Select(r => r.N).ToList());
    }

    [Fact]
    public void Filtered_count_projection_into_a_named_dto_goes_native()
    {
        // The DTO spelling reaches NativeProjectionBinder's MemberInit branch.
        var collection = Seed(nameof(Filtered_count_projection_into_a_named_dto_goes_native), MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 0) })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal([0, 0, 0, 0, 1, 3], rows.Select(r => r.N).ToList());
    }

    [Fact]
    public void Filtered_count_projection_alongside_sibling_leaves_goes_native()
    {
        var collection = Seed(nameof(Filtered_count_projection_alongside_sibling_leaves_goes_native), MatchRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, Filtered = b.Posts.Count(p => p.Rank > 0), All = b.Posts.Count })
            .OrderBy(r => r.Title).ToList();

        // Covers the ragged rows too, where a filtered and an unfiltered $size share one $project. "none"/"one"/
        // "three" all have All == 3 but differ in Filtered, so a filtered/unfiltered mix-up fails.
        Assert.Equal(
            [("empty", 0, 0), ("missing", 0, 0), ("none", 0, 3), ("null", 0, 0), ("one", 1, 3), ("three", 3, 3)],
            rows.Select(r => (r.Title, r.Filtered, r.All)).ToList());
    }

    [Fact]
    public void Filtered_count_projection_through_an_owned_reference_hop_goes_native()
    {
        var collection = Seed(
            nameof(Filtered_count_projection_through_an_owned_reference_hop_goes_native),
            RowWithNotes("two", 2), RowWithNotes("none", 0));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Home.Notes.Count(n => n.Length > 0) })
            .OrderBy(r => r.Title).ToList();

        Assert.Equal([("none", 0), ("two", 1)], rows.Select(r => (r.Title, r.N)).ToList());
    }

    [Fact]
    public void Arithmetic_projection_leaf_containing_a_filtered_count_goes_native()
    {
        // NativeProjectionBinder's arithmetic projection-leaf branch places no restriction on operand kinds, so
        // an arithmetic wrapper around a filtered count goes native. The $filter/$size composition keeps the
        // $ifNull wrap, so missing/null Posts arrays agree across modes.
        var collection = Seed(nameof(Arithmetic_projection_leaf_containing_a_filtered_count_goes_native), MatchRows());

        List<(string Title, int X)> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode, BlogModel);
            return db.Entities.AsNoTracking()
                .Select(b => new { b.Title, X = b.Posts.Count(p => p.Rank > 0) * 2 })
                .ToList().OrderBy(r => r.Title).Select(r => (r.Title, r.X)).ToList();
        }

        // Matching counts doubled; ragged rows have 0 matches.
        var expected = new List<(string, int)>
        {
            ("empty", 0), ("missing", 0), ("none", 0), ("null", 0), ("one", 2), ("three", 6)
        };

        Assert.Equal(expected, Run(MongoQueryMode.NativeOnly));
        Assert.Equal(expected, Run(MongoQueryMode.Native));
        Assert.Equal(expected, Run(MongoQueryMode.DriverLinq));
    }

    // Bare spelling: Select(b => b.Posts.Count(pred)). Goes native as a tier-2 leaf (arm 1a of
    // NativeProjectionBinder.TryDeriveSyntheticAlias, `_v` / Synthetic); this pins that values are correct in
    // the non-NativeOnly modes.
    [Fact]
    public void Bare_filtered_count_projection_returns_correct_values_under_Native_and_DriverLinq()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_returns_correct_values_under_Native_and_DriverLinq), MatchRows());

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var counts = db.Entities.AsNoTracking()
                .OrderBy(b => b.Title)
                .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList();

            Assert.Equal([0, 0, 0, 0, 1, 3], counts);
        }
    }

    // Arm 1a of NativeProjectionBinder.TryDeriveSyntheticAlias admits a MongoSizeExpression or
    // MongoFilteredSizeExpression as the top node of a bare selector whose un-stripped driver fallback cannot
    // abort (IsFallbackSafeBareSizeLeaf), under `_v` / Synthetic. The filtered kind is fallback-safe because the
    // driver renders {$sum: {$map: …}} and $map tolerates a missing array. NativeOnly succeeding is the routing
    // proof; values match the fallback modes.
    //
    // Still declined: the same count through an owned-reference hop (b.Home.Notes.Count(pred), a dotted path) —
    // see IsFallbackSafeBareSizeLeaf and NativeComputedBareProjectionTests.Bare_FILTERED_count_leaf_through_an_
    // owned_reference_HOP_is_declined_and_answers_correctly. A primitive-collection count never reaches tier 2.
    //
    // Covers present, empty, absent and BSON-null arrays; PrefixedMatchRows() keeps the ragged rows in the
    // late-decline legs.
    [Fact]
    public void Bare_filtered_count_projection_goes_native_under_NativeOnly()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_goes_native_under_NativeOnly), PrefixedMatchRows());
        var prefix = "m_";

        // Collect-then-assert; see LegOutcome.
        const string expected = "[0,0,0,0,1,3]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList())));
        }

        // Late-decline legs: a captured-local StartsWith declines at render time, after the alias-addressed
        // shaper is committed — the one route where a bare projection's alias miss is silent. The explicit
        // DriverLinq leg verifies UseQueryMode(DriverLinq) still restores the previous path.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList())));
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

    // Pins the server-side rendering: `$size` over `$filter` over `$ifNull` in the aggregation dialect, under
    // the reserved `_v` alias the shaper reads. No array-index form exists for a predicated count
    // (MongoFilteredSizeExpression is a sealed sibling of MongoSizeExpression so Tier 1 never fires for it).
    // Also asserts no empty pipeline, so a revert to client-side folding fails.
    //
    // Runs only under NativeOnly; late-decline and DriverLinq legs are in
    // Bare_filtered_count_projection_goes_native_under_NativeOnly. The default Native mode's MQL is expected to
    // match but is not pinned anywhere.
    [Fact]
    public void Bare_filtered_count_projection_emits_a_native_size_over_filter_under_the_reserved_alias()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_emits_a_native_size_over_filter_under_the_reserved_alias),
            MatchRows());
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        var counts = db.Entities.AsNoTracking().OrderBy(b => b.Title)
            .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList();

        // NativeOnly succeeding is the routing proof; the values show the pipeline answers correctly.
        Assert.Equal([0, 0, 0, 0, 1, 3], counts);

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$project", mql);
        Assert.Contains("$size", mql);
        Assert.Contains("$filter", mql);
        Assert.Contains("$ifNull", mql);
        // Scoped to $project field names; see ProjectStageCommitsTheSyntheticAlias.
        Assert.True(ProjectStageCommitsTheSyntheticAlias(spy));
        // Guards against a revert to the client-side fold.
        Assert.DoesNotContain("aggregate([])", mql);
    }

    // A captured-local predicate goes native in every mode: as a tier-2 leaf (arm 1a of TryDeriveSyntheticAlias) the
    // local is an ordinary pipeline parameter, whereas the client-side rebuild path fails on the unevaluable query-parameter node.
    //
    // Oracle: in-memory LINQ over the same Expression object (`selector` server-side vs `selector.Compile()`
    // over materialized entities). Valid here because the predicate is `p.Rank > threshold` over ranks of 5 and
    // -5 with no missing/null Rank field, so the DriverLinq legs (whose $filter is not null-guarded) agree too (see
    // the "gt" row of FilteredCountSelectors). The ragged states here are array-level, which $ifNull and EF both
    // answer as 0.
    [Fact]
    public void Bare_filtered_count_projection_with_a_captured_parameter_goes_native_in_every_mode()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_with_a_captured_parameter_goes_native_in_every_mode),
            PrefixedMatchRows());
        var threshold = 0;
        var prefix = "m_";

        // One Expression object used both ways; `threshold` is still extracted as a query parameter.
        Expression<Func<Blog, int>> selector = b => b.Posts.Count(p => p.Rank > threshold);

        List<int> oracle;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            oracle = db.Entities.AsNoTracking().ToList()
                .OrderBy(b => b.Title).Select(selector.Compile()).ToList();
        }

        var expected = "[" + string.Join(",", oracle) + "]";

        // Collect every leg's outcome before asserting; see LegOutcome.
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(selector).ToList())));
        }

        // Late-decline legs; see Bare_filtered_count_projection_goes_native_under_NativeOnly.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(selector).ToList())));
        }

        // Assert the oracle itself so agreement with a silently-wrong oracle cannot pass.
        Assert.Equal([0, 0, 0, 0, 1, 3], oracle);

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

    // Bare correlated predicate: the Count is the whole selector body. MongoExpressionTranslator.SelfParam is
    // wired through NativeProjectionBinder for every Select, so ReferencesEnclosingScope resolves b.Title
    // against the outer scope and the MongoFilteredSizeExpression leaf goes native in every mode.
    [Fact]
    public void Bare_correlated_element_predicate_now_goes_native_EF421()
    {
        var collection = Seed(
            nameof(Bare_correlated_element_predicate_now_goes_native_EF421),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }));

        // PostDoc writes Title = "p", never equal to "x", so the correct count is 0.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var result = db.Entities.AsNoTracking().Select(b => b.Posts.Count(p => p.Title == b.Title)).ToList();
            Assert.Equal([0], result);
        }
    }

    // ---- Differential in-memory oracle ----
    //
    // A filtered count can silently return a wrong number, so the oracle is in-memory LINQ over the same
    // Expression object. Driver-LINQ can't serve: a wrapped count renders a bare $size with no $ifNull there and
    // aborts on ragged data.
    //
    // Array states: multi / single / empty / missing / BSON null. Element states in the multi rows: match,
    // non-match, predicate field missing, predicate field explicitly null.
    private static BsonDocument[] DifferentialRows() =>
    [
        Row("multi_mixed", new BsonArray
        {
            PostDoc(rank: 5, heading: "a"),      // matches Rank > 0
            PostDoc(rank: -5, heading: "b"),     // does not
            PostDoc(rank: null, heading: "c"),   // Rank explicitly null
            NoRankPostDoc("d")                   // Rank field ABSENT
        }),
        // Control for multi_mixed: same two real elements without the null/missing pair. Used by
        // Filtered_count_oracle_is_non_vacuous_on_the_element_axis.
        Row("multi_mixed_no_ragged", new BsonArray { PostDoc(rank: 5, heading: "a"), PostDoc(rank: -5, heading: "b") }),
        Row("multi_all_match", new BsonArray { PostDoc(rank: 1, heading: "a"), PostDoc(rank: 2, heading: "b") }),
        Row("multi_none_match", new BsonArray { PostDoc(rank: -1, heading: "a"), PostDoc(rank: -2, heading: "b") }),
        Row("single_match", new BsonArray { PostDoc(rank: 7, heading: "a") }),
        Row("single_no_match", new BsonArray { PostDoc(rank: -7, heading: "a") }),
        Row("empty", new BsonArray()),
        Row("missing", null),
        Row("null", BsonNull.Value)
    ];

    // PostDoc always writes Rank (BsonNull for null), so a missing Rank needs its own builder.
    private static BsonDocument NoRankPostDoc(string heading)
        => new()
        {
            { "Heading", heading }, { "Other", 0 }, { "Title", "p" }, { "Comments", new BsonArray() },
            // Pinned is non-nullable, so every seeded post must carry it.
            { "Pinned", false }, { "Flagged", BsonNull.Value }
        };

    // Selectors that agree with in-memory LINQ across every DifferentialRows() state, including ragged elements.
    // BSON total order is missing < null < numbers:
    // "gt" (p.Rank > 0): null/missing > 0 is false, matching LINQ's lifted null semantics.
    // "eq"/"ne": equality against a non-null constant agrees.
    // "and" (p.Rank > 0 && p.Rank < 6): the "< 6" conjunct is null-guarded; the "> 0" conjunct already excludes null.
    // "field_to_field" (p.Rank > p.Other): the lower side (p.Other) is null-guarded.
    // "arithmetic" (p.Rank + 1 > 0): gt-family; agrees.
    //
    // "<"-family predicates are covered by GuardedRelationalSelectors below; "null_check" (missing vs null, not
    // relational) still diverges and is pinned by its own test.
    public static IEnumerable<object[]> FilteredCountSelectors() =>
    [
        ["gt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 0) })],
        ["eq", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank == 5) })],
        ["ne", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank != 5) })],
        ["and", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 0 && p.Rank < 6) })],
        ["field_to_field", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > p.Other) })],
        ["arithmetic", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank + 1 > 0) })]
    ];

    [Theory]
    [MemberData(nameof(FilteredCountSelectors))]
    public void Filtered_count_projection_equals_the_in_memory_oracle_for_every_array_and_element_state(
        string name, Expression<Func<Blog, TitleCount>> selector)
    {
        var collection = Seed($"diff_{name}", DifferentialRows());

        // Oracle: evaluate the same selector in memory over materialized entities.
        List<(string, int)> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        // Server: must go native (NativeOnly) and agree exactly.
        List<(string, int)> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // Proves a ragged element (Rank missing or explicitly null) actually moves a count on this seed — i.e. ragged
    // elements are not silently dropped. Detects presence/absence, not missing-vs-null discrimination (that is
    // pinned by Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling).
    //
    // "ne" (p.Rank != 5) is the sensitive row: multi_mixed counts 3 (-5, null, missing), multi_mixed_no_ragged
    // counts 1. "arithmetic" is not sensitive (Rank + 1 > 0 is false for null and missing alike).
    [Fact]
    public void Filtered_count_oracle_is_non_vacuous_on_the_element_axis()
    {
        var collection = Seed(nameof(Filtered_count_oracle_is_non_vacuous_on_the_element_axis), DifferentialRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        Expression<Func<Blog, TitleCount>> ne =
            b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank != 5) };
        var rows = db.Entities.AsNoTracking().Select(ne).ToList().ToDictionary(r => r.Title, r => r.N);

        Assert.Equal(3, rows["multi_mixed"]);
        Assert.Equal(1, rows["multi_mixed_no_ragged"]);
        Assert.NotEqual(rows["multi_mixed_no_ragged"], rows["multi_mixed"]);
    }

    // Asserts NativeOnly agrees with DriverLinq (same BSON semantics), that both diverge from in-memory LINQ (so
    // a fix that removes the divergence turns this red), and pins the multi_mixed count.
    private void AssertDivergesFromLinqOracle(
        IMongoCollection<Blog> collection, Expression<Func<Blog, TitleCount>> selector, int expectedMultiMixedN)
    {
        List<(string, int)> linqOracle;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            linqOracle = db.Entities.AsNoTracking().ToList()
                .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        // NativeOnly, not Native: Native would fall back silently if the shape declined.
        List<(string, int)> nativeOnly;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            nativeOnly = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        List<(string, int)> driverLinq;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driverLinq = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        Assert.Equal(driverLinq, nativeOnly);

        // A rendering change that removes the divergence fails here.
        Assert.NotEqual(linqOracle, nativeOnly);

        // multi_mixed carries all four element states.
        Assert.Equal(expectedMultiMixedN, nativeOnly.Single(r => r.Item1 == "multi_mixed").Item2);
    }

    // Asserts NativeOnly equals in-memory LINQ, pins the multi_mixed count, and pins DriverLinq's (still divergent)
    // multi_mixed count, so a change on either path is noticed.
    private void AssertMatchesLinqOracleWhileDriverLinqDiverges(
        IMongoCollection<Blog> collection, Expression<Func<Blog, TitleCount>> selector,
        int expectedMultiMixedN, int driverLinqMultiMixedN)
    {
        List<(string, int)> linqOracle;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            linqOracle = db.Entities.AsNoTracking().ToList()
                .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        List<(string, int)> nativeOnly;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            nativeOnly = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        List<(string, int)> driverLinq;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driverLinq = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        Assert.Equal(linqOracle, nativeOnly);
        Assert.Equal(expectedMultiMixedN, nativeOnly.Single(r => r.Item1 == "multi_mixed").Item2);
        Assert.Equal(driverLinqMultiMixedN, driverLinq.Single(r => r.Item1 == "multi_mixed").Item2);
    }

    // A relational element predicate over a nullable leaf follows C# lifted semantics (null < c is false): the
    // renderer null-guards the lower operand inside $filter's cond, as everywhere else in the aggregation dialect.
    // DriverLinq still diverges: its unguarded $filter counts null and missing, which BSON orders below every number.
    //
    // On multi_mixed (5, -5, null, missing):
    //   "lt" (p.Rank < 0): LINQ and native N=1; DriverLinq N=3.
    //   "or" (p.Rank > 4 || p.Rank < -4): LINQ and native N=2; DriverLinq N=4.
    [Fact]
    public void Filtered_count_relational_operator_matches_in_memory_linq_on_ragged_data()
    {
        var collection = Seed(
            nameof(Filtered_count_relational_operator_matches_in_memory_linq_on_ragged_data),
            DifferentialRows());

        AssertMatchesLinqOracleWhileDriverLinqDiverges(
            collection, b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < 0) },
            expectedMultiMixedN: 1, driverLinqMultiMixedN: 3);
        AssertMatchesLinqOracleWhileDriverLinqDiverges(
            collection, b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 4 || p.Rank < -4) },
            expectedMultiMixedN: 2, driverLinqMultiMixedN: 4);
    }

    // Relational element predicates whose lower operand may be null. Each row: name, selector, the hand-computed
    // multi_mixed (5, -5, null, missing) count under C# semantics.
    public static IEnumerable<object[]> GuardedRelationalSelectors()
    {
        var threshold = 0;
        return
        [
            ["lt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < 0) }), 1],
            ["lte", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank <= -5) }), 1],
            // Constant on the left: the lower side is the Right operand.
            ["flipped_gt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => 0 > p.Rank) }), 1],
            ["flipped_gte", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => -5 >= p.Rank) }), 1],
            ["field_to_field_lt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < p.Other) }), 1],
            ["parameter_lt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < threshold) }), 1],
            // !(null < 0) is true in C#: the $not-wrapped guarded pair must be the exact complement (5, null, missing).
            ["negated_lt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => !(p.Rank < 0)) }), 3],
            // Correlated: goes through the two-scope element translator (outer b.Title renders at the root).
            ["correlated_lt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < 0 && p.Title != b.Title) }), 1],
            // >/>= with the field on the left: the lower side is a constant, so no guard and no change.
            ["gt", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 4) }), 1],
            ["gte", (Expression<Func<Blog, TitleCount>>)(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank >= 5) }), 1]
        ];
    }

    [Theory]
    [MemberData(nameof(GuardedRelationalSelectors))]
    public void Filtered_count_relational_element_predicate_equals_the_in_memory_oracle(
        string name, Expression<Func<Blog, TitleCount>> selector, int expectedMultiMixedN)
    {
        var collection = Seed($"guarded_{name}", DifferentialRows());

        List<(string, int)> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        List<(string, int)> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        // Hand oracle first, so agreement with a silently-wrong in-memory oracle cannot pass.
        Assert.Equal(expectedMultiMixedN, expected.Single(r => r.Item1 == "multi_mixed").Item2);
        Assert.Equal(expected, actual);
    }

    // The Where spelling ($expr over $size of $filter) renders the same element-scoped cond.
    [Fact]
    public void Filtered_count_relational_element_predicate_in_where_excludes_null_and_missing_elements()
    {
        var collection = Seed(
            nameof(Filtered_count_relational_element_predicate_in_where_excludes_null_and_missing_elements),
            DifferentialRows());
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // C#: exactly one element below 0 in multi_mixed (-5), multi_mixed_no_ragged (-5) and single_no_match (-7).
        // multi_none_match has two. Unguarded, multi_mixed would count 3.
        var titles = db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Rank < 0) == 1).ToList()
            .Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(["multi_mixed", "multi_mixed_no_ragged", "single_no_match"], titles);
    }

    // The guard references the element-scoped field ($$e.Rank), exactly as the comparison does, and a >= with a
    // constant lower side is emitted unguarded.
    [Fact]
    public void Filtered_count_element_guard_references_the_element_scoped_field()
    {
        var collection = Seed(
            nameof(Filtered_count_element_guard_references_the_element_scoped_field), DifferentialRows());

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy))
        {
            _ = db.Entities.AsNoTracking().Select(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < 0) }).ToList();
            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
            Assert.Contains("""{ "$and" : [{ "$gt" : ["$$e.Rank", null] }, { "$lt" : ["$$e.Rank", 0] }] }""", mql);
        }

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy))
        {
            _ = db.Entities.AsNoTracking().Select(b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank >= 5) }).ToList();
            var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
            Assert.Contains("""{ "$gte" : ["$$e.Rank", 5] }""", mql);
            Assert.DoesNotContain("null]", mql);
        }
    }

    // ---- Quantifiers over ragged elements ----
    //
    // An uncorrelated Any/All renders a query-dialect $elemMatch (already type-bracketed; untouched). A correlated
    // one (the `p.Title != b.Title` conjunct is always true, since PostDoc writes Title "p") renders
    // $anyElementTrue/$allElementsTrue over a $map, whose "in" carries the element-scoped guard. Hand oracle only:
    // DriverLinq throws on the missing/null arrays and, on the rest, treats null/missing Rank as below 0.
    private static BsonDocument[] QuantifierRows() =>
    [
        Row("only_ragged", new BsonArray { PostDoc(rank: null, heading: "a"), NoRankPostDoc("b") }),
        Row("neg_null", new BsonArray { PostDoc(rank: -5, heading: "a"), PostDoc(rank: null, heading: "b") }),
        Row("neg_missing", new BsonArray { PostDoc(rank: -5, heading: "a"), NoRankPostDoc("b") }),
        Row("all_neg", new BsonArray { PostDoc(rank: -5, heading: "a"), PostDoc(rank: -6, heading: "b") }),
        Row("pos_neg", new BsonArray { PostDoc(rank: 5, heading: "a"), PostDoc(rank: -5, heading: "b") }),
        Row("empty", new BsonArray()),
        Row("missing", null),
        Row("null", BsonNull.Value)
    ];

    // Each row: name, predicate, hand-computed C# answer (titles, sorted).
    public static TheoryData<string, Expression<Func<Blog, bool>>, string[]> RaggedQuantifierCases() => new()
    {
        // null < 0 is false: a row whose only candidates are null/missing has no match.
        { "any_lt", b => b.Posts.Any(p => p.Rank < 0), ["all_neg", "neg_missing", "neg_null", "pos_neg"] },
        { "correlated_any_lt", b => b.Posts.Any(p => p.Rank < 0 && p.Title != b.Title), ["all_neg", "neg_missing", "neg_null", "pos_neg"] },
        // [-5, null] is false: null < 0 is false. A missing guard or an inverted negation shows up here.
        { "all_lt", b => b.Posts.All(p => p.Rank < 0), ["all_neg", "empty", "missing", "null"] },
        { "correlated_all_lt", b => b.Posts.All(p => p.Rank < 0 && p.Title != b.Title), ["all_neg", "empty", "missing", "null"] },
        { "correlated_all_flipped", b => b.Posts.All(p => 0 > p.Rank && p.Title != b.Title), ["all_neg", "empty", "missing", "null"] },
        // !All ≡ Any(!pred): the negator flips the kind and $not-wraps the guarded pair.
        { "negated_correlated_all_lt", b => !b.Posts.All(p => p.Rank < 0 && p.Title != b.Title), ["neg_missing", "neg_null", "only_ragged", "pos_neg"] },
        { "negated_correlated_any_lt", b => !b.Posts.Any(p => p.Rank < 0 && p.Title != b.Title), ["empty", "missing", "null", "only_ragged"] },
        // >= with the field on the left: constant lower side, unguarded, unchanged.
        { "correlated_all_gte", b => b.Posts.All(p => p.Rank >= -6 && p.Title != b.Title), ["all_neg", "empty", "missing", "null", "pos_neg"] },
        { "correlated_any_gt", b => b.Posts.Any(p => p.Rank > 0 && p.Title != b.Title), ["pos_neg"] },
    };

    [Theory]
    [MemberData(nameof(RaggedQuantifierCases))]
    public void Quantifier_over_ragged_elements_equals_the_hand_and_in_memory_oracles(
        string name, Expression<Func<Blog, bool>> predicate, string[] expected)
    {
        var collection = Seed($"quant_{name}", QuantifierRows());

        List<string> linqOracle;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            linqOracle = db.Entities.AsNoTracking().ToList()
                .Where(predicate.Compile()).Select(b => b.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();
        }

        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t, StringComparer.Ordinal).ToList();
        }

        Assert.Equal(expected, linqOracle);
        Assert.Equal(expected, actual);
    }

    // Accepted, documented divergence, unaffected by the relational null guard because the predicate class is
    // equality. "null_check" (p.Rank == null): LINQ N=2 on multi_mixed (null and missing both
    // materialize as null); native/DriverLinq N=1 — only the explicit null matches `{$eq: ["$$e.Rank", null]}`.
    //
    // Mechanism: one BSON total order serves $eq and relational operators alike; a missing field has
    // $cmp: [field, null] = -1, so $eq is false. The CLR collapses missing and null into one `null`, so any
    // comparison that distinguishes them disagrees with it.
    [Fact]
    public void Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling()
    {
        var collection = Seed(
            nameof(Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling), DifferentialRows());

        AssertDivergesFromLinqOracle(
            collection, b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank == null) }, expectedMultiMixedN: 1);
    }

    // ---- Predicate spelling: b.Posts.Count(p => p.Rank > 0) compared against a threshold ----
    //
    // Mirrors NativeOwnedCollectionCountTests.Count_result_equals_the_in_memory_oracle_for_every_array_length_
    // and_state. "Rank > 0" agrees with in-memory LINQ (the "gt" row above), so every threshold belongs here.
    public static IEnumerable<object[]> FilteredCountPredicates() =>
    [
        [0, (Expression<Func<Blog, bool>>)(b => b.Posts.Count(p => p.Rank > 0) >= 0)],
        [1, (Expression<Func<Blog, bool>>)(b => b.Posts.Count(p => p.Rank > 0) >= 1)],
        [2, (Expression<Func<Blog, bool>>)(b => b.Posts.Count(p => p.Rank > 0) >= 2)],
        [3, (Expression<Func<Blog, bool>>)(b => b.Posts.Count(p => p.Rank > 0) >= 3)],
        ["eq0", (Expression<Func<Blog, bool>>)(b => b.Posts.Count(p => p.Rank > 0) == 0)],
        ["ne0", (Expression<Func<Blog, bool>>)(b => b.Posts.Count(p => p.Rank > 0) != 0)]
    ];

    [Theory]
    [MemberData(nameof(FilteredCountPredicates))]
    public void Count_result_equals_the_in_memory_oracle_for_every_array_and_element_state(
        object name, Expression<Func<Blog, bool>> predicate)
    {
        var collection = Seed($"diffpred_{name}", DifferentialRows());

        // Oracle: evaluate the same predicate in memory over materialized rows.
        List<string> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Where(predicate.Compile()).Select(b => b.Title).OrderBy(t => t).ToList();
        }

        // Server: must go native (NativeOnly) and agree exactly.
        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }
}
