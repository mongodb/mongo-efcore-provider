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
/// <c>Any()</c>, <c>Count()</c>, <c>Count</c> and <c>Length</c> over a primitive-collection property (<c>string[]</c>,
/// <c>List&lt;int&gt;</c>) reuse the owned-collection size forms (<c>"path.0": {$exists: true}</c>, null-safe
/// <c>$size</c>). Seeded ragged: empty, missing, explicit null, one and three elements must all agree with LINQ.
/// </summary>
public class NativePrimitiveCollectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class TagRow
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string[] Tags { get; set; } = [];
        public List<int> Nums { get; set; } = [];
    }

    [Theory]
    [InlineData("tags_any", new[] { "one", "three" })]
    [InlineData("tags_not_any", new[] { "empty", "missing", "null" })]
    [InlineData("nums_any", new[] { "one", "three" })]
    [InlineData("tags_count_call_eq_3", new[] { "three" })]
    [InlineData("tags_length_eq_0", new[] { "empty", "missing", "null" })]
    [InlineData("tags_length_gt_0", new[] { "one", "three" })]
    [InlineData("tags_length_ne_1", new[] { "empty", "missing", "null", "three" })]
    [InlineData("nums_count_eq_1", new[] { "one" })]
    [InlineData("nums_count_gt_1", new[] { "three" })]
    [InlineData("tags_length_eq_parameter", new[] { "three" })]
    [InlineData("tags_any_and_contains", new[] { "one", "three" })]
    [InlineData("string_length_control", new[] { "one" })]
    public void Primitive_collection_size_goes_native_and_matches_linq(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var n = 3;
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "tags_any" => q.Where(x => x.Tags.Any()),
                    "tags_not_any" => q.Where(x => !x.Tags.Any()),
                    "nums_any" => q.Where(x => x.Nums.Any()),
                    "tags_count_call_eq_3" => q.Where(x => x.Tags.Count() == 3),
                    "tags_length_eq_0" => q.Where(x => x.Tags.Length == 0),
                    "tags_length_gt_0" => q.Where(x => x.Tags.Length > 0),
                    "tags_length_ne_1" => q.Where(x => x.Tags.Length != 1),
                    "nums_count_eq_1" => q.Where(x => x.Nums.Count == 1),
                    "nums_count_gt_1" => q.Where(x => x.Nums.Count > 1),
                    "tags_length_eq_parameter" => q.Where(x => x.Tags.Length == n),
                    "tags_any_and_contains" => q.Where(x => x.Tags.Any() && x.Tags.Contains("a")),
                    // string.Length must not be mistaken for an array length.
                    _ => q.Where(x => x.Name.Length == 3)
                };
                return q.OrderBy(x => x.Name).Select(x => x.Name).ToList();
            },
            [.. expected],
            // Driver-LINQ renders Length == 0 as { Tags: { $size: 0 } }, which skips the missing and null rows that a
            // materialized (empty) array and its own != 1 / !Any() forms include. Wrong on main.
            driverKnownWrong: shape == "tags_length_eq_0");
    }

    [Fact]
    public void A_value_converted_collection_declines()
    {
        var collection = Seed();

        // Stored as one delimited string: there is no array to size or index.
        List<string> Run(MongoQueryMode mode)
        {
            using var db = SingleEntityDbContext.Create(
                collection,
                mb => mb.Entity<TagRow>().Property(e => e.Tags).HasConversion(
                    v => string.Join(",", v), v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)),
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                });
            return db.Entities.Where(x => x.Tags.Length > 1).Select(x => x.Name).ToList();
        }

        // NativeOnly declines; Native falls back to driver-LINQ, which refuses a value-converted property (EF-337).
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        Assert.Throws<NotSupportedException>(() => Run(MongoQueryMode.Native));
    }

    private static SingleEntityDbContext<TagRow> Create(IMongoCollection<TagRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<TagRow> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("tags") + Guid.NewGuid().ToString("N")[..8];
        var typed = database.MongoDatabase.GetCollection<TagRow>(name);
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "empty" }, { "Tags", new BsonArray() }, { "Nums", new BsonArray() } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "missing" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "null" }, { "Tags", BsonNull.Value }, { "Nums", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "one" }, { "Tags", new BsonArray { "a" } }, { "Nums", new BsonArray { 1 } } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "three" }, { "Tags", new BsonArray { "a", "b", "c" } }, { "Nums", new BsonArray { 1, 2, 3 } } }
        ]);
        return typed;
    }
}
