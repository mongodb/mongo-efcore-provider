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
/// EF-322: an All quantifier over an OWNED (embedded) collection navigation translates natively to a NEGATED
/// $elemMatch over the exact complement of the element predicate. Each admitted shape asserts a NativeOnly
/// routing proof; each excluded shape asserts a clean decline.
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

    // ------------------------------------------------------------------
    // Shared row builders — each returns a FRESH document (new ObjectId) per call, built exactly once so the
    // full-matrix and well-formed seeds cannot desynchronize.
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

    // An element whose Rank field is ABSENT. THE critical row: naive operator inversion ($gt → $lte) reports
    // All == true here, because neither $gt nor $lte matches a missing field, where LINQ (null > 5 == false)
    // says All == false. Any regression to inversion must make a test on this row fail.
    private static BsonDocument MissingFieldRow()
        => Row("missingfield", new BsonArray { PostWithoutRank(heading: "a") });

    // An element whose Rank is explicitly BSON null — same reasoning as MissingFieldRow.
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

    // A Post carrying its own Comments array, for the All-within-Any / Any-within-All / All-within-All nesting
    // tests below. Rank/Other/Title are unused by those tests' predicates, so fixed, present, non-null values
    // are fine — only Comments varies per call.
    private static BsonDocument PostWithComments(string heading, BsonArray comments)
        => new()
        {
            { "Rank", 0 }, { "Heading", heading }, { "Other", 0 }, { "Title", "p" },
            { "Comments", comments }
        };

    private static BsonDocument CommentDoc(int age) => new() { { "Age", age } };

    private static BsonDocument NoteDoc(int length) => new() { { "Length", length } };

    // Home/Tags are always seeded present-but-empty: both are separate required properties on Blog, unrelated
    // to what these rows test, and a document missing them fails materialization with an unrelated error the
    // moment a predicate returns the row as a full Blog.
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

    // Row variant carrying explicit Tags content, for the primitive-collection Contains-fallback test below —
    // every OTHER row builder in this file leaves Tags as an empty array (irrelevant to what those rows test),
    // which would make Tags.All(t => t != "x") vacuously true for all of them and unable to discriminate a
    // wrong implementation from a correct one.
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

    // Rows whose Posts is a real, non-null ARRAY — element-level missing/null fields are fine here.
    //
    // Spike refinement (measured, and wider than the Any slice's equivalent seed): the driver's own All
    // translation ($expr: {$allElementsTrue: {$map: …}}) aborts the aggregate ONLY on an array-level
    // missing/null Posts — "$allElementsTrue's argument must be an array, but is null". With every array
    // present but ELEMENTS carrying a missing or explicit-null Rank, DriverLinq runs and agrees with both the
    // in-memory oracle and the native MQL. So MissingFieldRow/NullFieldRow BELONG in the parity seed: they put
    // an independent driver cross-check on exactly the element states where a wrong complement shows up.
    // Only the array-level missing/null rows are confined to the NativeOnly-plus-hand-verified leg.
    private IMongoCollection<Blog> SeedWellFormed(string name)
        => Seed(name, AllPassRow(), OneFailsRow(), MissingFieldRow(), NullFieldRow(), EmptyPostsRow());

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
    // full-matrix seed, whose missing/null Posts rows abort the driver's own $allElementsTrue translation.
    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    [Fact]
    public void Owned_collection_All_goes_native()
    {
        var collection = SeedMatrix(nameof(Owned_collection_All_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Rank > 5)));

        // allpass: both elements pass. empty/missing/null: All over an empty sequence is true.
        // missingfield/nullfield: null > 5 is false, so All is FALSE — the rows a naive inversion gets wrong.
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
        // I-2 (final whole-branch review): mirrors NativeOwnedCollectionPredicateTests's
        // Owned_collection_Any_with_captured_parameter_element_predicate_goes_native, whose comment explains
        // why this axis needs its own test rather than relying on the shared guard's Any coverage: a captured
        // value in the element predicate becomes an EF query parameter — and on EF8/EF9 an EF query parameter
        // IS a ParameterExpression (a "__"-prefixed name), unlike EF10's typed QueryParameterExpression. The
        // correlated-element-predicate guard (ReferencesEnclosingScope) that All shares with Any therefore has
        // to exempt query parameters explicitly via NativeQueryParameter.TryGetQueryParameterName, or a
        // captured value in an All element predicate would decline on EF8/EF9 ONLY — invisible on EF10, since
        // every other test in this file uses inline constants. MUST be verified on EF8, not just EF10 — that
        // is the whole point of this test.
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
        // Called out separately from the matrix test because it is the semantic most likely to be "fixed"
        // into a regression by someone who reads $not/$elemMatch as "the array must be non-empty".
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
            // Each element satisfies ONE condition but not both: All must be FALSE. A De Morgan slip that
            // ANDed the complements instead of ORing them would wrongly return this row.
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

        // Pins BOTH levels: the enclosing $not/$elemMatch AND the inner $not over the operator document.
        // Captured from an actual run (see the report) — not hand-written.
        AssertMql(spyLogger,
            "{ \"$match\" : { \"Posts\" : { \"$not\" : { \"$elemMatch\" : { \"Rank\" : { \"$not\" : { \"$gt\" : 5 } } } } } } }");
    }

    [Fact]
    public void Owned_collection_All_with_a_conjunction_emits_a_de_morgan_or()
    {
        var collection = SeedWellFormed(nameof(Owned_collection_All_with_a_conjunction_emits_a_de_morgan_or));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, BlogModel, out var spyLogger);

        _ = db.Entities.AsNoTracking()
            .Where(b => b.Posts.All(p => p.Rank > 5 && p.Heading == "yes")).ToList();

        // Captured from an actual run (see the report) — not hand-written.
        AssertMql(spyLogger,
            "{ \"$match\" : { \"Posts\" : { \"$not\" : { \"$elemMatch\" : { \"$or\" : [{ \"Rank\" : { \"$not\" : { \"$gt\" : 5 } } }, { \"Heading\" : { \"$ne\" : \"yes\" } }] } } } } }");
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
        // Proves the array path is built scope-relatively and composes through an owned single-ref hop:
        // the emitted path must be "Home.Notes", not "Notes".
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
        // The negator has no exact complement for a field-to-field comparison. Proven to decline under
        // NativeOnly AND to produce correct rows via the fallback — a decline is only safe if the path it
        // falls back to actually works.
        var collection = SeedWellFormed(
            nameof(All_with_a_field_to_field_element_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => p.Rank > p.Other)));
        Assert.Equal(new[] { "allpass", "empty", "onefails" }, titles);
    }

    [Fact]
    public void All_with_an_arithmetic_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // Same reasoning as the field-to-field case above: p.Rank + 1 is an arithmetic operand, which the
        // translated comparison renders as a non-query-native ($expr) node the negator has no complement for.
        var collection = SeedWellFormed(
            nameof(All_with_an_arithmetic_element_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => p.Rank + 1 > 5)));
        // allpass: 9+1>5 and 7+1>5, both true -> All true. onefails: 1+1>5 is false -> All false.
        // empty: All over an empty sequence is true.
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    [Fact]
    public void All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // Post.Title collides with Blog.Title, so a mis-scoped owner-rooted condition would select DIFFERENT
        // rows — which is what makes this decline test discriminating rather than vacuous.
        //
        //   owner-scoped (CORRECT): b.Title == "match" is constant per blog, so All(p => ...) over the
        //   blog's non-empty Posts reduces to whether the OWNER's Title is "match". Blog "match" has
        //   Title == "match" and one post -> All true. Blog "other" has Title == "other" -> All false.
        //   Correct fallback result: ["match"].
        //
        //   element-scoped (WRONG — what a mis-resolved `b.Title` retargeted at Post.Title would return):
        //   blog "match"'s single post has Title "other" (Post.Title != "match") -> All false -> excluded.
        //   blog "other"'s single post has Title "match" (Post.Title == "match") -> All true (vacuously, one
        //   satisfying element) -> included. Wrong result: ["other"].
        //
        // Asserting the returned titles, not just that NativeOnly throws, is what actually proves the
        // fallback resolves against the OWNER rather than the element — a mis-scoped fallback would still
        // throw under NativeOnly and pass a title-less version of this test silently.
        var collection = Seed(nameof(All_with_a_correlated_element_predicate_declines_and_falls_back_to_correct_rows),
            Row("match", new BsonArray { PostDoc(rank: 9, heading: "a", title: "other") }),
            Row("other", new BsonArray { PostDoc(rank: 9, heading: "a", title: "match") }));

        var titles = AssertDeclinesCleanly(collection, q => q.Where(b => b.Posts.All(p => b.Title == "match")));
        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Primitive_collection_All_is_rewritten_upstream_and_is_unaffected_by_this_slice()
    {
        // EF Core's own AllAnyToContainsRewritingExpressionVisitor rewrites All(x => x != c) into
        // !Contains(c) BEFORE the native translator sees it, so no All node ever reaches the quantifier
        // matcher for a primitive-element collection. This is the mirror image of the sibling file's
        // Primitive_collection_Any_still_falls_back_via_the_Contains_path: Any(x => x == c) rewrites to
        // Contains(c); All(x => x != c) rewrites to !Contains(c). Both land on the SAME pre-existing
        // Contains/$in path (MongoExpressionTranslator.TryMatchContainsMethod + TryResolveMember), unchanged
        // by this slice.
        //
        // EMPIRICALLY DETERMINED (not assumed — verified against a live run before writing this assertion):
        // for `b.Tags.All(t => t != "x")`, TryMatchContainsMethod matches the rewritten !Tags.Contains("x")
        // with collection = b.Tags (a field) and item = "x" (a constant) — the MIRROR IMAGE of the one shape
        // TryResolveMember's "item must resolve to a bare field" restriction admits (`list.Contains(x.Field)`,
        // where the roles are reversed). TryResolveMember therefore declines on the constant item, so this
        // shape FALLS BACK — NativeOnly throws NativeTranslationNotSupportedException, and Native/DriverLinq
        // both execute the underlying Contains correctly. The point of this test, per the design note above
        // the sibling Any test, is only that the shape is UNCHANGED by this slice (still routes exactly as it
        // did before this slice, whichever way that is, since the All-quantifier matcher never sees it at
        // all), not that it must go native.
        // Dedicated seed (not SeedWellFormed/SeedMatrix): every shared row builder in this file leaves Tags
        // as an empty array, which would make Tags.All(t => t != "x") vacuously true for every row and unable
        // to discriminate a wrong Contains/$nin implementation from a correct one — only the NativeOnly-throws
        // routing proof would carry any weight. RowWithTags gives real, discriminating Tags values instead:
        //   "hasX"      Tags = ["x", "y"] -> "x" fails t != "x" -> All false.
        //   "noX"       Tags = ["y", "z"] -> every element satisfies t != "x" -> All true.
        //   "emptyTags" Tags = []          -> vacuously true (kept for parity with the empty-sequence case).
        // A dedicated seed also means none of the 17 already-verified expectations elsewhere in this file
        // shift (nothing shares this seed).
        var collection = Seed(
            nameof(Primitive_collection_All_is_rewritten_upstream_and_is_unaffected_by_this_slice),
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

        // Routing proof: falls back (see the empirical finding above) — NativeOnly throws rather than
        // silently taking a different path than Native/DriverLinq just exercised.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Entities.AsNoTracking().Where(b => b.Tags.All(t => t != "x")).ToList());
        }
    }

    [Fact]
    public void Primitive_collection_All_with_equality_reaches_the_matcher_and_declines_at_path_resolution()
    {
        // Spike finding: Tags.All(t => t == "x") is NOT rewritten by EF's AllAnyToContainsRewriting (which
        // only handles All(x => x != c) / Any(x => x == c)), and it arrives in a shape the Any slice's notes
        // said could not occur — Enumerable.All (not Queryable), a BARE unquoted lambda, and NO AsQueryable()
        // wrapper. The generalized matcher therefore MATCHES it, and it must decline one step later because
        // TryResolveOwnedCollectionPath requires an embedded collection NAVIGATION and Tags is a primitive
        // collection property. Verified: UnwrapAsQueryable passes an unwrapped source through unchanged, so
        // this is a clean decline, not a crash — this test is what keeps it that way.
        var collection = SeedWellFormed(
            nameof(Primitive_collection_All_with_equality_reaches_the_matcher_and_declines_at_path_resolution));

        AssertDeclinesCleanly(collection, q => q.Where(b => b.Tags.All(t => t == "x")));
    }

    // ------------------------------------------------------------------
    // Differential matrix — the primary correctness bar for the negator
    // ------------------------------------------------------------------
    //
    // A mis-negated element predicate returns WRONG ROWS rather than declining, and the driver-LINQ oracle
    // cannot cover the missing/null states (its own All translation aborts the aggregate on such a document).
    // So the oracle here is IN-MEMORY LINQ over the materialized entities: the SAME expression is sent to the
    // server and, compiled, evaluated client-side. Using one expression for both legs is what makes this a
    // real differential test rather than two hand-written predicates that can silently disagree.

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
        // Not the brief's literal `p.Heading!.StartsWith("a")`: that predicate goes NATIVE (verified —
        // StartsWith over a bare param-rooted member hits TryResolveMember's fast path directly, no owned-path
        // resolution needed), but the `!` null-forgiving operator has NO runtime effect — it only suppresses
        // the nullable-reference compiler warning — so evaluating the identical brief expression against
        // DifferentialRows' "headingNull" row (Heading explicitly null) throws NullReferenceException from the
        // IN-MEMORY ORACLE side (predicate.Compile()), not from translation. That is a defect in the brief's
        // literal predicate against this shared matrix, not a decline, so this case stays IN the theory with a
        // null-guarded rewrite that is still a genuine single-expression differential test of StartsWith
        // negation (and, via the leading `!= null` conjunct, De Morgan over a mixed Regex/Equality pair — a
        // combination neither "and" nor "or" above exercises, since both those cases pair Rank with Heading
        // equality, not a null-guard with StartsWith).
        { "startswith",      b => b.Posts.All(p => p.Heading != null && p.Heading.StartsWith("a")) },
        { "nested-any",      b => b.Posts.All(p => p.Comments.Any(c => c.Age > 5)) },
        { "nested-all",      b => b.Posts.All(p => p.Comments.All(c => c.Age > 5)) },
        { "negated-all",     b => !b.Posts.All(p => p.Rank > 5) },
        // Any regressions: this path must be completely unaffected by the slice.
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

        // Server: the query must go NATIVE (NativeOnly is the only reliable signal) and agree exactly.
        // The projection is client-side on purpose — a bare-scalar Select would itself not be native and
        // would throw under NativeOnly for reasons unrelated to the quantifier.
        List<string> actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                .Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(expected, actual);
    }

    // The brief's "in" theory case — Contains over a nullable element leaf accessed via ".Value" — declines
    // under NativeOnly rather than going native (verified), so per this task's brief it is pulled out of the
    // strict-equality theory above and asserted here instead: NativeOnly must throw cleanly, and the
    // DriverLinq-backed fallback (Native/DriverLinq) must still return results agreeing with a hand-verified
    // expected set. Root cause: the element-scoped translator All's negator builds is rooted on the OWNED
    // ELEMENT type (Post), which is never a document root — TryResolveMember's fast path only accepts a BARE
    // "p.Foo" member access, and "p.Rank!.Value" is a ".Value" property access wrapping that member access, so
    // it falls through to the dotted-owned-path resolver, which declines outright for a non-document-root scope.
    //
    // This uses SeedWellFormed, NOT the full DifferentialRows: DriverLinq's own All-over-collection translation
    // (the fallback this decline lands on) renders as $expr/$allElementsTrue, which — same as the file's
    // existing Any/All notes document — throws a MongoCommandException when the ARRAY itself is missing/null
    // (DifferentialRows' MissingPostsRow/NullPostsRow), so AssertDeclinesCleanly's DriverLinq leg cannot even
    // execute against the full matrix. SeedWellFormed (real, non-null Posts arrays; element-level Rank may
    // still be missing/null) is exactly the seed the file's OTHER decline tests already use for this reason.
    //
    // Expected titles, hand-verified (confirmed empirically via Native/DriverLinq parity before writing this
    // assertion — see the task report): allpass (ranks 9,7, both in {7,9} -> All true); onefails (ranks 9,1 ->
    // 1 not in {7,9} -> All false, excluded); missingfield/nullfield (Rank absent/null -> Contains(null) does
    // not match -> All false, excluded); empty (All over an empty sequence is vacuously true).
    [Fact]
    public void All_with_a_nullable_leaf_Contains_predicate_declines_and_falls_back_to_correct_rows()
    {
        var collection = SeedWellFormed(
            nameof(All_with_a_nullable_leaf_Contains_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(
            collection, q => q.Where(b => b.Posts.All(p => new[] { 7, 9 }.Contains(p.Rank!.Value))));
        Assert.Equal(new[] { "allpass", "empty" }, titles);
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
        // Closes an "or" coverage gap flagged in review: no OTHER row pairs a missing/null Rank with a
        // Heading that is not "a", so a relational-negation bug that reaches through the OrElse arm's
        // (correct) AND-composition — e.g. GreaterThan wrongly INVERTING to LessThanOrEqual instead of
        // $not-wrapping — went undetected by "or" specifically. Reasoning: for `Rank > 5 || Heading == "a"`,
        // the CORRECT complement is `$not:{$gt:5} AND Heading != "a"` — and $not:{$gt:5} matches a
        // missing/null Rank (relational operators don't match missing/null, so $not of one does). The FIRST
        // element below (no Rank field, Heading "z") therefore satisfies the correct complement outright
        // (missing Rank -> $not:{$gt:5} is true; "z" != "a" is true) -> that element matches $elemMatch ->
        // this row's All is correctly FALSE. A buggy relational negation using $lte:5 instead does NOT match
        // a missing Rank (relational operators never match missing/null) -> the buggy complement fails to
        // match EITHER element in this row -> All wrongly comes back TRUE. The SECOND element (Rank 9,
        // Heading "a") is the contrasting element satisfying the predicate via the Heading disjunct, so the
        // row isn't a degenerate single-element case and All's "one bad element is enough" semantics are
        // still exercised.
        Row("orRelationalGap", new BsonArray { PostWithoutRank(heading: "z"), PostDoc(rank: 9, heading: "a") }),
    ];
}
