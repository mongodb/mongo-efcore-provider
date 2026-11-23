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
/// <c>d.Dict["key"]</c> compared with a value and <c>d.Dict.ContainsKey("key")</c> over a string-keyed dictionary
/// property: a dictionary is stored as a BSON document, so an entry is the sub-field <c>Dict.key</c>
/// (<c>MongoDictionaryEntryExpression</c>). Seeded ragged: key present, key absent, explicit null value, empty
/// dictionary, and no dictionary element at all.
/// <para>
/// Whole-dictionary equality compares a document, so a parameter dictionary whose first key is an operator
/// (<c>{ "$ne": "zzz" }</c>) must be compared as a value, never read as an operator that matches every row. No stored
/// document equals the operator-shaped parameters used here.
/// </para>
/// </summary>
public class NativeDictionaryTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Dictionary<string, int> Counts { get; set; } = [];
        public Dictionary<string, string?> Labels { get; set; } = [];
    }

    // A separate root for the owned-element case: an owned navigation on Row would make ExecuteDelete/ExecuteUpdate refuse.
    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Item> Items { get; set; } = [];
    }

    public class Item
    {
        public string Sku { get; set; } = "";
        public Dictionary<string, string> Attrs { get; set; } = [];
    }

    private static readonly Dictionary<string, string?> NeZzz = new() { ["$ne"] = "zzz" };

    [Theory]
    [InlineData("contains_key", new[] { "both", "only_a" })]
    [InlineData("not_contains_key", new[] { "empty", "none", "only_b" })]
    [InlineData("contains_key_or", new[] { "both", "only_a", "only_b" })]
    [InlineData("count_eq", new[] { "both" })]
    [InlineData("count_eq_parameter", new[] { "both" })]
    [InlineData("count_ge", new[] { "both", "only_a" })]
    [InlineData("count_range", new[] { "both" })]
    [InlineData("count_mirrored", new[] { "both" })]
    [InlineData("label_eq", new[] { "both" })]
    [InlineData("label_ne_null", new[] { "both" })]
    [InlineData("label_eq_null", new[] { "empty", "none", "only_a", "only_b" })]
    public void Dictionary_entry_goes_native_and_matches_the_driver(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var ten = 10;
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "contains_key" => q.Where(x => x.Counts.ContainsKey("a")),
                    "not_contains_key" => q.Where(x => !x.Counts.ContainsKey("a")),
                    "contains_key_or" => q.Where(x => x.Counts.ContainsKey("a") || x.Counts.ContainsKey("b")),
                    "count_eq" => q.Where(x => x.Counts["a"] == 10),
                    "count_eq_parameter" => q.Where(x => x.Counts["a"] == ten),
                    "count_ge" => q.Where(x => x.Counts["a"] >= 5),
                    "count_range" => q.Where(x => x.Counts["a"] >= 5 && x.Counts["b"] < 100),
                    "count_mirrored" => q.Where(x => 10 == x.Counts["a"]),
                    "label_eq" => q.Where(x => x.Labels["l"] == "red"),
                    "label_ne_null" => q.Where(x => x.Labels["l"] != null),
                    _ => q.Where(x => x.Labels["l"] == null)
                };
                return q.OrderBy(x => x.Name).Select(x => x.Name).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void A_key_a_dotted_path_cannot_address_declines()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);

        // "a.b" would be read as the nested path a -> b.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(x => x.Counts.ContainsKey("a.b")).Select(x => x.Name).ToList());
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(x => x.Counts["$x"] == 1).Select(x => x.Name).ToList());
    }

    [Fact]
    public void A_parameterized_key_declines()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        var key = "a";

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(x => x.Counts.ContainsKey(key)).Select(x => x.Name).ToList());
    }

    [Theory]
    [InlineData("eq_plain", new[] { "both" })]
    [InlineData("eq_operator_shaped", new string[0])]
    [InlineData("ne_operator_shaped", new[] { "both", "empty", "none", "only_a", "only_b" })]
    public void Whole_dictionary_equality_with_operator_shaped_parameter_compares_as_a_value(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var red = new Dictionary<string, string?> { ["l"] = "red" };
                var ne = NeZzz;
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "eq_plain" => q.Where(x => x.Labels == red),
                    "eq_operator_shaped" => q.Where(x => x.Labels == ne),
                    _ => q.Where(x => x.Labels != ne)
                };
                return q.OrderBy(x => x.Name).Select(x => x.Name).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void Terminal_operators_with_operator_shaped_dictionary_parameter_match_nothing()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var ne = NeZzz;
                return
                [
                    db.Entities.Count(x => x.Labels == ne).ToString(),
                    db.Entities.Any(x => x.Labels == ne).ToString(),
                    db.Entities.Where(x => x.Labels == ne).Select(x => x.Name).FirstOrDefault() ?? "<none>"
                ];
            },
            ["0", "False", "<none>"]);
    }

    [Fact]
    public void Contains_over_operator_shaped_dictionaries_never_matches()
    {
        var collection = Seed();
        var list = new List<Dictionary<string, string?>> { NeZzz };
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(collection, mode);
            try
            {
                Assert.Empty(db.Entities.Where(x => list.Contains(x.Labels)).Select(x => x.Name).ToList());
            }
            catch (MongoCommandException)
            {
                // The server rejects an operator-shaped document inside $in ("cannot nest $ under $in"): loud, not wrong.
            }
        }
    }

    [Fact]
    public void Owned_element_dictionary_equality_with_operator_shaped_parameter_matches_nothing()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("dictOwned") + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Owner>(name);
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "red" },
                { "Items", new BsonArray { new BsonDocument { { "Sku", "s1" }, { "Attrs", new BsonDocument("l", "red") } } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "empty" },
                { "Items", new BsonArray { new BsonDocument { { "Sku", "s2" }, { "Attrs", new BsonDocument() } } } }
            }
        ]);

        var red = new Dictionary<string, string> { ["l"] = "red" };
        var ne = new Dictionary<string, string> { ["$ne"] = "zzz" };
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = SingleEntityDbContext.Create(
                    collection,
                    modelBuilderAction: mb => mb.Entity<Owner>().OwnsMany(o => o.Items),
                    optionsBuilderAction: b =>
                    {
                        b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                        new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                    });
                return
                [
                    string.Join(",", db.Entities.Where(o => o.Items.Any(i => i.Attrs == red)).Select(o => o.Name).ToList()),
                    string.Join(",", db.Entities.Where(o => o.Items.Any(i => i.Attrs == ne)).Select(o => o.Name).ToList())
                ];
            },
            ["red", ""]);
    }

