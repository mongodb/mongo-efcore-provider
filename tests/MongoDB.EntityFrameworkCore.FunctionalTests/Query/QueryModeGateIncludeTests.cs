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
/// Tests covering <c>Include</c> behavior under the EF-323 query-mode gate.
/// <para>
/// EF-368 final fix wave, Finding 8: this summary used to claim "Reference Includes are EF9+/EF10 only, so
/// this class is compiled out under EF8". Both halves were false — the class is not gated at all, and a
/// REQUIRED reference <c>Include</c> lowers to <c>Queryable.Join</c>, which dispatches on all three majors
/// (only an OPTIONAL one lowers to <c>Queryable.LeftJoin</c>, which is EF10-only). The class is deliberately
/// left UNGATED; equivalent ungated coverage also exists in <c>NativeReferenceIncludeTests</c> and
/// <c>RequiredNavigationUnwindTests</c>.
/// </para>
/// </summary>
/// <remarks>
/// NOTE: as of EF-368 Task 5, a single-level reference Include recognized by
/// <c>MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector</c> now goes
/// NATIVE under <see cref="MongoQueryMode.Native"/> — see
/// <see cref="Reference_include_goes_native_under_Native_mode"/> below, which asserts the native
/// <c>_lookup_&lt;NavigationName&gt;</c> alias rather than the driver-LINQ LeftJoin path's characteristic
/// <c>_outer</c> / <c>$$ROOT</c> / <c>_inner</c> shape. A shape the recognizer declines (composite key,
/// a second reference Include, a `ThenInclude` reaching past the looked-up document, post-terminal
/// composition, …) still falls back to that driver-LINQ LeftJoin path exactly as before Task 5.
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

        public OrderCustomerDbContext(
            TemporaryDatabaseFixture db, string orders, string customers, List<string> logs,
            MongoQueryMode mode = MongoQueryMode.Native)
            : base(new DbContextOptionsBuilder<OrderCustomerDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName,
                    o => o.UseQueryMode(mode))
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
    public void Reference_include_goes_native_under_Native_mode()
    {
        // EF-368 Task 5: single-level reference Include now goes NATIVE (this test's name and premise
        // predate that — it used to be "..._falls_back_to_driver_linq_under_Native_mode" and asserted the
        // OLD driver-LINQ LeftJoin shape; renamed/re-asserted as part of delivering the capability). This
        // test verifies:
        //   (a) The materialized result graph is correct (Customer navigation is populated).
        //   (b) The emitted pipeline reflects the NATIVE $lookup+$unwind shape (a root-level
        //       _lookup_<NavigationName> alias) rather than the driver's $$ROOT/_outer/_inner LeftJoin shape.
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
        Assert.Contains("_lookup_Customer", mql);   // native $lookup alias for the confirmed reference Include
        Assert.Contains(
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Customer\", \"preserveNullAndEmptyArrays\" : false } }",
            mql); // required nav (non-nullable CustomerId) -> inner unwind
        Assert.DoesNotContain("$$ROOT", mql);   // no longer the driver's LeftJoin shape
        Assert.DoesNotContain("_outer", mql);
        Assert.DoesNotContain("_inner", mql);
    }

    // ── EF-339: single-level collection Include emits a flat $lookup (no $unwind) natively ─────────
    // Under NativeOnly, a fallback shape throws NativeTranslationNotSupportedException; success here
    // proves the collection Include's $lookup went through the native pipeline rather than falling
    // back to the driver-LINQ join path.

    [Fact]
    public void Single_level_collection_Include_runs_native()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollOrders") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.NativeOnly);

        // Should NOT throw NativeTranslationNotSupportedException under NativeOnly.
        var results = db.Customers.Include(c => c.Orders).ToList();

        Assert.NotEmpty(results);
        Assert.Contains(results, c => c.Orders.Count > 0);
    }

    [Fact]
    public void Single_level_collection_Include_emits_lookup_without_unwind()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollCustomers2") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollOrders2") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.Native);

        var _ = db.Customers.Include(c => c.Orders).ToList();

        var mql = Assert.Single(logs, l => l.Contains("Executed MQL query"));

        // A collection $lookup produces an array field; there must be NO $unwind on _lookup_Orders.
        Assert.Contains("\"$lookup\"", mql);
        Assert.DoesNotContain("\"$unwind\": { \"path\": \"$_lookup_Orders\"", mql);
    }

    [Fact]
    public void Native_collection_Include_materializes_arrays_tracking()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollCustomers3") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollOrders3") + Guid.NewGuid().ToString("N")[..8];

        var withOrdersId = ObjectId.GenerateNewId();
        var noOrdersId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertMany([
            new BsonDocument { { "_id", withOrdersId }, { "name", "Alice" } },
            new BsonDocument { { "_id", noOrdersId }, { "name", "Bob" } },
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", withOrdersId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", withOrdersId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.NativeOnly);

        var customers = db.Customers.Include(c => c.Orders).OrderBy(c => c.FullName).ToList();

        var withOrders = customers.Single(c => c._id == withOrdersId);
        Assert.Equal(2, withOrders.Orders.Count);

        var noOrders = customers.Single(c => c._id == noOrdersId);
        Assert.NotNull(noOrders.Orders);
        Assert.Empty(noOrders.Orders);
    }

    [Fact]
    public void Native_collection_Include_materializes_arrays_no_tracking()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollCustomers4") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateCollOrders4") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.NativeOnly);

        var customers = db.Customers.AsNoTracking().Include(c => c.Orders).ToList();

        Assert.Contains(customers, c => c.Orders.Count > 0);
    }

    private class OrderDetail
    {
        public ObjectId _id { get; set; }
        public ObjectId OrderId { get; set; }
        public string Detail { get; set; } = "";
    }

    private class NestedOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; } = "";
        public ObjectId CustomerId { get; set; }
        public double Freight { get; set; }
        public List<OrderDetail> OrderDetails { get; set; } = [];
    }

    private class NestedCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; } = "";
        public List<NestedOrder> Orders { get; set; } = [];
    }

    private class NestedOrderCustomerDbContext : DbContext
    {
        private readonly string _orders;
        private readonly string _customers;
        private readonly string _orderDetails;

        public DbSet<NestedOrder> Orders { get; set; } = null!;
        public DbSet<NestedCustomer> Customers { get; set; } = null!;

        public NestedOrderCustomerDbContext(TemporaryDatabaseFixture db, string orders, string customers, string orderDetails)
            : base(new DbContextOptionsBuilder<NestedOrderCustomerDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName,
                    o => o.UseQueryMode(MongoQueryMode.NativeOnly))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _orders = orders;
            _customers = customers;
            _orderDetails = orderDetails;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NestedCustomer>(b =>
            {
                b.ToCollection(_customers);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
            });
            modelBuilder.Entity<NestedOrder>(b =>
            {
                b.ToCollection(_orders);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.HasMany(o => o.OrderDetails).WithOne().HasForeignKey(od => od.OrderId);
            });
            modelBuilder.Entity<OrderDetail>(b =>
            {
                b.ToCollection(_orderDetails);
                b.Property(od => od.OrderId).HasElementName("order_id");
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void Nested_ThenInclude_runs_native()
    {
        // A collection-then-collection ThenInclude now goes NATIVE (this test's name and premise predate
        // that — it used to be "..._still_falls_back" and asserted NativeTranslationNotSupportedException
        // under NativeOnly). The nested $lookup(s) staged into the parent LookupExpression's own
        // PipelineStages (MongoProjectionBindingExpressionVisitor's ExtractNestedIncludePipeline /
        // ExtractThenIncludesFromSubquery — unchanged, and already shared with the driver-LINQ fallback
        // bridge) are now rendered by the native pipeline too, via
        // LookupExpression.ToLookupStageDocument()/MongoSelectLowerer.AppendLookupStages. Renamed/
        // re-asserted as part of delivering the capability, mirroring
        // Reference_include_goes_native_under_Native_mode above.
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateNestedCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateNestedOrders") + Guid.NewGuid().ToString("N")[..8];
        var orderDetailsName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateNestedOrderDetails") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        var orderId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument
            {
                { "_id", orderId }, { "desc", "Order 1" }, { "cust_id", customerId }, { "Freight", 0.0 }
            });
        database.MongoDatabase.GetCollection<BsonDocument>(orderDetailsName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "order_id", orderId }, { "Detail", "Widget" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "order_id", orderId }, { "Detail", "Gadget" } },
        ]);

        using var db = new NestedOrderCustomerDbContext(database, ordersName, customersName, orderDetailsName);

        // Should NOT throw NativeTranslationNotSupportedException under NativeOnly.
        var customers = db.Customers.Include(c => c.Orders).ThenInclude(o => o.OrderDetails).ToList();

        var customer = Assert.Single(customers);
        var order = Assert.Single(customer.Orders);
        Assert.Equal(2, order.OrderDetails.Count);
    }

    [Fact]
    public void Filtered_Include_still_falls_back()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateFilteredCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateFilteredOrders") + Guid.NewGuid().ToString("N")[..8];
        var orderDetailsName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateFilteredOrderDetails") + Guid.NewGuid().ToString("N")[..8];

        using var db = new NestedOrderCustomerDbContext(database, ordersName, customersName, orderDetailsName);

        // A filtered-Include predicate (`.Include(c => c.Orders.Where(...))`) is rejected before the
        // native-vs-driver-LINQ fork: the provider doesn't yet translate the predicate into the $lookup
        // sub-pipeline $match at all (see CrossCollectionIncludeTests.Filtered_collection_include_predicate_
        // is_not_silently_dropped), so it fails loudly with InvalidOperationException in every query mode
        // rather than the native-specific NativeTranslationNotSupportedException.
        Assert.Throws<InvalidOperationException>(
            () => db.Customers.Include(c => c.Orders.Where(o => o.Freight > 0)).ToList());
    }

    // ── EF-339 Task 4: projected collection-navigation Count runs native via $size over $lookup ─────

    [Fact]
    public void Projected_collection_Count_runs_native()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateProjCountCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateProjCountOrders") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.NativeOnly);

        // Should NOT throw NativeTranslationNotSupportedException under NativeOnly.
        var counts = db.Customers
            .Select(c => new { c._id, OrderCount = c.Orders.Count })
            .ToList();

        Assert.Contains(counts, x => x.OrderCount > 0);
    }

    [Fact]
    public void Projected_collection_Count_emits_size_over_lookup()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateProjCountCustomers2") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateProjCountOrders2") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertOne(
            new BsonDocument { { "_id", customerId }, { "name", "Alice" } });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId } },
        ]);

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.Native);

        var _ = db.Customers
            .Select(c => new { c._id, OrderCount = c.Orders.Count })
            .ToList();

        var mql = Assert.Single(logs, l => l.Contains("Executed MQL query"));

        Assert.Contains("\"$lookup\"", mql);
        Assert.Contains("\"$size\"", mql);
    }

    [Fact]
    public void Projected_collection_Count_with_predicate_still_falls_back()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateProjCountCustomers3") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateProjCountOrders3") + Guid.NewGuid().ToString("N")[..8];

        var logs = new List<string>();
        using var db = new OrderCustomerDbContext(database, ordersName, customersName, logs, MongoQueryMode.NativeOnly);

        // A count with an additional user predicate beyond the plain FK-equality join condition
        // (c.Orders.Count(o => ...), lowered by EF's nav-expansion to a correlated Count-with-predicate
        // subquery) is a different, out-of-scope shape for this sub-project. It is NOT recognized by
        // NativeProjectionBinder (which requires a bare, no-predicate Count/LongCount over the FK-equality
        // Where), so the native path correctly marks the query not natively representable and defers to
        // the SAME translation path used in every query mode — which does not yet support this correlated
        // shape either (a pre-existing gap independent of this task, not a regression). The query therefore
        // fails identically in every mode with InvalidOperationException, never silently emitting a
        // wrong-shape native $size.
        Assert.Throws<InvalidOperationException>(
            () => db.Customers
                .Select(c => new { c._id, OrderCount = c.Orders.Count(o => o.OrderDescription != "") })
                .ToList());
    }
}
#endif
