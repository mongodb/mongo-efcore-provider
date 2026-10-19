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
/// An owned entity compared with a captured or constructed value (<c>p.Home == x</c>, <c>Visits.Contains(x)</c>,
/// <c>Visits.Any(v =&gt; v == x)</c>) is a per-property conjunction, as the driver-LINQ bridge rewrites it: stored field
/// order and extra stored elements are irrelevant. The <c>extra</c> and <c>reordered</c> rows discriminate that from a
/// whole-sub-document match.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedEntityEqualityTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Addr
    {
        public string City { get; set; } = "";

        // Non-nullable, so a runtime-null parameter comparand is not all-nullable properties.
        public int Zip { get; set; }
    }

    public class Person
    {
        public string Id { get; set; } = "";
        public Addr? Home { get; set; }
        public List<Addr> Visits { get; set; } = null!;
    }

    [Theory]
    [InlineData("home_eq", new[] { "extra", "homeX", "reordered" })]
    [InlineData("home_ne", new[] { "homeMissing", "homeNull", "homeY", "visitsEmpty", "visitsMissing", "visitsNull", "visitsX" })]
    [InlineData("home_eq_inline", new[] { "extra", "homeX", "reordered" })]
    [InlineData("home_eq_and_other", new[] { "homeX", "reordered" })]
    public void Owned_equality_goes_native_and_matches_the_driver(string shape, string[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var x = new Addr { City = "X", Zip = 1 };
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "home_eq" => q.Where(p => p.Home == x),
                    "home_ne" => q.Where(p => p.Home != x),
                    "home_eq_inline" => q.Where(p => p.Home == new Addr { City = "X", Zip = 1 }),
                    "home_eq_and_other" => q.Where(p => p.Home == x && p.Id != "extra"),
                    _ => q.Where(p => p.Home == x && p.Id == "extra")
                };
                return q.Select(p => p.Id).OrderBy(id => id).ToList();
            },
            [.. expected]);
    }

    // No driver-LINQ oracle: a row whose array is missing or BSON null aborts its translation
    // ($anyElementTrue's argument must be an array), which is exactly what these rows are for.
    [Theory]
    [InlineData("contains", new[] { "visitsX" })]
    [InlineData("not_contains", new[] { "extra", "homeMissing", "homeNull", "homeX", "homeY", "reordered", "visitsEmpty", "visitsMissing", "visitsNull" })]
    [InlineData("any_eq", new[] { "visitsX" })]
    public void Owned_collection_Contains_and_Any_equality_go_native_over_stored_state(string shape, string[] expected)
    {
        var collection = Seed();
        List<string> Run(MongoQueryMode mode)
        {
            using var db = Create(collection, mode);
            var x = new Addr { City = "X", Zip = 1 };
            var q = db.Entities.AsNoTracking();
            q = shape switch
            {
                "contains" => q.Where(p => p.Visits.Contains(x)),
                "not_contains" => q.Where(p => !p.Visits.Contains(x)),
                _ => q.Where(p => p.Visits.Any(v => v == x))
            };
            return q.Select(p => p.Id).OrderBy(id => id).ToList();
        }

        Assert.Equal(expected, Run(MongoQueryMode.NativeOnly));
        Assert.Equal(expected, Run(MongoQueryMode.Native));
    }

    [Fact]
    public void A_runtime_null_comparand_matches_the_driver()
    {
        var collection = Seed();

        // Zip is non-nullable, so the parameter comparand is admitted: a null becomes per-property null checks.
        NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Create(collection, mode);
            Addr? none = null;
            return db.Entities.AsNoTracking().Where(p => p.Home == none).Select(p => p.Id).OrderBy(id => id).ToList();
        });
    }

    private static SingleEntityDbContext<Person> Create(IMongoCollection<Person> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            mb =>
            {
                mb.Entity<Person>().OwnsOne(p => p.Home);
                mb.Entity<Person>().OwnsMany(p => p.Visits);
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static BsonDocument AddrDoc(string city, int zip) => new() { { "City", city }, { "Zip", zip } };

    private IMongoCollection<Person> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("ownedeq") + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", "homeX" }, { "Home", AddrDoc("X", 1) } },
            new BsonDocument { { "_id", "homeY" }, { "Home", AddrDoc("Y", 1) } },
            new BsonDocument { { "_id", "homeMissing" } },
            new BsonDocument { { "_id", "homeNull" }, { "Home", BsonNull.Value } },
            // An extra stored element: per-property equality still matches; a whole-document match would not.
            new BsonDocument { { "_id", "extra" }, { "Home", new BsonDocument { { "City", "X" }, { "Zip", 1 }, { "Extra", "e" } } } },
            // Another field order: ditto.
            new BsonDocument { { "_id", "reordered" }, { "Home", new BsonDocument { { "Zip", 1 }, { "City", "X" } } } },
            new BsonDocument { { "_id", "visitsX" }, { "Visits", new BsonArray { AddrDoc("X", 1), AddrDoc("Y", 2) } } },
            new BsonDocument { { "_id", "visitsEmpty" }, { "Visits", new BsonArray() } },
            new BsonDocument { { "_id", "visitsMissing" } },
            new BsonDocument { { "_id", "visitsNull" }, { "Visits", BsonNull.Value } }
        ]);
        return database.MongoDatabase.GetCollection<Person>(name);
    }
}
