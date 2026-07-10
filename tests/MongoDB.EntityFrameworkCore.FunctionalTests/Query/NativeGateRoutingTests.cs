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
/// EF-323 native-gate <b>parity</b> regression tests. Each test probes the branch's real risk class —
/// <em>mis-routing</em>: the compile-time gate claiming a query is native-eligible when the native
/// pipeline cannot faithfully reproduce the driver-LINQ semantics.
/// <para>
/// Every shape is exercised three ways:
/// <list type="number">
///   <item><b>Parity</b> — run the SAME query under <see cref="MongoQueryMode.Native"/> and under
///   <see cref="MongoQueryMode.DriverLinq"/>; assert the results are equal (catches a divergence whether
///   the query went native or fell back).</item>
///   <item><b>Routing probe</b> — run it under <see cref="MongoQueryMode.NativeOnly"/> and assert whichever
///   is the actual current behavior (succeeds = went native; throws
///   <see cref="NativeTranslationNotSupportedException"/> = fell back), documenting + locking the routing.</item>
/// </list>
/// </para>
/// MQL shape cannot prove a query went native (native and driver-LINQ filter/sort/paging pipelines are
/// structurally identical), so <see cref="MongoQueryMode.NativeOnly"/> is the only reliable routing signal.
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
    /// Runs <paramref name="query"/> under <see cref="MongoQueryMode.Native"/> and under
    /// <see cref="MongoQueryMode.DriverLinq"/> against the same collection and asserts the two
    /// result sequences are equal (order-sensitive). This is the core mis-routing check.
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
    /// Runs <paramref name="query"/> under <see cref="MongoQueryMode.NativeOnly"/> and reports whether it
    /// went native (returns <see langword="true"/>) or fell back (throws
    /// <see cref="NativeTranslationNotSupportedException"/>, returns <see langword="false"/>).
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

    // A.1 — string property stored as ObjectId via [BsonRepresentation] / HasBsonRepresentation.
    //       The native renderer must serialize the string constant through the property serializer so it
    //       becomes an ObjectId in the $match (not a string), matching driver-LINQ.

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
        // OrderBy a string-converted enum: native sorts on the stored string ("Active" < "Closed" < "Suspended"),
        // which must match the driver-LINQ ordering. ThenBy Name to make the order deterministic for ties.
        AssertParity(collection,
            q => q.OrderBy(e => e.Status).ThenBy(e => e.Name).Select(e => e.Name), EnumModel);
    }

    [Fact]
    public void A_enum_as_string_where_equals_routing()
    {
        var collection = SeedEnum(nameof(A_enum_as_string_where_equals_routing));
        // Locked routing: an enum equality predicate falls BACK to driver-LINQ. EF emits the comparison as
        // `(int)e.Status == (int)Status.Active`, i.e. a Convert of the member to the enum's underlying type;
        // MongoExpressionTranslator.HasNumericConvert treats that cast as semantically significant and refuses
        // the shape (conservative — worst case is a fallback, never a wrong result). Parity holds via fallback
        // (see A_enum_as_string_where_equals_parity).
        Assert.False(WentNative(collection, q => q.Where(e => e.Status == Status.Active).ToList(), EnumModel));
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
        // Locked routing: an owned sub-property predicate is NOT natively representable (the translator
        // only resolves members rooted directly on the parameter), so it falls back to driver-LINQ.
        Assert.False(WentNative(collection, q => q.Where(e => e.Address.City == "NYC").ToList(), AddressModel));
    }

    [Fact]
    public void B_owned_subproperty_order_by_routing()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_order_by_routing));
        Assert.False(WentNative(collection,
            q => q.OrderBy(e => e.Address.City).ThenBy(e => e.Name).ToList(), AddressModel));
    }

    [Fact]
    public void B_owned_subproperty_projection_falls_back_under_NativeOnly()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_projection_falls_back_under_NativeOnly));
        // Locked fallback boundary: a projection whose leaf is an owned sub-property (e.Address.City) is NOT
        // natively representable — the projection translator only resolves members rooted directly on the
        // parameter — so it must fall back to driver-LINQ rather than emit a wrong-shape $project. Pins the
        // boundary so a future loosening of the binder can't silently start emitting a nested $project.
        Assert.False(WentNative(collection, q => q.Select(e => new { e.Address.City }), AddressModel));
    }

    [Fact]
    public void B_owned_entity_projection_falls_back_under_NativeOnly()
    {
        var collection = SeedAddress(nameof(B_owned_entity_projection_falls_back_under_NativeOnly));
        // Projecting the whole owned entity (e.Address) is likewise not natively representable (the leaf is a
        // navigation, not a scalar field) and must fall back.
        Assert.False(WentNative(collection, q => q.Select(e => new { e.Address }), AddressModel));
    }

    [Fact]
    public void B_owned_subproperty_projection_parity()
    {
        var collection = SeedAddress(nameof(B_owned_subproperty_projection_parity));
        // Correctness under the fallback path: results must still match driver-LINQ.
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
        // Query over the base set with a predicate: the base set returns ALL discriminator values, so the
        // only filter is on Name. Both "Felix" rows (one Cat, one Dog) must come back, in the same order.
        // Materialize whole entities (a server-side projection of GetType()/string-concat is not supported on
        // either path) and compute the type tag client-side so the comparison still distinguishes Cat from Dog.
        AssertParity(collection,
            q => q.Where(b => b.Name == "Felix").OrderBy(b => b.Id).AsEnumerable()
                .Select(b => b.Name + ":" + b.GetType().Name),
            TphModel);
    }

    [Fact]
    public void C_tph_base_predicate_routing()
    {
        var collection = SeedTph(nameof(C_tph_base_predicate_routing));
        // Locked routing: a base-set predicate carries no implicit discriminator, so the native $match on
        // Name is faithful — it goes native.
        Assert.True(WentNative(collection,
            q => q.Where(b => b.Name == "Felix").OrderBy(b => b.Id).ToList(), TphModel));
    }

    [Fact]
    public void C_tph_oftype_derived_parity()
    {
        var collection = SeedTph(nameof(C_tph_oftype_derived_parity));
        // OfType<Cat>() narrows by the implicit discriminator predicate. If the native pipeline dropped that
        // predicate it would return Dog rows (and the shaper would mis-materialize). Parity must hold: only
        // the two Cats come back, never a Dog. Compute the projection client-side (AsEnumerable) so the test
        // probes routing/discriminator correctness, not server-side projection support.
        AssertParity(collection,
            q => q.OfType<Cat>().OrderBy(c => c.Name).AsEnumerable().Select(c => c.Name + ":" + c.Whiskers),
            TphModel);
    }

    [Fact]
    public void C_tph_oftype_derived_routing()
    {
        var collection = SeedTph(nameof(C_tph_oftype_derived_routing));
        // Locked routing (EF-347): OfType<TDerived>() now builds a discriminator $eq/$in conjunct into the
        // native Predicate slot (TryBuildDiscriminatorPredicate) instead of unconditionally calling
        // MarkNotNativelyRepresentable(), so a representable TPH narrowing goes native.
        Assert.True(WentNative(collection,
            q => q.OfType<Cat>().OrderBy(c => c.Name).ToList(), TphModel));
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  Shape D — native projection pushdown (EF-331): terminal anonymous member-access Select
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
    public void D_computed_projection_throws_under_NativeOnly()
    {
        var collection = SeedCustomer(nameof(D_computed_projection_throws_under_NativeOnly));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(c => new { Doubled = c.Age * 2 }).ToList());
    }

    [Fact]
    public void D_renamed_alias_projection_runs_native_under_NativeOnly_and_carries_correct_values()
    {
        // EF-331: proves the alias -> element indirection ({ Renamed: "$Name" }) reads back correctly under
        // the renamed member. Under NativeOnly a driver-LINQ fallback would throw; success proves the
        // $project went native, and the value assertions prove the alias correctly maps to the source field.
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
        // EF-331: covers the MemberInitExpression arm of the projection translator (distinct from the
        // anonymous-type arm exercised above). Under NativeOnly a driver-LINQ fallback would throw;
        // success proves the $project went native for a named-DTO member-init projection.
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
    //  Shape E — native projection that emits the _id output field (EF-331 $project _id-suppression)
    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  MongoPipelineFactory.RenderProject suppresses the default _id ({ _id: 0 }) UNLESS the projection
    //  deliberately emits an "_id" output field. Every other projection test exercises only the suppress
    //  branch; this shape projects the key member (whose element name is literally "_id"), so the emitted
    //  $project contains an "_id" entry and the suppression is correctly skipped. Load-bearing + otherwise
    //  untested.

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

        // Under NativeOnly a driver-LINQ fallback would throw; success proves the $project went native.
        // Because the projected member is the key (element name "_id"), the emitted $project carries an
        // "_id" field and the _id-suppression branch is skipped — the values below prove _id round-trips.
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