#if !EF8
    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Bulk_writes_filtered_by_operator_shaped_dictionary_parameter_touch_nothing(MongoQueryMode mode)
    {
        var ne = NeZzz;

        var collection = Seed();
        using (var db = Create(collection, mode))
        {
            db.Entities.Where(x => x.Labels == ne).ExecuteDelete();
            db.Entities.Where(x => x.Labels == ne).OrderBy(x => x.Name).Take(100).ExecuteDelete();
            db.Entities.Where(x => x.Labels == ne).OrderBy(x => x.Name).Take(100)
                .ExecuteUpdate(s => s.SetProperty(x => x.Name, "updated"));
        }

        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        Assert.Equal(5, raw.CountDocuments(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0, raw.CountDocuments(new BsonDocument("Name", "updated")));
    }
#endif

    private static SingleEntityDbContext<Row> Create(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Row> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("dict") + Guid.NewGuid().ToString("N")[..8];
        var typed = database.MongoDatabase.GetCollection<Row>(name);
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "both" },
                { "Counts", new BsonDocument { { "a", 10 }, { "b", 20 } } }, { "Labels", new BsonDocument { { "l", "red" } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "only_a" },
                { "Counts", new BsonDocument { { "a", 7 } } }, { "Labels", new BsonDocument { { "l", BsonNull.Value } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "only_b" },
                { "Counts", new BsonDocument { { "b", 1 } } }, { "Labels", new BsonDocument() }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "empty" },
                { "Counts", new BsonDocument() }, { "Labels", new BsonDocument() }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "none" } }
        ]);
        return typed;
    }
}
