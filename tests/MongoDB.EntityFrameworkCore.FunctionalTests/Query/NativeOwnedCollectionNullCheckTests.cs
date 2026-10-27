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
/// <c>ownedCollection == null</c> / <c>!= null</c> compares the STORED array: missing and explicit BSON null are null,
/// an empty array is not. The oracle is hand-computed over stored state, never over materialized entities, which
/// normalize a missing or null array to empty.
/// </summary>
public class NativeOwnedCollectionNullCheckTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Parent
    {
        public string Id { get; set; } = "";
        public List<Child> Children { get; set; } = null!;
    }

    public class Child
    {
        public string Name { get; set; } = "";
    }

    public static TheoryData<string, string[]> Cases() => new()
    {
        { "eq_null", ["missing", "null"] },
        { "null_eq", ["missing", "null"] },
        { "ne_null", ["empty", "one", "two"] },
        { "not_eq_null", ["empty", "one", "two"] },
        { "ne_null_and_count", ["one", "two"] },
        { "eq_null_or_empty", ["empty", "missing", "null"] }
    };

    private static IQueryable<Parent> Apply(IQueryable<Parent> q, string shape)
        => shape switch
        {
            "eq_null" => q.Where(p => p.Children == null),
            "null_eq" => q.Where(p => null == p.Children),
            "ne_null" => q.Where(p => p.Children != null),
            "not_eq_null" => q.Where(p => !(p.Children == null)),
            "ne_null_and_count" => q.Where(p => p.Children != null && p.Children.Count > 0),
            "eq_null_or_empty" => q.Where(p => p.Children == null || p.Children.Count == 0),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Owned_collection_null_checks_match_stored_state_in_every_mode(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                // Ids only: a matched missing/null row would otherwise materialize as an empty collection.
                return Apply(db.Entities.AsNoTracking(), shape).Select(p => p.Id).OrderBy(id => id).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void A_matched_null_row_materializes_an_empty_collection()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);

        var matched = db.Entities.AsNoTracking().Where(p => p.Children == null).OrderBy(p => p.Id).ToList();

        Assert.Equal(["missing", "null"], matched.Select(p => p.Id));
        Assert.All(matched, p => Assert.Empty(p.Children));
    }

    private static SingleEntityDbContext<Parent> Create(IMongoCollection<Parent> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            mb => mb.Entity<Parent>().OwnsMany(p => p.Children),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // Stored states: missing, explicit null, empty, one, two.
    private IMongoCollection<Parent> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("ownednull") + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", "missing" } },
            new BsonDocument { { "_id", "null" }, { "Children", BsonNull.Value } },
            new BsonDocument { { "_id", "empty" }, { "Children", new BsonArray() } },
            new BsonDocument { { "_id", "one" }, { "Children", new BsonArray { new BsonDocument("Name", "a") } } },
            new BsonDocument
            {
                { "_id", "two" }, { "Children", new BsonArray { new BsonDocument("Name", "a"), new BsonDocument("Name", "b") } }
            }
        ]);
        return database.MongoDatabase.GetCollection<Parent>(name);
    }
}
