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

    // Ctor-only DTO: NewExpression.Members is null for `new CustomerOrderSummary(g.Key, g.Count())`.
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

    // Ctor-only DTO wrapping only the key, no aggregate (the shape of EF's GroupBy_nominal_type_count).
    private class CustomerIdOnly
    {
        public string CustomerId { get; }

        public CustomerIdOnly(string customerId)
        {
            CustomerId = customerId;
        }
    }

    // Object-initializer (MemberInitExpression) DTO key.
    private class CustomerEmployeeKey
    {
        public string CustomerId { get; set; } = "";
        public int EmployeeId { get; set; }
    }

    // MemberInit key with a member whose element name is renamed via [BsonElement]; must decline rather than
    // crash g.Key's readback. Two field-ref members so the guard is shown to check every member. (A literal
    // constant in the key, like `EmployeeId = 0` above, would hit a separate driver-LINQ bridge gap:
    // "$group does not support inclusion-style expressions".)
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

        // Success under NativeOnly proves native. OrderBy is client-side because a server-side OrderBy over
        // GroupBy().Select() is a separate gap.
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

        // Success under NativeOnly proves native.
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
        // Native would write $group._id under the CLR name ("CustomerId") while g.Key's class-map readback
        // expects "cid", crashing with FormatException. NativeOnly must throw; Native must fall back and match
        // DriverLinq.
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

        // Success under NativeOnly proves native.
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
