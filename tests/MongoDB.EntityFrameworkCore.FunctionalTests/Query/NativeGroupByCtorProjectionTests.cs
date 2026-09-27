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

using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeGroupByCtorProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string CustomerId { get; set; } = "";
        public int EmployeeId { get; set; }
        public decimal Total { get; set; }
    }

    // A ctor-only DTO — no member named "key"/"count" matches a constructor parameter of the same name by
    // the compiler's rules, so NewExpression.Members is null for `new CustomerOrderSummary(g.Key, g.Count())`.
    private class CustomerOrderSummary
    {
        public string CustomerId { get; }
        public int OrderCount { get; }

        public CustomerOrderSummary(string customerId, int orderCount)
        {
            CustomerId = customerId;
            OrderCount = orderCount;
        }
    }

    // A ctor-only DTO wrapping ONLY the key, with no aggregate at all — the exact shape EF Core's own
    // GroupBy_nominal_type_count spec test uses.
    private class CustomerIdOnly
    {
        public string CustomerId { get; }

        public CustomerIdOnly(string customerId)
        {
            CustomerId = customerId;
        }
    }

    // A MemberInitExpression (object-initializer) DTO key — new NominalType { A = ..., B = ... } — as opposed
    // to the existing ctor-only DTO tests above, whose keys are all plain scalar members.
    private class CustomerEmployeeKey
    {
        public string CustomerId { get; set; } = "";
        public int EmployeeId { get; set; }
    }

    // EF-322 SP7 fix-wave (Finding C1): a MemberInitExpression key DTO whose bound member's stored BSON
    // element name is renamed away from its CLR member name via [BsonElement] — the shape that must decline
    // (fall back to driver-LINQ) rather than crash g.Key's own readback. Deliberately TWO field-ref members
    // (unlike the original single-member version of this fixture): a round-2 re-review probe
    // (Probe_two_field_ref_member_init_key_driver_linq, run manually against DriverLinq mode and then
    // removed) showed a two-field-ref MemberInit key with NO literal constant passes cleanly under
    // driver-LINQ. The earlier "$group does not support inclusion-style expressions" failure this fixture
    // used to route around was NOT caused by having 2+ members — it was caused by a LITERAL CONSTANT bound
    // into one of the key parts (see CustomerEmployeeKey's `EmployeeId = 0` usage above, a pre-existing,
    // unrelated MongoEFToLinqTranslatingExpressionVisitor bridge gap for a literal mixed into $group._id,
    // orthogonal to element renaming and out of scope here). Using two real field-ref members lets this
    // test's fallback assertions prove C1's guard checks EVERY bound member (not just the first) end-to-end,
    // not just at the unit level.
    private class RenamedElementCustomerKey
    {
        [BsonElement("cid")]
        public string CustomerId { get; set; } = "";
        public int EmployeeId { get; set; }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void GroupBy_select_with_two_argument_ctor_only_dto_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_two_argument_ctor_only_dto_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "Total", 10m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "Total", 20m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "B" }, { "Total", 5m } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the ctor-only DTO result selector went native. The OrderBy is applied client-side via
        // AsEnumerable() — a server-side OrderBy composed directly on a GroupBy().Select() result is a
        // separate, pre-existing gap (every native GroupBy test in NativeGroupByTests.cs follows the same
        // AsEnumerable().OrderBy() pattern) unrelated to the ctor-only DTO shape this test targets.
        var results = db.Entities
            .GroupBy(o => o.CustomerId)
            .Select(g => new CustomerOrderSummary(g.Key, g.Count()))
            .AsEnumerable()
            .OrderBy(r => r.CustomerId)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].CustomerId);
        Assert.Equal(2, results[0].OrderCount);
        Assert.Equal("B", results[1].CustomerId);
        Assert.Equal(1, results[1].OrderCount);
    }

    [Fact]
    public void GroupBy_select_with_member_init_dto_key_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_member_init_dto_key_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "EmployeeId", 1 }, { "Total", 10m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "EmployeeId", 1 }, { "Total", 20m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "B" }, { "EmployeeId", 2 }, { "Total", 5m } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the MemberInitExpression key selector went native.
        var results = db.Entities
            .GroupBy(o => new CustomerEmployeeKey { CustomerId = o.CustomerId, EmployeeId = 0 })
            .Select(g => new { Sum = g.Sum(o => o.Total), g.Key })
            .AsEnumerable()
            .OrderBy(r => r.Key.CustomerId)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].Key.CustomerId);
        Assert.Equal(30m, results[0].Sum);
        Assert.Equal("B", results[1].Key.CustomerId);
        Assert.Equal(5m, results[1].Sum);
    }

    [Fact]
    public void GroupBy_select_with_member_init_dto_key_with_renamed_element_declines_to_driver_linq()
    {
        // Final-review fix (Finding C1): a MemberInitExpression DTO key whose bound member's stored BSON
        // element name differs from its own CLR member name (here, CustomerId is stored as "cid" via
        // [BsonElement]) must DECLINE the native shape — admitting it would write $group._id under the CLR
        // name ("CustomerId") while g.Key's own driver class-map readback expects the element name ("cid"),
        // crashing with FormatException. Proves the decline two ways: NativeOnly throws
        // NativeTranslationNotSupportedException, and default (Native) mode falls back cleanly and returns
        // the SAME results as DriverLinq mode (no crash, no silently wrong data).
        //
        // Round-2 (re-review): the key has TWO field-ref members (CustomerId renamed, EmployeeId not) rather
        // than one, so this end-to-end test — not just the unit test — proves the guard inspects every bound
        // member, not just the first it encounters. A round-2 probe confirmed a two-field-ref MemberInit key
        // with no literal constant passes cleanly under driver-LINQ, so this widening doesn't reintroduce the
        // orthogonal "inclusion-style expressions" gap (that gap needs a literal constant in the key, per
        // CustomerEmployeeKey's `EmployeeId = 0` usage above).
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_member_init_dto_key_with_renamed_element_declines_to_driver_linq)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "EmployeeId", 1 }, { "Total", 10m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "EmployeeId", 1 }, { "Total", 20m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "B" }, { "EmployeeId", 2 }, { "Total", 5m } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        static List<(string CustomerId, decimal Sum)> Run(SingleEntityDbContext<Order> db)
            => db.Entities
                .GroupBy(o => new RenamedElementCustomerKey { CustomerId = o.CustomerId, EmployeeId = o.EmployeeId })
                .Select(g => new { Sum = g.Sum(o => o.Total), g.Key })
                .AsEnumerable()
                .OrderBy(r => r.Key.CustomerId)
                .Select(r => (r.Key.CustomerId, r.Sum))
                .ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = SingleEntityDbContext.Create(
                collection,
                optionsBuilderAction: b =>
                {
                    b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                    new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
                });

            var results = Run(db);
            Assert.Equal(2, results.Count);
            Assert.Equal("A", results[0].CustomerId);
            Assert.Equal(30m, results[0].Sum);
            Assert.Equal("B", results[1].CustomerId);
            Assert.Equal(5m, results[1].Sum);
        }

        using var nativeOnlyDb = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(nativeOnlyDb));
    }

    [Fact]
    public void GroupBy_select_with_one_argument_ctor_only_dto_and_no_aggregate_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(GroupBy_select_with_one_argument_ctor_only_dto_and_no_aggregate_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "Total", 10m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "A" }, { "Total", 20m } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", "B" }, { "Total", 5m } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the zero-aggregate, ctor-only DTO key projection went native.
        var results = db.Entities
            .GroupBy(o => o.CustomerId)
            .Select(g => new CustomerIdOnly(g.Key))
            .AsEnumerable()
            .OrderBy(r => r.CustomerId)
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].CustomerId);
        Assert.Equal("B", results[1].CustomerId);
    }
}
