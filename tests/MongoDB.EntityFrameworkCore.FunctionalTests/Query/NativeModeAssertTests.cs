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
/// Exercises <see cref="NativeModeAssert.NativeAndExpected{T}"/> and <see cref="NativeModeAssert.TwiceWithDifferentValues{T}"/>
/// over a trivial model.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeModeAssertTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int? Score { get; set; }
    }

    [Fact]
    public void NativeAndExpected_over_a_simple_where()
    {
        var collection = Seed(nameof(NativeAndExpected_over_a_simple_where));

        var result = NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().Where(x => x.Score > 5).OrderBy(x => x.Title)
                    .Select(x => x.Title).ToList();
            },
            ["ten"]);

        Assert.Equal(["ten"], result);
    }

    [Fact]
    public void TwiceWithDifferentValues_hits_the_cached_plan_with_new_values()
    {
        var collection = Seed(nameof(TwiceWithDifferentValues_hits_the_cached_plan_with_new_values));

        NativeModeAssert.TwiceWithDifferentValues(
            p =>
            {
                using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
                var value = (int?)p;
                return db.Entities.AsNoTracking().Where(x => x.Score == value).OrderBy(x => x.Title)
                    .Select(x => x.Title).ToList();
            },
            null, ["missing", "null"],
            1, ["one"]);
    }

    private IMongoCollection<Item> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "ten"}, {"Score", 10}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "one"}, {"Score", 1}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "null"}, {"Score", BsonNull.Value}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "missing"}}
        ]);

        return database.MongoDatabase.GetCollection<Item>(collectionName);
    }

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
