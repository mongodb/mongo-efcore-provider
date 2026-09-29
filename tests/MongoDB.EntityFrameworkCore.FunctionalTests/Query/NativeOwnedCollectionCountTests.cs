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
/// A count over an owned collection navigation, compared against a value, translates natively: an array-index
/// existence test for an integer-constant threshold, otherwise $expr over a null-safe $size.
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

    // MQL capture via SpyLoggerProvider (TestMqlLoggerFactory/AssertMql exist only in SpecificationTests).
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
    /// Describes the emitted <c>$project</c> stage by which of <c>"N"</c> (member-name alias) and <c>"_v"</c>
    /// (the <c>Synthetic</c> alias) appear in it as quoted field names.
    /// </summary>
    /// <remarks>
    /// Scoped to the <c>$project</c> stage because the logged command includes database/collection names, so a bare
    /// <c>"_v"</c> search would depend on the test name. Returns a string so callers fold it into their collected
    /// legs rather than asserting in place.
    /// </remarks>
    private static string ProjectAliasSummary(SpyLoggerProvider spyLogger)
    {
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        var start = mql.IndexOf("$project", StringComparison.Ordinal);
        if (start < 0)
            return "no $project stage";

        var stage = mql[start..];
        return $"N={stage.Contains("\"N\"", StringComparison.Ordinal)} _v={stage.Contains("\"_v\"", StringComparison.Ordinal)}";
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
        // Nullable so a missing or null stored field materializes rather than throws.
        public int? Rank { get; set; }
        public string? Heading { get; set; }
        public int? Other { get; set; }

        // Deliberately collides with Blog.Title to exercise the correlated-element-predicate guard: the
        // element-scoped translator resolves members by name.
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

    // A named DTO so the same Expression<Func<Blog, TitleCount>> can run on the server and compile for the
    // in-memory oracle; also exercises NativeProjectionBinder's MemberInit branch.
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

    // Rows differ only in array length plus the "no elements" states — the whole input space a cardinality
    // predicate is sensitive to.
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

    // Home/Tags are seeded present-but-empty: they are required, and a missing one fails materialization with
    // an unrelated error.
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

    // One row with a non-empty Posts and a non-empty, different-length Home.Notes, so
    // Count_projection_alongside_sibling_leaves_goes_native's third leaf discriminates.
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

    // SeedLengths' six rows with a shared "c_" prefix, so a parameterized StartsWith late-decline leg still covers
    // the missing/null rows that discriminate bare $size from $size over $ifNull. Counts ordered by title:
    // c_len0..c_len3, c_missing, c_null => 0,1,2,3,0,0.
    private IMongoCollection<Blog> SeedPrefixedLengths(string name)
        => Seed(name,
            LenRow("c_len0", 0), LenRow("c_len1", 1), LenRow("c_len2", 2), LenRow("c_len3", 3),
            Row("c_missing", posts: null), Row("c_null", BsonNull.Value));

    /// <summary>
    /// Runs <paramref name="query"/> and describes the outcome as a short string, so callers collect every leg and
    /// assert them together; a first-leg failure would otherwise hide the rest. Same vocabulary as
    /// <c>NativeComputedBareProjectionTests.LegOutcome</c> and <c>NativeOwnedCollectionFilteredCountTests.LegOutcome</c>.
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

    // Every array length state plus the "no elements" states. "missing" and "null" are what a query-dialect $size
    // gets wrong (neither matches $size: 0, but Count is 0 for both).
    private IMongoCollection<Blog> SeedLengths(string name)
        => Seed(name,
            LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3),
            Row("missing", posts: null), Row("null", BsonNull.Value));

    // Posts is always a real array: the driver's own count renders a bare $size under $expr, which aborts on a
    // missing or null array, so the DriverLinq oracle can only run against these rows.
    private IMongoCollection<Blog> SeedWellFormed(string name)
        => Seed(name, LenRow("len0", 0), LenRow("len1", 1), LenRow("len2", 2), LenRow("len3", 3));

    // Runs `query` in one mode and reduces it to the Title list. Mode orchestration lives in NativeModeAssert.
    private List<string> RunTitles(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query, MongoQueryMode mode)
    {
        using var db = CreateContext(collection, mode, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    private List<string> AssertNativeAndParity(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => NativeModeAssert.NativeAndParity(mode => RunTitles(collection, query, mode));

    // Asserts a shape declines cleanly under NativeOnly and that the fallback is correct (Native == DriverLinq);
    // returns the results for the caller to check against expected values.
    private List<string> AssertDeclinesCleanly(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => NativeModeAssert.DeclinesCleanly(mode => RunTitles(collection, query, mode));

    // Proves a shape goes native (NativeOnly succeeds) without a DriverLinq oracle leg, for seeds whose missing/null
    // Posts rows abort the driver's translation.
    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => RunTitles(collection, query, MongoQueryMode.NativeOnly);

    // `threshold` is a captured parameter, so every row routes to the $expr tier, not the array-index form. The
    // constant tier is covered by Count_comparison_emits_the_array_index_form and the const-gt* matrix rows.
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
        // Count == 0 is true for empty, missing and null arrays; a query-dialect { $size: 0 } would match only
        // "len0".
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

        // Without $ifNull the missing/null rows would abort the aggregate.
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
        // The constant tier is pure query dialect, so it is legal inside $elemMatch, where $expr is a server error.
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
        // Pins that an arithmetic projection leaf containing a count goes native (the count is an ordinary operand
        // in TranslateOperand). In a $project the null-safe $size applies, so a missing/null array yields 0 rather
        // than aborting. See also Bare_embedded_collection_Count_projection_goes_native_for_present_arrays and
        // Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array.
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
        // The plain sibling of Arithmetic_projection_leaf_containing_a_count_goes_native: Count on its own.
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
    public void Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike()
    {
        // The DriverLinq leg of the wrapped count. The driver alone would render a bare $size (a server error on a
        // missing/null array); MongoEFToLinqTranslatingExpressionVisitor.TryRewriteEmbeddedCollectionNavigationCount
        // coalesces the field to an empty collection first, so DriverLinq agrees with Native on ragged data.
        // (NativeProjectionBinder's NullCoalesceSyntheticBareCountBody deliberately leaves wrapped bodies alone.)
        var wellFormed = SeedWellFormed(
            nameof(Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike));

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
            nameof(Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike)
            + "ragged");

        using (var db = CreateContext(ragged, MongoQueryMode.DriverLinq, BlogModel))
        {
            var raggedRows = db.Entities.AsNoTracking()
                .Select(b => new { b.Title, N = b.Posts.Count })
                .ToList().OrderBy(r => r.Title).ToList();

            Assert.Equal(
                [("len0", 0), ("len1", 1), ("len2", 2), ("len3", 3), ("missing", 0), ("null", 0)],
                raggedRows.Select(r => (r.Title, r.N)).ToArray());
        }

        // Parity leg on the same ragged seed: Native also answers 0 for the missing and null rows.
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
    public void Constant_projection_leaf_is_safely_admitted_via_the_project_literal_wrap(string leafKind)
    {
        // A bare constant/parameter leaf (`X = 5`, `X = 0`, `X = false`, a captured local) goes native because
        // MongoPipelineFactory.RenderProject $literal-wraps it; unwrapped, 0/false would be read as an
        // exclusion/inclusion flag ("Cannot do exclusion on field X in inclusion projection"). The 0/false rows are
        // what catch a regression of the wrap or the gate; `X = 5` alone would not.
        var collection = SeedWellFormed(
            nameof(Constant_projection_leaf_is_safely_admitted_via_the_project_literal_wrap) + leafKind);

        var captured = 7;

        // Each selector pairs a real member leaf with the constant/parameter leaf under test.
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

        // The oracle is in-memory LINQ over the same rendering, so expected values cannot drift.
        List<string> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            var compiled = render.Compile();
            expected = db.Entities.AsNoTracking().ToList().Select(compiled).OrderBy(v => v).ToList();
        }

        // Native: correct values, and no server abort from a bare 0/false.
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            Assert.Equal(expected, run(db));
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            Assert.Equal(expected, run(db));
        }

        // NativeOnly: correct values with no fallback.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Equal(expected, run(db));
        }
    }

    [Fact]
    public void Owned_collection_count_projection_emits_a_null_safe_size()
    {
        // $ifNull is mandatory: $size on a missing or null array aborts the whole aggregate. The "missing" and
        // "null" rows would abort without it.
        var collection = SeedLengths(nameof(Owned_collection_count_projection_emits_a_null_safe_size));

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking().Select(b => new { b.Title, N = b.Posts.Count }).ToList();

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$project", mql);
        Assert.Contains("$size", mql);
        Assert.Contains("$ifNull", mql);
        Assert.Contains("Posts", mql);
    }

    // A bare `Posts.Count` projection goes native under the reserved `_v` alias (ProjectionAliasTier.Synthetic,
    // via NativeProjectionBinder.TryDeriveSyntheticAlias / IsFallbackSafeBareSizeLeaf). The MQL assertion pins the
    // server-side $size over $ifNull and the absence of the old empty `aggregate([])` client-side fold.
    //
    // Still declined (see NativeComputedBareProjectionTests): an owned-reference hop (b.Home.Notes.Count), ISet<T>
    // navigations, primitive-collection counts, and arithmetic containing a count.
    //
    // Present arrays only. With this well-formed seed the late-decline legs exercise the silent-alias route but
    // can't fail on it; discrimination rests on the ragged seed in
    // Bare_and_wrapped_count_projections_both_go_native_from_the_same_model and on
    // NativeComputedBareProjectionTests. Don't collapse those into this one.
    [Fact]
    public void Bare_embedded_collection_Count_projection_goes_native_for_present_arrays()
    {
        var collection = SeedWellFormed(
            nameof(Bare_embedded_collection_Count_projection_goes_native_for_present_arrays));
        var prefix = "len";

        // Collect-then-assert: every leg runs before any is asserted. See LegOutcome.
        const string expected = "[0,1,2,3]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} direct", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList())));
        }

        // Late-decline legs: a captured-local StartsWith declines at render time, after the alias-addressed shaper
        // is committed — the one route where a bare projection's alias miss is silent. The DriverLinq leg is
        // required because UseQueryMode(DriverLinq) must restore the previous path.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList())));
        }

        // Emitted-MQL pin, collected with the legs; alias check scoped to the $project field names.
        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger))
        {
            legs.Add(("NativeOnly MQL", LegOutcome(() =>
            {
                _ = db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList();

                var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
                return string.Join(
                    " ",
                    $"$project={mql.Contains("$project", StringComparison.Ordinal)}",
                    $"$size={mql.Contains("$size", StringComparison.Ordinal)}",
                    $"$ifNull={mql.Contains("$ifNull", StringComparison.Ordinal)}",
                    // `_v` as a $project field name ties the emitted key to the alias the shaper reads.
                    ProjectAliasSummary(spyLogger),
                    $"empty={mql.Contains("aggregate([])", StringComparison.Ordinal)}");
            })));
        }

        Assert.Equal(
            [
                ("NativeOnly direct", expected),
                ("Native direct", expected),
                ("DriverLinq direct", expected),
                ("Native late-decline", expected),
                ("DriverLinq late-decline", expected),
                ("NativeOnly MQL", "$project=True $size=True $ifNull=True N=False _v=True empty=False")
            ],
            legs);
    }

    [Fact]
    public void Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array()
    {
        // A missing or null stored array projects as Count 0 (EF-358 normalizes it to an empty collection on every
        // path). The count is computed server-side as {$size: {$ifNull: [...]}} under `_v`; the sibling test pins
        // the MQL. A routing flip must not move a value, and the ragged rows are where a wrong rendering would.
        // The full ragged net lives in NativeComputedBareProjectionTests.
        var collection = SeedLengths(
            nameof(Bare_embedded_collection_Count_projection_returns_zero_for_a_missing_or_null_array));

        // Collect-then-assert, so a regression in one mode cannot hide the others. See LegOutcome.
        var legs = new List<(string Leg, string Outcome)>();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode}", LegOutcome(
                () => db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList().OrderBy(n => n).ToList())));
        }

        Assert.Equal(
            [
                ("Native", "[0,0,0,1,2,3]"),
                ("DriverLinq", "[0,0,0,1,2,3]"),
                ("NativeOnly", "[0,0,0,1,2,3]")
            ],
            legs);
    }

    [Fact]
    public void Count_inside_an_owned_SelectMany_inner_filter_goes_native()
    {
        // The count's array path is element-relative ("Comments"); MongoFieldPrefixRewriter must prefix it to
        // "Posts.Comments" to address the $unwind-ed element, or this shape throws.
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

        spy.AssertExecutedMqlContains(expectedMatch);
    }

    [Fact]
    public void Count_equality_emits_a_merged_two_key_match()
    {
        var collection = SeedWellFormed(nameof(Count_equality_emits_a_merged_two_key_match));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => b.Posts.Count == 2).ToList();

        spy.AssertExecutedMqlContains("{ \"Posts.1\" : { \"$exists\" : true }, \"Posts.2\" : { \"$exists\" : false } }");
    }

    [Fact]
    public void A_parameterized_threshold_emits_expr_with_a_null_safe_size()
    {
        var collection = SeedWellFormed(nameof(A_parameterized_threshold_emits_expr_with_a_null_safe_size));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);
        var threshold = 1;

        db.Entities.AsNoTracking().Where(b => b.Posts.Count > threshold).ToList();

        // One fragment rather than separate Contains checks, so the nesting order of $expr/$size/$ifNull is pinned.
        spy.AssertExecutedMqlContains("{ \"$expr\" : { \"$gt\" : [{ \"$size\" : { \"$ifNull\" : [\"$Posts\", []] } }, { \"$literal\" : 1 }] } }");
    }

    [Fact]
    public void Bare_Any_still_emits_the_index_zero_form()
    {
        // The bare-Any() unification's byte-identity bar, asserted from the user-facing side.
        var collection = SeedWellFormed(nameof(Bare_Any_still_emits_the_index_zero_form));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => b.Posts.Any()).ToList();

        spy.AssertExecutedMqlContains("{ \"Posts.0\" : { \"$exists\" : true } }");
    }

    [Fact]
    public void Negated_bare_Any_still_emits_the_index_zero_absent_form()
    {
        var collection = SeedWellFormed(nameof(Negated_bare_Any_still_emits_the_index_zero_absent_form));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spy);

        db.Entities.AsNoTracking().Where(b => !b.Posts.Any()).ToList();

        spy.AssertExecutedMqlContains("{ \"Posts.0\" : { \"$exists\" : false } }");
    }

    [Fact]
    public void A_predicated_Count_now_goes_native()
    {
        // A predicated Count goes native via $expr over a null-safe $size of a $filter (MongoFilteredSizeExpression).
        // A wrong $filter/$size composition returns wrong rows rather than declining, so this asserts parity with
        // DriverLinq across the ragged seed (the driver's $sum-over-$map fallback also tolerates missing/null arrays).
        // Full breadth lives in NativeOwnedCollectionFilteredCountTests.
        var collection = SeedLengths(nameof(A_predicated_Count_now_goes_native));

        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.Count(p => p.Rank > 0) > 1));

        // Ranks are 0..n-1: len2 -> one passes, len3 -> two pass, missing/null -> 0.
        Assert.Equal(new[] { "len3" }, titles);
    }

    [Fact]
    public void A_primitive_collection_Count_declines_and_falls_back_to_correct_rows()
    {
        // TryResolveOwnedCollectionPath requires an embedded collection navigation; Tags is a primitive-collection
        // property. Deferred until primitive collections get Any/All/Count together.
        var collection = Seed(nameof(A_primitive_collection_Count_declines_and_falls_back_to_correct_rows),
            RowWithTags("notags"), RowWithTags("twotags", "a", "b"));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Tags.Count > 1));

        Assert.Equal(new[] { "twotags" }, titles);
    }

    [Fact]
    public void A_parameterized_count_inside_a_quantifier_declines_and_falls_back_to_correct_rows()
    {
        // $expr is a server error inside $elemMatch, so the parameterized tier must decline there;
        // IsQueryDialectRenderable handles it.
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
        // Accepted asymmetry: Count <= @param is native, but !(Count > @param) declines because the negator requires
        // query-dialect renderability. A coverage gap, not a correctness one.
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
    // An off-by-one or wrong negation returns wrong rows rather than declining, and the DriverLinq oracle can't
    // cover missing/null arrays. So the same expression is sent to the server and compiled for an in-memory oracle.

    public static TheoryData<string, Expression<Func<Blog, bool>>> CountMatrixCases()
    {
        var data = new TheoryData<string, Expression<Func<Blog, bool>>>();

        // ---- CONSTANT tier: must be inline literals (a captured loop variable becomes a parameter and routes to
        // the $expr tier). 0/1/2 cover every boundary the arithmetic distinguishes. ----
        //
        // EF Core rewrites `Count() > 0` to `Any()` upstream, so these rows take the bare-Any() path, not
        // TryRenderSizeComparison's GreaterThan arm:
        //   const-gt0     -> Any()
        //   and           -> (Any() AndAlso (Count() < 3))
        //   nested-count  -> Posts.Any(o => Comments.Any())
        // `>= 1`, `!= 0`, `== 0`, `< 0` and `<= 0` arrive unrewritten. The GreaterThan arm at n = 0 is reachable
        // only from a hand-built tree.
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

        // Any/All regression rows: must be unaffected.
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

        // Server: must go native (NativeOnly) and agree exactly.
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
        // Differential gate: the same Expression is sent to the server and compiled for the oracle. The missing/null
        // Posts rows are the ones a bare $size would abort on.
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
        // b.Home.Notes.Count: TryResolveOwnedCollectionPath walks the owned reference hop.
        var collection = Seed(nameof(Count_projection_through_an_owned_reference_hop_goes_native),
            RowWithNotes("none", 0), RowWithNotes("one", 1), RowWithNotes("three", 3));

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        var rows = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Home.Notes.Count })
            .ToList().OrderBy(r => r.Title).ToList();

        Assert.Equal(
            [("none", 0), ("one", 1), ("three", 3)],
            rows.Select(r => (r.Title, r.N)).ToArray());

        // Pin the resolved path "Home.Notes"; the values alone can't distinguish it from a coincidentally
        // same-shaped wrong path.
        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("Home.Notes", mql);
    }

    [Fact]
    public void Count_projection_alongside_sibling_leaves_goes_native()
    {
        // len2's Home.Notes has 3 elements (not its Posts.Count of 2), so a Notes leaf reading the wrong slot or
        // always 0 is caught; SeedLengths alone leaves every Notes empty.
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

    // Bare and wrapped count projections both go native from the same model but under different aliases: the
    // wrapped form under its member name (`N`), the bare form under the reserved `_v`. That alias difference keeps
    // emit side and shaper agreeing for each; values alone can't see an alias collapse, so the MQL is pinned too.
    // See the "POSITION, precisely" comment in MongoProjectionBindingExpressionVisitor.VisitMethodCall.
    //
    // States: present (c_len0..c_len3), empty, absent, BSON null. The wrapped form's DriverLinq leg is pinned by
    // Wrapped_count_projection_under_DriverLinq_works_for_present_and_ragged_arrays_alike.
    [Fact]
    public void Bare_and_wrapped_count_projections_both_go_native_from_the_same_model()
    {
        var collection = SeedPrefixedLengths(
            nameof(Bare_and_wrapped_count_projections_both_go_native_from_the_same_model));
        var prefix = "c_";

        // Collect-then-assert, so a wrapped-half regression can't hide the bare half. See LegOutcome.
        const string expected = "[0,1,2,3,0,0]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode, BlogModel);

            legs.Add(($"{mode} wrapped", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => new { N = b.Posts.Count }).ToList().Select(r => r.N).ToList())));

            legs.Add(($"{mode} bare", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList())));
        }

        // The bare form's DriverLinq leg and both late-decline legs, where a bare alias miss is silent and the
        // $ifNull rewrite over the driver's un-stripped push-down is exercised.
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            legs.Add(("DriverLinq bare", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList())));
        }

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            legs.Add(($"{mode} bare late-decline", LegOutcome(
                () => db.Entities.AsNoTracking().Where(b => b.Title.StartsWith(prefix)).OrderBy(b => b.Title)
                    .Select(b => b.Posts.Count).ToList())));
        }

        // Alias pins are part of the collected set, so they still run when a leg above regresses. Both aliases
        // read back correctly, so an alias collapse is visible only here.
        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var wrappedSpy))
        {
            legs.Add(("NativeOnly wrapped alias", LegOutcome(() =>
            {
                _ = db.Entities.AsNoTracking().Select(b => new { N = b.Posts.Count }).ToList();
                return ProjectAliasSummary(wrappedSpy);
            })));
        }

        using (var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var bareSpy))
        {
            legs.Add(("NativeOnly bare alias", LegOutcome(() =>
            {
                _ = db.Entities.AsNoTracking().Select(b => b.Posts.Count).ToList();
                return ProjectAliasSummary(bareSpy);
            })));
        }

        Assert.Equal(
            [
                ("NativeOnly wrapped", expected),
                ("NativeOnly bare", expected),
                ("Native wrapped", expected),
                ("Native bare", expected),
                ("DriverLinq bare", expected),
                ("Native bare late-decline", expected),
                ("DriverLinq bare late-decline", expected),
                ("NativeOnly wrapped alias", "N=True _v=False"),
                ("NativeOnly bare alias", "N=False _v=True")
            ],
            legs);
    }

    [Fact]
    public void Filtered_count_projection_now_goes_native_EF359()
    {
        // A filtered count projection emits { $project: { N: { $size: { $filter: ... } } } } and is correct in
        // every mode. Full breadth lives in NativeOwnedCollectionFilteredCountTests.
        //
        // LenRow ranks are 0..n-1, so "Rank > 0" counts length - 1: len0 -> 0, len1 -> 0, len2 -> 1, len3 -> 2;
        // missing/null -> 0.
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
