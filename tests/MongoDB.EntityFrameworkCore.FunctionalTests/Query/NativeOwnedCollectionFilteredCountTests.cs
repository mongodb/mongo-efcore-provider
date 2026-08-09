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
/// EF-359: a PREDICATED element count over an OWNED (embedded) collection navigation. Task 2 shipped the
/// PREDICATE spelling — <c>b.Posts.Count(p =&gt; p.Rank &gt; 0)</c> compared against a threshold — as <c>$expr</c>
/// over a null-safe <c>$size</c> of a <c>$filter</c>. Task 3 (this file's projection-leaf tests below) shipped
/// shape A, the PROJECTION spelling, <c>Select(b =&gt; new { N = b.Posts.Count(pred) })</c>, as a <c>$project</c>
/// leaf of the same <c>{$size: {$filter: ...}}</c> shape — see the sibling
/// <see cref="NativeOwnedCollectionCountTests"/>'s <c>Filtered_count_projection_now_goes_native_EF359</c>, which
/// records the bug this closed.
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

    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

    /// <summary>
    /// Whether the reserved <c>ProjectionAliasTier</c> <c>Synthetic</c> alias <c>_v</c> appears in the
    /// emitted <c>$project</c> stage AS A FIELD NAME.
    /// </summary>
    /// <remarks>
    /// <b>The scoping is the point (A4-3 review, M1).</b> The logged message is the WHOLE command, database and
    /// collection names included, so a bare <c>Assert.Contains("_v", mql)</c> can pass on a <c>_v</c> that has
    /// nothing to do with the projection — and the collection name is derived from the TEST NAME, so it would
    /// change under a rename. Cutting to the <c>$project</c> stage and matching the QUOTED key form is what
    /// makes the assertion mean "the projection's field name". Mirrors
    /// <c>NativeOwnedCollectionCountTests.ProjectAliasSummary</c>, which additionally reports the member-name
    /// alias because that file's disjointness test needs both.
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
        // Nullable ON PURPOSE: a missing or explicitly-null stored field must MATERIALIZE (as null) rather
        // than throw, or the missing-field state cannot be exercised at all.
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

    // A named DTO, copied from the sibling file for fixture parity. Used by
    // Filtered_count_projection_into_a_named_dto_goes_native (Task 3), which reaches NativeProjectionBinder's
    // MemberInit branch — a different branch from the anonymous-type tests in this file.
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

    // Rows differ in the number of elements SATISFYING the predicate — the input space a filtered count is
    // sensitive to, and the axis LenRow alone cannot control (its ranks are 0..n-1, so "Rank > 0" and length are
    // correlated). Each row's Posts carry `matching` elements with Rank = 5 and `nonMatching` with Rank = -5.
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

    // MatchRows() with every title carrying a shared "m_" prefix. Added by EF-405 slice A4-3 for the
    // parameterized-Where LATE-DECLINE legs: a captured-local StartsWith is what makes the native factory
    // decline AFTER the alias-addressed shaper has already been committed, and that route is the only one in
    // this file where a bare projection's alias miss is SILENT. MatchRows()' own titles ("none"/"one"/"three"/
    // "empty"/"missing"/"null") share no prefix, so a late-decline leg over that seed would quietly drop the
    // ragged rows — and the ragged rows are the ONLY ones that discriminate a bare $size from a $size over
    // $ifNull. Same six rows, same counts, same title ORDER (m_empty, m_missing, m_none, m_null, m_one,
    // m_three), so [0,0,0,0,1,3] is the expected vector for either seed.
    private static BsonDocument[] PrefixedMatchRows() =>
    [
        MatchRow("m_none", 0, 3), MatchRow("m_one", 1, 2), MatchRow("m_three", 3, 0),
        Row("m_empty", new BsonArray()), Row("m_missing", null), Row("m_null", BsonNull.Value)
    ];

    /// <summary>
    /// Runs <paramref name="query"/> and describes what it did as a short string, so a caller can COLLECT every
    /// leg's outcome and assert them together instead of aborting on the first.
    /// </summary>
    /// <remarks>
    /// The collect-then-assert shape is not a style preference, and this file is where the defect it guards
    /// against was found: written as a loop of direct assertions, a regression in the FIRST leg aborts the test
    /// and the remaining legs never execute — which is exactly how
    /// <c>Bare_filtered_count_projection_with_a_captured_parameter_goes_native_in_every_mode</c>'s mandatory
    /// explicit-<c>DriverLinq</c> leg went unexercised while the slice claimed "zero MongoCommandException
    /// across all runs" (EF-405 A4-2 review, I2). Adopted as the convention in A4-2 and applied to every leg
    /// set this file's A4-3 flips introduce. Mirrors
    /// <c>NativeComputedBareProjectionTests.LegOutcome</c> exactly, deliberately — the two files describe
    /// outcomes with the same vocabulary so a reader can compare their assertions side by side.
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

        // NOTE (deviation from the brief's literal snippet, deliberate): the brief wrote
        // `.Select(b => b.Title)` directly on the IQueryable before `.ToList()`. That would make the bare-scalar
        // Select itself part of the server-side query — a SEPARATE, pre-existing SP3-wide boundary this task does
        // not touch (a bare-scalar terminal projection never populates Select.Projection, so it always falls back
        // to driver-LINQ, which NativeOnly forbids) — so the shape would throw for a reason UNRELATED to whether
        // the count predicate itself goes native, defeating the test's purpose. Materializing whole entities
        // FIRST (native, proven by NativeOnly succeeding) and projecting titles CLIENT-SIDE afterward — exactly
        // the established idiom throughout this file's siblings (see e.g. NativeOwnedCollectionCountTests'
        // AssertNativeOnlyMatches) — is what actually proves the predicate goes native.
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

        // See the deviation note on Filtered_count_predicate_goes_native above: ToList() before Select, not after.
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
        // THE TIER-1 FAIL-CLOSED TRIPWIRE. An array-index existence test answers the UNFILTERED count's question,
        // so if MongoFilteredSizeExpression is ever collapsed into MongoSizeExpression this returns the wrong rows
        // with no error. Goes red the moment that happens. See MongoFilteredSizeExpression's remarks.
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
    public void Correlated_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // Post.Title deliberately collides with Blog.Title: the element-scoped translator resolves members by NAME,
        // so without ReferencesEnclosingScope this would silently retarget b.Title at the ELEMENT and return the
        // wrong rows under the default Native mode. The guard is what makes this a decline instead.
        var collection = Seed(
            nameof(Correlated_element_predicate_declines_and_falls_back_to_correct_rows),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "h") }));

        // ORDER IS DELIBERATE (fix round 1): the wrong-rows assertion runs FIRST and in its own `using` block, so
        // it is independently load-bearing rather than merely unreachable filler after the NativeOnly throw below.
        // xUnit stops a test method at its first failed assertion — if the NativeOnly Assert.Throws ran first and
        // the ReferencesEnclosingScope guard were ever deleted, that assertion alone would already fail and this
        // Native-mode Assert.Empty would never execute, silently failing to prove the wrong-DATA hazard the guard
        // exists to prevent. Mutation-verified (delete ReferencesEnclosingScope, rebuild, run this test): WITH
        // this ordering the Native leg goes red on its own (returns the row — "p" is compared to itself via the
        // misresolved b.Title, a tautology — instead of staying empty); confirmed independently of the NativeOnly
        // leg, which also goes red separately.
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            // "p" (PostDoc's Title) != "x" (the Blog's Title), so the correct answer is NO rows. An element-scoped
            // misresolution would compare the element's own Title to itself and return the row.
            Assert.Empty(db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Title == b.Title) > 0).ToList());
        }

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => p.Title == b.Title) > 0).ToList());
        }
    }

    [Fact]
    public void Element_predicate_outside_the_renderable_set_declines()
    {
        var collection = Seed(
            nameof(Element_predicate_outside_the_renderable_set_declines),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // A regex predicate has no aggregation-dialect rendering (CanRender declines it), so the whole predicate
        // declines at TRANSLATE time rather than throwing at render time.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking()
                .Where(b => b.Posts.Count(p => p.Heading!.StartsWith("h")) > 0).ToList());
    }

    [Fact]
    public void Primitive_element_collection_filtered_count_declines()
    {
        var collection = Seed(nameof(Primitive_element_collection_filtered_count_declines), RowWithTags("x", "a", "bb"));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Tags is a mapped primitive-collection PROPERTY, not a navigation — TryResolveOwnedCollectionPath's
        // final-hop check declines it.
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

        // $expr is a HARD SERVER ERROR inside $elemMatch, so a filtered count there must decline at translate
        // time — if it were admitted the whole query would throw at EXECUTION time under the default Native mode.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking()
                .Where(b => b.Posts.Any(p => p.Comments.Count(c => c.Age > 0) > 1)).ToList());

        // REGRESSION TRIPWIRE: the UNFILTERED count in the same position renders as an array-index test and must
        // stay native.
        Assert.Single(db.Entities.AsNoTracking().Where(b => b.Posts.Any(p => p.Comments.Count > 1)).ToList());
    }

    // MECHANISM, TRACED at the whole-branch review — and it is NOT the negator, contrary to how this decline was
    // first documented. MongoExpressionTranslator's Not arm gates on
    // `operand is MongoBinaryExpression { Left: MongoSizeExpression }`; a MongoFilteredSizeExpression fails that
    // pattern, so MongoExpressionNegator.TryNegate is NEVER CALLED for this shape. The node falls through to
    // `return new MongoUnaryExpression(Not, operand)` and the decline happens later, at RENDER time:
    // MongoQueryLanguageRenderer.RenderUnary's operand is a MongoBinaryExpression that is NOT a query-native
    // comparison (its Left is a MongoFilteredSizeExpression, not a MongoFieldExpression), so it reaches the
    // "only supports Not over a MongoFieldExpression or a query-native comparison" throw, which
    // MongoShapedQueryCompilingExpressionVisitor.TryBuildPipeline's typed
    // `catch (NativeTranslationNotSupportedException) when (mode != MongoQueryMode.NativeOnly)` converts into a
    // driver-LINQ fallback. CONSEQUENCE FOR A FUTURE EDITOR: adding a MongoExpressionNegator arm for the new node
    // would change NOTHING here, because the negator is never reached. (The negator does independently fail
    // closed, via its own IsQueryDialectRenderable gate — but that is a second, UNREACHED line of defence, not
    // the operative mechanism.)
    //
    // Added at the whole-branch review: this was the only entry on the Query/AGENTS.md "declines that fall back
    // gracefully, each pinned" list with no named test.
    [Fact]
    public void Negated_filtered_count_comparison_declines_and_falls_back_to_correct_rows()
    {
        // Deliberately NOT MatchRows(): the fallback here is driver-LINQ, which renders a bare $size with no
        // $ifNull and ABORTS the aggregate on a missing or explicitly-null array (see
        // NativeOwnedCollectionCountTests.Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_
        // aborts_on_a_missing_array). Those two rows would make the Native leg throw for a reason unrelated to
        // this decline, so the seed is the well-formed subset plus an empty array.
        var collection = Seed(
            nameof(Negated_filtered_count_comparison_declines_and_falls_back_to_correct_rows),
            MatchRow("none", 0, 3), MatchRow("one", 1, 2), MatchRow("three", 3, 0), Row("empty", new BsonArray()));

        // NativeOnly does not swallow the render-time throw, so the decline is observable rather than silent.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Where(b => !(b.Posts.Count(p => p.Rank > 0) > 1)).ToList());
        }

        // Native falls back and must return the exact COMPLEMENT of the un-negated predicate over this seed
        // (Filtered_count_predicate_goes_native's "three" is the only row with more than one matching element).
        // Asserted as real rows, so a decline that silently returned nothing — or everything — fails here rather
        // than passing vacuously.
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var titles = db.Entities.AsNoTracking()
                .Where(b => !(b.Posts.Count(p => p.Rank > 0) > 1))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

            Assert.Equal(["empty", "none", "one"], titles);
        }
    }

    // A THIRD INCIDENTAL WIDENING, unplanned, and it CONTRADICTS design §6's decline table row "two-scope
    // (SelectMany) filtered count → declines outright" (corrected in the design doc's §11 as-built deltas, not in
    // §6's dated body). Found at the whole-branch review. Mechanism:
    // NativeSelectManyBinder.TryBuildOwnedInnerFilter translates an inner-only owned SelectMany filter with a
    // SINGLE-SCOPE element-scoped MongoExpressionTranslator — NOT the two-scope translator design §6 assumed —
    // so TryResolveOwnedCollectionPath's two-scope decline never fires at all, and once Task 2 taught
    // TranslateOperand to recognize a predicated count as an ordinary operand, this shape reached native for
    // free. It emits, after `$unwind: "$Posts"`, a `$match` of
    // `{$expr: {$gt: [{$size: {$filter: {input: {$ifNull: ["$Posts.Comments", []]}, as: "e",
    // cond: {$gt: ["$$e.Age", 0]}}}}, 1]}}` — legal because that `$match` is top-level (not inside $elemMatch),
    // and correctly addressed because `Posts.Comments` names the unwound element's own array.
    //
    // This is the FILTERED analogue of
    // NativeOwnedCollectionCountTests.Count_inside_an_owned_SelectMany_inner_filter_goes_native, which exists
    // because the MongoFieldPrefixRewriter case is LOAD-BEARING rather than defensive: the count's array path is
    // ELEMENT-relative ("Comments") and Rewrite must prefix it to "Posts.Comments" to address the $unwind-ed
    // element. The filtered node goes through the SAME rewriter case, so it needs the same net.
    //
    // MEASURED BEFORE AND AFTER, not inferred: at the branch base 33fdc58 (throwaway worktree, probe over this
    // exact shape) it threw InvalidOperationException containing "could not be translated" in ALL THREE modes —
    // Native, DriverLinq and NativeOnly alike. So this is a HARD-FAIL -> native fix, NOT the fallback -> native
    // flip the review that requested this test assumed, and there was never a driver-LINQ oracle to compare
    // against — the row assertion below IS the oracle.
    //
    // The threshold is chosen so the FILTER is load-bearing in the assertion: "mid" has 2 comments but only 1
    // with Age > 0, so an UNFILTERED `Comments.Count > 1` would include it. A rendering that dropped the $filter
    // (or ignored its cond) would return ["many", "mid"] and fail here.
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

    // Branch-review finding (fix round 1): the projection-side gates this task widens do not make EVERY
    // predicated-count projection native — several element-predicate/collection shapes still reach the SAME
    // EF-359 translation-time crash (InvalidOperationException, "could not be translated", identically under
    // Native/DriverLinq/NativeOnly) that this task's own opening test used to pin for the p.Rank > 0 shape.
    // Not a regression — none of these ever worked — but undocumented until now, and this file's convention is
    // to state a decline's disposition precisely ("hard-fails in EVERY mode" vs. "falls back gracefully").
    //
    // MEASURED (not assumed): all four spellings below throw the identical InvalidOperationException in all
    // three modes on the shipped tree. Mechanism, traced, and STATED PER ROW because Task 4 (the bare-spelling
    // fix) changed what keeps two of these three rows green — a stale blanket claim here previously said the
    // wrong thing and was corrected in fix round 1 of Task 4:
    //
    // Correlated and Primitive (both a PREDICATED Count/LongCount): CanRender (or, for Primitive,
    // TryResolveOwnedCollectionPath's final-hop check) declines the element predicate/collection INSIDE
    // TranslateOperand's count branch, so TranslateOperand returns null for the WHOLE leaf — not just the count
    // sub-expression — and NativeProjectionBinder.TryTranslateLeaf therefore fails this leaf entirely (there is
    // no partial/fallback leaf translation). TryPopulateNativeProjection then returns false,
    // MarkNotNativelyRepresentable() sets Route = Fallback, and the REGISTRATION block added by Task 3 (gated on
    // Route == Projection) never runs. With Route == Fallback, control reaches
    // MongoProjectionBindingExpressionVisitor.VisitMethodCall's Queryable switch, whose CountWithPredicate/
    // LongCountWithPredicate arm — as of Task 4 — DOES match a predicated Count now (this is stale-corrected:
    // it is NOT true, as of Task 4, that this switch arm matches only the predicate-less overload). What keeps
    // BOTH rows green is that arm's own decline guards: for Correlated, the Count call is nested inside this
    // test's `new {...}` projection rather than being the whole selector body, so the arm's
    // `_translatedRootExpression` identity check fails and it declines (a second, independent guard,
    // `ContainsShaperReference`, would also decline this predicate — it references the outer `b`, already
    // rewritten to the query root's entity shaper by the time this visitor runs — but the identity mismatch is
    // what actually fires first, short-circuiting before that second check runs); for Primitive, `Tags` is a
    // mapped primitive-collection PROPERTY rather than a navigation, so `visitedSource` is never a
    // CollectionShaperExpression at all and the arm declines on that pre-existing, unrelated check regardless of
    // identity. Either way, declining here falls through to the SAME generic `methodCallExpression.Update(...)`
    // rebuild path that crashes with the pre-existing EF-359 InvalidOperationException — unaffected by Task 4's
    // widening, which only ever produces a WORKING result for the true bare spelling.
    //
    // Posts.Where(pred).Count() (a PREDICATE-LESS Count over a Where-filtered source): genuinely unaffected by
    // Task 4 — EF Core does NOT fuse this into Count(pred) upstream of this provider's translator (measured
    // directly), so it never reaches the predicated arm at all; it is CountWithoutPredicate, the pre-existing
    // EF-357 arm, which Task 4 did not touch. It reaches the identical crash as its own, structurally distinct
    // case, not merely a duplicate of the plain Count(pred) shape.
    [Fact]
    public void Correlated_primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode()
    {
        var collection = Seed(
            nameof(Correlated_primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode),
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

        // Correlated: Post.Title deliberately collides with Blog.Title (see the Blog/Post class comments) —
        // ReferencesEnclosingScope declines this predicate before the element-scoped translator is built.
        AssertHardFailsEverywhere(
            q => q.Select(b => new { b.Title, N = b.Posts.Count(p => p.Title == b.Title) }).Cast<object>());

        // Primitive collection: Tags is a mapped primitive-collection PROPERTY, not a navigation —
        // TryResolveOwnedCollectionPath's final-hop check declines it.
        AssertHardFailsEverywhere(
            q => q.Select(b => new { b.Title, N = b.Tags.Count(t => t.Length > 1) }).Cast<object>());

        // Posts.Where(pred).Count(): a DIFFERENT method-call shape from Count(pred), not fused upstream by EF.
        AssertHardFailsEverywhere(
            q => q.Select(b => new { b.Title, N = b.Posts.Where(p => p.Rank > 0).Count() }).Cast<object>());
    }

    // Kept as a SEPARATE Fact from the three declines above, deliberately: this row's disposition is tied to
    // CanRender, which the repo owner has RULED to keep for now on scope grounds (EF-359 fix round 2) even
    // though removing it would turn this hard crash into a graceful decline/working fallback (the design doc's
    // stated justification for keeping it was measured false — see the CanRender call site in
    // MongoExpressionTranslator.cs and the matching Query/AGENTS.md note). The improvement is filed as EF-365
    // ("A non-renderable element predicate in a filtered Count(pred) projection hard-fails where a graceful
    // fallback is available") — EF-365 is what re-baselines THIS test alone when it ships (Native/DriverLinq
    // will start returning correct values, NativeOnly will start declining CLEANLY instead of crashing), while
    // the other three declines above are unaffected by it — keeping this row in its own test means only this
    // one needs re-baselining, not the whole group.
    [Fact]
    public void Non_renderable_element_predicate_filtered_projection_still_hard_fails_in_every_mode()
    {
        var collection = Seed(
            nameof(Non_renderable_element_predicate_filtered_projection_still_hard_fails_in_every_mode),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking()
                    .Select(b => new { b.Title, N = b.Posts.Count(p => p.Heading!.StartsWith("h")) }).ToList());
            Assert.Contains("could not be translated", ex.Message);
        }
    }

    [Fact]
    public void Filtered_count_projection_goes_native()
    {
        // This is the shape EF-359 was filed to close: Select(b => new { b.Title, N = b.Posts.Count(pred) })
        // threw InvalidOperationException identically under Native/DriverLinq/NativeOnly before Task 3. See the
        // flipped NativeOwnedCollectionCountTests.Filtered_count_projection_now_goes_native_EF359, which records
        // that history; this file carries the full breadth.
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
        // The DTO spelling reaches NativeProjectionBinder's MemberInit branch, which the anonymous-type tests
        // do not.
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

        // Full tuple list across MatchRows(), not just the "none" row (fix round 1, Minor 5) — this covers the
        // ragged (missing/null) rows too, the one shape where a filtered $size:$filter and an unfiltered $size
        // share a single $project. "none"/"one"/"three" each have 3 total Posts elements (All == 3) but differ in
        // how many satisfy Rank > 0 (Filtered == 0/1/3) — otherwise a filtered/unfiltered mix-up would pass.
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

    // NOTE on the brief's step 1 "Arithmetic_over_a_filtered_count_projection_goes_native": that test is NOT
    // added here as a second test. Arithmetic_projection_leaf_containing_a_filtered_count_goes_native (below)
    // already pins this exact shape (Count(pred) * 2) — it predates this task (added in fix round 1 of Task 2,
    // as an incidental widening this task's own gate change does not touch) and its assertions are a SUPERSET
    // of what the brief's version asks for: it checks real per-row values (not just NotEmpty) AND asserts
    // Native/NativeOnly/DriverLinq parity across the full ragged MatchRows() seed, including missing/null
    // arrays. Adding a second, narrower copy would only duplicate coverage, so it is deliberately skipped —
    // see this task's report for the record of that decision.
    [Fact]
    public void Arithmetic_projection_leaf_containing_a_filtered_count_goes_native()
    {
        // SECOND WIDENING (fix round 1, unreported by the original task report). NativeProjectionBinder's
        // pre-existing arithmetic projection-leaf branch (EF-347) gates only on a BinaryExpression arithmetic
        // top node plus TryTranslateValue succeeding — it has no restriction on WHICH operand kinds populate
        // that success. Now that TranslateOperand recognizes a predicated count as an ordinary operand (this
        // task), an arithmetic wrapper around one reaches native too, with no code change to the binder itself.
        // Measured benign and parity-preserving across all three modes, including a missing/null Posts array
        // (the $filter/$size composition inherits the same $ifNull wrap the bare filtered-count leaf uses).
        var collection = Seed(nameof(Arithmetic_projection_leaf_containing_a_filtered_count_goes_native), MatchRows());

        List<(string Title, int X)> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode, BlogModel);
            return db.Entities.AsNoTracking()
                .Select(b => new { b.Title, X = b.Posts.Count(p => p.Rank > 0) * 2 })
                .ToList().OrderBy(r => r.Title).Select(r => (r.Title, r.X)).ToList();
        }

        // MatchRows(): "none" 0 matching -> 0*2=0; "one" 1 matching -> 1*2=2; "three" 3 matching -> 3*2=6;
        // "empty"/"missing"/"null" all have 0 matches -> 0.
        var expected = new List<(string, int)>
        {
            ("empty", 0), ("missing", 0), ("none", 0), ("null", 0), ("one", 2), ("three", 6)
        };

        Assert.Equal(expected, Run(MongoQueryMode.NativeOnly));
        Assert.Equal(expected, Run(MongoQueryMode.Native));
        Assert.Equal(expected, Run(MongoQueryMode.DriverLinq));
    }

    // EF-359 Task 4 — shape C, the BARE spelling: Select(b => b.Posts.Count(p => p.Rank > 0)). EF-359 fixed a
    // CRASH here: before it, this shape threw in every mode; after it, it folded the count client-side over an
    // empty pipeline and returned correct values, exactly as the bare UNFILTERED count had done since
    // owned-data slice 7. THE ROUTE HAS SINCE CHANGED AND THE VALUES HAVE NOT — EF-405 slice A4-2 admitted both
    // size kinds as bare tier-2 projection leaves (arm 1a of NativeProjectionBinder.TryDeriveSyntheticAlias,
    // under the reserved `_v` alias and the Synthetic tier), so the count is now computed SERVER-SIDE. The
    // reason this test still passes unchanged is exactly the contract: a routing flip must not move a value.
    // Its NAME, however, claimed the route rather than the values, so A4-3 corrected it in place; the routing
    // claim now lives in Bare_filtered_count_projection_goes_native_under_NativeOnly below, which is the flipped
    // tripwire that used to lock the decline.
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

    // FLIPPED TRIPWIRE (EF-405 slice A4-3). This was
    // `Bare_filtered_count_projection_declines_cleanly_under_NativeOnly`, and it existed to LOCK the decline so
    // that lifting it would have to be a visible edit rather than a silently-relaxed gate. Slice A4-2 lifted it
    // deliberately: arm 1a of NativeProjectionBinder.TryDeriveSyntheticAlias admits a MongoSizeExpression or
    // MongoFilteredSizeExpression as the TOP node of a bare selector body whose un-stripped driver fallback
    // cannot abort (IsFallbackSafeBareSizeLeaf), committing it under `_v` / ProjectionAliasTier.Synthetic. The
    // NON-DOTTED array-path rule still applies to THIS (filtered) kind, which is protected structurally — the
    // driver renders {$sum: {$map: …}} and $map tolerates a missing array — but it no longer applies to the
    // unfiltered kind, where the gate calls the A4-0 rewrite's own matcher instead. The lock is
    // replaced, not deleted: what the decline assertion used to pin is now pinned as a CAPABILITY — NativeOnly
    // SUCCEEDING is the routing proof, and the values it returns are asserted against the same vector the two
    // fallback modes return.
    //
    // WHAT IS STILL DECLINED, so this flip is not read as "every neighbouring shape converted": the same count
    // through an owned-reference HOP (b.Home.Notes.Count(pred), a DOTTED array path) is STILL declined, by that
    // same method, and deliberately — see NativeProjectionBinder.IsFallbackSafeBareSizeLeaf's remarks and
    // NativeComputedBareProjectionTests.Bare_FILTERED_count_leaf_through_an_owned_reference_HOP_is_declined_and_
    // answers_correctly. A primitive-collection count (b.Tags.Count) never reaches tier 2 at all.
    //
    // States exercised: present (one/three), empty, element ABSENT, explicitly BSON null — all four, read INTO,
    // on the root-declared navigation. The seed is PrefixedMatchRows() rather than MatchRows() so that the
    // late-decline legs below still cover the two ragged rows; see that helper's own comment.
    [Fact]
    public void Bare_filtered_count_projection_goes_native_under_NativeOnly()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_goes_native_under_NativeOnly), PrefixedMatchRows());
        var prefix = "m_";

        // COLLECT-THEN-ASSERT: every leg runs, then the whole set is asserted together. See LegOutcome's
        // remarks — the defect that convention closes was found on this file's own captured-parameter tripwire.
        const string expected = "[0,0,0,0,1,3]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList())));
        }

        // THE MANDATORY LATE-DECLINE LEGS. A captured-local StartsWith has no native regex rendering, so the
        // native factory declines at RENDER time, after the alias-addressed shaper has already been committed —
        // the one route in this suite where a bare projection's alias miss is SILENT rather than loud. The
        // explicit-DriverLinq leg is the rubric-level obligation: the native default's carve-out is conditional
        // on UseQueryMode(DriverLinq) restoring the previous path, so a leg that never runs is a leg that
        // cannot discharge it.
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

    // FLIPPED TRIPWIRE (EF-405 slice A4-3). This was `Bare_filtered_count_projection_folds_client_side`, and it
    // asserted `aggregate([])` — no $project and no $size — as the MEASURED proof that the whole document,
    // array and all, was fetched and counted in process. That claim was true, and it was the emitted-MQL half
    // of the same lock the sibling above carried in routing form. A4-2 lifted the lock deliberately (arm 1a of
    // TryDeriveSyntheticAlias, see that sibling's comment), so the pipeline is no longer empty and the count is
    // no longer folded client-side. The assertion is replaced rather than weakened: it now pins the SERVER-SIDE
    // rendering positively, and pins the absence of the old empty pipeline, so a silent revert to the fold is
    // caught from both directions.
    //
    // The shape is `$size` over `$filter` over `$ifNull`, in the AGGREGATION dialect and never as a
    // query-dialect array-index form — no array-index form exists for a PREDICATED count, which is a
    // load-bearing invariant rather than a limitation (MongoFilteredSizeExpression is a sealed SIBLING of
    // MongoSizeExpression precisely so Tier 1 can never fire for it). `_v` is the reserved Synthetic alias the
    // bare arm commits under, and asserting it here is what ties the emitted key to the alias the shaper reads.
    //
    // KNOWN AND ACCEPTED (A4-3 review): this test has NO late-decline leg and NO explicit-DriverLinq leg — the
    // sibling Bare_filtered_count_projection_goes_native_under_NativeOnly carries both for this shape, and
    // duplicating them here would net nothing. The consequence worth stating rather than leaving to be
    // rediscovered: because this test runs under NativeOnly, the emitted MQL for this shape under the DEFAULT
    // Native mode is NOT pinned anywhere. The two modes are expected to emit the same pipeline (the gate that
    // differs between them only decides whether a DECLINE throws or falls back), but that expectation is
    // untested, so a divergence between them would show up as a value regression in the sibling rather than as
    // an MQL failure here.
    [Fact]
    public void Bare_filtered_count_projection_emits_a_native_size_over_filter_under_the_reserved_alias()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_emits_a_native_size_over_filter_under_the_reserved_alias),
            MatchRows());
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        var counts = db.Entities.AsNoTracking().OrderBy(b => b.Title)
            .Select(b => b.Posts.Count(p => p.Rank > 0)).ToList();

        // NativeOnly succeeding at all is the routing proof; the values keep the MQL assertions from being the
        // whole test, since an MQL shape alone cannot show the pipeline answers correctly.
        Assert.Equal([0, 0, 0, 0, 1, 3], counts);

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$project", mql);
        Assert.Contains("$size", mql);
        Assert.Contains("$filter", mql);
        Assert.Contains("$ifNull", mql);
        // SCOPED to the $project stage's field names (A4-3 review, M1) — a bare Contains("_v", mql) would match
        // anywhere in the logged command, including the collection name, which is derived from this method's
        // own name and so would change under a rename.
        Assert.True(ProjectStageCommitsTheSyntheticAlias(spy));
        // The old lock, asserted in the negative so a revert to the client-side fold reddens here too.
        Assert.DoesNotContain("aggregate([])", mql);
    }

    // FLIPPED TRIPWIRE (EF-405 slice A4-3) — AND THE SPECIAL CASE OF THE SIX, because what it locked was not a
    // decline-with-a-working-fallback but a HARD FAIL IN EVERY MODE.
    //
    // HISTORY, kept because it is the whole reason this test exists. EF-359 Task 4 took its own brief's
    // CONTINGENCY: a captured local arrived at the CLIENT-SIDE rebuild as an EF query-parameter node the fold
    // could not evaluate (the predicate lambda is deliberately not re-Visited), measured as
    // ArgumentException("must be reducible node") from the lambda compiler, and a ContainsQueryParameter guard
    // turned that into a clean InvalidOperationException("could not be translated") — identically under Native,
    // DriverLinq AND NativeOnly. The test was renamed from the brief's literal
    // "..._with_a_captured_parameter" (which asserted correct VALUES) to pin that decline instead, a smaller,
    // honest fix over a half-working one. This test then LOCKED it.
    //
    // WHY THE LOCK IS LIFTED, and by what. That whole failure mode was a property of the client-side rebuild,
    // which this shape no longer reaches: EF-405 slice A4-2 admits the bare filtered count as a tier-2
    // projection leaf (arm 1a of NativeProjectionBinder.TryDeriveSyntheticAlias, `_v` /
    // ProjectionAliasTier.Synthetic), so the count is computed SERVER-SIDE and the captured local is an
    // ordinary query parameter substituted into the pipeline. The ContainsQueryParameter guard is not what
    // changed — the shape simply stops arriving at it.
    //
    // AN ORACLE EXISTS, AND IT IS USED — the spike recorded this flip as INFERRED-an-improvement and UNVERIFIED
    // against one, which is not good enough for a shape whose whole prior behaviour was "returns nothing".
    // The oracle is IN-MEMORY LINQ over the SAME Expression object: `selector` is sent to the server on one leg
    // and `selector.Compile()` is applied to whole entities materialized under Native on the other, so the two
    // sides cannot silently drift apart the way two hand-written predicates can. The oracle is legitimate here
    // for a reason worth stating rather than assuming: this seed's element predicate is `p.Rank > threshold`
    // over ranks of exactly 5 and -5, with NO missing or explicitly-null Rank FIELD anywhere, so it sits
    // squarely in the AGREEING half of the documented BSON-total-order divergence (see the "gt" row of
    // FilteredCountSelectors and the two documented-divergence tests below) — the ragged states this seed does
    // carry are ARRAY-level (empty / element absent / explicit BSON null), which the $ifNull wrap and EF's own
    // missing-array normalization both answer as 0. A `<`-family captured predicate would NOT be oracle-checkable
    // this way, and must not be added here on the strength of this one passing.
    //
    // States exercised: present (m_one/m_three), empty, element ABSENT, explicitly BSON null — all four, read
    // INTO, on the root-declared navigation.
    [Fact]
    public void Bare_filtered_count_projection_with_a_captured_parameter_goes_native_in_every_mode()
    {
        var collection = Seed(
            nameof(Bare_filtered_count_projection_with_a_captured_parameter_goes_native_in_every_mode),
            PrefixedMatchRows());
        var threshold = 0;
        var prefix = "m_";

        // ONE Expression object, used both ways. `threshold` is a captured local either way — the compiler
        // still emits a display-class field access, which EF still extracts as a query parameter — so hoisting
        // the lambda into a variable does not change the shape under test.
        Expression<Func<Blog, int>> selector = b => b.Posts.Count(p => p.Rank > threshold);

        List<int> oracle;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            oracle = db.Entities.AsNoTracking().ToList()
                .OrderBy(b => b.Title).Select(selector.Compile()).ToList();
        }

        var expected = "[" + string.Join(",", oracle) + "]";

        // EVERY MODE'S LEG EXECUTES, and the outcomes are collected before any of them is asserted. Written as
        // a foreach of direct assertions, a change of behaviour in the FIRST mode aborts the test and the
        // remaining legs never run at all — which is exactly how this shape's mandatory explicit-DriverLinq leg
        // went silently unexercised while a slice claimed "zero MongoCommandException across all runs" (EF-405
        // A4-2 review, I2). Collecting first means a regression reports what every mode did, not just the first.
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(selector).ToList())));
        }

        // The mandatory late-decline legs, for the reason the sibling capability test states: a captured-local
        // StartsWith declines at RENDER time, after the alias-addressed shaper has been committed, and that is
        // the only route in this suite where a bare projection's alias miss is silent.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(selector).ToList())));
        }

        // The oracle is asserted EXPLICITLY as well as used, so a leg set that agreed with a silently-wrong
        // oracle (e.g. one that had itself started returning zeros) cannot pass vacuously.
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

    // Fix round 1 (Quality review): the top-level-identity guard restores the WRAPPED correlated residual
    // (Correlated_primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode above), but a BARE
    // correlated predicate — Select(b => b.Posts.Count(p => p.Title == b.Title)) — is a DIFFERENT shape the
    // identity check does NOT protect: this Count call IS the top-level selector body, so identity holds and the
    // arm proceeds. Before the structural fix below, that regressed to a WORSE failure than the pre-existing one:
    // ReplacingExpressionVisitor has already rewritten the outer `b` in the predicate to the query root's entity
    // shaper by the time this visitor runs, and since the predicate is deliberately not re-Visited, the unresolved
    // shaper reference survived into the rebuilt client-side Enumerable.Count call and blew up downstream at
    // shaper-compile time with KeyNotFoundException("...'EmptyProjectionMember'...") instead of the clean,
    // pre-existing InvalidOperationException("could not be translated") every other declined shape in this file
    // gets. The fix: decline whenever the predicate body contains a provider/EF shaper node (StructuralTypeShaperExpression /
    // ProjectionBindingExpression / EntityProjectionExpression) — the structural property the identity guard was
    // only ever a PROXY for. Measured A/B at HEAD (this fix): byte-identical to base c5b467b —
    // InvalidOperationException, "could not be translated", under Native, DriverLinq, AND NativeOnly alike. This
    // crash fires at TRANSLATION time (MongoProjectionBindingExpressionVisitor.Translate, reached unconditionally
    // from TranslateSelect), before MongoQueryMode is ever read by the compile-time gate — so NativeOnly gets the
    // identical exception, NOT a clean NativeTranslationNotSupportedException decline (an earlier draft of this
    // test wrongly assumed the latter for the NativeOnly leg and was corrected once measured; this matches the
    // design doc's own account of the pre-existing EF-359 crash family, and the sibling
    // Correlated_primitive_and_where_count_filtered_projections_still_hard_fail_in_every_mode test above, which
    // asserts the same InvalidOperationException across all three modes for its own correlated/primitive/Where
    // rows).
    [Fact]
    public void Bare_correlated_element_predicate_still_hard_fails_in_every_mode()
    {
        var collection = Seed(
            nameof(Bare_correlated_element_predicate_still_hard_fails_in_every_mode),
            Row("x", new BsonArray { PostDoc(rank: 1, heading: "hello") }));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            var ex = Assert.Throws<InvalidOperationException>(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count(p => p.Title == b.Title)).ToList());
            Assert.Contains("could not be translated", ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Task 5 — the differential in-memory oracle
    // ------------------------------------------------------------------
    //
    // A filtered count can return a silently WRONG NUMBER rather than an error, so an in-memory oracle over the
    // SAME Expression object (sent to the server and, separately, compiled for client-side evaluation) is the
    // gate here — not driver-LINQ parity. Driver-LINQ is not usable as the oracle for this shape: a WRAPPED count
    // renders a bare $size with no $ifNull under DriverLinq and aborts on ragged data (see the sibling
    // NativeOwnedCollectionCountTests' Wrapped_count_projection_under_DriverLinq_works_for_present_arrays_and_
    // aborts_on_a_missing_array), so a driver-LINQ oracle leg simply cannot run over this seed at all.
    //
    // Array states: multi / single / empty / MISSING / explicit BSON null. Element states, within the multi rows:
    // predicate matches, does not match, the predicate's field is MISSING, and the predicate's field is
    // explicitly BSON null — the two states a $filter cond evaluates against a non-existent value, and the ones
    // an in-memory oracle over nullable CLR properties disagrees with if the rendering is wrong.
    private static BsonDocument[] DifferentialRows() =>
    [
        Row("multi_mixed", new BsonArray
        {
            PostDoc(rank: 5, heading: "a"),      // matches Rank > 0
            PostDoc(rank: -5, heading: "b"),     // does not
            PostDoc(rank: null, heading: "c"),   // Rank explicitly null
            NoRankPostDoc("d")                   // Rank field ABSENT
        }),
        // The CONTROL for multi_mixed's ragged pair: the same two "real" elements (rank=5 match, rank=-5
        // non-match), WITHOUT the explicit-null/missing pair. Exists solely to prove the oracle theory's seed
        // is non-vacuous on the ELEMENT axis — see Filtered_count_oracle_is_non_vacuous_on_the_element_axis
        // below, which is a DIFFERENT axis from the one the $ifNull-removal mutation (task report step 4)
        // proves (that one is array-level: it aborts the aggregate on a missing/null Posts ARRAY, and says
        // nothing about a missing/null element FIELD within a present array).
        Row("multi_mixed_no_ragged", new BsonArray { PostDoc(rank: 5, heading: "a"), PostDoc(rank: -5, heading: "b") }),
        Row("multi_all_match", new BsonArray { PostDoc(rank: 1, heading: "a"), PostDoc(rank: 2, heading: "b") }),
        Row("multi_none_match", new BsonArray { PostDoc(rank: -1, heading: "a"), PostDoc(rank: -2, heading: "b") }),
        Row("single_match", new BsonArray { PostDoc(rank: 7, heading: "a") }),
        Row("single_no_match", new BsonArray { PostDoc(rank: -7, heading: "a") }),
        Row("empty", new BsonArray()),
        Row("missing", null),
        Row("null", BsonNull.Value)
    ];

    // PostDoc always writes a Rank element (BsonNull when the argument is null), so a MISSING Rank needs its own
    // builder — missing-vs-null is exactly the distinction this oracle exists to check, and conflating them would
    // silently drop the "field absent" state DifferentialRows needs.
    private static BsonDocument NoRankPostDoc(string heading)
        => new() { { "Heading", heading }, { "Other", 0 }, { "Title", "p" }, { "Comments", new BsonArray() } };

    // ---- Oracle-theory rows: MEASURED (not assumed) to AGREE with in-memory LINQ across every
    // DifferentialRows() state, including the ragged (missing-field / explicit-null-field) elements. ----
    //
    // "gt" (p.Rank > 0): a GREATER-THAN comparison over a nullable leaf. Measured to agree: BSON's total element
    // order is missing < null < numbers, so both "null > 0" and "missing > 0" render false — the same answer
    // LINQ's lifted-operator null propagation gives for (int?)null > 0. (Mirror image of "lt", which is why only
    // the LESS-THAN family diverges here, not every relational operator.)
    // "eq"/"ne" (p.Rank == 5 / != 5): equality against a non-null CONSTANT. Measured to agree.
    // "and" (p.Rank > 0 && p.Rank < 6): CONTAINS a "<" sub-clause, yet measured to AGREE with in-memory LINQ —
    // the divergence is MASKED here: for a null/missing Rank the "> 0" conjunct is already false in BOTH
    // dialects (per the "gt" finding above), so the AND is false either way regardless of what "< 6" alone would
    // have answered. Do not generalize from "lt" alone diverging to "any predicate containing <" diverging —
    // it depends on whether a sibling conjunct already resolves the ragged rows to the same answer.
    // "field_to_field" (p.Rank > p.Other): GREATER-THAN-family, so the same "gt" reasoning applies; measured to
    // agree. This agreement is DIRECTIONAL, not a property of "comparing two fields" in general — the REVERSED
    // direction, p.Rank < p.Other, was measured to diverge exactly like "lt" (multi_mixed: in-memory LINQ N=1,
    // native N=3, because Other is 0 for every seeded Post, making this structurally identical to "Rank < 0").
    // Do not add a "<" field-to-field variant here expecting it to still agree.
    // "arithmetic" (p.Rank + 1 > 0): GREATER-THAN-family; measured to agree, including on the ragged elements —
    // see Filtered_count_oracle_is_non_vacuous_on_the_element_axis below for why this row (unlike "ne") turns
    // out NOT to be sensitive to whether the ragged elements are present at all.
    //
    // NOT here, each MEASURED to diverge and moved to a documented-divergence test below. ALL THREE are now
    // covered by the SAME owner ruling — accept and document, no rendering change, no null-guard, no decline
    // (this comment previously said "null_check" was a SEPARATE, NOT-YET-RULED finding; the ruling was made in
    // Task 6 and it is corrected here):
    // "lt" (named in the brief) and "or" (an UNMASKED instance of the same relational divergence, reached via a
    // disjunct) — see Filtered_count_relational_operator_diverges_from_in_memory_linq_on_ragged_data_by_owner_
    // ruling. "null_check" (p.Rank == null) is kept in its own test,
    // Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling, only because the predicate CLASS
    // differs (an EQUALITY comparison rather than a relational one) — the disposition is identical, and so is
    // the mechanism: ONE BSON total order, missing < null < numbers, serves $eq and the relational operators
    // alike.
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

        // Oracle: materialize whole entities, then evaluate the SAME selector in memory. Sending one Expression
        // object to both sides is what makes this a differential test rather than two hand-written predicates
        // that can silently diverge.
        List<(string, int)> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        // Server: must go NATIVE (NativeOnly is the only reliable signal) and agree exactly.
        List<(string, int)> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Select(selector).ToList()
                .Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // Fix round 1: proves the oracle theory's seed is non-vacuous on the ELEMENT axis — that a ragged element
    // (a Rank field that is missing, or present-but-explicitly-null) actually MOVES a count on this seed. That
    // is a DIFFERENT axis from the one the task report's $ifNull-removal mutation proves (that one is
    // array-level — it aborts the aggregate on a missing/null Posts ARRAY entirely, and says nothing about a
    // ragged FIELD inside an array that IS present).
    //
    // WHAT THIS TEST PROVES, STATED NARROWLY — the original wording here OVERCLAIMED and is corrected in place
    // (Task 6). It used to say "a rendering that could not tell a missing element field from an explicitly-null
    // one would still pass every row of
    // Filtered_count_projection_equals_the_in_memory_oracle_for_every_array_and_element_state above". This test
    // does NOT establish that: removing the ragged PAIR moves "ne"'s count 3 -> 1 whichever ragged element is
    // removed, so what it detects is ragged-element PRESENCE/ABSENCE, not missing-vs-null DISCRIMINATION. The
    // genuine missing-vs-null discrimination proof already exists in this file — the "null_check" divergence
    // row in Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling below, where the same
    // `p.Rank == null` predicate counts 1 for an EXPLICIT-null Rank and 0 for a MISSING Rank. Read that test
    // for the missing-vs-null property; read this one for "ragged elements are not silently dropped".
    //
    // "ne" (p.Rank != 5) is MEASURED to be the row that is actually sensitive to the ragged elements' PRESENCE:
    // on "multi_mixed" (rank=5, rank=-5, rank=explicit-null, rank=MISSING) it counts 3 — the -5 element plus
    // BOTH the null and the missing element (neither equals 5, so both satisfy !=); on "multi_mixed_no_ragged"
    // (the same rank=5/rank=-5 pair with the ragged two removed) it counts 1. A rendering that silently dropped,
    // miscounted, or mishandled a ragged element would move this number, and this is the row that would catch
    // it.
    //
    // CORRECTION TO A FIX-ROUND-1 REVIEW CLAIM: the review that requested this test also named "arithmetic"
    // (p.Rank + 1 > 0) as sensitive to the ragged elements, alongside "ne". Measured directly (not taken on
    // trust): it is NOT — mixed=1, no_ragged=1, identical — because Rank+1>0 already evaluates false for both
    // the null and the missing element (same "gt"-family reasoning as the oracle-theory comment above), so
    // removing them changes nothing. Recorded as a correction rather than silently repeating the unverified
    // claim; "ne" alone is the row this test relies on.
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

    // Shared helper for the two documented-divergence tests below: proves NativeOnly agrees with DriverLinq
    // (parity — both apply the same BSON dialect semantics) AND that the pair genuinely diverges from the
    // in-memory LINQ oracle (so a future fix that removes the divergence turns this red instead of staying
    // vacuously green), then pins the exact measured multi_mixed count.
    private void AssertDivergesFromLinqOracle(
        IMongoCollection<Blog> collection, Expression<Func<Blog, TitleCount>> selector, int expectedMultiMixedN)
    {
        List<(string, int)> linqOracle;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            linqOracle = db.Entities.AsNoTracking().ToList()
                .Select(selector.Compile()).Select(r => (r.Title, r.N)).OrderBy(r => r.Item1).ToList();
        }

        // NativeOnly, not Native: Native would fall back SILENTLY if the shape ever declined, so this leg is
        // what actually proves the shape goes native (a decline here would throw, not pass vacuously).
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

        // Parity: native and DriverLinq agree with each other (both apply the same BSON dialect semantics).
        Assert.Equal(driverLinq, nativeOnly);

        // The divergence itself, not just its absence from the parity check: if a future rendering change
        // type-brackets the dialect and native starts agreeing with LINQ, this line — not just a comment —
        // goes red.
        Assert.NotEqual(linqOracle, nativeOnly);

        // The exact measured count on the one row carrying all four element states, pinned so a silent shift
        // in either direction is caught.
        Assert.Equal(expectedMultiMixedN, nativeOnly.Single(r => r.Item1 == "multi_mixed").Item2);
    }

    // THE DOCUMENTED, ACCEPTED DIVERGENCE (owner ruling, EF-359 Task 5). A RELATIONAL element predicate over a
    // NULLABLE element leaf can diverge from in-memory LINQ semantics on ragged data (a missing or
    // explicitly-null element field), because MongoDB's $filter `cond` dialect is NOT TYPE-BRACKETED: there is
    // one consistent BSON total order, missing < null < numbers, that plain LINQ null-propagation does not
    // reproduce. Native and the driver's own LINQ v3 provider (DriverLinq) AGREE WITH EACH OTHER on every ragged
    // row measured below — both take the same BSON semantics — and both DIVERGE from in-memory LINQ. The repo
    // owner ruled: accept and document, no rendering change, no null-guard, no decline.
    //
    // MEASURED per row, against DifferentialRows()'s "multi_mixed" row (rank=5 match, rank=-5 non-match,
    // rank=EXPLICIT NULL, rank=MISSING):
    //
    //   "lt" (p.Rank < 0), NAMED BY THE BRIEF: in-memory LINQ N=1 (only rank=-5); native/DriverLinq N=3 — BOTH
    //     the explicit-null AND the missing element ALSO satisfy "$lt: [Rank, 0]", because missing and null both
    //     sort below every number (including negatives) in the BSON total order, so "null < 0" and "missing < 0"
    //     render true, where LINQ's lifted "(int?)null < 0" is false.
    //
    //   "or" (p.Rank > 4 || p.Rank < -4): NOT a new mechanism — it is the SAME already-ruled relational
    //     divergence as "lt", reached through a disjunct instead of a bare comparison, with nothing masking it
    //     (contrast "and" above, whose sibling ">0" conjunct DOES mask the same "<" sub-clause). In-memory LINQ
    //     N=2 (rank=5 satisfies "> 4"; rank=-5 satisfies "< -4"; null/missing satisfy neither); native/DriverLinq
    //     N=4 — the explicit-null AND missing elements both additionally satisfy "< -4" for the same BSON-order
    //     reason as "lt", adding 2 more matches this OR does not mask.
    [Fact]
    public void Filtered_count_relational_operator_diverges_from_in_memory_linq_on_ragged_data_by_owner_ruling()
    {
        var collection = Seed(
            nameof(Filtered_count_relational_operator_diverges_from_in_memory_linq_on_ragged_data_by_owner_ruling),
            DifferentialRows());

        AssertDivergesFromLinqOracle(
            collection, b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank < 0) }, expectedMultiMixedN: 3);
        AssertDivergesFromLinqOracle(
            collection, b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank > 4 || p.Rank < -4) }, expectedMultiMixedN: 4);
    }

    // RULED, and the ruling is the SAME as for the two relational rows above: ACCEPT AND DOCUMENT — no
    // rendering change, no null-guard, no decline. This test carried a deliberately NEUTRAL name
    // ("..._pending_owner_ruling") and a "NOT-YET-RULED" comment while the question was open (escalated in fix
    // round 1 of this task); the owner ruled in Task 6, so both are corrected in place here. It stays a
    // SEPARATE Fact from the relational pair only because the predicate CLASS differs ("==" against null rather
    // than a relational comparison) — the disposition is identical, and the mechanism turns out to be identical
    // too (see MECHANISM below: one BSON total order serves $eq and the relational operators alike).
    //
    // "null_check" (p.Rank == null): the brief's own stated premise — "a missing BSON field materializes as a
    // null CLR property, so this should agree" — was measured FALSE. In-memory LINQ N=2 on "multi_mixed" (both
    // the explicit-null AND the missing element materialize as a null int? and satisfy `== null`); native/
    // DriverLinq N=1 — ONLY the explicit-null element matches `{$eq: ["$$e.Rank", null]}`. Isolated directly
    // (two single-element rows, one explicit-null-Rank, one missing-Rank): the explicit-null row counts 1, the
    // missing-Rank row counts 0.
    //
    // MECHANISM (corrected in fix round 1 — the original wording, "a divide within the dialect's own equality
    // operator," was wrong; a live-server probe with $unwind + $project against an absent field measured
    // $type = "missing", $cmp: [field, null] = -1, $eq: [field, null] = false, $lt: [field, 0] = true, and
    // $cmp = 0 for an explicit null): there is ONE CONSISTENT BSON TOTAL ORDER, missing < null < numbers, used
    // by $eq AND the relational operators ALIKE — $eq is false because $cmp != 0, and $lt is true for exactly
    // the same reason. There is no inconsistency between MongoDB's own operators. The actual gap is that the
    // CLR collapses two distinct BSON values (missing and explicit null) into a single `null`, so ANY dialect
    // comparison that can distinguish them (as $eq legitimately can, via $cmp != 0) will disagree with a CLR
    // model that cannot.
    [Fact]
    public void Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling()
    {
        var collection = Seed(
            nameof(Filtered_count_null_check_diverges_from_in_memory_linq_by_owner_ruling), DifferentialRows());

        AssertDivergesFromLinqOracle(
            collection, b => new TitleCount { Title = b.Title, N = b.Posts.Count(p => p.Rank == null) }, expectedMultiMixedN: 1);
    }

    // ---- Predicate spelling: b.Posts.Count(p => p.Rank > 0) compared against a threshold. ----
    //
    // Mirrors NativeOwnedCollectionCountTests.Count_result_equals_the_in_memory_oracle_for_every_array_length_
    // and_state exactly: the SAME Expression<Func<Blog, bool>> is sent to the server and compiled for in-memory
    // evaluation. This predicate uses only "Rank > 0" (a relational comparison already proven to AGREE with
    // in-memory LINQ on DifferentialRows() above — see the "gt" row of FilteredCountSelectors), so every
    // threshold row here belongs in the ordinary oracle theory; no row needs to move to the documented-divergence
    // treatment.
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
}
