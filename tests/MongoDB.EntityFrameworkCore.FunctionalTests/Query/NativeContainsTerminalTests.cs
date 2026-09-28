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
/// <c>Queryable.Contains</c> as a terminal operator — over a bare scalar-field Select
/// (<c>Select(c =&gt; c.CustomerID).Contains("ALFKI")</c>, EF's <c>Contains_top_level</c>) and over a whole-entity
/// source (<c>Where(...).Contains(order)</c> / <c>.Contains(null)</c>) — bound natively as
/// <c>Any(x =&gt; x == item)</c>.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeContainsTerminalTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Value { get; set; }
        public List<string?> Tags { get; set; } = [];
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(
        [
            new Row { Id = ObjectId.GenerateNewId(), Name = "a", Value = 1 },
            new Row { Id = ObjectId.GenerateNewId(), Name = "b", Value = 2 },
            new Row { Id = ObjectId.GenerateNewId(), Name = "c", Value = 3 },
        ]);
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    private List<bool> Run(string name, Func<SingleEntityDbContext<Row>, bool> query)
    {
        var collection = Seed(name);
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return new List<bool> { query(db) };
        });
    }

    [Fact]
    public void Scalar_select_contains_constant_goes_native()
    {
        Assert.Equal([true], Run(nameof(Scalar_select_contains_constant_goes_native) + "T",
            db => db.Entities.Select(r => r.Name).Contains("b")));
        Assert.Equal([false], Run(nameof(Scalar_select_contains_constant_goes_native) + "F",
            db => db.Entities.Select(r => r.Name).Contains("zzz")));
    }

    [Fact]
    public void Scalar_select_contains_parameter_goes_native()
    {
        var wanted = "c";
        Assert.Equal([true], Run(nameof(Scalar_select_contains_parameter_goes_native),
            db => db.Entities.Select(r => r.Name).Contains(wanted)));
    }

    [Fact]
    public void Take_before_Contains_only_tests_the_paged_rows()
        => Assert.Equal([false], Run(nameof(Take_before_Contains_only_tests_the_paged_rows),
            db => db.Entities.OrderBy(r => r.Value).Select(r => r.Name).Take(1).Contains("b")));

    [Fact]
    public void Entity_contains_goes_native()
    {
        var collection = Seed(nameof(Entity_contains_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            var b = db.Entities.Single(r => r.Name == "b");
            return new List<bool> { db.Entities.Where(r => r.Value >= 2).Contains(b), db.Entities.Where(r => r.Value >= 3).Contains(b) };
        });
        Assert.Equal([true, false], result);
    }

    [Fact]
    public void Entity_contains_null_is_false_natively()
        => Assert.Equal([false], Run(nameof(Entity_contains_null_is_false_natively),
            db => db.Entities.Where(r => r.Value > 0).Contains(null!)));

    // A computed projection has no single backing field to match against: decline, don't guess.
    [Fact]
    public void Contains_over_computed_projection_declines_cleanly()
    {
        var collection = Seed(nameof(Contains_over_computed_projection_declines_cleanly));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return new List<bool> { db.Entities.Select(r => r.Name + "!").Contains("b!") };
        });
        Assert.Equal([true], result);
    }

    // Pre-existing latent bug surfaced by Contains(null): an entity-equality comparand that is a query PARAMETER
    // whose runtime value is null used to hand null to the key serializer (NullReferenceException at Build).
    [Fact]
    public void Where_entity_equals_null_local_returns_no_rows_natively()
    {
        var collection = Seed(nameof(Where_entity_equals_null_local_returns_no_rows_natively));
        Row? none = null;
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.Where(r => r == none).Select(r => r.Name).ToList();
        });
        Assert.Empty(result);
    }

    public class Node
    {
        public ObjectId Id { get; set; }
        public ObjectId? ParentId { get; set; }
        public Node? Parent { get; set; }
    }

    // Final review C1: an entity Contains over a projection THROUGH a (self-referencing) navigation must not be
    // translated against the root entity — `Select(n => n.Parent).Contains(leaf)` asks "is leaf anyone's parent?".
    [Fact]
    public void Entity_contains_over_navigation_projection_is_not_matched_against_the_root()
    {
        var collection = database.MongoDatabase.GetCollection<Node>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Entity_contains_over_navigation_projection_is_not_matched_against_the_root))
            + Guid.NewGuid().ToString("N")[..8]);
        var root = new Node { Id = ObjectId.GenerateNewId() };
        var leaf = new Node { Id = ObjectId.GenerateNewId(), ParentId = root.Id };
        collection.InsertMany([root, leaf]);

        using var db = SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
        });
        var leafEntity = db.Entities.Single(n => n.Id == leaf.Id);

        bool? answer = null;
        var ex = Record.Exception(() => answer = db.Entities.Select(n => n.Parent).Contains(leafEntity));
        // Either decline cleanly, or answer as LINQ does (leaf is nobody's parent).
        if (ex is null)
            Assert.False(answer);
        else
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
    }

    // A terminal Contains over an ARRAY-typed field: LINQ compares the item to each whole array (reference equality
    // for a List), whereas a query-dialect {Tags: value} match also matches element-wise — a document whose Tags
    // CONTAINS a null would satisfy `Contains(null)`. Such a source must not be matched in the query dialect.
    [Fact]
    public void Contains_null_over_array_field_projection_does_not_match_arrays_containing_null()
    {
        var collection = Seed(nameof(Contains_null_over_array_field_projection_does_not_match_arrays_containing_null));
        collection.InsertOne(new Row { Id = ObjectId.GenerateNewId(), Name = "d", Value = 4, Tags = ["x", null] });
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        bool? answer = null;
        var ex = Record.Exception(() => answer = db.Entities.Select(r => r.Tags).Contains(null));
        if (ex is null)
            Assert.False(answer); // no row's Tags array IS null
        else
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
    }
}
