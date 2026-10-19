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
/// <c>Mql.Exists</c>, <c>Mql.IsMissing</c> and <c>Mql.IsNullOrMissing</c> test the STORED element: present, absent, or
/// null-or-absent. Over a path that may hold an array, <c>{ f: null }</c> would also match an array that merely CONTAINS
/// null, so the null forms render through <c>$expr</c> there; row <c>r4</c> (<c>Tags: [null]</c>) discriminates that.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeFieldPresenceTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public string Id { get; set; } = "";
        public string? S { get; set; }
        public string?[]? Tags { get; set; }
    }

    [Theory]
    [InlineData("s_exists", new[] { "r1", "r2", "r4", "r5" })]
    [InlineData("s_missing", new[] { "r3" })]
    [InlineData("s_null_or_missing", new[] { "r2", "r3" })]
    [InlineData("s_not_exists", new[] { "r3" })]
    [InlineData("s_not_null_or_missing", new[] { "r1", "r4", "r5" })]
    [InlineData("tags_exists", new[] { "r1", "r2", "r4", "r5" })]
    [InlineData("tags_null_or_missing", new[] { "r2", "r3" })]
    [InlineData("tags_not_null_or_missing", new[] { "r1", "r4", "r5" })]
    [InlineData("combined", new[] { "r2" })]
    [InlineData("ef_property_spelling", new[] { "r2", "r3" })]
    public void Presence_tests_match_the_stored_state_in_every_mode(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "s_exists" => q.Where(r => Mql.Exists(r.S)),
                    "s_missing" => q.Where(r => Mql.IsMissing(r.S)),
                    "s_null_or_missing" => q.Where(r => Mql.IsNullOrMissing(r.S)),
                    "s_not_exists" => q.Where(r => !Mql.Exists(r.S)),
                    "s_not_null_or_missing" => q.Where(r => !Mql.IsNullOrMissing(r.S)),
                    "tags_exists" => q.Where(r => Mql.Exists(r.Tags)),
                    "tags_null_or_missing" => q.Where(r => Mql.IsNullOrMissing(r.Tags)),
                    "tags_not_null_or_missing" => q.Where(r => !Mql.IsNullOrMissing(r.Tags)),
                    "combined" => q.Where(r => Mql.IsNullOrMissing(r.S) && Mql.Exists(r.Tags)),
                    _ => q.Where(r => Mql.IsNullOrMissing(EF.Property<string>(r, "S")))
                };
                return q.Select(r => r.Id).OrderBy(id => id).ToList();
            },
            [.. expected],
            // Driver-LINQ renders IsNullOrMissing(array) as { Tags: null }, which also matches an array that CONTAINS null
            // (r4). Wrong on main.
            driverKnownWrong: shape is "tags_null_or_missing" or "tags_not_null_or_missing");
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
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("presence") + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", "r1" }, { "S", "x" }, { "Tags", new BsonArray { "a" } } },
            new BsonDocument { { "_id", "r2" }, { "S", BsonNull.Value }, { "Tags", BsonNull.Value } },
            new BsonDocument { { "_id", "r3" } },
            new BsonDocument { { "_id", "r4" }, { "S", "y" }, { "Tags", new BsonArray { BsonNull.Value } } },
            new BsonDocument { { "_id", "r5" }, { "S", "z" }, { "Tags", new BsonArray() } }
        ]);
        return database.MongoDatabase.GetCollection<Row>(name);
    }
}
