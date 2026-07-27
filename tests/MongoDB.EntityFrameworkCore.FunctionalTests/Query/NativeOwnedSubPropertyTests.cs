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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-322 Task 2 gate tests: predicates and sort keys over owned single-reference (OwnsOne) sub-properties
/// (at any owned-ref nesting depth) must go native instead of falling back to driver-LINQ.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedSubPropertyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private class Person
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Location Home { get; set; } = null!;
    }

    private class Location
    {
        public string City { get; set; } = "";
        public bool IsPrimary { get; set; }
        public Geo Geo { get; set; } = null!;
    }

    private class Geo
    {
        public string Country { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> PersonModel =
        mb => mb.Entity<Person>().OwnsOne(p => p.Home, h => h.OwnsOne(x => x.Geo));

    // Seeds 3 people: NYC/US/primary, LA/US/non-primary, and one with NO Home element (absent owned ref).
    private IMongoCollection<Person> SeedPeople(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Ann" },
                { "Home", new BsonDocument
                    { { "City", "NYC" }, { "IsPrimary", true }, { "Geo", new BsonDocument { { "Country", "US" } } } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" },
                { "Home", new BsonDocument
                    { { "City", "LA" }, { "IsPrimary", false }, { "Geo", new BsonDocument { { "Country", "US" } } } } }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Cid" } },
        ]);
        return database.MongoDatabase.GetCollection<Person>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Owned_subproperty_equality_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_equality_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        // A bare-scalar trailing Select is itself not native (pre-existing limitation) — under NativeOnly that
        // could throw for PROJECTION reasons and mask whether the PREDICATE went native. Materialize whole
        // entities instead (owned whole-entity is already native) and assert on .Name in memory.
        var names = db.Entities.AsNoTracking().Where(p => p.Home.City == "NYC").ToList().Select(p => p.Name).ToList();

        Assert.Equal(["Ann"], names);
    }

    [Fact]
    public void Nested_owned_subproperty_equality_goes_native()
    {
        var collection = SeedPeople(nameof(Nested_owned_subproperty_equality_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var names = db.Entities.AsNoTracking()
            .Where(p => p.Home.Geo.Country == "US").OrderBy(p => p.Name).ToList()
            .Select(p => p.Name).ToList();

        Assert.Equal(["Ann", "Bob"], names);
    }

    [Fact]
    public void Owned_bare_bool_subproperty_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_bare_bool_subproperty_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var names = db.Entities.AsNoTracking().Where(p => p.Home.IsPrimary).ToList().Select(p => p.Name).ToList();

        Assert.Equal(["Ann"], names);
    }

    [Fact]
    public void Owned_subproperty_orderby_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_orderby_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        // Absent-Home doc (Cid) sorts first (missing field), then LA, then NYC.
        var names = db.Entities.AsNoTracking()
            .Where(p => p.Name != "Cid").OrderBy(p => p.Home.City).ToList()
            .Select(p => p.Name).ToList();

        Assert.Equal(["Bob", "Ann"], names);
    }

    [Fact]
    public void Owned_subproperty_predicate_matches_driver_linq_including_absent_owned()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_predicate_matches_driver_linq_including_absent_owned));

        using var native = CreateContext(collection, MongoQueryMode.Native, PersonModel);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, PersonModel);

        // Includes the absent-Home doc in the candidate set; both paths must agree it does not match.
        var nativeNames = native.Entities.AsNoTracking()
            .Where(p => p.Home.City == "NYC").Select(p => p.Name).OrderBy(n => n).ToList();
        var driverNames = driver.Entities.AsNoTracking()
            .Where(p => p.Home.City == "NYC").Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
        Assert.Equal(["Ann"], nativeNames);
    }

    [Fact]
    public void Owned_subproperty_dto_projection_goes_native()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_dto_projection_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var rows = db.Entities.AsNoTracking()
            .Where(p => p.Name != "Cid")
            .OrderBy(p => p.Name)
            .Select(p => new { p.Name, p.Home.City })
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Ann", "NYC"), (rows[0].Name, rows[0].City));
        Assert.Equal(("Bob", "LA"), (rows[1].Name, rows[1].City));
    }

    [Fact]
    public void Nested_owned_subproperty_dto_projection_goes_native()
    {
        var collection = SeedPeople(nameof(Nested_owned_subproperty_dto_projection_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PersonModel);

        var rows = db.Entities.AsNoTracking()
            .Where(p => p.Name == "Ann")
            .Select(p => new { p.Name, Country = p.Home.Geo.Country })
            .ToList();

        var row = Assert.Single(rows);
        Assert.Equal(("Ann", "US"), (row.Name, row.Country));
    }

    [Fact]
    public void Owned_subproperty_projection_matches_driver_linq_including_absent_owned()
    {
        var collection = SeedPeople(nameof(Owned_subproperty_projection_matches_driver_linq_including_absent_owned));
        using var native = CreateContext(collection, MongoQueryMode.Native, PersonModel);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, PersonModel);

        // Cid has no Home → City projects as null; both paths must agree.
        var nativeRows = native.Entities.AsNoTracking()
            .OrderBy(p => p.Name).Select(p => new { p.Name, p.Home.City }).ToList();
        var driverRows = driver.Entities.AsNoTracking()
            .OrderBy(p => p.Name).Select(p => new { p.Name, p.Home.City }).ToList();

        Assert.Equal(
            driverRows.Select(r => (r.Name, r.City)),
            nativeRows.Select(r => (r.Name, r.City)));
        Assert.Null(nativeRows.Single(r => r.Name == "Cid").City);
    }

    private class Coded
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public CodedLoc Home { get; set; } = null!;
    }

    private class CodedLoc
    {
        public string Code { get; set; } = "";
    }

    // Home.Code carries a value converter, so the stored form ("NYC!") differs from the CLR form ("NYC").
    private static readonly Action<ModelBuilder> CodedModel = mb =>
        mb.Entity<Coded>().OwnsOne(c => c.Home, h => h.Property(x => x.Code).HasConversion(v => v + "!", v => v.TrimEnd('!')));

    private IMongoCollection<Coded> SeedCoded(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Ann" },
                { "Home", new BsonDocument { { "Code", "NYC!" } } } // stored WITH the converter suffix
            },
        ]);
        return database.MongoDatabase.GetCollection<Coded>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Converted_owned_subproperty_projection_matches_driver_linq()
    {
        var collection = SeedCoded(nameof(Converted_owned_subproperty_projection_matches_driver_linq));
        using var native = CreateContext(collection, MongoQueryMode.Native, CodedModel);
        using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, CodedModel);

        var nativeCode = native.Entities.AsNoTracking().Select(c => new { c.Home.Code }).Single().Code;
        var driverCode = driver.Entities.AsNoTracking().Select(c => new { c.Home.Code }).Single().Code;

        // Both must return the converted CLR value "NYC" (not the raw stored "NYC!"). Before the guard,
        // Native leaks "NYC!" and this fails; the guard makes Native fall back to the correct driver path.
        Assert.Equal("NYC", driverCode);
        Assert.Equal(driverCode, nativeCode);
    }
}
