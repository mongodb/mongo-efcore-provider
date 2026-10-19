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

/// <summary>
/// The client-method-call sibling of <see cref="NativeCtorOnlyProjectionTests"/> (EF-322): a whole-entity
/// selector wrapped in an opaque client method call (e.g. <c>x =&gt; context.ClientMethod(x)</c>), as opposed
/// to a ctor-only DTO construction, is left on <c>NativeRoute.WholeEntity</c> with no <c>$project</c> — see
/// <see cref="NativeProjectionBinder"/>'s client-method-call switch arm.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeClientMethodProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string CustomerID { get; set; } = "";
        public bool IsLondon { get; set; }
    }

    private static bool ClientMethod(Customer c) => !c.IsLondon;

    private IMongoCollection<Customer> SeedCustomers(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ALFKI" }, { "IsLondon", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "AROUT" }, { "IsLondon", true } },
        ]);
        return database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void Select_with_context_based_client_method_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_context_based_client_method_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the whole-entity client-method wrap went native (NativeRoute.WholeEntity, no $project).
        var results = db.Entities.Select(c => ClientMethod(c)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(true, results);
        Assert.Contains(false, results);
    }

    [Fact]
    public void Select_with_context_based_client_method_still_works_under_explicit_driver_linq_mode()
    {
        var collection = SeedCustomers(
            nameof(Select_with_context_based_client_method_still_works_under_explicit_driver_linq_mode));
        using var db = CreateContext(collection, MongoQueryMode.DriverLinq);

        var results = db.Entities.Select(c => ClientMethod(c)).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(true, results);
        Assert.Contains(false, results);
    }

    [Fact]
    public void Select_with_context_based_client_method_composed_with_take_goes_native()
    {
        var collection = SeedCustomers(nameof(Select_with_context_based_client_method_composed_with_take_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var results = db.Entities.Select(c => ClientMethod(c)).Take(1).ToList();

        Assert.Single(results);
    }

    // EF.Property(x, "Name") is structurally identical to the client-method-wrap shape (a MethodCallExpression
    // whose sole argument is the whole entity) but must keep going through the EXISTING native field-projection
    // path, not the new whole-entity-wrap arm — see NativeProjectionBinder's IsEFPropertyMethod() exclusion.
    [Fact]
    public void Select_with_ef_property_still_goes_native_as_a_field_projection()
    {
        var collection = SeedCustomers(nameof(Select_with_ef_property_still_goes_native_as_a_field_projection));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var results = db.Entities.Select(c => EF.Property<string>(c, nameof(Customer.CustomerID))).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains("ALFKI", results);
        Assert.Contains("AROUT", results);
    }

    // A second operand also referencing the entity (e.g. a member read off it) is out of scope for this arm —
    // it declines and falls through to the bare-body default, which itself declines (falls back / throws under
    // NativeOnly), same as before this arm existed.
    private static string TwoOperandClientMethod(Customer c, string id) => id;

    [Fact]
    public void Select_with_client_method_referencing_entity_twice_still_declines_under_native_only()
    {
        var collection = SeedCustomers(
            nameof(Select_with_client_method_referencing_entity_twice_still_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(c => TwoOperandClientMethod(c, c.CustomerID)).ToList());
    }

    [Fact]
    public void Select_with_client_method_referencing_entity_twice_still_works_under_default_native_mode_via_fallback()
    {
        var collection = SeedCustomers(
            nameof(Select_with_client_method_referencing_entity_twice_still_works_under_default_native_mode_via_fallback));
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var results = db.Entities.Select(c => TwoOperandClientMethod(c, c.CustomerID)).ToList();

        Assert.Equal(2, results.Count);
    }

    // A client-method-wrapped Select composed with a subsequent Union must NOT combine via a native
    // $unionWith: the per-row RESULT differs from the raw entity, so comparing/deduping documents at the
    // pipeline level would be wrong. HasClientWrappedWholeEntityShaper routes this through the pre-existing
    // graceful-decline path instead (see MongoSelectDefinition's own remarks).
    [Fact]
    public void Select_with_client_method_composed_with_union_still_declines_under_native_only()
    {
        var collection = SeedCustomers(nameof(Select_with_client_method_composed_with_union_still_declines_under_native_only));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(c => ClientMethod(c)).Union(db.Entities.Select(c => ClientMethod(c))).ToList());
    }
}
