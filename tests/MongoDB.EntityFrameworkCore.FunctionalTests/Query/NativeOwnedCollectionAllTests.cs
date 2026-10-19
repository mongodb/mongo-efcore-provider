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
/// <c>All</c> over an owned (embedded) collection translates natively to a negated <c>$elemMatch</c> over the
/// exact complement of the element predicate. Admitted shapes assert a NativeOnly routing proof; excluded
/// shapes a clean decline.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionAllTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

    // MQL is captured via SpyLoggerProvider (TestMqlLoggerFactory/AssertMql exist only in SpecificationTests),
    // as in NativeSelectManyTests.
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

    // Assert.Contains on the pipeline fragment avoids coupling to the "Executed MQL query" message wrapper.
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
        // Nullable so a missing or explicitly-null stored field materializes as null rather than throwing; otherwise
        // the missing-field state can't be exercised.
        public int? Rank { get; set; }
        public string? Heading { get; set; }
        public int? Other { get; set; }

        // Collides with Blog.Title on purpose: the element-scoped translator resolves members by name, so this
        // exercises the correlated-element-predicate guard.
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

    // ------------------------------------------------------------------
    // Shared row builders; each returns a fresh document (new ObjectId) so the seeds can't desynchronize.
    // ------------------------------------------------------------------

    // Every element satisfies Rank > 5.
    private static BsonDocument AllPassRow()
        => Row("allpass", new BsonArray
        {
            PostDoc(rank: 9, heading: "a"),
            PostDoc(rank: 7, heading: "b"),
        });

    // One element fails Rank > 5 — the discriminating row for All.
    private static BsonDocument OneFailsRow()
        => Row("onefails", new BsonArray
        {
            PostDoc(rank: 9, heading: "a"),
            PostDoc(rank: 1, heading: "b"),
        });

    // Rank field absent. The critical row: naive inversion ($gt -> $lte) reports All == true, since neither
    // matches a missing field, but LINQ (null > 5 == false) says All == false.
    private static BsonDocument MissingFieldRow()
        => Row("missingfield", new BsonArray { PostWithoutRank(heading: "a") });

    // Rank explicitly BSON null; same reasoning as MissingFieldRow.
    private static BsonDocument NullFieldRow()
        => Row("nullfield", new BsonArray { PostDoc(rank: null, heading: "a") });

    private static BsonDocument EmptyPostsRow() => Row("empty", new BsonArray());
    private static BsonDocument MissingPostsRow() => Row("missing", posts: null);
    private static BsonDocument NullPostsRow() => Row("null", BsonNull.Value);

    private static BsonDocument PostDoc(int? rank, string? heading, int? other = 0, string title = "p")
        => new()
        {
            { "Rank", rank.HasValue ? rank.Value : BsonNull.Value },
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", other.HasValue ? other.Value : BsonNull.Value },
            { "Title", title },
            { "Comments", new BsonArray() }
        };

    private static BsonDocument PostWithoutRank(string? heading, string title = "p")
        => new()
        {
            { "Heading", heading is null ? BsonNull.Value : heading },
            { "Other", 0 }, { "Title", title }, { "Comments", new BsonArray() }
        };

    // A Post with its own Comments array, for the nested quantifier tests; only Comments varies.
    private static BsonDocument PostWithComments(string heading, BsonArray comments)
        => new()
        {
            { "Rank", 0 }, { "Heading", heading }, { "Other", 0 }, { "Title", "p" },
            { "Comments", comments }
        };

    private static BsonDocument CommentDoc(int age) => new() { { "Age", age } };

    private static BsonDocument NoteDoc(int length) => new() { { "Length", length } };

    // Home/Tags are always present-but-empty: they're required on Blog, and a missing one fails materialization
    // for unrelated reasons.
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

    // Row variant carrying explicit Home.Notes content, for the owned-single-ref-hop nesting test.
    private static BsonDocument RowWithNotes(string title, BsonArray notes)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", notes } } },
            { "Posts", new BsonArray() },
            { "Tags", new BsonArray() }
        };

    // Carries real Tags; every other builder leaves Tags empty, which would make Tags.All(t => t != "x")
    // vacuously true and non-discriminating.
    private static BsonDocument RowWithTags(string title, BsonArray tags)
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", title },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Posts", new BsonArray() },
            { "Tags", tags }
        };

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // The full matrix: every element state and every array state that changes $elemMatch semantics.
    private IMongoCollection<Blog> SeedMatrix(string name)
        => Seed(name, AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(),
                      EmptyPostsRow(), MissingPostsRow(), NullPostsRow());

    // Rows whose Posts is a real, non-null array. The driver's All translation ($allElementsTrue over $map) only
    // aborts on an array-level missing/null Posts, so element-level missing/null Rank rows belong here and get an
    // independent driver cross-check where a wrong complement would show up.
    private IMongoCollection<Blog> SeedWellFormed(string name)
        => Seed(name, AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(), EmptyPostsRow());

    // Runs `query` in one mode, reduced to the sorted Title list; mode orchestration lives in NativeModeAssert.
    private List<string> RunTitles(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query, MongoQueryMode mode)
    {
        using var db = CreateContext(collection, mode, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    private List<string> AssertNativeAndParity(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => NativeModeAssert.NativeAndParity(mode => RunTitles(collection, query, mode));

    // Asserts a clean decline under NativeOnly and that the fallback is correct (Native == DriverLinq); returns
    // the rows for a hand-verified expectation.
    private List<string> AssertDeclinesCleanly(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => NativeModeAssert.DeclinesCleanly(mode => RunTitles(collection, query, mode));

    // NativeOnly-only (no driver oracle), for seeds whose missing/null Posts rows abort the driver's
    // $allElementsTrue translation.
    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => RunTitles(collection, query, MongoQueryMode.NativeOnly);

    [Fact]
    public void Owned_collection_All_goes_native()
    {
        var collection = SeedMatrix(nameof(Owned_collection_All_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));

        // allpass: both elements pass. empty/missing/null: All over an empty sequence is true.
        // missingfield/nullfield: null > 5 is false, so All is false (the rows a naive inversion gets wrong).
        // onefails: one element fails.
        Assert.Equal(new[] { "allpass", "empty", "missing", "null" }, titles);
    }

    [Fact]
    public void Owned_collection_All_matches_driver_linq_on_well_formed_rows()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_matches_driver_linq_on_well_formed_rows));
        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    [Fact]
    public void Owned_collection_All_with_captured_parameter_element_predicate_goes_native()
    {
        // A captured value becomes an EF query parameter, which on EF8/EF9 is a ParameterExpression; the
        // correlated-element-predicate guard (ReferencesEnclosingScope) must exempt it via
        // NativeQueryParameter.TryGetQueryParameterName or this declines on EF8/EF9 only. Must be verified on EF8.
        var threshold = 5;
        var collection = SeedWellFormed(
            nameof(Owned_collection_All_with_captured_parameter_element_predicate_goes_native));

        var titles = AssertNativeAndParity(collection, q => q.Where(b => b.Posts.All(p => p.Rank > threshold)));
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    [Fact]
    public void Negated_owned_collection_All_goes_native()
    {
        var collection = SeedMatrix(nameof(Negated_owned_collection_All_goes_native));
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => !b.Posts.All(p => p.Rank > 5)));
        Assert.Equal(new[] { "missingfield", "nullfield", "onefails" }, titles);
    }

    [Fact]
    public void Owned_collection_All_over_an_empty_or_absent_array_is_true()
    {
        // Separate from the matrix test because $not/$elemMatch is easily misread as "the array must be non-empty".
        var collection = Seed(
            nameof(Owned_collection_All_over_an_empty_or_absent_array_is_true),
            EmptyPostsRow(), MissingPostsRow(), NullPostsRow());

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));
        Assert.Equal(new[] { "empty", "missing", "null" }, titles);
    }

    [Fact]
    public void Owned_collection_All_multi_condition_requires_every_element_to_satisfy_all_conditions()
    {
        var collection = Seed(
            nameof(Owned_collection_All_multi_condition_requires_every_element_to_satisfy_all_conditions),
            // Each element satisfies one condition but not both, so All is false; ANDing the complements instead of
            // ORing them (a De Morgan slip) would wrongly return this row.
            Row("split", new BsonArray { PostDoc(rank: 9, heading: "no"), PostDoc(rank: 1, heading: "yes") }),
            Row("both", new BsonArray { PostDoc(rank: 9, heading: "yes") }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "yes")));
        Assert.Equal(new[] { "both" }, titles);
    }

    [Fact]
    public void Owned_collection_All_emits_a_negated_elem_match()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_emits_a_negated_elem_match));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking().Where(b => b.Posts.All(p => p.Rank > 5)).ToList();

        // Pins both levels: the enclosing $not/$elemMatch and the inner $not over the operator document.
        spyLogger.AssertExecutedMqlContains("{ \"$match\" : { \"Posts\" : { \"$not\" : { \"$elemMatch\" : { \"Rank\" : { \"$not\" : { \"$gt\" : 5 } } } } } } }");
    }

    [Fact]
    public void Owned_collection_All_with_a_conjunction_emits_a_de_morgan_or()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_with_a_conjunction_emits_a_de_morgan_or));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking()
            .Where(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "yes")).ToList();

        spyLogger.AssertExecutedMqlContains("{ \"$match\" : { \"Posts\" : { \"$not\" : { \"$elemMatch\" : { \"$or\" : [{ \"Rank\" : { \"$not\" : { \"$gt\" : 5 } } }, { \"Heading\" : { \"$ne\" : \"yes\" } }] } } } } }");
    }

    [Fact]
    public void All_within_Any_goes_native()
    {
        var collection = Seed(nameof(All_within_Any_goes_native),
            Row("hasAllPassing", new BsonArray { PostWithComments("a", new BsonArray { CommentDoc(9), CommentDoc(7) }) }),
            Row("noneAllPassing", new BsonArray { PostWithComments("a", new BsonArray { CommentDoc(9), CommentDoc(1) }) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.All(c => c.Age > 5))));
        Assert.Equal(new[] { "hasAllPassing" }, titles);
    }

    [Fact]
    public void Any_within_All_goes_native()
    {
        var collection = Seed(nameof(Any_within_All_goes_native),
            Row("everyPostHasOne", new BsonArray { PostWithComments("a", new BsonArray { CommentDoc(9) }) }),
            Row("onePostHasNone", new BsonArray
            {
                PostWithComments("a", new BsonArray { CommentDoc(9) }),
                PostWithComments("b", new BsonArray { CommentDoc(1) })
            }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Comments.Any(c => c.Age > 5))));
        Assert.Equal(new[] { "everyPostHasOne" }, titles);
    }

    [Fact]
    public void All_within_All_goes_native()
    {
        var collection = Seed(nameof(All_within_All_goes_native),
            Row("allGood", new BsonArray { PostWithComments("a", new BsonArray { CommentDoc(9), CommentDoc(7) }) }),
            Row("oneBad", new BsonArray { PostWithComments("a", new BsonArray { CommentDoc(9), CommentDoc(1) }) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Comments.All(c => c.Age > 5))));
        Assert.Equal(new[] { "allGood" }, titles);
    }

    [Fact]
    public void All_over_a_collection_reached_through_an_owned_reference_goes_native()
    {
        // The array path is built scope-relatively through an owned single-ref hop: "Home.Notes", not "Notes".
        var collection = Seed(nameof(All_over_a_collection_reached_through_an_owned_reference_goes_native),
            RowWithNotes("allLong", new BsonArray { NoteDoc(9), NoteDoc(7) }),
            RowWithNotes("oneShort", new BsonArray { NoteDoc(9), NoteDoc(1) }));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Home.Notes.All(n => n.Length > 5)));
        Assert.Equal(new[] { "allLong" }, titles);
    }

    [Fact]
    public void Owned_collection_All_is_correct_for_a_tracking_query()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_is_correct_for_a_tracking_query));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.Where(b => b.Posts.All(p => p.Rank > 5))
            .ToList().Select(b => b.Title).OrderBy(t => t).ToList();

        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    [Fact]
    public void All_with_a_field_to_field_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // The negator has no exact complement for a field-to-field comparison; the decline is only safe if the
        // fallback returns correct rows.
        var collection = SeedWellFormed(
            nameof(All_with_a_field_to_field_element_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => p.Rank > p.Other)));
        Assert.Equal(new[] { "allpass", "empty", "onefails" }, titles);
    }

    [Fact]
    public void All_with_an_arithmetic_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // p.Rank + 1 renders as an $expr node, which the negator has no complement for.
        var collection = SeedWellFormed(
            nameof(All_with_an_arithmetic_element_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => p.Rank + 1 > 5)));
        // allpass: 9+1>5 and 7+1>5, both true -> All true. onefails: 1+1>5 is false -> All false.
        // empty: All over an empty sequence is true.
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    [Fact]
    public void All_with_a_correlated_element_predicate_now_goes_native_since_EF421()
    {
        // Natively representable via a two-scope translator + $allElementsTrue. `b.Title == "match"` doesn't depend
        // on p, so All holds iff the owner's Title is "match" and the blog has a post. Post.Title collides with
        // Blog.Title, so a mis-scoped (element-rooted) resolution would select different rows.
        var collection = Seed(nameof(All_with_a_correlated_element_predicate_now_goes_native_since_EF421),
            Row("match", new BsonArray { PostDoc(rank: 9, heading: "a", title: "other") }),
            Row("other", new BsonArray { PostDoc(rank: 9, heading: "a", title: "match") }));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => b.Title == "match")));
        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Primitive_collection_All_is_rewritten_upstream_and_now_goes_native_via_EF382()
    {
        // EF's AllAnyToContainsRewritingExpressionVisitor rewrites All(x => x != c) to !Contains(c) before native
        // translation, so this goes through the Contains mirror arm (MongoArrayContainsExpression, negated via Not)
        // and renders { Tags: { $ne: "x" } }.
        // Dedicated seed, since other rows have empty Tags (vacuously true):
        //   "hasX"      Tags = ["x", "y"] -> "x" fails t != "x" -> All false.
        //   "noX"       Tags = ["y", "z"] -> every element satisfies t != "x" -> All true.
        //   "emptyTags" Tags = []          -> vacuously true.
        var collection = Seed(
            nameof(Primitive_collection_All_is_rewritten_upstream_and_now_goes_native_via_EF382),
            RowWithTags("hasX", new BsonArray { "x", "y" }),
            RowWithTags("noX", new BsonArray { "y", "z" }),
            RowWithTags("emptyTags", new BsonArray()));

        List<string> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            native = db.Entities.AsNoTracking().Where(b => b.Tags.All(t => t != "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = db.Entities.AsNoTracking().Where(b => b.Tags.All(t => t != "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, native);
        Assert.Equal(new[] { "emptyTags", "noX" }, native);

        // NativeOnly succeeds, so this went native.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            var nativeOnly = db.Entities.AsNoTracking().Where(b => b.Tags.All(t => t != "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
            Assert.Equal(new[] { "emptyTags", "noX" }, nativeOnly);
        }
    }

    [Fact]
    public void Primitive_collection_All_with_equality_reaches_the_matcher_and_declines_at_path_resolution()
    {
        // Tags.All(t => t == "x") isn't rewritten upstream and arrives as Enumerable.All with a bare lambda and no
        // AsQueryable(). The matcher matches it, then TryResolveOwnedCollectionPath declines because Tags is a
        // primitive collection, not a navigation. Pins a clean decline rather than a crash.
        var collection = SeedWellFormed(
            nameof(Primitive_collection_All_with_equality_reaches_the_matcher_and_declines_at_path_resolution));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Tags.All(t => t == "x")));
    }

    // ------------------------------------------------------------------
    // Differential matrix — the primary correctness bar for the negator
    // ------------------------------------------------------------------
    //
    // A mis-negated element predicate returns wrong rows rather than declining, and the driver oracle aborts on
    // missing/null arrays. So the oracle is in-memory LINQ: the same expression is sent to the server and
    // compiled client-side.

    public static TheoryData<string, Expression<Func<Blog, bool>>> AllMatrixCases() => new()
    {
        { "eq",              b => b.Posts.All(p => p.Rank == 9) },
        { "ne",              b => b.Posts.All(p => p.Rank != 9) },
        { "lt",              b => b.Posts.All(p => p.Rank < 5) },
        { "lte",             b => b.Posts.All(p => p.Rank <= 5) },
        { "gt",              b => b.Posts.All(p => p.Rank > 5) },
        { "gte",             b => b.Posts.All(p => p.Rank >= 5) },
        { "and",             b => b.Posts.All(p => p.Rank > 5 && p.Heading == "a") },
        { "or",              b => b.Posts.All(p => p.Rank > 5 || p.Heading == "a") },
        { "not",             b => b.Posts.All(p => !(p.Rank > 5)) },
        { "eq-null",         b => b.Posts.All(p => p.Rank == null) },
        { "ne-null",         b => b.Posts.All(p => p.Rank != null) },
        // Null-guarded because `p.Heading!` has no runtime effect, so the in-memory oracle would throw on the
        // "headingNull" row. The `!= null` conjunct also exercises De Morgan over a mixed null-guard/StartsWith pair.
        { "startswith",      b => b.Posts.All(p => p.Heading != null && p.Heading.StartsWith("a")) },
        { "nested-any",      b => b.Posts.All(p => p.Comments.Any(c => c.Age > 5)) },
        { "nested-all",      b => b.Posts.All(p => p.Comments.All(c => c.Age > 5)) },
        { "negated-all",     b => !b.Posts.All(p => p.Rank > 5) },
        // Any: must be unaffected.
        { "any-gt",          b => b.Posts.Any(p => p.Rank > 5) },
        { "any-bare",        b => b.Posts.Any() },
        { "negated-any",     b => !b.Posts.Any(p => p.Rank > 5) },
    };

    [Theory]
    [MemberData(nameof(AllMatrixCases))]
    public void Quantifier_result_equals_the_in_memory_oracle_for_every_element_and_array_state(
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

        // Server: must go native (NativeOnly) and agree exactly. The projection is client-side because a bare-scalar
        // Select wouldn't itself be native.
        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // TryResolveMember peels Nullable<T>.Value, so `p.Rank!.Value` resolves against the owned element type and
    // goes native as { Posts: { $not: { $elemMatch: { Rank: { $nin: [7, 9] } } } } }. A missing or null Rank
    // matches $nin, excluding the row, which matches LINQ's All.
    //
    // Uses SeedWellFormed because DriverLinq's $allElementsTrue translation throws on a missing/null Posts array;
    // see the next test for those states.
    //
    // Expected: allpass (9,7 both in {7,9}) true; onefails excluded; missingfield/nullfield excluded; empty true.
    [Fact]
    public void All_with_a_nullable_leaf_Contains_predicate_goes_native_since_EF400()
    {
        var collection = SeedWellFormed(
            nameof(All_with_a_nullable_leaf_Contains_predicate_goes_native_since_EF400));

        var titles = AssertNativeAndParity(
            collection, q => q.Where(b => b.Posts.All(p => new[] { 7, 9 }.Contains(p.Rank!.Value))));
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    // Covers the missing/null Posts array states the test above excludes. Driver LINQ throws on these
    // ($allElementsTrue on a null argument); natively, $not/$elemMatch matches them vacuously and they are
    // correctly included, as LINQ's All over an empty sequence.
    //
    // No driver oracle is possible, so this uses AssertNativeOnlyMatches with a hand-verified expectation:
    // allpass included; onefails excluded; missingfield/nullfield ($nin matches missing/null Rank) excluded;
    // empty/missing/null included. Same as Owned_collection_All_goes_native, which is the cross-check.
    [Fact]
    public void All_with_a_nullable_leaf_Contains_predicate_is_correct_for_missing_and_null_ARRAYS_too()
    {
        var collection = SeedMatrix(
            nameof(All_with_a_nullable_leaf_Contains_predicate_is_correct_for_missing_and_null_ARRAYS_too));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => new[] { 7, 9 }.Contains(p.Rank!.Value))));

        Assert.Equal(new[] { "allpass", "empty", "missing", "null" }, titles);
    }

    // Every element state crossed with every array state that changes quantifier semantics.
    private static BsonDocument[] DifferentialRows() =>
    [
        AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(),
        EmptyPostsRow(), MissingPostsRow(), NullPostsRow(),
        Row("belowAndAbove", new BsonArray { PostDoc(rank: 1, heading: "a"), PostDoc(rank: 9, heading: "b") }),
        Row("exactBoundary", new BsonArray { PostDoc(rank: 5, heading: "a") }),
        Row("mixedNullAndValue", new BsonArray { PostDoc(rank: null, heading: "a"), PostDoc(rank: 9, heading: "a") }),
        Row("headingNull", new BsonArray { PostDoc(rank: 9, heading: null) }),
        Row("withComments", new BsonArray { PostWithComments("a", new BsonArray { CommentDoc(9), CommentDoc(1) }) }),
        Row("emptyComments", new BsonArray { PostWithComments("a", new BsonArray()) }),
        // For `Rank > 5 || Heading == "a"` the correct complement is `$not:{$gt:5} AND Heading != "a"`, which matches
        // the first element (no Rank, Heading "z"), so All is false. A buggy $lte:5 inversion doesn't match a missing
        // Rank, so All would wrongly be true. The second element satisfies the predicate via the Heading disjunct.
        Row("orRelationalGap", new BsonArray { PostWithoutRank(heading: "z"), PostDoc(rank: 9, heading: "a") }),
    ];

    // ------------------------------------------------------------------
    // Dotted-path resolution through a nested owned single-reference hop, from inside a quantifier's
    // element-scoped predicate. A separate fixture (NestedRef*) so the shared Post type stays unchanged.
    // ------------------------------------------------------------------

    public class NestedRefBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<NestedRefPost> Posts { get; set; } = [];
    }

    public class NestedRefPost
    {
        public int? Rank { get; set; }
        public NestedRefAuthor? Author { get; set; }
    }

    public class NestedRefAuthor
    {
        public string? City { get; set; }
    }

    private static readonly Action<ModelBuilder> NestedRefBlogModel = mb =>
        mb.Entity<NestedRefBlog>().OwnsMany(b => b.Posts, p => p.OwnsOne(x => x.Author));

    private SingleEntityDbContext<NestedRefBlog> CreateNestedRefContext(
        IMongoCollection<NestedRefBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: NestedRefBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Quantifier_element_predicate_through_nested_owned_reference_hop_goes_native()
    {
        // p.Author.City from inside Any's predicate is a two-hop dotted path rooted at the element scope (Post), not
        // the document root. "Springfield" vs "Shelbyville" makes it discriminating: an empty $match returns nothing,
        // a mis-scoped one both.
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Quantifier_element_predicate_through_nested_owned_reference_hop_goes_native)));
        var collection = database.MongoDatabase.GetCollection<NestedRefBlog>(raw.CollectionNamespace.CollectionName);

        using (var seedDb = CreateNestedRefContext(collection, MongoQueryMode.DriverLinq))
        {
            seedDb.Entities.Add(new NestedRefBlog
            {
                Title = "match",
                Posts = [new NestedRefPost { Rank = 1, Author = new NestedRefAuthor { City = "Springfield" } }]
            });
            seedDb.Entities.Add(new NestedRefBlog
            {
                Title = "nomatch",
                Posts = [new NestedRefPost { Rank = 2, Author = new NestedRefAuthor { City = "Shelbyville" } }]
            });
            seedDb.SaveChanges();
        }

        using var db = CreateNestedRefContext(collection, MongoQueryMode.NativeOnly);

        // NativeOnly succeeds and the returned title proves the correct row matched.
        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => p.Author!.City == "Springfield"))
            .ToList().Select(b => b.Title).ToList();

        Assert.Equal(new[] { "match" }, titles);
    }

    public class NestedRefKeyedBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<NestedRefKeyedPost> Posts { get; set; } = [];
    }

    public class NestedRefKeyedPost
    {
        public int? Rank { get; set; }
        public NestedRefKeyedAuthor? Author { get; set; }
    }

    // A composite-key owned single-reference type, nested: the serializer nests a local "_id" at the type's own
    // position (e.g. "Author._id.City"), mirroring the root's composite key.
    public class NestedRefKeyedAuthor
    {
        public string City { get; set; } = "";
        public string Country { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> NestedRefKeyedBlogModel = mb =>
        mb.Entity<NestedRefKeyedBlog>().OwnsMany(b => b.Posts, p =>
            p.OwnsOne(x => x.Author, a => a.HasKey(x => new { x.City, x.Country })));

    private SingleEntityDbContext<NestedRefKeyedBlog> CreateNestedRefKeyedContext(
        IMongoCollection<NestedRefKeyedBlog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: NestedRefKeyedBlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Quantifier_element_predicate_through_nested_owned_reference_composite_key_leaf_goes_native()
    {
        // The leaf (City) is both a composite-key component of its own type (Author) and reached through a non-root
        // scope, so the path must compose the hop prefix with the leaf's "_id." rewrite: "Author._id.City", not
        // "Author.City". Dropping either piece silently addresses the wrong field and matches nothing.
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Quantifier_element_predicate_through_nested_owned_reference_composite_key_leaf_goes_native)));
        var collection = database.MongoDatabase.GetCollection<NestedRefKeyedBlog>(raw.CollectionNamespace.CollectionName);

        using (var seedDb = CreateNestedRefKeyedContext(collection, MongoQueryMode.DriverLinq))
        {
            seedDb.Entities.Add(new NestedRefKeyedBlog
            {
                Title = "match",
                Posts = [new NestedRefKeyedPost
                {
                    Rank = 1, Author = new NestedRefKeyedAuthor { City = "Springfield", Country = "USA" }
                }]
            });
            seedDb.Entities.Add(new NestedRefKeyedBlog
            {
                Title = "nomatch",
                Posts = [new NestedRefKeyedPost
                {
                    Rank = 2, Author = new NestedRefKeyedAuthor { City = "Shelbyville", Country = "USA" }
                }]
            });
            seedDb.SaveChanges();
        }

        using var db = CreateNestedRefKeyedContext(collection, MongoQueryMode.NativeOnly);

        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => p.Author!.City == "Springfield"))
            .ToList().Select(b => b.Title).ToList();

        Assert.Equal(new[] { "match" }, titles);
    }
}
