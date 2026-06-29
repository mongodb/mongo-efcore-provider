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

#if !EF8 && !EF9
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Tests covering <c>Include</c> behavior under the EF-323 query-mode gate. Reference Includes are EF9+/EF10
/// only, so this class is compiled out under EF8.
/// </summary>
/// <remarks>
/// NOTE: Single-level reference Include is NOT yet native — native reference-Include is deferred to the
/// Includes sub-project. Under <see cref="MongoQueryMode.Native"/> the provider falls back to the driver-LINQ
/// LeftJoin path, which emits the characteristic <c>_outer</c> / <c>$$ROOT</c> / <c>_inner</c> pipeline shape
/// rather than a native <c>_lookup_&lt;NavigationName&gt;</c> alias.
/// </remarks>
[XUnitCollection("QueryTests")]
public class QueryModeGateIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; } = "";
        public ObjectId CustomerId { get; set; }
        public Customer Customer { get; set; } = null!;
    }

    private class Customer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    private class OrderCustomerDbContext : DbContext
    {
        private readonly string _orders;
        private readonly string _customers;
        private readonly List<string> _logs;

        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Customer> Customers { get; set; } = null!;

        public OrderCustomerDbContext(TemporaryDatabaseFixture db, string orders, string customers, List<string> logs)
            : base(new DbContextOptionsBuilder<OrderCustomerDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName,
                    o => o.UseQueryMode(MongoQueryMode.Native))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .LogTo(logs.Add)
                .EnableSensitiveDataLogging()
                .Options)
        {
            _orders = orders;
            _customers = customers;
            _logs = logs;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(_customers);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_orders);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void Reference_include_falls_back_to_driver_linq_under_Native_mode()
    {
        // Single-level reference Include is NOT yet native — native reference-Include is deferred to the
        // Includes sub-project. Under MongoQueryMode.Native the provider falls back to the driver-LINQ
        // LeftJoin path. This test verifies:
        //   (a) The materialized result graph is correct (Customer navigation is populated).
        //   (b) The emitted pipeline reflects the driver-LINQ shape (contains the $$ROOT / _outer / _inner
        //       markers that characterise the LeftJoin path) and does NOT contain a native _lookup_ alias.
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateOrders") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs);

        var orders = db.Orders.Include(o => o.Customer).OrderBy(o => o.OrderDescription).ToList();

        // (a) Correct materialized graph: both orders have their Customer navigation populated.
        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.All(orders, o => Assert.Equal("Alice", o.Customer.FullName));

        // (b) Driver-LINQ LeftJoin shape: $$ROOT is projected as _outer, related documents collected as _inner.
        //     This is distinct from the future native path which would use a _lookup_<NavigationName> alias.
        var mql = Assert.Single(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains("$$ROOT", mql);      // driver projects principal rows as $$ROOT before joining
        Assert.Contains("_outer", mql);      // LeftJoin wrapper field for principal document
        Assert.Contains("_inner", mql);      // LeftJoin wrapper field for related document
        Assert.DoesNotContain("_lookup_", mql);  // native path would use _lookup_<NavigationName> alias
    }
}
#endif
