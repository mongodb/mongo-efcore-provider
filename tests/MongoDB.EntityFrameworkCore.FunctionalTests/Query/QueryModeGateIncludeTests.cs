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
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Tests covering <c>Include</c> behavior under the query-mode gate.
/// <para>
/// Not version-gated: a required reference <c>Include</c> lowers to <c>Queryable.Join</c> on every EF major
/// (only an optional one uses the EF10-only <c>LeftJoin</c>).
/// </para>
/// </summary>
/// <remarks>
/// A recognized single-level reference Include goes native (root-level <c>_lookup_&lt;NavigationName&gt;</c> alias);
/// a declined shape falls back to the driver-LINQ LeftJoin path (<c>_outer</c> / <c>$$ROOT</c> / <c>_inner</c>).
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
        // Verifies (a) the materialized graph is correct and (b) the pipeline has the native $lookup+$unwind
        // shape rather than the driver's $$ROOT/_outer/_inner LeftJoin shape.
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

        // (b) Native shape, not the driver-LINQ LeftJoin shape.
        var mql = Assert.Single(logs, l => l.Contains("Executed MQL query"));
        Assert.Contains("_lookup_Customer", mql);   // native $lookup alias for the confirmed reference Include
        Assert.Contains(
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Customer\", \"preserveNullAndEmptyArrays\" : false } }",
            mql); // required nav (non-nullable CustomerId) -> inner unwind
        Assert.DoesNotContain("$$ROOT", mql);   // not the driver's LeftJoin shape
        Assert.DoesNotContain("_outer", mql);
        Assert.DoesNotContain("_inner", mql);
    }

    // Single-level collection Include emits a flat $lookup (no $unwind) natively; success under NativeOnly
    // proves it didn't fall back.

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

        public NestedOrderCustomerDbContext(
            TemporaryDatabaseFixture db, string orders, string customers, string orderDetails,
            MongoQueryMode mode = MongoQueryMode.NativeOnly, ILoggerFactory? loggerFactory = null)
            : base(Configure(new DbContextOptionsBuilder<NestedOrderCustomerDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName,
                    o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)), loggerFactory)
                .Options)
        {
            _orders = orders;
            _customers = customers;
            _orderDetails = orderDetails;
        }

        // Optional MQL capture so a caller can assert the route taken, not just the data. Null means no
        // UseLoggerFactory call at all.
        private static DbContextOptionsBuilder<NestedOrderCustomerDbContext> Configure(
            DbContextOptionsBuilder<NestedOrderCustomerDbContext> builder, ILoggerFactory? loggerFactory)
            => loggerFactory is null ? builder : builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();

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
        // A collection-then-collection ThenInclude goes native: the nested $lookup(s) staged into the parent
        // LookupExpression's PipelineStages are rendered via MongoSelectLowerer.AppendLookupStages.
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
    public void Explicit_DriverLinq_mode_is_unaffected_by_a_separate_Include_plus_projected_list_of_the_same_nav()
    {
        // A projected `Orders = c.Orders.ToList()` alongside a separate `.Include(c => c.Orders).ThenInclude(...)`
        // on the same navigation (the Multi_level_includes_are_applied_with_skip shape): explicit DriverLinq must
        // still return correct data and actually take the DriverLinq route. The array-typed leaf makes the
        // mixed shaper fold the projection client-side, so a genuine DriverLinq run never emits `$project`.
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateDriverLinqCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateDriverLinqOrders") + Guid.NewGuid().ToString("N")[..8];
        var orderDetailsName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateDriverLinqOrderDetails") + Guid.NewGuid().ToString("N")[..8];

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

        var (loggerFactory, spy) = SpyLoggerProvider.Create();
        using var db = new NestedOrderCustomerDbContext(
            database, ordersName, customersName, orderDetailsName, MongoQueryMode.DriverLinq, loggerFactory);

        var results = db.Customers
            .Include(c => c.Orders).ThenInclude(o => o.OrderDetails)
            .Select(c => new { c.FullName, Orders = c.Orders.ToList() })
            .ToList();

        var row = Assert.Single(results);
        Assert.Equal("Alice", row.FullName);
        var order = Assert.Single(row.Orders);
        Assert.Equal("Order 1", order.OrderDescription);
        Assert.Equal(2, order.OrderDetails.Count);

        // The route assertion: a genuine DriverLinq/mixed-shaper execution for this shape never emits a
        // native $project — confirm this run didn't silently go native instead.
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.DoesNotContain("$project", mql);
        Assert.Equal(["Gadget", "Widget"], order.OrderDetails.Select(d => d.Detail).OrderBy(d => d).ToArray());
    }

    [Fact]
    public void NativeOnly_mode_goes_native_for_a_separate_Include_plus_projected_list_of_the_same_nav()
    {
        // Positive twin of the DriverLinq test above, under the context's default NativeOnly: succeeding proves
        // native, and a terminal `$project` retaining `FullName`/`_lookup_Orders`/`_id` confirms the route.
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateNativeOnlyCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateNativeOnlyOrders") + Guid.NewGuid().ToString("N")[..8];
        var orderDetailsName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateNativeOnlyOrderDetails") + Guid.NewGuid().ToString("N")[..8];

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

        var (loggerFactory, spy) = SpyLoggerProvider.Create();
        using var db = new NestedOrderCustomerDbContext(
            database, ordersName, customersName, orderDetailsName, MongoQueryMode.NativeOnly, loggerFactory);

        // Should NOT throw NativeTranslationNotSupportedException under NativeOnly.
        var results = db.Customers
            .Include(c => c.Orders).ThenInclude(o => o.OrderDetails)
            .Select(c => new { c.FullName, Orders = c.Orders.ToList() })
            .ToList();

        var row = Assert.Single(results);
        Assert.Equal("Alice", row.FullName);
        var order = Assert.Single(row.Orders);
        Assert.Equal("Order 1", order.OrderDescription);
        Assert.Equal(2, order.OrderDetails.Count);
        Assert.Equal(["Gadget", "Widget"], order.OrderDetails.Select(d => d.Detail).OrderBy(d => d).ToArray());

        // The route assertion: a genuinely native execution for this shape emits a terminal $project.
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("$project", mql);
    }

    [Fact]
    public void Filtered_Include_still_falls_back()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateFilteredCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateFilteredOrders") + Guid.NewGuid().ToString("N")[..8];
        var orderDetailsName = TemporaryDatabaseFixtureBase.CreateCollectionName("GateFilteredOrderDetails") + Guid.NewGuid().ToString("N")[..8];

        using var db = new NestedOrderCustomerDbContext(database, ordersName, customersName, orderDetailsName);

        // A filtered-Include predicate is rejected before the native/driver-LINQ fork (see
        // CrossCollectionIncludeTests.Filtered_collection_include_predicate_is_not_silently_dropped), so it
        // throws InvalidOperationException in every mode.
        Assert.Throws<InvalidOperationException>(
            () => db.Customers.Include(c => c.Orders.Where(o => o.Freight > 0)).ToList());
    }

    // Projected collection-navigation Count runs native via $size over $lookup.

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

        // c.Orders.Count(o => ...) is a correlated Count-with-predicate, which NativeProjectionBinder doesn't
        // recognize and the fallback doesn't support either, so it throws InvalidOperationException in every
        // mode rather than emitting a wrong native $size.
        Assert.Throws<InvalidOperationException>(
            () => db.Customers
                .Select(c => new { c._id, OrderCount = c.Orders.Count(o => o.OrderDescription != "") })
                .ToList());
    }
}
#endif
