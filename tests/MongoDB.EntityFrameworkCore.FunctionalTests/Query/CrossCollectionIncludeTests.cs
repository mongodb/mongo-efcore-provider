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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Tests for cross-collection Join, Include, and navigation property access.
/// IMPORTANT: C# property names intentionally differ from BSON element names
/// to verify that EF element name mappings are respected through the $lookup pipeline.
/// </summary>
[XUnitCollection("QueryTests")]
public class CrossCollectionIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Model_has_correct_navigations_for_cross_collection_entities()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();
        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        var orderType = db.Model.FindEntityType(typeof(Order))!;
        var customerType = db.Model.FindEntityType(typeof(Customer))!;

        Assert.Null(customerType.FindOwnership());
        Assert.Null(orderType.FindOwnership());

        var customerNav = orderType.FindNavigation(nameof(Order.Customer));
        Assert.NotNull(customerNav);
        Assert.Equal(typeof(Customer), customerNav.TargetEntityType.ClrType);

        var ordersNav = customerType.FindNavigation(nameof(Customer.Orders));
        Assert.NotNull(ordersNav);
        Assert.True(ordersNav.IsCollection);
    }

    [Fact]
    public void Basic_query_without_include_works()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();
        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        var order = db.Orders.First();
        Assert.NotNull(order);
        Assert.NotNull(order.OrderDescription);
    }

    [Fact]
    public void Include_reference_navigation_materializes_related_entity()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.FullName);
    }

    [Fact]
    public void Include_reference_navigation_with_no_tracking()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.AsNoTracking().Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.FullName);
    }

    [Fact]
    public void Include_collection_navigation_materializes_related_entities()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var customer = db.Customers.Include(c => c.Orders).First(c => c.FullName == "Alice");

        Assert.NotNull(customer.Orders);
        Assert.Equal(2, customer.Orders.Count);
    }

    [Fact]
    public void Include_reference_navigation_null_fk_returns_null()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        var orphanOrder = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "desc", "Orphan order" }
        };
        database.MongoDatabase.GetCollection<BsonDocument>(ordersCollection).InsertOne(orphanOrder);

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.Include(o => o.Customer).First(o => o.OrderDescription == "Orphan order");

        Assert.Null(order.Customer);
    }

    [Fact]
    public void Where_on_navigation_property_with_entity_projection()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var orders = db.Orders
            .Where(o => o.Customer.FullName == "Alice")
            .Select(o => new { o._id, o.OrderDescription })
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.OrderDescription));
    }

    [Fact]
    public void Where_on_navigation_property_with_projection()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var orders = db.Orders
            .Where(o => o.Customer.FullName == "Alice")
            .Select(o => new { o.OrderDescription })
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.OrderDescription));
    }

    [Fact]
    public void Select_navigation_property_projects_correctly()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var customerNames = db.Orders.Select(o => o.Customer.FullName).ToList();

        Assert.Equal(3, customerNames.Count);
        Assert.Contains("Alice", customerNames);
        Assert.Contains("Bob", customerNames);
    }

    [Fact]
    public void Select_anonymous_with_navigation_property()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var result = db.Orders
            .Select(o => new { o.OrderDescription, CustomerName = o.Customer.FullName })
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.Contains(result, r => r.OrderDescription == "Order 1" && r.CustomerName == "Alice");
    }

    [Fact]
    public void Include_multiple_navigations_on_same_entity()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        // Include both reference (Customer) and then get customer's Orders (collection)
        var order = db.Orders
            .Include(o => o.Customer)
            .First();

        Assert.NotNull(order.Customer);

        // Now test customer with collection include
        var customer = db.Customers
            .Include(c => c.Orders)
            .First(c => c.FullName == "Alice");

        Assert.NotNull(customer.Orders);
        Assert.Equal(2, customer.Orders.Count);
    }

    [Fact]
    public void Include_multi_level_materializes_nested_entities()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders
            .Include(o => o.Customer)
                .ThenInclude(c => c.Orders)
            .First();

        Assert.NotNull(order.Customer);
        Assert.NotNull(order.Customer.Orders);
        Assert.True(order.Customer.Orders.Count > 0);
    }

    [Fact]
    public void Include_self_join_materializes_related_entity()
    {
        var staffName = TemporaryDatabaseFixtureBase.CreateCollectionName("Staff") + Guid.NewGuid().ToString("N")[..8];
        var managerId = ObjectId.GenerateNewId();
        var employeeId = ObjectId.GenerateNewId();

        var staff = database.MongoDatabase.GetCollection<BsonDocument>(staffName);
        staff.InsertMany([
            new BsonDocument { { "_id", managerId }, { "emp_name", "Boss" } },
            new BsonDocument { { "_id", employeeId }, { "emp_name", "Worker" }, { "mgr_id", managerId } }
        ]);

        using var db = new StaffDbContext(database, staffName);
        var allStaff = db.Staff.Include(s => s.Manager).ToList();
        var employee = allStaff.First(s => s.EmployeeName == "Worker");

        Assert.NotNull(employee.Manager);
        Assert.Equal("Boss", employee.Manager.EmployeeName);
        Assert.Null(allStaff.First(s => s.EmployeeName == "Boss").Manager);
    }

    // BSON uses: desc, cust_id for Orders; name for Customers
    // C# uses:   OrderDescription, CustomerId for Orders; FullName for Customers
    private (string ordersCollection, string customersCollection) SetupOrdersAndCustomers()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("IncludeCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("IncludeOrders") + Guid.NewGuid().ToString("N")[..8];

        var customerId1 = ObjectId.GenerateNewId();
        var customerId2 = ObjectId.GenerateNewId();

        var customers = database.MongoDatabase.GetCollection<BsonDocument>(customersName);
        customers.InsertMany([
            new BsonDocument { { "_id", customerId1 }, { "name", "Alice" } },
            new BsonDocument { { "_id", customerId2 }, { "name", "Bob" } }
        ]);

        var orders = database.MongoDatabase.GetCollection<BsonDocument>(ordersName);
        orders.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", customerId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", customerId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 3" }, { "cust_id", customerId2 } }
        ]);

        return (ordersName, customersName);
    }

    class Order
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer Customer { get; set; }
    }

    class Customer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public List<Order> Orders { get; set; }
    }

    class OrderCustomerDbContext : DbContext
    {
        private readonly TemporaryDatabaseFixture _database;
        private readonly string _ordersCollection;
        private readonly string _customersCollection;

        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }

        public OrderCustomerDbContext(
            TemporaryDatabaseFixture database,
            string ordersCollection,
            string customersCollection)
            : base(new DbContextOptionsBuilder<OrderCustomerDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .Options)
        {
            _database = database;
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(_customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.HasMany(c => c.Orders)
                    .WithOne(o => o.Customer)
                    .HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
            });
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }

    class StaffMember
    {
        public ObjectId _id { get; set; }
        public string EmployeeName { get; set; }
        public ObjectId? ManagerId { get; set; }
        public StaffMember Manager { get; set; }
        public List<StaffMember> DirectReports { get; set; }
    }

    class StaffDbContext : DbContext
    {
        private readonly string _collectionName;

        public DbSet<StaffMember> Staff { get; set; }

        public StaffDbContext(TemporaryDatabaseFixture database, string collectionName)
            : base(new DbContextOptionsBuilder<StaffDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .Options)
        {
            _collectionName = collectionName;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<StaffMember>(b =>
            {
                b.ToCollection(_collectionName);
                b.Property(s => s.EmployeeName).HasElementName("emp_name");
                b.Property(s => s.ManagerId).HasElementName("mgr_id");
                b.HasOne(s => s.Manager)
                    .WithMany(s => s.DirectReports)
                    .HasForeignKey(s => s.ManagerId);
            });
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }
}
