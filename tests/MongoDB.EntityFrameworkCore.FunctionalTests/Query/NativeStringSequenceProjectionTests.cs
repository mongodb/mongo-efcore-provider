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
/// `e.City.AsEnumerable()`/`.ToList()`/`.ToArray()` — treating a string as its own <c>IEnumerable&lt;char&gt;</c>
/// (EF's own AsEnumerable_over_string/ToList_over_string/ToArray_over_string conformance shapes) — used to
/// decline the WHOLE projecting Select outright. It needs no server-side computation: the wrapping call only
/// changes the leaf's CLR materialization, never the stored value, so
/// <c>NativeProjectionBinder.TryTranslateLeaf</c> now admits it as a bare field leaf (pushing down only the raw
/// string) and the read side re-applies the original .NET call to the raw value it reads back — see
/// <c>MongoProjectionBindingExpressionVisitor.Visit</c> and
/// <c>MongoProjectionBindingRemovingExpressionVisitor</c>.
/// </summary>
/// <remarks>
/// EVERY shape here is exercised in all three <see cref="MongoQueryMode"/>s deliberately, not just
/// <see cref="MongoQueryMode.NativeOnly"/>. Going native for this leaf means the whole
/// <see cref="System.Linq.Enumerable"/>-over-string call is erased from the shaper (registered as ONE
/// projection member), which is exactly what <c>ProjectionAnalyzer.UntranslatableProjectionFinder</c> — the
/// EF-250/EF-231 guard that keeps this shape AWAY from the driver's own LINQ v3 push-down — looks for. So
/// <see cref="MongoQueryMode.DriverLinq"/> and the mixed shaper are the legs that regress if the native leaf
/// forgets to announce itself (<c>MongoSelectDefinition.HasStringSequenceProjectionLeaf</c>); the
/// <see cref="MongoQueryMode.NativeOnly"/> leg alone cannot see that class of break at all.
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
    // An owned-array or owned-reference-nav leaf in the same projection is entity/collection-typed, so
    // ProjectionAnalyzer.CanPushDown refuses to hand the query to the driver's LINQ v3 provider and the
    // MIXED client-side shaper (MongoMixedProjectionBindingRemovingExpressionVisitor) reads WHOLE, un-projected
    // documents instead. That visitor sees the RAW Enumerable.*-over-string MethodCallExpression this leaf
    // registers, which its ordinary field-access path cannot resolve — so it needs the same rebuild-around-a-
    // raw-read handling the native read side does, just against a whole document instead of a $project alias.
    // Without it the leaf reads as `Document element 'Property' is missing but required` (aliased arm) or hands
    // the shaper a whole BsonDocument where a char sequence was expected (bare/null-alias arm).

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

    // An OWNED-NESTED string field (`b.Address.City`) — the leaf translates to a DOTTED MongoFieldExpression,
    // which TryTranslateLeaf still admits because the property is default-serialized (only a dotted AND
    // non-default-serialized field declines). Both read sides therefore have to cope with it: the native one
    // reads the $project alias, the mixed one resolves the owned sub-document itself.
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
    // The raw read this leaf rebuilds the call around goes through the source IProperty (not a bare element
    // read), so a value converter on that property must still apply: the stored form is "X<city>", the
    // materialized char sequence must be "<city>".

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

        // Seed through a BsonDocument handle, NOT IMongoCollection<ConvRow>.InsertMany: the latter uses the
        // DRIVER's POCO serializer, which knows nothing about EF's value converter, and would store the
        // UNPREFIXED city — the very stored shape this test needs to distinguish itself from.
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
