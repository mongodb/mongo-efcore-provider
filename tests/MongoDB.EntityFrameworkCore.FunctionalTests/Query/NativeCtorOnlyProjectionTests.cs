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
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeCtorOnlyProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 1: whole-entity ctor argument — bypasses projection, lands on NativeRoute.WholeEntity
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string CustomerID { get; set; } = "";
    }

    private class CustomerDtoWithEntityInCtor
    {
        public string Id { get; }
        public CustomerDtoWithEntityInCtor(Customer customer) => Id = customer.CustomerID;
    }

    private IMongoCollection<Customer> SeedCustomers(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ALFKI" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ANATR" } },
        ]);
        return database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Select_with_whole_entity_ctor_only_dto_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_whole_entity_ctor_only_dto_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Succeeding under NativeOnly proves it went native (NativeRoute.WholeEntity, no $project).
        var results = db.Entities.Select(x => new CustomerDtoWithEntityInCtor(x)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "ALFKI");
        Assert.Contains(results, r => r.Id == "ANATR");
    }

    [Fact]
    public void Select_with_whole_entity_ctor_only_dto_still_works_under_explicit_driver_linq_mode()
    {
        var collection = SeedCustomers(
            nameof(Select_with_whole_entity_ctor_only_dto_still_works_under_explicit_driver_linq_mode));
        using var db = CreateContext(collection, MongoQueryMode.DriverLinq);

        // The driver-LINQ path must still work for this shape.
        var results = db.Entities.Select(x => new CustomerDtoWithEntityInCtor(x)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "ALFKI");
        Assert.Contains(results, r => r.Id == "ANATR");
    }

    [Fact]
    public void Select_with_whole_entity_ctor_only_dto_composed_with_take_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_whole_entity_ctor_only_dto_composed_with_take_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // VisitProjectedQuery's WholeEntity branch must stay correct with a composed terminal ($limit) after the
        // ctor-wrap Select.
        var results = db.Entities.Select(x => new CustomerDtoWithEntityInCtor(x)).Take(1).ToList();

        var dto = Assert.Single(results);
        Assert.True(dto.Id is "ALFKI" or "ANATR");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 2a: scalar ctor argument
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class IdWrapper
    {
        public string Value { get; }
        public IdWrapper(string value) => Value = value;
    }

    [Fact]
    public void Select_with_scalar_ctor_only_dto_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_scalar_ctor_only_dto_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var results = db.Entities.Select(x => new IdWrapper(x.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Value == "ALFKI");
        Assert.Contains(results, r => r.Value == "ANATR");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 2b: owned single-reference nav-entity ctor argument
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Blog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public Address Address { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";
    }

    private class AddressDto
    {
        public string City { get; }
        public AddressDto(Address address) => City = address.City;
    }

    private static readonly Action<ModelBuilder> BlogModel = mb => mb.Entity<Blog>().OwnsOne(b => b.Address);

    private IMongoCollection<Blog> SeedBlogWithAddress(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Title", "Alpha" },
            { "Address", new BsonDocument { { "City", "NYC" } } }
        });
        return database.MongoDatabase.GetCollection<Blog>(coll.CollectionNamespace.CollectionName);
    }

    // Deliberate decline: the sub-case 2 path calls TryBindAsBareProjection with a placeholder alias, which never
    // passes the owned-nav-entity-leaf arm's `alias == ownedNavElementName` gate. Falls back under Native; throws
    // under NativeOnly.
    [Fact]
    public void Select_with_owned_nav_entity_ctor_only_dto_still_declines_under_native_only()
    {
        var collection = SeedBlogWithAddress(
            nameof(Select_with_owned_nav_entity_ctor_only_dto_still_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(b => new AddressDto(b.Address)).ToList());
    }

    [Fact]
    public void Select_with_owned_nav_entity_ctor_only_dto_still_works_under_default_native_mode_via_fallback()
    {
        var collection = SeedBlogWithAddress(
            nameof(Select_with_owned_nav_entity_ctor_only_dto_still_works_under_default_native_mode_via_fallback));
        using var db = CreateContext(collection, MongoQueryMode.Native, BlogModel);

        // AsNoTracking: a tracking query rejects projecting an owned entity without its owner (EF constraint).
        var results = db.Entities.AsNoTracking().Select(b => new AddressDto(b.Address)).ToList();

        var dto = Assert.Single(results);
        Assert.Equal("NYC", dto.City);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 3: two-argument ctor-only DTO
    //
    //  Bound by NativeProjectionBinder's Members-null, 2+-argument NewExpression arm; the shaper reads by index
    //  (see MongoSelectDefinition.HasPositionalCtorProjectionShaper).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class TwoArgDto
    {
        public string A { get; }
        public string B { get; }
        public TwoArgDto(string a, string b) { A = a; B = b; }
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_two_argument_ctor_only_dto_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Succeeding under NativeOnly proves it went native.
        var results = db.Entities.Select(x => new TwoArgDto(x.CustomerID, x.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(r.A, r.B));
        Assert.Contains(results, r => r.A == "ALFKI");
        Assert.Contains(results, r => r.A == "ANATR");
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_still_works_under_explicit_driver_linq_mode()
    {
        var collection = SeedCustomers(
            nameof(Select_with_two_argument_ctor_only_dto_still_works_under_explicit_driver_linq_mode));
        using var db = CreateContext(collection, MongoQueryMode.DriverLinq);

        // The driver-LINQ path must still work for this shape.
        var results = db.Entities.Select(x => new TwoArgDto(x.CustomerID, x.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(r.A, r.B));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Sub-case 4: client construction over a whole-entity operand — `new object[] { x }`,
    //  `new List<object> { x }`, `new Wrapper(x) { City = x.City }`. Whole documents are fetched
    //  (NativeRoute.WholeEntity, no $project) and the construction runs client-side over the
    //  materialized, tracked entity (NativeProjectionBinder.IsClientOnlyWholeEntityTree).
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Person
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string? City { get; set; }
    }

    private class PersonWrapper
    {
        public PersonWrapper(Person person) => Person = person;
        public Person Person { get; }

        // Deliberately not named after the element it is assigned from (`City`): a read by the member name off the raw
        // document would answer null.
        public string? Town { get; set; }
    }

    private IMongoCollection<Person> SeedPeople(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Ann" }, { "City", "Oslo" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "City", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Cid" } },
        ]);
        return database.MongoDatabase.GetCollection<Person>(coll.CollectionNamespace.CollectionName);
    }

    private static string Describe(Person person)
        => $"{person.Name}|{person.City ?? "<null>"}";

    [Fact]
    public void Whole_entity_into_object_array_goes_native_with_parity()
    {
        var collection = SeedPeople(nameof(Whole_entity_into_object_array_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.OrderBy(p => p.Name).Select(p => new object[] { p }).ToList()
                .Select(row => $"{row.Length}:{Describe((Person)row[0])}")
                .ToList();
        });

        Assert.Equal(["1:Ann|Oslo", "1:Bob|<null>", "1:Cid|<null>"], results);
    }

    [Fact]
    public void Whole_entity_into_object_list_goes_native_with_parity()
    {
        var collection = SeedPeople(nameof(Whole_entity_into_object_list_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.OrderBy(p => p.Name).Select(p => new List<object> { p }).ToList()
                .Select(row => $"{row.Count}:{Describe((Person)row[0])}")
                .ToList();
        });

        Assert.Equal(["1:Ann|Oslo", "1:Bob|<null>", "1:Cid|<null>"], results);
    }

    [Fact]
    public void Whole_entity_ctor_argument_with_member_assignment_goes_native_with_parity()
    {
        var collection = SeedPeople(nameof(Whole_entity_ctor_argument_with_member_assignment_goes_native_with_parity));

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.OrderBy(p => p.Name).Select(p => new PersonWrapper(p) { Town = p.City }).ToList()
                .Select(w => $"{Describe(w.Person)}/{w.Town ?? "<null>"}")
                .ToList();
        });

        Assert.Equal(["Ann|Oslo/Oslo", "Bob|<null>/<null>", "Cid|<null>/<null>"], results);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Whole_entity_in_client_construction_is_the_tracked_instance(int shape)
    {
        var collection = SeedPeople(nameof(Whole_entity_in_client_construction_is_the_tracked_instance) + shape);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Loaded first, so identity resolution must hand back this very instance from the construction query.
        var ann = db.Entities.Single(p => p.Name == "Ann");

        var query = db.Entities.OrderBy(p => p.Name);
        var people = shape switch
        {
            0 => query.Select(p => new object[] { p }).ToList().Select(r => (Person)r[0]).ToList(),
            1 => query.Select(p => new List<object> { p }).ToList().Select(r => (Person)r[0]).ToList(),
            _ => query.Select(p => new PersonWrapper(p) { Town = p.City }).ToList().Select(w => w.Person).ToList()
        };

        Assert.Equal(3, people.Count);
        Assert.Same(ann, people[0]);
        Assert.Equal(3, db.ChangeTracker.Entries<Person>().Count());
        Assert.All(people, p => Assert.Same(p, db.Entities.Local.Single(l => l.Id == p.Id)));
    }

    // An owned-reference operand beside the whole entity is not a whole-entity operand (after nav-expansion it is not a
    // member chain off the selector parameter), so the binder declines. Only the NativeOnly decline is asserted: the
    // Native/DriverLinq fallback for this shape throws NullReferenceException in BsonBinding.GetBsonDocument, as it
    // did before this shape rule existed.
    [Fact]
    public void Whole_entity_and_its_owned_reference_into_object_array_declines_under_native_only()
    {
        var collection = SeedBlogWithAddress(
            nameof(Whole_entity_and_its_owned_reference_into_object_array_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, BlogModel);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(b => new object[] { b, b.Address }).ToList());
    }

    // Control: no whole-entity operand, so this must not switch to whole-document fetching (a scalar container is a
    // separate shape).
    [Fact]
    public void Scalar_only_array_construction_is_not_fetched_as_whole_documents()
    {
        var collection = SeedPeople(nameof(Scalar_only_array_construction_is_not_fetched_as_whole_documents));

        var results = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.OrderBy(p => p.Name).Select(p => new[] { p.Name }).ToList()
                .Select(row => string.Join(",", row))
                .ToList();
        });

        Assert.Equal(["Ann", "Bob", "Cid"], results);
    }

    // HasClientWrappedWholeEntityShaper keeps the wrapped operand out of a native set-op combine: the per-row result is
    // the array, not the entity document a $unionWith would dedupe.
    [Fact]
    public void Whole_entity_object_array_union_declines()
    {
        var collection = SeedPeople(nameof(Whole_entity_object_array_union_declines));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(p => p.Name == "Ann").Select(p => new object[] { p })
                .Union(db.Entities.Where(p => p.Name == "Bob").Select(p => new object[] { p }))
                .ToList());
    }
}
