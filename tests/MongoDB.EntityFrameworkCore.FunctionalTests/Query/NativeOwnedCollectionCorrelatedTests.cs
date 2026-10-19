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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-421: a correlated owned-collection element predicate — one referencing the immediately enclosing
/// entity, e.g. <c>b.Posts.Count(p =&gt; p.Title == b.Title) &gt; 0</c> — now goes native via a two-scope
/// translator, instead of declining to driver-LINQ.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedCollectionCorrelatedTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction)
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

    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        // DELIBERATELY COLLIDES with Blog.Title — a mis-scoped (element-scoped) resolution of `b.Title` would
        // retarget at Post.Title and return the wrong rows, making this seed discriminating rather than
        // vacuous. Mirrors NativeOwnedCollectionAllTests' identical seeding rationale.
        public string Title { get; set; } = "";
        public int? Rank { get; set; }
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsMany(b => b.Posts);

    private IMongoCollection<Blog> Seed(string name, params (string BlogTitle, (string PostTitle, int? Rank)[] Posts)[] rows)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany(rows.Select(r => new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Title", r.BlogTitle },
            { "Posts", new BsonArray(r.Posts.Select(p => new BsonDocument
                {
                    { "Title", p.PostTitle },
                    { "Rank", p.Rank.HasValue ? p.Rank.Value : BsonNull.Value }
                }))
            }
        }));
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    private List<string> AssertNativeOnlyMatches(
        IMongoCollection<Blog> collection, Func<IQueryable<Blog>, IQueryable<Blog>> query)
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);
        return query(db.Entities.AsNoTracking()).ToList().Select(b => b.Title).OrderBy(t => t).ToList();
    }

    [Fact]
    public void Correlated_filtered_Count_predicate_goes_native()
    {
        // "match": a post whose Title equals the OWNER's Title -> Count(pred) == 1 -> > 0 -> included.
        // "other": no post's Title equals the owner's Title ("other" vs post titles "x"/"y") -> excluded.
        var collection = Seed(nameof(Correlated_filtered_Count_predicate_goes_native),
            ("match", [("match", 1), ("x", 2)]),
            ("other", [("x", 1), ("y", 2)]));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Count(p => p.Title == b.Title) > 0));

        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Correlated_Any_goes_native()
    {
        var collection = Seed(nameof(Correlated_Any_goes_native),
            ("match", [("match", 1), ("x", 2)]),
            ("other", [("x", 1), ("y", 2)]));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.Any(p => p.Title == b.Title)));

        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Correlated_All_goes_native()
    {
        // "allMatch": every post's Title equals the owner's Title -> All true -> included.
        // "notAll": one post's Title does NOT equal the owner's Title -> All false -> excluded.
        var collection = Seed(nameof(Correlated_All_goes_native),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]));

        var titles = AssertNativeOnlyMatches(
            collection, q => q.Where(b => b.Posts.All(p => p.Title == b.Title)));

        Assert.Equal(new[] { "same" }, titles);
    }

    [Fact]
    public void Correlated_Any_and_All_are_correct_against_an_in_memory_oracle()
    {
        // Differential check, mirroring NativeOwnedCollectionAllTests' own matrix pattern: the SAME
        // expression evaluated in memory over materialized rows must agree with the native (NativeOnly)
        // result — proving this isn't merely "doesn't throw" but answers the correct rows, including the
        // empty-Posts / no-match / all-match / one-mismatch states.
        var collection = Seed(nameof(Correlated_Any_and_All_are_correct_against_an_in_memory_oracle),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]),
            ("nomatch", [("x", 1), ("y", 2)]),
            ("empty", []));

        System.Linq.Expressions.Expression<Func<Blog, bool>>[] predicates =
        [
            b => b.Posts.Any(p => p.Title == b.Title),
            b => b.Posts.All(p => p.Title == b.Title),
            b => b.Posts.Count(p => p.Title == b.Title) > 0
        ];

        foreach (var predicate in predicates)
        {
            List<string> expected;
            using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
            {
                expected = db.Entities.AsNoTracking().ToList()
                    .Where(predicate.Compile()).Select(b => b.Title).OrderBy(t => t).ToList();
            }

            List<string> actual;
            using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
            {
                actual = db.Entities.AsNoTracking().Where(predicate).ToList()
                    .Select(b => b.Title).OrderBy(t => t).ToList();
            }

            Assert.Equal(expected, actual);
        }
    }

    public class RefBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public RefOwner Owner { get; set; } = null!;
        public List<RefPost> Posts { get; set; } = [];
    }

    public class RefOwner
    {
        public string City { get; set; } = "";
    }

    public class RefPost
    {
        public string City { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> RefBlogModel = mb =>
    {
        mb.Entity<RefBlog>().OwnsOne(b => b.Owner);
        mb.Entity<RefBlog>().OwnsMany(b => b.Posts);
    };

    [Fact]
    public void Correlated_Any_through_an_outer_owned_single_reference_hop_goes_native()
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Correlated_Any_through_an_outer_owned_single_reference_hop_goes_native)));
        var collection = database.MongoDatabase.GetCollection<RefBlog>(raw.CollectionNamespace.CollectionName);

        using (var seedDb = CreateContext(collection, MongoQueryMode.DriverLinq, RefBlogModel))
        {
            seedDb.Entities.Add(new RefBlog
            {
                Title = "match", Owner = new RefOwner { City = "Springfield" },
                Posts = [new RefPost { City = "Springfield" }, new RefPost { City = "Shelbyville" }]
            });
            seedDb.Entities.Add(new RefBlog
            {
                Title = "nomatch", Owner = new RefOwner { City = "Springfield" },
                Posts = [new RefPost { City = "Shelbyville" }]
            });
            seedDb.SaveChanges();
        }

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, RefBlogModel);

        var titles = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => p.City == b.Owner.City))
            .ToList().Select(b => b.Title).ToList();

        Assert.Equal(new[] { "match" }, titles);
    }

    [Fact]
    public void Correlated_Count_as_a_computed_sort_key_goes_native()
    {
        var collection = Seed(nameof(Correlated_Count_as_a_computed_sort_key_goes_native),
            ("a", [("a", 1)]),                 // 1 matching post
            ("b", [("x", 1), ("y", 2)]),       // 0 matching posts
            ("c", [("c", 1), ("c", 2)]));      // 2 matching posts

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var titles = db.Entities.AsNoTracking()
            .OrderBy(b => b.Posts.Count(p => p.Title == b.Title))
            .Select(b => b.Title).ToList();

        Assert.Equal(new[] { "b", "a", "c" }, titles);
    }

    [Fact]
    public void Correlated_Count_predicate_inside_a_projection_leaf_goes_native()
    {
        // A bare Any(pred) as a projection VALUE (as opposed to a Where PREDICATE) is not admitted by
        // TranslateOperand/NativeProjectionBinder at all today — that is a separate, pre-existing, orthogonal
        // gap (an uncorrelated bare Any projection leaf fails identically) and out of EF-421's scope. A
        // COMPARISON leaf (`Count(pred) > 0`) is *also* not admitted — NativeProjectionBinder.TryTranslateLeaf's
        // final gate only admits a bare MongoFilteredSizeExpression VALUE, not a MongoBinaryExpression wrapping
        // one (that gate has no arm for a comparison operator at all, only the dedicated arithmetic-operator
        // arm above it, which GreaterThan isn't). So this test instead projects the RAW `Count(pred)` value —
        // exactly the admitted MongoFilteredSizeExpression leaf shape (the same one the sort-key test above
        // already proves is SelfParam-aware, just via NativeSlotPopulator's OrderBy arm rather than
        // NativeProjectionBinder's Select arm) — and derives ">0" client-side after materializing. This still
        // proves SelfParam correctly reaches NativeProjectionBinder, the actual point of this test, without
        // requiring new production capability for bare-quantifier-as-value or comparison-as-value.
        var collection = Seed(nameof(Correlated_Count_predicate_inside_a_projection_leaf_goes_native),
            ("match", [("match", 1)]),
            ("nomatch", [("x", 1)]));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        var results = db.Entities.AsNoTracking()
            .Select(b => new { b.Title, MatchingPostCount = b.Posts.Count(p => p.Title == b.Title) })
            .OrderBy(r => r.Title).ToList()
            .Select(r => (r.Title, HasMatchingPost: r.MatchingPostCount > 0));

        Assert.Equal(
            new[] { (Title: "match", HasMatchingPost: true), (Title: "nomatch", HasMatchingPost: false) },
            results.ToArray());
    }

    [Fact]
    public void Correlated_All_inside_a_scalar_aggregate_predicate_goes_native()
    {
        // "same" and "mixed" both have a post whose title equals the owner's title (so Any(...) is true for
        // both); "neither" does not, giving All(...) a genuine false case to detect a mis-scoped resolution.
        var collection = Seed(nameof(Correlated_All_inside_a_scalar_aggregate_predicate_goes_native),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]),
            ("neither", [("x", 1), ("y", 2)]));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Root-level All(pred) is itself a scalar-aggregate terminal (NativeCardinalityBinder.TryBindAggregate)
        // whose OWN predicate lambda parameter becomes SelfParam for the translator that predicate is built
        // with — so a correlated Any/All/Count(pred) NESTED inside it can match against that root b.
        var allBlogsHaveAMatchingPost = db.Entities.AsNoTracking()
            .All(b => b.Posts.Any(p => p.Title == b.Title));

        Assert.False(allBlogsHaveAMatchingPost); // "neither" has no post matching its own title.
    }

    [Fact]
    public void Root_level_All_of_an_explicitly_negated_correlated_Any_goes_native_and_matches_the_oracle()
    {
        // Directly exercises MongoExpressionNegator's new MongoQuantifierExpression case (EF-421 Task 7
        // review fix): NativeCardinalityBinder.TryBindAggregate's All arm translates this predicate to a
        // MongoQuantifierExpression (from the OUTER `!` — the negator sees `!Any(pred)` first, not `Any(pred)`
        // as in the sibling test above) and then negates the WHOLE predicate again to push it as a $match
        // conjunct — so this specifically requires negating a quantifier that ALREADY carries a negated
        // element predicate, unlike the sibling test's directly-translated bare Any.
        var collection = Seed(nameof(Root_level_All_of_an_explicitly_negated_correlated_Any_goes_native_and_matches_the_oracle),
            ("same", [("same", 1), ("same", 2)]),
            ("mixed", [("mixed", 1), ("other", 2)]),
            ("neither", [("x", 1), ("y", 2)]),
            ("empty", []));

        System.Linq.Expressions.Expression<Func<Blog, bool>> predicate = b => !b.Posts.Any(p => p.Title == b.Title);

        bool expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList().All(predicate.Compile());
        }

        bool actual;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel))
        {
            actual = db.Entities.AsNoTracking().All(predicate);
        }

        Assert.Equal(expected, actual);
        Assert.False(actual); // "same" and "mixed" both have a self-matching post, so !Any(...) is false for them.
    }

    public class ConvertedBoolBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public bool Flag { get; set; }
        public List<ConvertedBoolPost> Posts { get; set; } = [];
    }

    public class ConvertedBoolPost
    {
        public string Title { get; set; } = "";
        public int Rank { get; set; }
    }

    // HasConversion<string>() stores a bool as the non-empty ("True"/"False") string — a value BOTH of which
    // are truthy in MongoDB's truthiness sense, regardless of the underlying CLR value. This is exactly the
    // hazard EF-421's final review found: MongoOuterFieldExpression (this bare Flag access, when correlated)
    // was missing from three (really four) truthiness guards in MongoAggregationExpressionRenderer.
    private static readonly Action<ModelBuilder> ConvertedBoolBlogModel = mb =>
    {
        mb.Entity<ConvertedBoolBlog>().Property(b => b.Flag).HasConversion<string>();
        mb.Entity<ConvertedBoolBlog>().OwnsMany(b => b.Posts);
    };

    private IMongoCollection<ConvertedBoolBlog> SeedConvertedBoolBlog(string name, bool flag, params (string Title, int Rank)[] posts)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Title", "blog" },
            // Written directly as the driver's own default bool->string conversion would ("True"/"False"),
            // matching what HasConversion<string>() actually stores — both non-empty, hence truthy, strings.
            { "Flag", flag.ToString() },
            { "Posts", new BsonArray(posts.Select(p => new BsonDocument { { "Title", p.Title }, { "Rank", p.Rank } })) }
        });
        return database.MongoDatabase.GetCollection<ConvertedBoolBlog>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Correlated_Any_over_a_bare_value_converted_outer_bool_declines_instead_of_answering_wrong()
    {
        // b.Flag is FALSE (stored as the non-empty, hence truthy, string "False"). A raw-field
        // $anyElementTrue over $map would truthiness-test that raw string and silently answer TRUE regardless
        // of the actual CLR value — this must decline cleanly (NativeOnly throws) instead.
        var collection = SeedConvertedBoolBlog(
            nameof(Correlated_Any_over_a_bare_value_converted_outer_bool_declines_instead_of_answering_wrong),
            flag: false, ("x", 1));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, ConvertedBoolBlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Posts.Any(p => b.Flag)).ToList());
    }

    [Fact]
    public void Correlated_All_over_a_bare_value_converted_outer_bool_declines_instead_of_answering_wrong()
    {
        var collection = SeedConvertedBoolBlog(
            nameof(Correlated_All_over_a_bare_value_converted_outer_bool_declines_instead_of_answering_wrong),
            flag: false, ("x", 1));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, ConvertedBoolBlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Posts.All(p => b.Flag)).ToList());
    }

    [Fact]
    public void Correlated_Any_over_an_AndAlso_wrapped_value_converted_outer_bool_declines_instead_of_answering_wrong()
    {
        // b.Posts.Any(p => p.Rank > 1 && b.Flag) — the operand-of-&&/|| shape named explicitly in the review
        // finding, exercising MongoAggregationExpressionRenderer.CanRenderLogicalOperand's fixed arm.
        var collection = SeedConvertedBoolBlog(
            nameof(Correlated_Any_over_an_AndAlso_wrapped_value_converted_outer_bool_declines_instead_of_answering_wrong),
            flag: false, ("x", 2));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, ConvertedBoolBlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Posts.Any(p => p.Rank > 1 && b.Flag)).ToList());
    }

    [Fact]
    public void Correlated_filtered_Count_predicate_over_a_bare_value_converted_outer_bool_declines_instead_of_answering_wrong()
    {
        // The $filter-"cond" sibling of the quantifier hazard above: Count(pred) renders the same bare
        // outer-scoped bool inside $filter's "cond", which MongoDB also evaluates by truthiness.
        var collection = SeedConvertedBoolBlog(
            nameof(Correlated_filtered_Count_predicate_over_a_bare_value_converted_outer_bool_declines_instead_of_answering_wrong),
            flag: false, ("x", 1));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, ConvertedBoolBlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.AsNoTracking().Where(b => b.Posts.Count(p => b.Flag) > 0).ToList());
    }

    [Fact]
    public void Correlated_Any_and_All_over_a_missing_or_null_Posts_array_do_not_throw_and_match_the_oracle()
    {
        // Final-review fix (Finding 5): the existing "empty" seed rows elsewhere in this file use a genuine
        // empty BSON array ([]), never a MISSING Posts field or an explicit BsonNull — so the $ifNull wrapper
        // MongoAggregationExpressionRenderer.RenderQuantifier documents as MANDATORY (a $map over a missing or
        // null array is a hard server error, not just a wrong answer) has never actually been exercised by a
        // test. This seeds both a document with NO "Posts" field at all and one with "Posts": null.
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Correlated_Any_and_All_over_a_missing_or_null_Posts_array_do_not_throw_and_match_the_oracle)));
        coll.InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "missing" } }, // no Posts field at all
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", "null" }, { "Posts", BsonNull.Value } }
        ]);
        var collection = database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        // Any over a missing/null array is false (nothing to satisfy it); All is true (vacuously, no
        // counter-example exists) — exactly LINQ's Any/All-over-empty-sequence semantics.
        var anyResults = db.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => p.Title == b.Title))
            .ToList();
        Assert.Empty(anyResults);

        var allResults = db.Entities.AsNoTracking()
            .Where(b => b.Posts.All(p => p.Title == b.Title))
            .Select(b => b.Title).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "missing", "null" }, allResults);
    }
}
