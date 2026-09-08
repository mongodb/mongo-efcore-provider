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

using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class NativeGroupByCtorProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public string CustomerId { get; set; } = "";
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
}
