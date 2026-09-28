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
/// Any/All/Count over an owned (embedded) collection translates natively to <c>$elemMatch</c>/<c>$exists</c>.
/// Admitted shapes assert a NativeOnly routing proof plus NativeOnly == DriverLinq parity; excluded shapes
/// assert a clean decline.
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
        // Deliberately collides with Blog.Title, so an owner-rooted `b.Title` in an Any lambda would mis-resolve
        // (not just fail) under by-name resolution. See Correlated_element_predicate_now_goes_native_since_EF421.
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

    // Shared row builders, so SeedBlogs and SeedWellFormedBlogs stay identical for the rows they share (several
    // expectations are derived from one another). Each call returns a fresh document.
    //
    // Post.Title is required; its values make owner-scoped and element-scoped readings of `b.Title` differ.

    // Posts with a matching element (plus a non-matching one). Neither post's own Title is "match" (the
    // owner's is), which makes the correlation test discriminating.
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

    // Posts present, no element matches. Its post's Title is "match", so a mis-scoped `b.Title == "match"`
    // selects this row instead of MatchRow.
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

    // "missing"/"null" only vary Posts; Home/Tags are present (as in "empty") because they are required and the
    // rows must still materialize.

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

    // SeedBlogs without the "missing"/"null" rows, on which the DriverLinq oracle crashes (see
    // AssertNativeOnlyMatches). Lets accept tests also assert NativeOnly == DriverLinq parity.
    private IMongoCollection<Blog> SeedWellFormedBlogs(string name)
        => Seed(name, MatchRow(), NoMatchRow(), EmptyPostsRow());

    // Runs `query` in one mode and returns the sorted titles; mode orchestration lives in NativeModeAssert.
    private List<string> RunTitles(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query, MongoQueryMode mode)
    {
        using var db = CreateContext(collection, mode, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    private List<string> AssertNativeAndParity(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => NativeModeAssert.NativeAndParity(mode => RunTitles(collection, query, mode));

    private List<string> AssertDeclinesCleanly(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => NativeModeAssert.DeclinesCleanly(mode => RunTitles(collection, query, mode));

    // The driver's Any()/Count() translation ($expr $anyElementTrue/$size) throws MongoCommandException for the
    // whole aggregate when a document's array is missing or null, so there's no DriverLinq oracle over the full
    // SeedBlogs matrix. Native $elemMatch/$exists has no such limitation. These shapes are proven by NativeOnly
    // succeeding plus hand-verified expected titles.
    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
        => RunTitles(collection, query, MongoQueryMode.NativeOnly);

    // For a declining shape over the full matrix: NativeOnly declines cleanly, and the fallback hits the same
    // driver MongoCommandException described above (asserted by concrete type).
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

        // Independent-oracle leg on the well-formed seed.
        var wellFormed = SeedWellFormedBlogs(nameof(Owned_collection_Any_with_predicate_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Heading == "x")));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all()
    {
        var collection = SeedBlogs(nameof(Owned_collection_Any_multi_condition_requires_one_element_to_satisfy_all));

        // Heading == "z" && Rank == 5 are satisfied by different elements of "match", so it must not match;
        // a dotted-path translation would get this wrong.
        var both = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "x" && p.Rank == 5)));
        Assert.Equal(["match"], both);

        var split = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Heading == "z" && p.Rank == 5)));
        Assert.Empty(split);

        // Independent-oracle leg: proves the "one element must satisfy all conjuncts" $elemMatch semantics.
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

        // Independent-oracle leg on the well-formed seed; same expected set.
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

        // Independent-oracle leg: the negated sets lose the missing/null rows.
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

        // Independent-oracle leg.
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

        // Independent-oracle leg.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Owned_collection_Any_through_owned_reference_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Home.Notes.Any(n => n.Body == "b")));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_composes_with_other_conjuncts_natively()
    {
        // Full matrix without an oracle: DriverLinq would only survive the missing/null rows if the server
        // happened to evaluate the Title conjunct first, which isn't guaranteed.
        var collection = SeedBlogs(nameof(Owned_collection_Any_composes_with_other_conjuncts_natively));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Title == "match" && b.Posts.Any(p => p.Rank > 3)));
        Assert.Equal(["match"], titles);

        // Independent-oracle leg on the well-formed seed.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Owned_collection_Any_composes_with_other_conjuncts_natively) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(
            wellFormed, q => q.Where(b => b.Title == "match" && b.Posts.Any(p => p.Rank > 3)));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Any_with_captured_parameter_element_predicate_goes_native()
    {
        // On EF8/EF9 a captured value becomes a "__"-prefixed ParameterExpression (not EF10's
        // QueryParameterExpression), so the correlated-predicate handling must exempt query parameters or this
        // would decline on EF8/EF9 only.
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
    public void Correlated_element_predicate_now_goes_native_since_EF421()
    {
        // An element predicate reaching the owner (`b.Title`) must resolve by parameter identity to the outer
        // scope ($anyElementTrue over $map), not by name against Post.Title. The seed makes the readings differ:
        //
        //   owner-scoped (correct): blogs whose own Title == "match" and that have at least one post -> ["match"]
        //   element-scoped (wrong): blogs having a post whose Title == "match"                        -> ["nomatch"]
        //
        // Well-formed seed so the DriverLinq leg can run.
        var wellFormed = SeedWellFormedBlogs(nameof(Correlated_element_predicate_now_goes_native_since_EF421));

        var titles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any(p => b.Title == "match")));
        Assert.Equal(["match"], titles);

        // Mixed element-only and correlated conjuncts: owner-scoped gives ["match"], element-scoped gives []
        // (the only post titled "match" has Rank 2).
        var mixed = SeedWellFormedBlogs(
            nameof(Correlated_element_predicate_now_goes_native_since_EF421) + "_Mixed");
        var mixedTitles = AssertNativeAndParity(
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
        // Element contents in stored order, not just the count.
        Assert.Equal(["x", "z"], blogs[0].Posts.Select(p => p.Heading));
    }

    [Fact]
    public void Field_to_field_element_predicate_declines_cleanly()
    {
        var collection = SeedBlogs(nameof(Field_to_field_element_predicate_declines_cleanly));

        AssertDeclinesCleanlyNoFallbackOracle(collection, q => q.Where(b => b.Posts.Any(p => p.Rank > p.Other)));

        // On the well-formed seed the fallback can run, distinguishing a graceful decline from a crash.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Field_to_field_element_predicate_declines_cleanly) + "_WellFormed");
        var wfTitles = AssertDeclinesCleanly(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Rank > p.Other)));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Nested_owned_scalar_leaf_in_element_now_goes_native_via_EF424()
    {
        // TryResolveOwnedFieldPath builds scope-relative paths, so p.Geo.Country resolves to "Geo.Country"
        // inside the element's $elemMatch, with no double-prefixing.
        var collection = SeedBlogs(nameof(Nested_owned_scalar_leaf_in_element_now_goes_native_via_EF424));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Any(p => p.Geo.Country == "US")));
        Assert.Equal(["match"], titles);

        // Independent-oracle leg on the well-formed seed.
        var wellFormed = SeedWellFormedBlogs(
            nameof(Nested_owned_scalar_leaf_in_element_now_goes_native_via_EF424) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Any(p => p.Geo.Country == "US")));
        Assert.Equal(["match"], wfTitles);
    }

    [Fact]
    public void Primitive_collection_Any_now_goes_native_via_the_Contains_path()
    {
        // EF rewrites `Tags.Any(t => t == "x")` to `Tags.Contains("x")`, so this is handled by the Contains
        // path's `arrayField.Contains(constant)` arm (MongoArrayContainsExpression), not the quantifier matcher.
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

        // Routing proof: NativeOnly succeeds.
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
        // An Any filter inside an owned SelectMany: TryBuildOwnedInnerFilter resolves "Comments" relative to Post
        // and MongoFieldPrefixRewriter composes "Posts.Comments" for the unwound element. No driver-LINQ oracle
        // (it fails in every mode there). Uses an anonymous projection because TryReadProjection rejects a bare
        // scalar SelectMany leaf for unrelated reasons.
        //
        // Expected value is hand-computed: only "match" has a Post with Comment Text "t", Heading "x".
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
        // All(pred) translates to a negated $elemMatch over the exact complement of pred
        // ({ Posts: { $not: { $elemMatch: { Heading: { $ne: "x" } } } } }). $elemMatch never matches an empty,
        // missing or null array, so All is vacuously true there, as in LINQ.
        var collection = SeedBlogs(nameof(All_over_owned_collection_goes_native));

        // Full-matrix leg, hand-derived:
        //   "match"  — Posts = [Heading:"x", Heading:"z"]. The "z" element satisfies ¬pred (Heading != "x"),
        //              so the $elemMatch DOES find a violator ⇒ the enclosing $not is false ⇒ All is false.
        //   "nomatch" — Posts = [Heading:"y"]. "y" != "x" satisfies ¬pred ⇒ same as above ⇒ All is false.
        //   "empty"  — Posts = []. No element can satisfy ¬pred ⇒ $elemMatch is false ⇒ $not is true ⇒ All true.
        //   "missing" — no Posts field at all. $elemMatch cannot match a missing field ⇒ same as empty ⇒ true.
        //   "null"   — Posts is explicit BSON null. $elemMatch cannot match a non-array field ⇒ same ⇒ true.
        // So the surviving (All == true) titles are exactly ["empty", "missing", "null"].
        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.All(p => p.Heading == "x")));
        Assert.Equal(["empty", "missing", "null"], titles);

        // Independent-oracle leg on the well-formed seed: only "empty" satisfies All vacuously.
        var wellFormed = SeedWellFormedBlogs(nameof(All_over_owned_collection_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.All(p => p.Heading == "x")));
        Assert.Equal(["empty"], wfTitles);
    }

    [Fact]
    public void Owned_collection_Count_predicate_goes_native()
    {
        // .Count in a predicate uses the array-index existence form ({"Posts.1": {$exists: true}}), a plain
        // dotted-path match that, unlike the driver's $size, doesn't crash on missing/null arrays.
        var collection = SeedBlogs(nameof(Owned_collection_Count_predicate_goes_native));

        var titles = AssertNativeOnlyMatches(collection, q => q.Where(b => b.Posts.Count > 1));

        // Only "match" has more than one Post (2); "nomatch" has 1; "empty"/"missing"/"null" all count as 0
        // (a missing/null embedded array counts as empty, matching LINQ's own List<T>.Count over EF's
        // materialized-as-empty-list reading of a missing/null owned collection).
        Assert.Equal(["match"], titles);

        // Independent-oracle leg on the well-formed seed.
        var wellFormed = SeedWellFormedBlogs(nameof(Owned_collection_Count_predicate_goes_native) + "_WellFormed");
        var wfTitles = AssertNativeAndParity(wellFormed, q => q.Where(b => b.Posts.Count > 1));
        Assert.Equal(["match"], wfTitles);
    }
}
