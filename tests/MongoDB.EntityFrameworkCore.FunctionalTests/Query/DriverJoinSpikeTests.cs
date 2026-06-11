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

using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Spike tests to verify the MongoDB C# driver's LINQ provider generates $lookup
/// for Join and GroupJoin operations. These tests use the driver directly (no EF).
/// </summary>
[XUnitCollection("QueryTests")]
public class DriverJoinSpikeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Driver_Join_generates_lookup_and_returns_results()
    {
        var (orders, customers) = SetupData();

        var result = orders.AsQueryable()
            .Join(
                customers.AsQueryable(),
                o => o.CustomerId,
                c => c.Id,
                (o, c) => new { OrderDesc = o.Description, CustomerName = c.Name })
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.OrderDesc == "Order 1" && r.CustomerName == "Alice");
        Assert.Contains(result, r => r.OrderDesc == "Order 2" && r.CustomerName == "Alice");
        Assert.Contains(result, r => r.OrderDesc == "Order 3" && r.CustomerName == "Bob");
    }

    [Fact]
    public void Driver_GroupJoin_generates_lookup_and_returns_grouped_results()
    {
        var (orders, customers) = SetupData();

        var result = customers.AsQueryable()
            .GroupJoin(
                orders.AsQueryable(),
                c => c.Id,
                o => o.CustomerId,
                (c, orderGroup) => new { CustomerName = c.Name, OrderCount = orderGroup.Count() })
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.CustomerName == "Alice" && r.OrderCount == 2);
        Assert.Contains(result, r => r.CustomerName == "Bob" && r.OrderCount == 1);
    }

    [Fact]
    public void Driver_GroupJoin_SelectMany_DefaultIfEmpty_produces_left_join()
    {
        var (orders, customers) = SetupData();

        // This is the pattern EF generates for Include via LeftJoin
        var result = orders.AsQueryable()
            .GroupJoin(
                customers.AsQueryable(),
                o => o.CustomerId,
                c => c.Id,
                (o, group) => new { Outer = o, Group = group })
            .SelectMany(
                x => x.Group.DefaultIfEmpty(),
                (x, c) => new { Order = x.Outer, Customer = c })
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.Order.Description == "Order 1" && r.Customer!.Name == "Alice");
    }

    [Fact]
    public void Driver_GroupJoin_SelectMany_returns_BsonDocuments()
    {
        var (orders, customers) = SetupData();

        // Check the raw BsonDocument structure from the driver
        var bsonOrders = database.MongoDatabase.GetCollection<BsonDocument>(orders.CollectionNamespace.CollectionName);
        var bsonCustomers = database.MongoDatabase.GetCollection<BsonDocument>(customers.CollectionNamespace.CollectionName);

        var result = bsonOrders.AsQueryable()
            .GroupJoin(
                bsonCustomers.AsQueryable(),
                o => o["CustomerId"],
                c => c["_id"],
                (o, group) => new { Outer = o, Group = group })
            .SelectMany(
                x => x.Group.DefaultIfEmpty(),
                (x, c) => new { x.Outer, Inner = c })
            .ToList();

        Assert.Equal(3, result.Count);
        // Check the structure - Outer should be a BsonDocument with order fields
        var first = result.First();
        Assert.NotNull(first.Outer);
        Assert.True(first.Outer.Contains("Description"));
    }

    [Fact]
    public void Driver_Join_with_entity_results()
    {
        var (orders, customers) = SetupData();

        var result = orders.AsQueryable()
            .Join(
                customers.AsQueryable(),
                o => o.CustomerId,
                c => c.Id,
                (o, c) => new OrderWithCustomer { Order = o, Customer = c })
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.All(result, r =>
        {
            Assert.NotNull(r.Order);
            Assert.NotNull(r.Customer);
        });
    }

    private (IMongoCollection<SpikeOrder> orders, IMongoCollection<SpikeCustomer> customers) SetupData()
    {
        var customerId1 = ObjectId.GenerateNewId();
        var customerId2 = ObjectId.GenerateNewId();

        var customersName = "spike_customers_" + Guid.NewGuid().ToString("N")[..8];
        var ordersName = "spike_orders_" + Guid.NewGuid().ToString("N")[..8];

        var customers = database.MongoDatabase.GetCollection<SpikeCustomer>(customersName);
        customers.InsertMany([
            new SpikeCustomer { Id = customerId1, Name = "Alice" },
            new SpikeCustomer { Id = customerId2, Name = "Bob" }
        ]);

        var orders = database.MongoDatabase.GetCollection<SpikeOrder>(ordersName);
        orders.InsertMany([
            new SpikeOrder { Id = ObjectId.GenerateNewId(), Description = "Order 1", CustomerId = customerId1 },
            new SpikeOrder { Id = ObjectId.GenerateNewId(), Description = "Order 2", CustomerId = customerId1 },
            new SpikeOrder { Id = ObjectId.GenerateNewId(), Description = "Order 3", CustomerId = customerId2 }
        ]);

        return (orders, customers);
    }

    class SpikeOrder
    {
        public ObjectId Id { get; set; }
        public string Description { get; set; }
        public ObjectId CustomerId { get; set; }
    }

    class SpikeCustomer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
    }

    class OrderWithCustomer
    {
        public SpikeOrder Order { get; set; }
        public SpikeCustomer Customer { get; set; }
    }
}
