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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322: an Any quantifier over an OWNED (embedded) collection navigation translates natively to
/// $elemMatch. Each admitted shape asserts a NativeOnly routing proof plus NativeOnly == DriverLinq value
/// parity; each excluded shape asserts a clean decline (throws only under NativeOnly, correct results
/// under Native).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionPredicateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
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

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Home Home { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
        public List<string> Tags { get; set; } = [];
    }

    private class Post
    {
        public string Heading { get; set; } = "";
        // DELIBERATELY COLLIDES with Blog.Title (same name, same CLR type). Without a colliding name the
        // correlated-element-predicate guard cannot be exercised: the element-scoped translator resolves
        // members by NAME, so an owner-rooted `b.Title` inside the Any lambda only MIS-RESOLVES (rather than
        // declining for the unrelated reason "no such property on Post") when Post declares a Title too.
        // Seeded so the two interpretations give different answers — see
        // Correlated_element_predicate_declines_and_falls_back_to_correct_rows.
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public int Other { get; set; }
        public Geo Geo { get; set; } = null!;
        public List<Comment> Comments { get; set; } = [];
    }

    private class Comment
    {
        public string Text { get; set; } = "";
    }

    private class Geo
    {
        public string Country { get; set; } = "";
    }

    private class Home
    {
        public List<Note> Notes { get; set; } = [];
    }

    private class Note
    {
        public string Body { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
    {
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p =>
        {
            p.OwnsOne(x => x.Geo);
            p.OwnsMany(x => x.Comments);
        });
        mb.Entity<Blog>().OwnsOne(b => b.Home, h => h.OwnsMany(x => x.Notes));
    };

    // ------------------------------------------------------------------
    // Shared row builders
    // ------------------------------------------------------------------
    //
    // Fix round 2 (M-c): SeedBlogs and SeedWellFormedBlogs both need the "match"/"nomatch"/"empty" rows to be
    // BYTE-IDENTICAL — the two seeds are the two legs of the same tests, and nine hand-computed assertions
    // depend on the rows agreeing (e.g. Negated_owned_collection_Any_goes_native's well-formed expectation is
    // derived by subtracting the missing/null rows from the full-matrix one). These rows used to be duplicated
    // verbatim in both seeds, where a one-sided edit could silently desynchronize the legs, so they are built
    // here exactly once. Each builder returns a FRESH document (new ObjectId) per call.
    //
    // Post.Title is present on every seeded post because it is a required non-nullable property (a post
    // document missing it would fail materialization); its VALUES are chosen so that owner-scoped and
    // element-scoped readings of `b.Title` give DIFFERENT answers — see
    // Correlated_element_predicate_declines_and_falls_back_to_correct_rows.

    // Posts with a matching element (plus a second, non-matching element). NOTE neither post's own Title is
    // "match" (the OWNER's Title is) — that asymmetry is what makes the correlation test discriminating.
    private static BsonDocument MatchRow()
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "match" },
            { "Home", new BsonDocument { { "Notes", new BsonArray { new BsonDocument { { "Body", "b" } } } } } },
            { "Tags", new BsonArray { "x" } },
            { "Posts", new BsonArray
                {
                    new BsonDocument
                    {
                        { "Heading", "x" }, { "Title", "px" }, { "Rank", 5 }, { "Other", 1 },
                        { "Geo", new BsonDocument { { "Country", "US" } } },
                        { "Comments", new BsonArray { new BsonDocument { { "Text", "t" } } } }
                    },
                    new BsonDocument
                    {
                        { "Heading", "z" }, { "Title", "pz" }, { "Rank", 1 }, { "Other", 9 },
                        { "Geo", new BsonDocument { { "Country", "FR" } } },
                        { "Comments", new BsonArray() }
                    }
                }
            }
        };

    // Posts present, no element matches. Its single post's own Title IS "match" — deliberately the value the
    // OWNER of MatchRow carries — so a mis-scoped `b.Title == "match"` selects THIS row instead of MatchRow.
    private static BsonDocument NoMatchRow()
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "nomatch" },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray { "y" } },
            { "Posts", new BsonArray
                {
                    new BsonDocument
                    {
                        { "Heading", "y" }, { "Title", "match" }, { "Rank", 2 }, { "Other", 2 },
                        { "Geo", new BsonDocument { { "Country", "FR" } } },
                        { "Comments", new BsonArray() }
                    }
                }
            }
        };

    // Posts present but an EMPTY array.
    private static BsonDocument EmptyPostsRow()
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "empty" },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray() },
            { "Posts", new BsonArray() }
        };

    // "missing"/"null" only omit/null-out Posts (the field under test) — Home/Tags are seeded the same as
    // "empty" (empty-but-present) purely so the entity still materializes: both are separate required
    // (non-nullable) properties on Blog, unrelated to what these two rows are testing, and a document missing
    // them entirely would fail materialization with an unrelated "Document element is missing for required
    // non-nullable property" error the moment any test's predicate returns one of these rows as a full Blog
    // (confirmed empirically — this is exactly what happened before this fix).

    // No Posts element at all.
    private static BsonDocument MissingPostsRow()
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "missing" },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray() },
        };

    // Posts explicitly BSON null.
    private static BsonDocument NullPostsRow()
        => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "null" }, { "Posts", BsonNull.Value },
            { "Home", new BsonDocument { { "Notes", new BsonArray() } } },
            { "Tags", new BsonArray() },
        };

    private IMongoCollection<Blog> Seed(string name, params BsonDocument[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Seeds five blogs covering every array state that changes $elemMatch / $exists semantics.
    private IMongoCollection<Blog> SeedBlogs(string name)
        => Seed(name, MatchRow(), NoMatchRow(), EmptyPostsRow(), MissingPostsRow(), NullPostsRow());

    // Fix round 1 (Task 4 review): the SAME three rows as SeedBlogs's "match"/"nomatch"/"empty" — i.e. every
    // row whose Posts is a real (non-null) array — but DELIBERATELY OMITS the "missing" and "null" rows.
    // Those two rows are exactly what makes the DriverLinq oracle unusable (see AssertNativeOnlyMatches's
    // comment below: the driver's Any()/Count() translation throws MongoCommandException the instant it
    // scans a document whose array field is missing/explicit-null — a pre-existing driver limitation, not a
    // property of the Any-quantifier SHAPES themselves). On this well-formed seed the DriverLinq oracle works
    // fine, so accept-shape tests can additionally assert NativeOnly == DriverLinq parity here (via
    // AssertNativeAndParity) — the independent check that would catch a mis-built $elemMatch (e.g. the
    // multi-condition "one element must satisfy ALL conjuncts" semantic) — while the full SeedBlogs matrix
    // (via AssertNativeOnlyMatches) remains the coverage for the missing/null/empty edge states that
    // DriverLinq itself cannot exercise.
    private IMongoCollection<Blog> SeedWellFormedBlogs(string name)
        => Seed(name, MatchRow(), NoMatchRow(), EmptyPostsRow());

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
    // against a hand-verified expected value. This is what a GRACEFUL decline actually promises: not just
    // "every mode throws" (that alone doesn't distinguish a graceful decline from a crash — see
    // AssertDeclinesCleanlyNoFallbackOracle below, which is what's left when this constructor's own
    // DriverLinq leg can't run at all) but "NativeOnly proves the decline, and the fallback path is proven
    // trustworthy by an independent oracle."
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

    // EMPIRICAL FINDING (Task 4, confirmed via isolated single-document probes, not assumed): the MongoDB C#
    // driver's own LINQ v3 translation of Any()/Count() over a collection navigation renders as an
    // $expr-based $anyElementTrue/$allElementsTrue/$size — and MongoDB's aggregation runtime throws
    // MongoCommandException ("...'s argument must be an array, but is null"/"...but was of type: missing")
    // the instant it evaluates that operator against a document whose array field is missing or explicit BSON
    // null. This aborts the WHOLE aggregate command, not just that one document — so a DriverLinq run over
    // SeedBlogs's "missing"/"null" rows crashes outright, for EVERY Any()/Count()-based query, unless an
    // earlier $and conjunct happens to short-circuit past those rows (as in
    // Owned_collection_Any_composes_with_other_conjuncts_natively, which the Title=="match" conjunct saves).
    // $elemMatch/$exists (the native query dialect this feature emits) has NO such limitation — treating a
    // missing/null array as "no element matches" without erroring is exactly the robustness this feature is
    // built to prove, and this crash is direct empirical evidence of the contrast. There is therefore no
    // working DriverLinq oracle for this seed's full state matrix FOR ANY SHAPE THAT READS Posts — every
    // caller of this helper except one queries Posts, whose "missing"/"null" rows are exactly what crashes
    // DriverLinq. (The one exception, Owned_collection_Any_through_owned_reference_goes_native, queries only
    // Home.Notes, which SeedBlogs seeds as a present, non-null array on ALL FIVE rows — that test's
    // full-matrix leg has a working DriverLinq oracle too, this helper just doesn't exercise it; the
    // independent-oracle leg below covers that separately, on the well-formed seed, via AssertNativeAndParity.)
    // Per this repo's established convention for shapes without one (see the SelectMany notes in
    // Query/AGENTS.md — "proven via NativeOnly succeeding plus an expected-in-memory-result-set assertion, not
    // Native == DriverLinq parity"), these are proven via NativeOnly (the routing proof) plus the
    // hand-verified expected titles each test already asserts.
    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    // Same root cause as AssertNativeOnlyMatches above, applied to a shape that correctly DECLINES native
    // translation: NativeOnly still proves the clean, intended decline (NativeTranslationNotSupportedException
    // — this shape genuinely is out of scope for this slice), but the FALLBACK that decline would normally
    // exercise (Native mode, and explicit DriverLinq) both execute the underlying Any()/Count() against
    // SeedBlogs's mixed missing/null array states and crash with MongoCommandException — the SAME unrelated,
    // pre-existing driver limitation, not a scope escape and not silently-wrong data. Every mode therefore
    // throws, but only NativeOnly's exception is the one this slice actually guarantees. Unlike the
    // ThrowsAny + IsNotType<KeyNotFoundException> pattern in
    // NativeSelectManyTests.Filtered_owned_nested_subproperty_predicate_hard_fails_in_every_mode_not_double_prefixed
    // (which discriminates a real provider partial-execution failure from a crash), the exception here is
    // always the driver's own MongoCommandException from the empirical finding above — so the assertion is
    // tightened to that concrete type rather than the weaker "any exception that isn't KeyNotFoundException"
    // check, which would never fail regardless of what actually gets thrown.
    private void AssertDeclinesCleanlyNoFallbackOracle(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => query(db.Entities.AsNoTracking()).ToList());
        }

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);
            Assert.ThrowsAny<MongoCommandException>(() => query(db.Entities.AsNoTracking()).ToList());
        }
    }

    [Fact]
    public void Owned_collection_Any_with_predicate_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_with_predicate_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "x")));

        Assert.Equal(["match"], titles);

        // Independent-oracle leg (fix round 1): well-formed seed (no missing/null Posts), so DriverLinq can
        // actually run — NativeOnly == DriverLinq parity, not just a hand-verified expectation.
        var wellFormed = SeedWellFormedBlogs(nameof(Owned_collection_Any_with_predicate_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Heading == "x")));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all));

        // "match" has an element with Heading "x" AND Rank 5, so it matches. Crucially, the conditions
        // Heading == "z" && Rank == 5 are each satisfied by DIFFERENT elements of "match" and must NOT match
        // — this is the semantic a dotted-path translation would get wrong.
        var both = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "x" && p.Rank == 5)));
        Assert.Equal(["match"], both);

        var split = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "z" && p.Rank == 5)));
        Assert.Empty(split);

        // Independent-oracle leg (fix round 1) — the MOST IMPORTANT parity check in this file: this is
        // exactly the "one element must satisfy ALL conjuncts" semantic that motivated a real $elemMatch AST
        // node over a cheaper dotted-path translation (which would wrongly match when different elements
        // each satisfy a different conjunct). A well-formed seed lets DriverLinq actually run, so this proves
        // (not just hand-verifies) that the emitted $elemMatch has the correct semantics.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all) + "_WellFormed");

        var wfBoth = AssertNativeAndParity(
            wellFormed, q => q.Where(b => b.Posts.Any(p => p.Heading == "x" && p.Rank == 5)));
        Assert.Equal(["match"], wfBoth);

        var wfSplit = AssertNativeAndParity(
            wellFormed, q => q.Where(b => b.Posts.Any(p => p.Heading == "z" && p.Rank == 5)));
        Assert.Empty(wfSplit);
    }

    [Fact]
    public void Owned_collection_bare_Any_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_collection_bare_Any_goes_native));

        // Present-and-non-empty only: an empty array, a missing field, and an explicit null all yield false.
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Any()));

        Assert.Equal(["match", "nomatch"], titles);

        // Independent-oracle leg (fix round 1): well-formed seed (match/nomatch/empty only — missing/null
        // excluded, per SeedWellFormedBlogs), so the expected set is the same as above.
        var wellFormed = SeedWellFormedBlogs(nameof(Owned_collection_bare_Any_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any()));
        Assert.Equal(["match", "nomatch"], wfTitles);
    }

    [Fact]
    public void Negated_owned_collection_Any_goes_native()
    {
        var collection = SeedBlogs(nameof(Negated_owned_collection_Any_goes_native));

        var negatedPredicate = AssertNativeOnlyMatches(
            collection, q => q.Where(b => !b.Posts.Any(p => p.Heading == "x")));
        Assert.Equal(["empty", "missing", "nomatch", "null"], negatedPredicate);

        var negatedBare = AssertNativeOnlyMatches(collection, q => q.Where(b => !b.Posts.Any()));
        Assert.Equal(["empty", "missing", "null"], negatedBare);

        // Independent-oracle leg (fix round 1): well-formed seed only has match/nomatch/empty, so the negated
        // sets are SMALLER than the full-matrix ones above (missing/null are gone) — computed fresh from the
        // well-formed seed, not assumed to match the full-matrix expectations.
        var wellFormed = SeedWellFormedBlogs(nameof(Negated_owned_collection_Any_goes_native) + "_WellFormed");

        var wfNegatedPredicate = AssertNativeAndParity(
            wellFormed, q => q.Where(b => !b.Posts.Any(p => p.Heading == "x")));
        Assert.Equal(["empty", "nomatch"], wfNegatedPredicate);

        var wfNegatedBare = AssertNativeAndParity(wellFormed, q => q.Where(b => !b.Posts.Any()));
        Assert.Equal(["empty"], wfNegatedBare);
    }

    [Fact]
    public void Nested_owned_collection_Any_goes_native()
    {
        var collection = SeedBlogs(nameof(Nested_owned_collection_Any_goes_native));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t"))));

        Assert.Equal(["match"], titles);

        // Independent-oracle leg (fix round 1).
        var wellFormed = SeedWellFormedBlogs(nameof(Nested_owned_collection_Any_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(
            wellFormed, q => q.Where(b => b.Posts.Any(p => p.Comments.Any(c => c.Text == "t"))));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_through_owned_reference_goes_native()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_through_owned_reference_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Home.Notes.Any(n => n.Body == "b")));

        Assert.Equal(["match"], titles);

        // Independent-oracle leg (fix round 1).
        var wellFormed = SeedWellFormedBlogs(
            nameof(Owned_collection_Any_through_owned_reference_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Home.Notes.Any(n => n.Body == "b")));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_composes_with_other_conjuncts_natively()
    {
        // Fix round 2 (M-d): the full-matrix leg is now ORACLE-FREE (AssertNativeOnlyMatches + a hand-verified
        // expectation), and the parity check moved to a well-formed-seed leg, matching every other accept test
        // in this file. Previously this test ran the full matrix through AssertNativeAndParity, whose DriverLinq
        // leg only survived because the aggregation runtime happened to evaluate the `Title == "match"` conjunct
        // of the $and first and so never reached the $anyElementTrue on the missing/null-Posts rows — conjunct
        // evaluation order is not a documented guarantee, so that leg was one server-side reordering away from
        // failing for a reason unrelated to what it tests.
        var collection = SeedBlogs(nameof(Owned_collection_Any_composes_with_other_conjuncts_natively));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Title == "match" && b.Posts.Any(p => p.Rank > 3)));
        Assert.Equal(["match"], titles);

        // Independent-oracle leg: well-formed seed only (no missing/null Posts), so DriverLinq runs regardless
        // of conjunct order — NativeOnly == DriverLinq parity.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Owned_collection_Any_composes_with_other_conjuncts_natively) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(
            wellFormed, q => q.Where(b => b.Title == "match" && b.Posts.Any(p => p.Rank > 3)));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_with_captured_parameter_element_predicate_goes_native()
    {
        // A captured value in the element predicate becomes an EF query parameter — and on EF8/EF9 an EF query
        // parameter IS a ParameterExpression (a "__"-prefixed name), unlike EF10's typed
        // QueryParameterExpression. The correlated-element-predicate guard therefore has to exempt query
        // parameters explicitly, or this shape would decline on EF8/EF9 only. This test pins that it does not.
        var collection = SeedBlogs(nameof(Owned_collection_Any_with_captured_parameter_element_predicate_goes_native));
        var heading = "x";

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Any(p => p.Heading == heading)));
        Assert.Equal(["match"], titles);

        var wellFormed = SeedWellFormedBlogs(
            nameof(Owned_collection_Any_with_captured_parameter_element_predicate_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Heading == heading)));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Correlated_element_predicate_declines_and_falls_back_to_correct_rows()
    {
        // CRITICAL-FINDING REGRESSION TEST (review fix C1). An element predicate that reaches OUT of the element
        // into the enclosing entity must DECLINE, because the element-scoped translator resolves members by NAME
        // with no parameter-identity check: `b.Title` (the OWNER's Title) would silently resolve against Post,
        // which also declares a Title, emitting { Posts: { $elemMatch: { Title: "match" } } } — a condition on
        // the ELEMENT, not the owner. GUARD REACHABILITY (this repo's convention for a guard test): the seed is
        // built so the two readings give DIFFERENT answers, so this input would otherwise be ACCEPTED and return
        // wrong rows — it is not an input that declines for some unrelated reason.
        //
        //   owner-scoped (CORRECT): blogs whose own Title == "match" and that have at least one post -> ["match"]
        //   element-scoped (WRONG): blogs having a post whose Title == "match"                        -> ["nomatch"]
        //
        // The well-formed seed is used because AssertDeclinesCleanly's Native/DriverLinq legs must actually run,
        // and the driver's own Any() translation crashes on the full matrix's missing/null Posts rows (see
        // AssertNativeOnlyMatches).
        var wellFormed = SeedWellFormedBlogs(nameof(Correlated_element_predicate_declines_and_falls_back_to_correct_rows));

        var titles = AssertDeclinesCleanly(wellFormed, q => q.Where(b => b.Posts.Any(p => b.Title == "match")));
        Assert.Equal(["match"], titles);

        // Mixed form: one element-only conjunct plus one correlated conjunct. Same discrimination —
        // owner-scoped gives ["match"] (its Title is "match" and it has a post with Rank 5 > 3), element-scoped
        // gives [] (the only post titled "match" has Rank 2).
        var mixed = SeedWellFormedBlogs(
            nameof(Correlated_element_predicate_declines_and_falls_back_to_correct_rows) + "_Mixed");
        var mixedTitles = AssertDeclinesCleanly(
            mixed, q => q.Where(b => b.Posts.Any(p => b.Title == "match" && p.Rank > 3)));
        Assert.Equal(["match"], mixedTitles);
    }

    [Fact]
    public void Owned_collection_Any_is_correct_when_tracked()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_is_correct_when_tracked));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        var blogs = db.Entities.Where(b => b.Posts.Any(p => p.Heading == "x")).ToList();

        Assert.Equal(["match"], blogs.Select(b => b.Title));
        // Element CONTENTS, not just cardinality (fix round 2, M-f) — proves the owned collection actually
        // materialized, in stored order, rather than merely counting two elements.
        Assert.Equal(["x", "z"], blogs[0].Posts.Select(p => p.Heading));
    }

    [Fact]
    public void Field_to_field_element_predicate_declines_cleanly()
    {
        var collection = SeedBlogs(nameof(Field_to_field_element_predicate_declines_cleanly));

        AssertDeclinesCleanlyNoFallbackOracle(collection, q => q.Where(b => b.Posts.Any(p => p.Rank > p.Other)));

        // Independent-oracle leg (fix round 1): on the full SeedBlogs matrix, every mode throws (see
        // AssertDeclinesCleanlyNoFallbackOracle's comment), which proves NativeOnly declines but does NOT
        // distinguish a graceful decline (correct fallback) from a crash. A well-formed seed lets the
        // fallback actually run: NativeOnly still throws, and Native == DriverLinq with correct values.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Field_to_field_element_predicate_declines_cleanly) + "_WellFormed");
        var wfTitles = AssertDeclinesCleanly(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Rank > p.Other)));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Nested_owned_scalar_leaf_in_element_now_goes_native_via_EF424()
    {
        // FLIPPED BY EF-424: this test USED TO ASSERT A DECLINE (as "..._declines_cleanly"), because
        // TryResolveOwnedFieldPath (MongoExpressionTranslator.Members.cs) declined unconditionally whenever
        // its own _entityType was not a document root — which the element-scoped translator built for this
        // Any's predicate always is (Post, not Blog). EF-424 replaced that GetDocumentPath()-based, root-only
        // construction with a SCOPE-RELATIVE one (joining each hop's own containing element name, mirroring
        // the sibling TryResolveOwnedCollectionPath), so p.Geo.Country now resolves to "Geo.Country" relative
        // to the element scope, which composes correctly with the enclosing $elemMatch — no double-prefixing,
        // and no root-relative path fighting a scope it was never built against. This is the SAME shape as
        // Owned_collection_Any_through_owned_reference_goes_native above (an owned single-reference hop reached
        // through Any), just with the hop INSIDE the collection element instead of before it.
        var collection = SeedBlogs(nameof(Nested_owned_scalar_leaf_in_element_now_goes_native_via_EF424));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Any(p => p.Geo.Country == "US")));
        Assert.Equal(["match"], titles);

        // Independent-oracle leg: well-formed seed (no missing/null Posts), so DriverLinq can actually run —
        // NativeOnly == DriverLinq parity, not just a hand-verified expectation.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Nested_owned_scalar_leaf_in_element_now_goes_native_via_EF424) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Geo.Country == "US")));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Primitive_collection_Any_now_goes_native_via_the_Contains_path()
    {
        // EF's AllAnyToContainsRewritingExpressionVisitor rewrites `Tags.Any(t => t == "x")` into
        // `Tags.Contains("x")` before the native translator sees it, so this shape never reaches the
        // quantifier matcher at all — it is handled entirely by the Contains path in
        // MongoExpressionTranslator, unchanged by the owned-collection-quantifier slice this file otherwise
        // covers.
        //
        // Before EF-382, that Contains path only recognized `values.Contains(e.Field)` ($in) — the mirror
        // shape here, `arrayField.Contains(constant)`, declined. EF-382 added the mirror arm
        // (MongoArrayContainsExpression), so this now goes native too — routing proof is NativeOnly
        // succeeding, per the Query AGENTS.md "MQL shape cannot prove a query went native" pitfall.
        var collection = SeedBlogs(nameof(Primitive_collection_Any_now_goes_native_via_the_Contains_path));

        List<string> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            native = db.Entities.AsNoTracking().Where(b => b.Tags.Any(t => t == "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        List<string> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, BlogModel))
        {
            driver = db.Entities.AsNoTracking().Where(b => b.Tags.Any(t => t == "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
        }

        Assert.Equal(driver, native);
        Assert.Equal(["match"], native);

        // Routing proof: now goes native (EF-382) — NativeOnly succeeds rather than throwing.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            var nativeOnly = db.Entities.AsNoTracking().Where(b => b.Tags.Any(t => t == "x"))
                .ToList().Select(b => b.Title).OrderBy(t => t).ToList();
            Assert.Equal(["match"], nativeOnly);
        }
    }

    [Fact]
    public void Owned_SelectMany_with_an_inner_Any_filter_now_works()
    {
        // EMERGENT NEW CAPABILITY (spike-confirmed), WITH A CORRECTED PROJECTION SHAPE. Before this slice
        // this shape hard-fails in EVERY mode (including DriverLinq) with InvalidOperationException "could
        // not be translated": the owned SelectMany binder's inner-filter translator could not handle Any, so
        // it declined after the binder had already engaged, and there is no driver-LINQ oracle. It works once
        // $elemMatch exists — NativeSelectManyBinder.TryBuildOwnedInnerFilter's element-scoped translator
        // resolves "Comments" relative to Post, and its existing MongoFieldPrefixRewriter.Rewrite(...,
        // "Posts") call composes that into "Posts.Comments", which correctly addresses the $unwind-ed
        // element.
        //
        // IMPLEMENTER CORRECTION: the brief's original result selector, `(b, p) => p.Heading` (a BARE SCALAR,
        // not an anonymous/DTO projection), does NOT exercise this at all — it fails for a completely
        // unrelated, pre-existing reason: NativeSelectManyBinder.TryBindTransparentIdentifierProjection's
        // TryReadProjection only accepts a NewExpression/MemberInitExpression leaf, so a bare `ti.Inner.Heading`
        // member access is rejected regardless of whether the inner Any-filter translates — confirmed
        // empirically (NativeOnly threw the GENERIC "Query projects a non-entity result" fallback exception,
        // not evidence of the Any-filter itself failing). A bare-scalar SelectMany projection is a separate,
        // long-documented limitation (see Query/AGENTS.md), not something this slice touches either way.
        // Using an anonymous projection (`new { p.Heading }`) — the same shape every other SelectMany test in
        // this codebase uses — lets the REAL capability under test (the inner Any-filter) actually reach
        // native translation; Heading is then extracted client-side, after materialization, which exercises
        // no further query translation.
        //
        // No oracle exists, so the expected value is hand-computed from the seed data: only the "match" blog
        // has a Post whose Comments contain Text "t", and that Post's Heading is "x".
        var collection = SeedBlogs(nameof(Owned_SelectMany_with_an_inner_Any_filter_now_works));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var headings = db.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => p.Comments.Any(c => c.Text == "t")), (b, p) => new { p.Heading })
            .ToList()
            .Select(x => x.Heading)
            .ToList();

        Assert.Equal(["x"], headings);
    }

    [Fact]
    public void All_over_owned_collection_goes_native()
    {
        // EF-322 Task 2 (commit 3759cb7, immediately before EF-335's top-level-All slice) made this shape go
        // native: All(pred) translates to a NEGATED $elemMatch over the EXACT complement of pred
        // ({ Posts: { $not: { $elemMatch: { Heading: { $ne: "x" } } } } }) — no element may satisfy ¬pred. That
        // form is also correct for an empty, missing, or explicitly-null array: $elemMatch can never match a
        // non-array/absent field, so the enclosing $not is true and All is (correctly) true, mirroring LINQ's
        // "All is vacuously true over an empty sequence" semantics. This test used to assert a clean DECLINE
        // (pre-Task-2 behavior) and was not updated when Task 2 landed — a scoping gap in that task's
        // verification (it ran the unit-test project, not this functional one), caught late by EF-335's
        // required whole-Query-subset sweep. Flipped here per the "invert, don't delete; keep a proof of
        // correctness" rule: NativeOnly is the routing proof, and the full SeedBlogs matrix is the value proof.
        var collection = SeedBlogs(nameof(All_over_owned_collection_goes_native));

        // Full-matrix leg (SeedBlogs: match/nomatch/empty/missing/null) — hand-derived, then verified against
        // the actual database rather than assumed:
        //   "match"  — Posts = [Heading:"x", Heading:"z"]. The "z" element satisfies ¬pred (Heading != "x"),
        //              so the $elemMatch DOES find a violator ⇒ the enclosing $not is false ⇒ All is false.
        //   "nomatch" — Posts = [Heading:"y"]. "y" != "x" satisfies ¬pred ⇒ same as above ⇒ All is false.
        //   "empty"  — Posts = []. No element can satisfy ¬pred ⇒ $elemMatch is false ⇒ $not is true ⇒ All true.
        //   "missing" — no Posts field at all. $elemMatch cannot match a missing field ⇒ same as empty ⇒ true.
        //   "null"   — Posts is explicit BSON null. $elemMatch cannot match a non-array field ⇒ same ⇒ true.
        // So the surviving (All == true) titles are exactly ["empty", "missing", "null"].
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Heading == "x")));
        Assert.Equal(["empty", "missing", "null"], titles);

        // Independent-oracle leg (well-formed seed: match/nomatch/empty only, no missing/null — see
        // SeedWellFormedBlogs's comment for why DriverLinq can't run over the full matrix). All() semantics
        // are unchanged by this routing change — LINQ's All() is vacuously true over an empty sequence, so
        // "empty" (Posts: []) satisfies All(p => p.Heading == "x") trivially, while "match" (Headings "x" and
        // "z") and "nomatch" (Heading "y") both have an element that fails — same expectation as before the
        // flip, just now proven via NativeOnly == DriverLinq parity instead of a clean-decline assertion.
        var wellFormed = SeedWellFormedBlogs(nameof(All_over_owned_collection_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.All(p => p.Heading == "x")));
        Assert.Equal(["empty"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Count_predicate_goes_native()
    {
        // FLIPPED by EF-322 Task 6 (the eligibility change): .Count in a predicate now goes native, via the
        // array-index existence form ({"Posts.1": {$exists: true}}) rather than declining. That form is a
        // plain dotted-path match — no $size, no $expr — so unlike the driver's own Count() translation
        // (which renders $expr-based $size and crashes on a missing/explicit-null array field, per the
        // AssertDeclinesCleanlyNoFallbackOracle comment above) it does NOT crash on SeedBlogs's missing/null
        // rows. So this is now proven via AssertNativeOnlyMatches (NativeOnly routing proof, full matrix —
        // DriverLinq still has no working oracle over missing/null rows for this shape) plus the well-formed
        // independent-oracle leg (NativeOnly == DriverLinq parity), exactly like the sibling Any/All tests.
        var collection = SeedBlogs(nameof(Owned_collection_Count_predicate_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count > 1));

        // Only "match" has more than one Post (2); "nomatch" has 1; "empty"/"missing"/"null" all count as 0
        // (a missing/null embedded array counts as empty, matching LINQ's own List<T>.Count over EF's
        // materialized-as-empty-list reading of a missing/null owned collection).
        Assert.Equal(["match"], titles);

        // Independent-oracle leg: well-formed seed (no missing/null Posts), so DriverLinq can actually run —
        // NativeOnly == DriverLinq parity, not just a hand-verified expectation.
        var wellFormed = SeedWellFormedBlogs(nameof(Owned_collection_Count_predicate_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Count > 1));
        Assert.Equal(["match"], wfTitles);
    }
}
