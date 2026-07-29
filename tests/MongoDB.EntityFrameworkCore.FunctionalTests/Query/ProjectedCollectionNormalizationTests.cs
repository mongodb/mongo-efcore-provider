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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-358: the PROJECTION path normalizes a missing or explicitly-BSON-null embedded array to an EMPTY
/// collection, matching what whole-entity materialization of the same document already does. It used to
/// materialize null, which made <c>Select(b =&gt; b.Posts)</c> return null for those rows and
/// <c>Select(b =&gt; b.Posts.Count)</c> throw ArgumentNullException from Enumerable.Count(null) — the residual
/// that kept EF-357 only partially closed.
/// </summary>
[XUnitCollection("QueryTests")]
public class ProjectedCollectionNormalizationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<Post> Posts { get; set; } = [];
    }

    public class Post
    {
        // Nullable ON PURPOSE: a missing stored element field must materialize rather than throw, or the
        // ragged-array states cannot be exercised at all. Same reasoning as NativeOwnedCollectionCountTests.Post.
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = [];
    }

    public class Comment
    {
        public string? Text { get; set; }
    }

    // Same shape as Blog, but the collection navigation is a HashSet<T>, not a List<T>. Proves the empty
    // collection PopulateCollection produces comes from the navigation's OWN IClrCollectionAccessor rather
    // than a hand-built List<T> — an implementation that fabricated a List<T> for the empty case would throw
    // InvalidCastException here while every List<T>-based test above stayed green.
    public class SetBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public HashSet<Post> Posts { get; set; } = [];
    }

    private static readonly Action<ModelBuilder> BlogModel = mb =>
        mb.Entity<Blog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    private static readonly Action<ModelBuilder> SetBlogModel = mb =>
        mb.Entity<SetBlog>().OwnsMany(b => b.Posts, p => p.OwnsMany(x => x.Comments));

    // Generic over the root entity type so both Blog (List<Post>) and SetBlog (HashSet<Post>, added below)
    // share one helper instead of two near-identical copies. SingleEntityDbContext.Create<T> is already
    // generic over the collection's document type, so this only needed the model-builder action factored
    // out to a parameter.
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder> model) where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // A null `posts` means the FIELD IS ABSENT; BsonNull.Value means the field is present and explicitly null.
    // Those are the two states whole-entity materialization normalizes and the projection path used not to.
    private static BsonDocument Row(string title, BsonValue? posts)
    {
        var doc = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Title", title } };
        if (posts is not null)
        {
            doc.Add("Posts", posts);
        }

        return doc;
    }

    private static BsonArray PostsOf(params string[] headings)
        => new(headings.Select(h => new BsonDocument
        {
            { "Heading", h }, { "Comments", new BsonArray() }
        }));

    // Titles are chosen so alphabetical order is deterministic and independent of insertion order:
    // empty < missing < null < two.
    private IMongoCollection<Blog> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            Row("two", PostsOf("a", "b")),
            Row("empty", new BsonArray()),
            Row("missing", posts: null),
            Row("null", BsonNull.Value)
        ]);
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection()
    {
        // THE MUTATION PIN for EF-358. It asserts returned DATA, not an exception type, deliberately: an
        // assertion pinning only an exception type usually cannot prove WHICH guard fired, and several
        // teeth-checks on this stack were vacuous for exactly that reason. Revert the Coalesce in
        // MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression case and the
        // Assert.NotNull below fails for the `missing` and `null` rows.
        var collection = Seed(nameof(Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();

        Assert.All(rows, posts => Assert.NotNull(posts));
        Assert.Equal([0, 0, 0, 2], rows.Select(p => p.Count).ToArray());
    }

    [Fact]
    public void Bare_count_projection_returns_zero_for_a_missing_or_null_array()
    {
        // EF-357, now FULLY closed. Owned-data slice 7 removed the translation-time ArgumentException; this
        // removes the materialization-time ArgumentNullException (Enumerable.Count(null)) that kept it partial.
        // The count itself is still folded CLIENT-SIDE here — a bare-scalar projection body never populates
        // Select.Projection, which is the SP3-wide bare-scalar boundary, not anything count-specific — so this
        // is exercising the normalized empty collection, not a server-side $size.
        var collection = Seed(nameof(Bare_count_projection_returns_zero_for_a_missing_or_null_array));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, BlogModel);

            var counts = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();

            Assert.Equal([0, 0, 0, 2], counts);
        }
    }

    [Fact]
    public void Whole_entity_materialization_is_unchanged()
    {
        // NOT a "control that never reached the null-collapse" — that framing was measured false (see
        // Projected_collection_equals_the_whole_entity_oracle_for_every_array_state's comment and the class doc
        // comment above). Pre-fix, whole-entity reached the IDENTICAL null-collapse computation as the
        // projection path; this fixture's Blog/Post classes happen to write `= []` on their collection
        // properties, so THIS fixture's whole-entity leg would have read back empty either way, pre- or
        // post-fix — it was never proof that whole-entity was already correct in general. What this test
        // actually establishes, post-fix: whole-entity materialization is unaffected by the EF-358 edits (still
        // fills collections through IClrCollectionAccessor.Add, so an absent array still contributes no
        // elements) and its agreement with the projection path above is the CROSS-PATH uniformity EF-358
        // delivers, not a pre-existing guarantee this test merely confirms.
        var collection = Seed(nameof(Whole_entity_materialization_is_unchanged));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();

        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Collection_include_is_unchanged()
    {
        // CONTROL. An owned collection Include is read off the same document; a cross-collection $lookup always
        // writes an array. Neither should move.
        var collection = Seed(nameof(Collection_include_is_unchanged));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).Include(b => b.Posts).ToList();

        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Array_projection_for_a_tracking_query_is_blocked_by_EF_Cores_owned_entity_tracking_rule()
    {
        // CORRECTED FROM THE ORIGINAL BRIEF, whose test of this name asserted the SAME normalization as the
        // no-tracking test above, just without AsNoTracking(). That cannot pass, for a reason unrelated to
        // EF-358: verified by decompiling Microsoft.EntityFrameworkCore.dll,
        // ShapedQueryCompilingExpressionVisitor.StructuralTypeMaterializerInjector.Inject walks the WHOLE shaper
        // tree and — for QueryTrackingBehavior.TrackAll only — throws CoreStrings.OwnedEntitiesCannotBeTrackedWithoutTheirOwner
        // for ANY visited owned StructuralTypeShaperExpression whose owner type was not ALSO visited. That check
        // is purely structural (which entity types the shaper tree materializes), not data-dependent, so
        // Select(b => b.Posts) under tracking hits it for EVERY row regardless of whether Posts is populated,
        // empty, missing, or null — identically before and after this ticket's production edit. There is no
        // tracking-query shape that exercises the EF-358 normalization for a BARE projected owned collection;
        // Array_projection_normalizes_for_a_tracking_query (no-tracking) and Collection_include_is_unchanged
        // (whole-entity Include, which DOES carry the owner) are the coverage for tracked/untracked and
        // Include, respectively.
        var collection = Seed(nameof(Array_projection_for_a_tracking_query_is_blocked_by_EF_Cores_owned_entity_tracking_rule));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.OrderBy(b => b.Title).Select(b => b.Posts).ToList());
        Assert.Contains("owned entities cannot be tracked without their owner", ex.Message);
    }

    [Fact]
    public void Non_list_collection_navigation_normalizes_through_its_own_accessor()
    {
        // The empty collection is built by PopulateCollection through the navigation's OWN
        // IClrCollectionAccessor, NOT hand-constructed. A HashSet<T> navigation is the cheapest proof of that:
        // an implementation that fabricated a List<T> for the empty case would throw InvalidCastException here
        // while every List<T>-based test above stayed green.
        var name = nameof(Non_list_collection_navigation_normalizes_through_its_own_accessor);
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany([
            Row("two", PostsOf("a", "b")),
            Row("empty", new BsonArray()),
            Row("missing", posts: null),
            Row("null", BsonNull.Value)
        ]);
        var collection = database.MongoDatabase
            .GetCollection<SetBlog>(raw.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.Native, SetBlogModel);

        var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();

        Assert.All(rows, posts => Assert.NotNull(posts));
        Assert.All(rows, posts => Assert.IsType<HashSet<Post>>(posts));
        Assert.Equal([0, 0, 0, 2], rows.Select(p => p.Count).ToArray());
    }

    [Fact]
    public void Nested_owned_collection_normalizes_a_ragged_inner_array()
    {
        // CORRECTED CLAIM (review finding, RE-MEASURED after a second review round contested it). This test's
        // original comment claimed the inner (Comments) array is read via BsonBinding.CreateGetBsonArray — the
        // parent-element else-branch in MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression
        // case — "a different source than the bound _projectionBindings variable the outer array uses", and that
        // this test proves that branch is covered. A first round of instrumentation disproved that. A second
        // reviewer then traced BsonDocumentInjectingExpressionVisitor's CollectionShaperExpression case and argued
        // the OPPOSITE — that it embeds a nested CollectionShaperExpression's InnerShaper unvisited, so nothing
        // pre-registers a bsonArray variable for Comments and the else-branch should fire after all — naming
        // reference-identity reuse of an already-registered key as the one gap that inspection alone could not
        // close. RE-MEASURED directly to settle it: both branches were instrumented with Console.Error.WriteLine
        // printing the branch and objectArrayProjection.Name, and the test was re-run with detailed logging. RAW
        // OUTPUT (two lines, in order, both runs, including a fresh run of the single-level
        // Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection test as a same-process
        // baseline):
        //   EF358-INSTRUMENT: IF-BRANCH (bound _projectionBindings variable) name=Posts
        //   EF358-INSTRUMENT: IF-BRANCH (bound _projectionBindings variable) name=Comments
        // VERDICT: the FIRST account was correct, the second reviewer's counter-hypothesis is refuted by direct
        // measurement. Both the outer (Posts) and inner (Comments) arrays resolve via the SAME if-branch — the
        // bound _projectionBindings variable — not CreateGetBsonArray, for this test AND for the plain
        // single-level projection test, which also has a Comments sub-shaper (Post owns Comments) despite never
        // naming it in the LINQ. Adding an explicit .Include(b => b.Posts).ThenInclude(p => p.Comments) on this
        // same owned model does not change that (checked in the same review round).
        //
        // So this test does NOT reach the CreateGetBsonArray branch, and does not prove it is covered. What it
        // DOES establish, and is kept for: a nested OWNED collection (a collection-of-collections, Post.Comments
        // inside Blog.Posts) normalizes its ragged inner array — present / absent / explicit-null — the same
        // way the outer array does, via the bound-variable branch's shared Coalesce.
        //
        // Where CreateGetBsonArray actually fires, and whether it can see a null/missing array (determined by
        // inspection, not measurement, per instruction — this is not a probe worth a container boot): the
        // else-branch's own comment describes "ThenInclude on collection-then-collection", and the shape that
        // matches it is a REFERENCE (cross-collection) $lookup-of-$lookup ThenInclude chain — e.g.
        // CrossCollectionRelationshipTests.Include_multi_level_then_include_does_not_duplicate
        // (Customer -> Orders -> OrderItems, three separate collections joined by two $lookups) — not an owned
        // chain. A $lookup stage always writes its target field as an array (empty when no document matches;
        // never absent, never explicit BSON null), so on the current call graph CreateGetBsonArray's argument
        // is never missing or null. The Coalesce on that branch is therefore DEFENSIVE, not load-bearing, as
        // things stand: no known caller can hand it a null. The "one line covers both array sources" framing
        // in the production comment is accurate as a statement about the CODE SHAPE (one Coalesce sits where
        // both branches' outputs converge), but its implied BENEFIT — that both sources need the null-guard —
        // is unproven for the second source and should not be read as a tested property.
        var name = nameof(Nested_owned_collection_normalizes_a_ragged_inner_array);
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));

        // One row, three posts: inner Comments present / absent / explicitly null.
        var posts = new BsonArray
        {
            new BsonDocument { { "Heading", "has" }, { "Comments", new BsonArray { new BsonDocument { { "Text", "c" } } } } },
            new BsonDocument { { "Heading", "absent" } },
            new BsonDocument { { "Heading", "null" }, { "Comments", BsonNull.Value } }
        };
        raw.InsertOne(Row("row", posts));
        var collection = database.MongoDatabase.GetCollection<Blog>(raw.CollectionNamespace.CollectionName);

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var projected = db.Entities.AsNoTracking().Select(b => b.Posts).ToList().Single();
        var byHeading = projected.OrderBy(p => p.Heading).ToList();

        Assert.All(byHeading, p => Assert.NotNull(p.Comments));
        Assert.Equal(new string?[] { "absent", "has", "null" }, byHeading.Select(p => p.Heading).ToArray());
        Assert.Equal([0, 1, 0], byHeading.Select(p => p.Comments.Count).ToArray());
    }

    [Fact]
    public void Projected_collection_equals_the_whole_entity_oracle_for_every_array_state()
    {
        // Cross-path agreement check, in the pattern owned-data slice 7 established for
        // Count_projection_equals_the_in_memory_oracle_for_every_array_length_and_state: the expected leg
        // materializes WHOLE ENTITIES and evaluates the same selector client-side, and the actual legs run the
        // equivalent projection query in both Native and DriverLinq mode.
        //
        // This is NOT an independent oracle: pre-EF-358 the whole-entity leg only produced empty collections
        // because this fixture's Post/Blog POCOs happen to write `= []` on their collection properties, not
        // because whole-entity materialization is immune to the bug class. Post-fix, normalization is uniform
        // across paths (see the class doc comment and Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection's
        // comment), so what this test genuinely establishes is CROSS-PATH AGREEMENT — whole-entity materialization,
        // array projection, and count all answering identically on the same documents. Do not "simplify" the
        // expected leg into a projection query: that would be asserting the fix against itself.
        //
        // The actual leg uses the BARE Select(b => b.Posts) shape, not Select(b => new { b.Title, b.Posts }):
        // that anonymous-projection shape with an entity-collection leaf is an ArgumentException in every mode
        // and every array state, independently of EF-358 (confirmed by direct probe, not assumed).
        var collection = Seed(nameof(Projected_collection_equals_the_whole_entity_oracle_for_every_array_state));

        // Both legs order by Title first, so comparing the two Count lists positionally is equivalent to
        // comparing (Title, Count) pairs without needing a Title leaf alongside the projected collection.
        List<int> expected;
        using (var db = CreateContext(collection, MongoQueryMode.Native, BlogModel))
        {
            expected = db.Entities.AsNoTracking().ToList()
                .OrderBy(b => b.Title).Select(b => b.Posts.Count).ToList();
        }

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            List<int> actual;
            using (var db = CreateContext(collection, mode, BlogModel))
            {
                actual = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                    .Select(b => b.Posts).ToList()
                    .Select(posts => posts.Count).ToList();
            }

            Assert.Equal(expected, actual);
        }
    }
}
