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
/// The projection path normalizes a missing or BSON-null embedded array to an empty collection, as whole-entity
/// materialization does, so <c>Select(b =&gt; b.Posts)</c> never yields null and <c>Select(b =&gt; b.Posts.Count)</c>
/// never throws from <c>Enumerable.Count(null)</c>.
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
        // Nullable so a missing stored field materializes rather than throws.
        public string? Heading { get; set; }
        public List<Comment> Comments { get; set; } = [];
    }

    public class Comment
    {
        public string? Text { get; set; }
    }

    // Like Blog but with a HashSet<T> navigation: an implementation that fabricated a List<T> for the empty case
    // would throw InvalidCastException here.
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

    // Generic over the root type so Blog and SetBlog share one helper.
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

    // A null `posts` means the field is absent; BsonNull.Value means present and explicitly null.
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

    // Titles sort deterministically: empty < missing < null < two.
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
        // The mutation pin: asserts returned data, not an exception type. Reverting the Coalesce in
        // MongoProjectionBindingRemovingExpressionVisitor's CollectionShaperExpression case fails Assert.NotNull
        // for the `missing` and `null` rows.
        var collection = Seed(nameof(Array_projection_normalizes_a_missing_or_null_array_to_an_empty_collection));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title).Select(b => b.Posts).ToList();

        Assert.All(rows, posts => Assert.NotNull(posts));
        Assert.Equal([0, 0, 0, 2], rows.Select(p => p.Count).ToArray());
    }

    [Fact]
    public void Bare_count_projection_returns_zero_for_a_missing_or_null_array()
    {
        // A bare Count over a missing/null array must return 0 rather than throw ArgumentNullException.
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
        // Whole-entity materialization is unaffected by the normalization and agrees with the projection path.
        // Not proof on its own: this fixture's POCOs initialize collections to `[]`, so whole-entity would read
        // empty either way.
        var collection = Seed(nameof(Whole_entity_materialization_is_unchanged));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).ToList();

        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Collection_include_is_unchanged()
    {
        // Control: an owned collection Include reads the same document; a cross-collection $lookup always writes
        // an array. Neither should move.
        var collection = Seed(nameof(Collection_include_is_unchanged));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var blogs = db.Entities.AsNoTracking().OrderBy(b => b.Title).Include(b => b.Posts).ToList();

        Assert.All(blogs, b => Assert.NotNull(b.Posts));
        Assert.Equal([0, 0, 0, 2], blogs.Select(b => b.Posts.Count).ToArray());
    }

    [Fact]
    public void Array_projection_for_a_tracking_query_is_blocked_by_EF_Cores_owned_entity_tracking_rule()
    {
        // Under TrackAll, EF Core's StructuralTypeMaterializerInjector throws OwnedEntitiesCannotBeTrackedWithoutTheirOwner
        // for any owned shaper whose owner isn't also materialized — structurally, for every row. So no tracking
        // query exercises the normalization for a bare projected owned collection; the no-tracking test and
        // Collection_include_is_unchanged cover the rest.
        var collection = Seed(nameof(Array_projection_for_a_tracking_query_is_blocked_by_EF_Cores_owned_entity_tracking_rule));

        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.OrderBy(b => b.Title).Select(b => b.Posts).ToList());
        Assert.Contains("owned entities cannot be tracked without their owner", ex.Message);
    }

    [Fact]
    public void Non_list_collection_navigation_normalizes_through_its_own_accessor()
    {
        // The empty collection comes from the navigation's own IClrCollectionAccessor, not a hand-built List<T>;
        // a HashSet<T> navigation would throw InvalidCastException otherwise.
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
        // A nested owned collection (Post.Comments inside Blog.Posts) normalizes its ragged inner array (present, absent,
        // explicit-null) like the outer one. Both arrays resolve via the bound _projectionBindings branch, not
        // BsonBinding.CreateGetBsonArray (reached only by $lookup-of-$lookup ThenInclude chains, whose arrays are never
        // missing or null, so the Coalesce there is defensive).
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
        // Cross-path agreement: the expected leg materializes whole entities and evaluates the selector client-side; the
        // actual legs run the projection in Native and DriverLinq. Not an independent oracle (this fixture's POCOs
        // initialize collections to `[]`), so don't turn the expected leg into a projection query. The bare
        // Select(b => b.Posts) is used because Select(b => new { b.Title, b.Posts }) throws ArgumentException here: Post's
        // Comments navigation auto-include fails MatchTypes at shaper build (EF-360).
        var collection = Seed(nameof(Projected_collection_equals_the_whole_entity_oracle_for_every_array_state));

        // Both legs order by Title, so comparing Count lists positionally is equivalent to comparing pairs.
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
