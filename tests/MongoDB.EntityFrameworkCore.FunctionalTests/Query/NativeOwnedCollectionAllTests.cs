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
    public void All_with_a_correlated_element_predicate_now_goes_native_since_EF421()
    {
        // SUPERSEDED by EF-421: this shape used to decline outright (see git history for the pre-EF-421
        // version of this test, which asserted AssertDeclinesCleanly). It is now natively representable via
        // a two-scope translator + $allElementsTrue — same seed, same expected rows (the correlated
        // predicate `b.Title == "match"` does not depend on p at all, so All reduces to whether the OWNER's
        // Title is "match" AND the blog has at least one post — "match" qualifies on both counts; "other"
        // fails the Title check). Post.Title collides with Blog.Title, so a mis-scoped (element-rooted)
        // resolution would select DIFFERENT rows, keeping this test discriminating rather than vacuous.
        var collection = Seed(nameof(All_with_a_correlated_element_predicate_now_goes_native_since_EF421),
            Row("match", new BsonArray { PostDoc(rank: 9, heading: "a", title: "other") }),
            Row("other", new BsonArray { PostDoc(rank: 9, heading: "a", title: "match") }));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => b.Title == "match")));
        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Primitive_collection_All_is_rewritten_upstream_and_now_goes_native_via_EF382()
    {
        // EF Core's own AllAnyToContainsRewritingExpressionVisitor rewrites All(x => x != c) into
        // !Contains(c) BEFORE the native translator sees it, so no All node ever reaches the quantifier
        // matcher for a primitive-element collection. This is the mirror image of the sibling file's
        // Primitive_collection_Any_now_goes_native_via_the_Contains_path: Any(x => x == c) rewrites to
        // Contains(c); All(x => x != c) rewrites to !Contains(c). Both land on the SAME Contains/$in-or-
        // array-contains path (MongoExpressionTranslator.TryMatchContainsMethod + the EF-382 mirror arm),
        // unchanged by the owned-collection-quantifier slice this file otherwise covers.
        //
        // Before EF-382: for `b.Tags.All(t => t != "x")`, TryMatchContainsMethod matched the rewritten
        // !Tags.Contains("x") with collection = b.Tags (a field) and item = "x" (a constant) — the MIRROR
        // IMAGE of the one shape TryResolveMember's "item must resolve to a bare field" restriction admits
        // (`list.Contains(x.Field)`, where the roles are reversed). TryResolveMember declined on the constant
        // item, so this fell back. EF-382 added the mirror arm (MongoArrayContainsExpression, with Not
        // flipping its Negated flag — see MongoExpressionTranslator's Not case), so `!Tags.Contains("x")` now
        // goes native as { Tags: { $ne: "x" } }.
        // Dedicated seed (not SeedWellFormed/SeedMatrix): every shared row builder in this file leaves Tags
        // as an empty array, which would make Tags.All(t => t != "x") vacuously true for every row and unable
        // to discriminate a wrong Contains/$nin implementation from a correct one — only the NativeOnly
        // routing proof would carry any weight. RowWithTags gives real, discriminating Tags values instead:
        //   "hasX"      Tags = ["x", "y"] -> "x" fails t != "x" -> All false.
        //   "noX"       Tags = ["y", "z"] -> every element satisfies t != "x" -> All true.
        //   "emptyTags" Tags = []          -> vacuously true (kept for parity with the empty-sequence case).
        // A dedicated seed also means none of the 17 already-verified expectations elsewhere in this file
        // shift (nothing shares this seed).
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

        // Routing proof: now goes native (EF-382) — NativeOnly succeeds rather than throwing.
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

    // FLIPPED BY EF-400 (EF-322 stream 1, slice A5) — this test USED TO ASSERT A DECLINE, and the paragraph
    // that stood here explained why: "TryResolveMember's fast path only accepts a BARE `p.Foo` member access,
    // and `p.Rank!.Value` is a `.Value` property access wrapping that member access, so it falls through to the
    // dotted-owned-path resolver, which declines outright for a non-document-root scope." That was accurate,
    // and A5 removed exactly that step — `TryResolveMember` now PEELS `Nullable<T>.Value` before its fast-path
    // switch, so `p.Rank!.Value` reduces to `p.Rank` and resolves against the owned ELEMENT type like any other
    // element member. The dotted-owned-path resolver is never reached, and its non-document-root decline (still
    // correct, and still live for a genuinely dotted element access) no longer applies to this shape.
    //
    // So this is an INCIDENTAL widening of A5, not one that slice planned: the peel sits in the one shared
    // resolver every scope reaches, so it widens the ELEMENT-scoped translator that Any/All build for their
    // $elemMatch child at the same time as it widens the root-scoped one. Verified, not assumed, that the
    // widening is value-preserving OVER THIS SEED: the expected titles below are UNCHANGED from the decline
    // era, and AssertNativeAndParity re-checks the native answer against DriverLinq's on every run. That scope
    // qualifier is load-bearing — SeedWellFormed deliberately excludes the missing/null ARRAY states, which are
    // exactly the states where behaviour moved (an exception became correct rows). See
    // All_with_a_nullable_leaf_Contains_predicate_is_correct_for_missing_and_null_ARRAYS_too below, which
    // covers them.
    //
    // The emitted form is { Posts: { $not: { $elemMatch: { Rank: { $nin: [7, 9] } } } } } — All(pred) as a
    // negated $elemMatch over the exact complement, with $nin as $in's own complement. It is correct for the
    // ragged element states for the reason $eq/$ne partitioning always gives here: a MISSING or explicitly-null
    // Rank matches $nin, so it satisfies the inner $elemMatch and excludes the row, which is what LINQ's All
    // answers for an element whose Contains(null) is false.
    //
    // This uses SeedWellFormed, NOT the full DifferentialRows: DriverLinq's own All-over-collection translation
    // (the parity oracle) renders as $expr/$allElementsTrue, which — same as the file's existing Any/All notes
    // document — throws a MongoCommandException when the ARRAY itself is missing/null (DifferentialRows'
    // MissingPostsRow/NullPostsRow), so the DriverLinq leg cannot execute against the full matrix. SeedWellFormed
    // (real, non-null Posts arrays; element-level Rank may still be missing/null) is exactly the seed the file's
    // OTHER parity tests already use for this reason.
    //
    // Expected titles, hand-verified and unchanged by the flip: allpass (ranks 9,7, both in {7,9} -> All true);
    // onefails (ranks 9,1 -> 1 not in {7,9} -> All false, excluded); missingfield/nullfield (Rank absent/null ->
    // Contains(null) does not match -> All false, excluded); empty (All over an empty sequence is vacuously true).
    [Fact]
    public void All_with_a_nullable_leaf_Contains_predicate_goes_native_since_EF400()
    {
        var collection = SeedWellFormed(
            nameof(All_with_a_nullable_leaf_Contains_predicate_goes_native_since_EF400));

        var titles = AssertNativeAndParity(
            collection, q => q.Where(b => b.Posts.All(p => new[] { 7, 9 }.Contains(p.Rank!.Value))));
        Assert.Equal(new[] { "allpass", "empty" }, titles);
    }

    // Minor 5 of EF-400's review round: the flip above is asserted over SeedWellFormed, which by construction
    // EXCLUDES the two array states where behaviour actually MOVED — so "value-preserving" was true but scoped
    // to a seed that could not have shown otherwise. This case covers exactly those states.
    //
    // BEFORE EF-400 the shape fell back, and the driver's own All translation ($expr/$allElementsTrue) ABORTS
    // the aggregate on a missing or explicitly-null Posts array ("$allElementsTrue's argument must be an array,
    // but is null") — so MissingPostsRow/NullPostsRow produced a MongoCommandException and no rows at all.
    // AFTER EF-400 the shape goes native, $not/$elemMatch matches a missing or null array vacuously, and both
    // rows are correctly INCLUDED — which is what LINQ's All over an empty sequence returns.
    //
    // So over these two states behaviour DID move: exception → correct rows. That is an improvement, and it is
    // not a break versus the published packages (which have no native path and therefore threw here too), but
    // it is a change and it belongs in a test rather than in a claim.
    //
    // No DriverLinq oracle leg is possible for this seed for the reason above — the oracle cannot execute — so
    // this uses AssertNativeOnlyMatches, the same helper every other full-matrix case in this file uses, with a
    // hand-verified expectation. Derivation for { Posts: { $not: { $elemMatch: { Rank: { $nin: [7, 9] } } } } }:
    // allpass (9,7 — no element is $nin, so nothing matches the inner $elemMatch) INCLUDED; onefails (9,1 — 1 is
    // $nin) excluded; missingfield/nullfield (a missing or null Rank matches $nin) excluded; empty/missing/null
    // (no array, or no elements, so the inner $elemMatch cannot match) INCLUDED. Identical to the expectation
    // Owned_collection_All_goes_native asserts for the same fixture, which is the cross-check.
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

    // ------------------------------------------------------------------
    // EF-424 — dotted-path resolution through a nested owned single-reference hop, from INSIDE a quantifier's
    // element-scoped predicate. Deliberately a SEPARATE fixture (NestedRef*), not an addition to Blog/Post
    // above: adding an owned single-reference navigation to the shared Post type would need every other
    // test's BlogModel/seed rows in this file to account for it.
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
        // EF-424: p.Author.City reached from INSIDE Any's element predicate is a two-hop dotted path rooted at
        // the owned-collection ELEMENT scope (Post), not the document root (NestedRefBlog) — TryResolveOwnedFieldPath
        // used to decline unconditionally for any non-root _entityType, forcing a fallback. The "Springfield"
        // vs "Shelbyville" split makes this DISCRIMINATING (not just "doesn't throw"): a wrong/empty native
        // $match would return nothing, a mis-scoped one could return both.
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

        // Succeeds under NativeOnly => went native (would throw NativeTranslationNotSupportedException before
        // the fix). The returned title proves it also matched the CORRECT row, not merely that it didn't throw.
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

    // A composite-PK-bearing owned single-reference type, NESTED (not the document root) — the reachable
    // combination EF-424's fix must also get right: EF's model builder accepts an explicit multi-property
    // HasKey on an OwnsOne target (verified via a throwaway probe), and the serializer nests a LOCAL "_id"
    // scoped to this type's own position in the document (e.g. "Author._id.City"), exactly mirroring how the
    // document root's own composite key nests under the document's top-level "_id".
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
        // The double-fix-interaction case called out in the EF-424 brief: the leaf (City) is BOTH a
        // composite-PK component of its OWN declaring type (Author, via explicit HasKey) AND reached through a
        // non-root scope (the Post collection element). Verified (via a throwaway probe reading the raw stored
        // BSON) that the serializer nests a LOCAL "_id" scoped to Author itself even though Author is not the
        // document root — { Posts: [{ Author: { _id: { City, Country }, ... } }] } — so the correct emitted
        // path composes the scope-relative hop prefix with the LEAF's own composite-PK "_id." rewrite:
        // "Author._id.City", not "Author.City". A fix that only added scope-relative hop-joining but dropped
        // (or wrongly root-gated) the composite-PK "_id." rewrite for a non-root leaf would silently address
        // the wrong field here and match nothing — asserting the correct row proves both pieces compose.
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
