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

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the whole-entity ctor-only DTO went native (via NativeRoute.WholeEntity — no $project).
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

        // This ticket adds a native path alongside the existing driver-LINQ fallback; it must not change the
        // fallback path's own behavior. Forcing DriverLinq here proves the pre-existing path still works.
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

        // Guards finding #1 of the post-review fix wave: VisitProjectedQuery's WholeEntity branch must stay
        // self-limiting even when a terminal operator (here Take, a $limit stage) is composed after the
        // ctor-wrap Select — success under NativeOnly proves the composed query still goes native rather than
        // silently mis-shaping or crashing.
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

    // Owned single-reference nav-entity ctor argument (`new AddressDto(b.Address)`) is a deliberate decline,
    // not a regression: the new switch arm's sub-case 2 path calls TryBindAsBareProjection with a placeholder
    // alias, which never matches the pre-existing owned-nav-entity-leaf arm's `alias == ownedNavElementName`
    // gate (that gate exists to catch a renamed member in the WRAPPED case and is out of scope to rework here
    // — see the EF-441 WRAPPED-path feature). So this shape falls through to Route == Fallback, exactly as it
    // did before this ticket: it throws under NativeOnly and falls back correctly under the default Native mode.
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

        // AsNoTracking: projecting an owned entity via the DTO ctor exposes its shape without the owner
        // entity, which a tracking query rejects (EF's own constraint, unrelated to native vs. fallback).
        var results = db.Entities.AsNoTracking().Select(b => new AddressDto(b.Address)).ToList();

        var dto = Assert.Single(results);
        Assert.Equal("NYC", dto.City);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Guard: two-argument ctor-only DTO still declines (falls back / throws under NativeOnly)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class TwoArgDto
    {
        public string A { get; }
        public string B { get; }
        public TwoArgDto(string a, string b) { A = a; B = b; }
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_still_declines_under_native_only()
    {
        var collection = SeedCustomers(nameof(Select_with_two_argument_ctor_only_dto_still_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(x => new TwoArgDto(x.CustomerID, x.CustomerID)).ToList());
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_still_works_under_default_native_mode_via_fallback()
    {
        var collection = SeedCustomers(nameof(Select_with_two_argument_ctor_only_dto_still_works_under_default_native_mode_via_fallback));
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var results = db.Entities.Select(x => new TwoArgDto(x.CustomerID, x.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
    }
}
