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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native-gate parity tests for mis-routing: the gate claiming a query is native-eligible when the native
/// pipeline can't reproduce driver-LINQ semantics. Each shape gets a parity test (same results under
/// <see cref="MongoQueryMode.Native"/> and <see cref="MongoQueryMode.DriverLinq"/>) and a routing test under
/// <see cref="MongoQueryMode.NativeOnly"/>, the only reliable routing signal (MQL shape can't prove it).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeGateRoutingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ── Shared helpers ────────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Asserts <paramref name="query"/> returns equal (order-sensitive) results under
    /// <see cref="MongoQueryMode.Native"/> and <see cref="MongoQueryMode.DriverLinq"/>.
    /// </summary>
    private void AssertParity<T, TResult>(
        IMongoCollection<T> collection,
        Func<IQueryable<T>, IEnumerable<TResult>> query,
        Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
    {
        List<TResult> native;
        using (var db = CreateContext(collection, MongoQueryMode.Native, modelBuilderAction))
            native = query(db.Entities).ToList();

        List<TResult> driver;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq, modelBuilderAction))
            driver = query(db.Entities).ToList();

        Assert.Equal(driver, native);
    }

    /// <summary>
    /// Returns whether <paramref name="query"/> runs under <see cref="MongoQueryMode.NativeOnly"/> (false if it
    /// throws <see cref="NativeTranslationNotSupportedException"/>).
    /// </summary>
    private bool WentNative<T, TResult>(
        IMongoCollection<T> collection,
        Func<IQueryable<T>, IEnumerable<TResult>> query,
        Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
    {
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, modelBuilderAction);
        try
        {
            _ = query(db.Entities).ToList();
            return true;
        }
        catch (NativeTranslationNotSupportedException)
        {
            return false;
        }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Shape A — value-converter / BsonRepresentation-backed properties in Where / OrderBy
    // ════════════════════════════════════════════════════════════════════════════════════════════

    // A.1 — string property stored as ObjectId. The constant must serialize through the property serializer so
    //       the $match compares an ObjectId, not a string.

    private class StringIdEntity
    {
        public ObjectId Id { get; set; }
        public string StringId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private IMongoCollection<StringIdEntity> SeedStringId(string name, out string targetStringId)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        var target = ObjectId.GenerateNewId();
        targetStringId = target.ToString();
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "StringId", target }, { "Name", "Alice" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "StringId", ObjectId.GenerateNewId() }, { "Name", "Bob" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "StringId", ObjectId.GenerateNewId() }, { "Name", "Carol" } },
        ]);
        return database.MongoDatabase.GetCollection<StringIdEntity>(coll.CollectionNamespace.CollectionName);
    }

    private static readonly Action<ModelBuilder> StringIdModel = mb =>
        mb.Entity<StringIdEntity>().Property(e => e.StringId).HasBsonRepresentation(BsonType.ObjectId);

    [Fact]
    public void A_string_as_objectId_where_equals_parity()
    {
        var collection = SeedStringId(nameof(A_string_as_objectId_where_equals_parity), out var target);
        AssertParity(collection, q => q.Where(e => e.StringId == target).Select(e => e.Name), StringIdModel);
    }

    [Fact]
    public void A_string_as_objectId_where_equals_routing()
    {
        var collection = SeedStringId(nameof(A_string_as_objectId_where_equals_routing), out var target);
        // Locked routing: a string-as-ObjectId equality predicate over the whole entity goes native.
        Assert.True(WentNative(collection, q => q.Where(e => e.StringId == target).ToList(), StringIdModel));
    }

    // A.2 — enum stored as string via HasConversion<string>(), in Where and OrderBy.

    private enum Status { Active, Suspended, Closed }

    private class EnumEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Status Status { get; set; }
    }

    private IMongoCollection<EnumEntity> SeedEnum(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Status", "Active" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Status", "Closed" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Status", "Suspended" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Dave" }, { "Status", "Active" } },
        ]);
        return database.MongoDatabase.GetCollection<EnumEntity>(coll.CollectionNamespace.CollectionName);
    }

    private static readonly Action<ModelBuilder> EnumModel = mb =>
        mb.Entity<EnumEntity>().Property(e => e.Status).HasConversion<string>();

    [Fact]
    public void A_enum_as_string_where_equals_parity()
    {
        var collection = SeedEnum(nameof(A_enum_as_string_where_equals_parity));
        AssertParity(collection,
            q => q.Where(e => e.Status == Status.Active).OrderBy(e => e.Name).Select(e => e.Name), EnumModel);
    }

    [Fact]
    public void A_enum_as_string_order_by_parity()
    {
        var collection = SeedEnum(nameof(A_enum_as_string_order_by_parity));
        // Native sorts on the stored string ("Active" < "Closed" < "Suspended"); ThenBy Name breaks ties.
        AssertParity(collection,
            q => q.OrderBy(e => e.Status).ThenBy(e => e.Name).Select(e => e.Name), EnumModel);
    }

    [Fact]
    public void A_enum_as_string_where_equals_routing()
    {
        var collection = SeedEnum(nameof(A_enum_as_string_where_equals_routing));
        // EF emits `(int)e.Status == (int)Status.Active`; the convert to the enum's underlying type is identity-like
        // (IsIdentityLikeConvert), so the constant keeps the property's serializer and renders "Active", matching
        // the string-stored rows.
        Assert.True(WentNative(collection, q => q.Where(e => e.Status == Status.Active).ToList(), EnumModel));
    }

    [Fact]
    public void A_enum_as_string_order_by_routing()
    {
        var collection = SeedEnum(nameof(A_enum_as_string_order_by_routing));
        Assert.True(WentNative(collection,
            q => q.OrderBy(e => e.Status).ThenBy(e => e.Name).ToList(), EnumModel));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Shape B — owned / nested navigation sub-property predicate (e.Address.City)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class PersonWithAddress
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Address Address { get; set; } = null!;
    }

    private class Address
    {
        public string City { get; set; } = "";
        public string Zip { get; set; } = "";
    }

    private IMongoCollection<PersonWithAddress> SeedAddress(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10001" } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" },
                { "Address", new BsonDocument { { "City", "LA" }, { "Zip", "90001" } } }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" },
                { "Address", new BsonDocument { { "City", "NYC" }, { "Zip", "10002" } } }
            },
        ]);
        return database.MongoDatabase.GetCollection<PersonWithAddress>(coll.CollectionNamespace.CollectionName);
    }

    private static readonly Action<ModelBuilder> AddressModel = mb =>
        mb.Entity<PersonWithAddress>().OwnsOne(p => p.Address);

    [Fact]
    public void B_owned_subproperty_where_equals_parity()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_where_equals_parity));
        AssertParity(collection,
            q => q.Where(e => e.Address.City == "NYC").OrderBy(e => e.Name).Select(e => e.Name), AddressModel);
    }

    [Fact]
    public void B_owned_subproperty_order_by_parity()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_order_by_parity));
        AssertParity(collection,
            q => q.OrderBy(e => e.Address.City).ThenBy(e => e.Name).Select(e => e.Name), AddressModel);
    }

    [Fact]
    public void B_owned_subproperty_where_equals_routing()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_where_equals_routing));
        // An owned sub-property resolves to a dotted path ("Address.City") via TryResolveOwnedFieldPath.
        Assert.True(WentNative(collection, q => q.Where(e => e.Address.City == "NYC").ToList(), AddressModel));
    }

    [Fact]
    public void B_owned_subproperty_order_by_routing()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_order_by_routing));
        // An owned sub-property sort key goes native, same mechanism as the predicate.
        Assert.True(WentNative(collection,
            q => q.OrderBy(e => e.Address.City).ThenBy(e => e.Name).ToList(), AddressModel));
    }

    [Fact]
    public void B_owned_subproperty_projection_now_goes_native()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_projection_now_goes_native));
        // A single owned-scalar leaf (e.Address.City) goes native through the shared TryResolveMember gate that
        // NativeProjectionBinder.TryTranslateLeaf uses; see the parity test below.
        Assert.True(WentNative(collection, q => q.Select(e => new { e.Address.City }), AddressModel));
    }

    [Fact]
    public void B_owned_entity_projection_tracking_query_throws_EFCore_guard()
    {
        // Projecting the whole owned entity (e.Address) goes native, but a tracking query projecting an owned entity
        // without its owner is rejected by EF Core's InjectStructuralTypeMaterializers guard in every mode.
        var collection = SeedAddress(nameof(B_owned_entity_projection_tracking_query_throws_EFCore_guard));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, AddressModel);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.Select(e => new { e.Address }).ToList());
        Assert.Contains("owned entities cannot be tracked without their owner", ex.Message);
    }

    [Fact]
    // `new { e.Address }` goes native under NativeOnly once AsNoTracking sidesteps the EF Core tracking guard.
    public void B_owned_entity_projection_no_tracking_goes_native_under_NativeOnly()
    {
        var collection = SeedAddress(nameof(B_owned_entity_projection_no_tracking_goes_native_under_NativeOnly));
        Assert.True(WentNative(
            collection, q => q.AsNoTracking().Select(e => new { e.Address }), AddressModel));
    }

    [Fact]
    public void B_owned_subproperty_projection_parity()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_projection_parity));
        // Results must match driver LINQ.
        AssertParity(collection,
            q => q.OrderBy(e => e.Name).Select(e => new { e.Name, e.Address.City }), AddressModel);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Shape C — TPH discriminator filtering
    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Highest mis-routing risk: if the native pipeline drops the implicit discriminator $match for a
    //  derived-type query, it returns sibling-type rows.

    private class Animal
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Cat : Animal
    {
        public int Whiskers { get; set; }
    }

    private class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    private IMongoCollection<Animal> SeedTph(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "_t", "Cat" }, { "Name", "Felix" }, { "Whiskers", 12 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "_t", "Dog" }, { "Name", "Rex" }, { "Breed", "Lab" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "_t", "Cat" }, { "Name", "Whiskers" }, { "Whiskers", 8 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "_t", "Dog" }, { "Name", "Felix" }, { "Breed", "Pug" } },
        ]);
        return database.MongoDatabase.GetCollection<Animal>(coll.CollectionNamespace.CollectionName);
    }

    private static readonly Action<ModelBuilder> TphModel = mb =>
    {
        mb.Entity<Animal>().HasDiscriminator<string>("_t")
            .HasValue<Animal>("Animal")
            .HasValue<Cat>("Cat")
            .HasValue<Dog>("Dog");
        mb.Entity<Cat>();
        mb.Entity<Dog>();
    };

    [Fact]
    public void C_tph_base_predicate_parity()
    {
        var collection = SeedTph(nameof(C_tph_base_predicate_parity));
        // The base set has no implicit discriminator, so both "Felix" rows (Cat and Dog) return in the same order.
        // Type tags are computed client-side (GetType() isn't server-translatable on either path).
        AssertParity(collection,
            q => q.Where(b => b.Name == "Felix").OrderBy(b => b.Id).AsEnumerable()
                .Select(b => b.Name + ":" + b.GetType().Name),
            TphModel);
    }

    [Fact]
    public void C_tph_base_predicate_routing()
    {
        var collection = SeedTph(nameof(C_tph_base_predicate_routing));
        // A base-set predicate carries no implicit discriminator, so the native $match on Name is faithful.
        Assert.True(WentNative(collection,
            q => q.Where(b => b.Name == "Felix").OrderBy(b => b.Id).ToList(), TphModel));
    }

    [Fact]
    public void C_tph_oftype_derived_parity()
    {
        var collection = SeedTph(nameof(C_tph_oftype_derived_parity));
        // OfType<Cat>() adds the implicit discriminator predicate; dropping it would return Dog rows. Projection is
        // client-side so the test probes discriminator correctness only.
        AssertParity(collection,
            q => q.OfType<Cat>().OrderBy(c => c.Name).AsEnumerable().Select(c => c.Name + ":" + c.Whiskers),
            TphModel);
    }

    [Fact]
    public void C_tph_oftype_derived_routing()
    {
        var collection = SeedTph(nameof(C_tph_oftype_derived_routing));
        // OfType<TDerived>() builds a discriminator $eq/$in into the native predicate (TryBuildDiscriminatorPredicate).
        Assert.True(WentNative(collection,
            q => q.OfType<Cat>().OrderBy(c => c.Name).ToList(), TphModel));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Shape D — native projection pushdown: terminal anonymous member-access Select
    // ════════════════════════════════════════════════════════════════════════════════════════════

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private IMongoCollection<Customer> SeedCustomer(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 30 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 17 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", 45 } },
        ]);
        return database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void D_anonymous_member_projection_runs_native_under_NativeOnly()
    {
        var collection = SeedCustomer(nameof(D_anonymous_member_projection_runs_native_under_NativeOnly));

        // Under NativeOnly a driver-LINQ fallback throws; success proves the $project went native.
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var results = db.Entities
            .Where(c => c.Age > 21)
            .Select(c => new { c.Name, c.Age })
            .ToList();

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void D_arithmetic_computed_projection_runs_native_under_NativeOnly()
    {
        // A numeric arithmetic leaf (c.Age * 2) renders as a computed $project; values prove the doubled result.
        var collection = SeedCustomer(nameof(D_arithmetic_computed_projection_runs_native_under_NativeOnly));

        Assert.True(WentNative(collection, q => q.Select(c => new { c.Name, Doubled = c.Age * 2 })));

        AssertParity(collection, q => q.OrderBy(c => c.Name).Select(c => new { c.Name, Doubled = c.Age * 2 }));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var results = db.Entities.OrderBy(c => c.Name).Select(c => new { c.Name, Doubled = c.Age * 2 }).ToList();
        Assert.Equal(
            [("Alice", 60), ("Bob", 34), ("Carol", 90)],
            results.Select(r => (r.Name, r.Doubled)).ToArray());
    }

    [Fact]
    public void D_string_computed_projection_throws_under_NativeOnly()
    {
        // Only numeric arithmetic and string concatenation (see NativeStringConcatTests) computed leaves go native;
        // ToUpper has no native translation, so NativeOnly throws.
        var collection = SeedCustomer(nameof(D_string_computed_projection_throws_under_NativeOnly));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(c => new { Greeting = c.Name.ToUpper() }).ToList());
    }

    [Fact]
    public void D_renamed_alias_projection_runs_native_under_NativeOnly_and_carries_correct_values()
    {
        // The alias -> element indirection ({ Renamed: "$Name" }) reads back under the renamed member.
        var collection = SeedCustomer(nameof(D_renamed_alias_projection_runs_native_under_NativeOnly_and_carries_correct_values));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var results = db.Entities
            .OrderBy(c => c.Name)
            .Select(c => new { Renamed = c.Name, c.Age })
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Renamed));
        Assert.Contains(results, r => r.Renamed == "Alice" && r.Age == 30);
        Assert.Contains(results, r => r.Renamed == "Bob" && r.Age == 17);
        Assert.Contains(results, r => r.Renamed == "Carol" && r.Age == 45);
    }

    private sealed class CustomerDto
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [Fact]
    public void D_member_init_dto_projection_runs_native_under_NativeOnly_and_carries_correct_values()
    {
        // The MemberInitExpression (named DTO) arm, distinct from the anonymous-type arm.
        var collection = SeedCustomer(nameof(D_member_init_dto_projection_runs_native_under_NativeOnly_and_carries_correct_values));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var results = db.Entities
            .OrderBy(c => c.Name)
            .Select(c => new CustomerDto { Name = c.Name, Age = c.Age })
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Name));
        Assert.Contains(results, r => r.Name == "Alice" && r.Age == 30);
        Assert.Contains(results, r => r.Name == "Bob" && r.Age == 17);
        Assert.Contains(results, r => r.Name == "Carol" && r.Age == 45);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Shape E — native projection that emits the _id output field
    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  RenderProject suppresses the default _id ({ _id: 0 }) unless the projection emits an "_id" output
    //  field. Projecting the key member (element name "_id") is the only test of the non-suppress branch.

    private class KeyedDoc
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; } = "";
    }

    private IMongoCollection<KeyedDoc> SeedKeyedDoc(string name, out ObjectId[] ids)
    {
        ids = [ObjectId.GenerateNewId(), ObjectId.GenerateNewId()];
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            new BsonDocument { { "_id", ids[0] }, { "Name", "Alpha" } },
            new BsonDocument { { "_id", ids[1] }, { "Name", "Beta" } },
        ]);
        return database.MongoDatabase.GetCollection<KeyedDoc>(coll.CollectionNamespace.CollectionName);
    }

    [Fact]
    public void E_projected_id_member_runs_native_under_NativeOnly_and_preserves_id()
    {
        var collection = SeedKeyedDoc(nameof(E_projected_id_member_runs_native_under_NativeOnly_and_preserves_id), out var ids);

        // The projected key emits an "_id" field, skipping suppression; the values prove _id round-trips.
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        var results = db.Entities
            .OrderBy(e => e.Name)
            .Select(e => new { e._id, e.Name })
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(["Alpha", "Beta"], results.Select(r => r.Name));
        Assert.Equal(ids[0], results[0]._id);
        Assert.Equal(ids[1], results[1]._id);
    }

    [Fact]
    public void E_projected_id_member_parity()
    {
        var collection = SeedKeyedDoc(nameof(E_projected_id_member_parity), out _);
        AssertParity(collection, q => q.OrderBy(e => e.Name).Select(e => new { e._id, e.Name }));
    }
}
