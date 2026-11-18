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
