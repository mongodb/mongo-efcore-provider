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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// `e.City.AsEnumerable()`/`.ToList()`/`.ToArray()` — a string treated as <c>IEnumerable&lt;char&gt;</c>. The call
/// only changes CLR materialization, so <c>NativeProjectionBinder.TryTranslateLeaf</c> admits it as a bare field
/// leaf (pushing down the raw string) and the read side re-applies the call — see
/// <c>MongoProjectionBindingExpressionVisitor.Visit</c> and <c>MongoProjectionBindingRemovingExpressionVisitor</c>.
/// </summary>
/// <remarks>
/// Every shape runs in all three <see cref="MongoQueryMode"/>s. The native leaf erases the
/// <see cref="System.Linq.Enumerable"/> call from the shaper, which is what
/// <c>ProjectionAnalyzer.UntranslatableProjectionFinder</c> looks for to keep this shape away from the driver's
/// LINQ push-down. If the leaf forgets to announce itself
/// (<c>MongoSelectDefinition.HasStringSequenceProjectionLeaf</c>), only the <see cref="MongoQueryMode.DriverLinq"/>
/// and mixed-shaper legs regress.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeStringSequenceProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string City { get; set; } = "";
    }

    private static readonly (string Label, string City)[] Rows =
    [
        ("a", "London"),
        ("b", "Berlin")
    ];

    private static readonly MongoQueryMode[] AllModes =
        [MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq];

    [Fact]
    public void Wrapped_AsEnumerable_over_string_projection_goes_native()
    {
        var collection = Seed(nameof(Wrapped_AsEnumerable_over_string_projection_goes_native));

        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, mode);
            var result = db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { x.Label, Property = x.City.AsEnumerable() }).ToList();

            Assert.Equal(
                new[] { ("a", "London"), ("b", "Berlin") },
                result.Select(r => (r.Label, new string(r.Property.ToArray()))));
        }
    }

    [Fact]
    public void Wrapped_ToList_over_string_projection_goes_native()
    {
        var collection = Seed(nameof(Wrapped_ToList_over_string_projection_goes_native));

        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, mode);
            var result = db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { x.Label, Property = x.City.ToList() }).ToList();

            Assert.Equal(
                new[] { ("a", "London"), ("b", "Berlin") },
                result.Select(r => (r.Label, new string(r.Property.ToArray()))));
        }
    }

    [Fact]
    public void Wrapped_ToArray_over_string_projection_goes_native()
    {
        var collection = Seed(nameof(Wrapped_ToArray_over_string_projection_goes_native));

        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, mode);
            var result = db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { x.Label, Property = x.City.ToArray() }).ToList();

            Assert.Equal(
                new[] { ("a", "London"), ("b", "Berlin") },
                result.Select(r => (r.Label, new string(r.Property))));
        }
    }

    [Fact]
    public void Bare_AsEnumerable_over_string_projection_goes_native()
    {
        var collection = Seed(nameof(Bare_AsEnumerable_over_string_projection_goes_native));

        foreach (var mode in AllModes)
        {
            using var db = CreateContext(collection, mode);
            var result = db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => x.City.AsEnumerable()).ToList();

            Assert.Equal(new[] { "London", "Berlin" }, result.Select(r => new string(r.ToArray())));
        }
    }

    // ── The mixed shaper ──────────────────────────────────────────────────────────
    //
    // An owned-array or owned-reference leaf alongside makes ProjectionAnalyzer.CanPushDown refuse the driver's
    // LINQ provider, so MongoMixedProjectionBindingRemovingExpressionVisitor reads whole documents and sees the raw
    // Enumerable.*-over-string call. It needs the same rebuild-around-a-raw-read as the native read side;
    // without it the leaf fails with "Document element 'Property' is missing but required" (aliased arm) or gets
    // a whole BsonDocument (bare/null-alias arm).

    public class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public string City { get; set; } = "";
        public Address Address { get; set; } = null!;
        public List<Post> Posts { get; set; } = null!;
    }

    public class Address
    {
        public string City { get; set; } = "";
    }

    public class Post
    {
        public int PostId { get; set; }
        public string Text { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>(b =>
    {
        b.OwnsOne(x => x.Address);
        b.OwnsMany(x => x.Posts, p => p.HasKey(y => y.PostId));
    });

    [Fact]
    public void String_sequence_leaf_beside_an_owned_array_leaf_reads_correctly()
    {
        var collection = SeedBlogs(nameof(String_sequence_leaf_beside_an_owned_array_leaf_reads_correctly));

        foreach (var mode in AllModes)
        {
            using var db = CreateBlogContext(collection, mode);
            var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new { Property = b.City.AsEnumerable(), b.Posts }).ToList();

            Assert.Equal(["London", "Berlin"], rows.Select(r => new string(r.Property.ToArray())));
            Assert.Equal(["p1", "p2"], rows[0].Posts.Select(p => p.Text));
            Assert.Equal(["q1"], rows[1].Posts.Select(p => p.Text));
        }
    }

    [Fact]
    public void String_sequence_leaf_beside_an_owned_reference_nav_leaf_reads_correctly()
    {
        var collection = SeedBlogs(nameof(String_sequence_leaf_beside_an_owned_reference_nav_leaf_reads_correctly));

        foreach (var mode in AllModes)
        {
            using var db = CreateBlogContext(collection, mode);
            var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new { Property = b.City.ToList(), b.Address }).ToList();

            Assert.Equal(["London", "Berlin"], rows.Select(r => new string(r.Property.ToArray())));
            Assert.Equal(["Ealing", "Mitte"], rows.Select(r => r.Address.City));
        }
    }

    // An owned-nested string (`b.Address.City`) translates to a dotted MongoFieldExpression, which TryTranslateLeaf
    // admits because it is default-serialized. The native side reads the $project alias; the mixed side resolves
    // the owned sub-document.
    [Fact]
    public void Owned_nested_string_field_sequence_reads_correctly()
    {
        var collection = SeedBlogs(nameof(Owned_nested_string_field_sequence_reads_correctly));

        foreach (var mode in AllModes)
        {
            using var db = CreateBlogContext(collection, mode);
            var rows = db.Entities.AsNoTracking().OrderBy(b => b.Title)
                .Select(b => new { b.Title, Property = b.Address.City.AsEnumerable() }).ToList();

            Assert.Equal(["a", "b"], rows.Select(r => r.Title));
            Assert.Equal(["Ealing", "Mitte"], rows.Select(r => new string(r.Property.ToArray())));
        }
    }

    // ── A VALUE-CONVERTED string property ────────────────────────────────────────
    //
    // The raw read goes through the source IProperty, so its converter must apply: stored "X<city>" must
    // materialize as "<city>".

    public class ConvRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string City { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> ConvRowModel = mb =>
        mb.Entity<ConvRow>().Property(e => e.City).HasConversion(v => "X" + v, v => v.Substring(1));

    [Fact]
    public void Value_converted_string_sequence_reads_correctly()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Value_converted_string_sequence_reads_correctly));

        // Seed via BsonDocument: the driver's POCO serializer ignores EF's value converter and would store the
        // unprefixed city.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "a" }, { "City", "XLondon" } },
            new BsonDocument { { "Label", "b" }, { "City", "XBerlin" } }
        ]);

        var collection = database.MongoDatabase.GetCollection<ConvRow>(name);

        foreach (var mode in AllModes)
        {
            using var db = SingleEntityDbContext.Create(
                collection,
                modelBuilderAction: ConvRowModel,
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                });

            var rows = db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { x.Label, Property = x.City.ToList() }).ToList();

            Assert.Equal(["a", "b"], rows.Select(r => r.Label));
            Assert.Equal(["London", "Berlin"], rows.Select(r => new string(r.Property.ToArray())));
        }
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.CreateCollection<Row>(name);
        collection.InsertMany(Rows.Select(r => new Row { Label = r.Label, City = r.City }));
        return collection;
    }

    private IMongoCollection<Blog> SeedBlogs(string name)
    {
        var collection = database.CreateCollection<Blog>(name);
        collection.InsertMany(
        [
            new Blog
            {
                Title = "a",
                City = "London",
                Address = new Address { City = "Ealing" },
                Posts = [new Post { PostId = 1, Text = "p1" }, new Post { PostId = 2, Text = "p2" }]
            },
            new Blog
            {
                Title = "b",
                City = "Berlin",
                Address = new Address { City = "Mitte" },
                Posts = [new Post { PostId = 3, Text = "q1" }]
            }
        ]);
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<Blog> CreateBlogContext(IMongoCollection<Blog> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BlogModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
