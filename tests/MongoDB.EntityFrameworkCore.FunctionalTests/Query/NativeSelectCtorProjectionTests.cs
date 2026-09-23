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
public class NativeSelectCtorProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string CustomerID { get; set; } = "";
        public string City { get; set; } = "";
    }

    // A ctor-only DTO — no member named "customerId"/"city" matches a constructor parameter of the same name
    // by the compiler's naming rule, so NewExpression.Members is null for
    // `new CustomerListItem(c.CustomerID, c.City)`. Mirrors EF Core's own Northwind
    // CustomerListItem(string id, string city).
    private class CustomerListItem
    {
        public string CustomerID { get; }
        public string City { get; }

        public CustomerListItem(string customerId, string city)
        {
            CustomerID = customerId;
            City = city;
        }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Select_with_two_argument_ctor_only_dto_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ALFKI" }, { "City", "Berlin" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ANATR" }, { "City", "Mexico" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "AROUT" }, { "City", "London" } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Under NativeOnly a shape that falls back throws NativeTranslationNotSupportedException; success
        // here proves the 2-argument ctor-only DTO Select — EF Core's own Northwind
        // Member_binding_after_ctor_arguments_fails_with_client_eval shape — went native.
        //
        // NOTE: .OrderBy(...)/.Take(...) are applied AFTER .AsEnumerable() rather than composed directly on
        // the query, mirroring NativeGroupByCtorProjectionTests/NativeSelectManyCtorProjectionTests exactly.
        // A server-side OrderBy/Take composed DIRECTLY on this Select now goes native too — see
        // Select_with_two_argument_ctor_only_dto_then_OrderBy_goes_native below — but this test is kept
        // client-side deliberately: it isolates the Select-only projection binder from the OrderBy arm, so a
        // future regression in either one fails independently rather than only as a combined shape.
        var results = db.Entities
            .Select(c => new CustomerListItem(c.CustomerID, c.City))
            .AsEnumerable()
            .OrderBy(c => c.City)
            .Take(3)
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal("Berlin", results[0].City);
        Assert.Equal("ALFKI", results[0].CustomerID);
        Assert.Equal("London", results[1].City);
        Assert.Equal("AROUT", results[1].CustomerID);
        Assert.Equal("Mexico", results[2].City);
        Assert.Equal("ANATR", results[2].CustomerID);
    }

    [Fact]
    public void Select_with_two_argument_ctor_only_dto_then_OrderBy_goes_native()
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(
            UniqueCollectionName(nameof(Select_with_two_argument_ctor_only_dto_then_OrderBy_goes_native)));
        coll.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ALFKI" }, { "City", "Berlin" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "ANATR" }, { "City", "Mexico" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerID", "AROUT" }, { "City", "London" } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(coll.CollectionNamespace.CollectionName);

        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Composed DIRECTLY on the query — the gap NativeSelectCtorProjectionTests' sibling test documents.
        // EF Core's nav-expansion rewrites the OrderBy key to `x => new CustomerListItem(x.CustomerID,
        // x.City).City`, whose receiver is the SAME Members-null NewExpression the Select projects.
        // Succeeding under NativeOnly (rather than throwing NativeTranslationNotSupportedException) proves
        // NativeSlotPopulator.PopulateSortSlot now resolves this shape.
        var results = db.Entities
            .Select(c => new CustomerListItem(c.CustomerID, c.City))
            .OrderBy(c => c.City)
            .Take(3)
            .ToList();

        Assert.Equal(3, results.Count);
        Assert.Equal("Berlin", results[0].City);
        Assert.Equal("ALFKI", results[0].CustomerID);
        Assert.Equal("London", results[1].City);
        Assert.Equal("AROUT", results[1].CustomerID);
        Assert.Equal("Mexico", results[2].City);
        Assert.Equal("ANATR", results[2].CustomerID);
    }
}
