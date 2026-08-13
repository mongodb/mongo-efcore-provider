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
using Microsoft.EntityFrameworkCore.Diagnostics;
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

#if !EF8 && !EF9
    [Fact]
    public void Include_reference_navigation_materializes_related_entity()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.FullName);
    }
#endif

#if !EF8 && !EF9
    [Fact]
    public void Include_reference_navigation_with_no_tracking()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.AsNoTracking().Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.FullName);
    }
#endif

    [Fact]
    public void Include_collection_navigation_materializes_related_entities()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var customer = db.Customers.Include(c => c.Orders).First(c => c.FullName == "Alice");

        Assert.NotNull(customer.Orders);
        Assert.Equal(2, customer.Orders.Count);
    }

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
    [Fact]
    public void Include_optional_reference_preserves_principal_with_null_fk()
    {
        // A bare reference Include (Orders.Include(o => o.Customer)) is a LEFT-OUTER join: principals whose
        // optional FK is null/absent must survive with a null navigation. This exercises the driver-native
        // left-join pipeline (manual $project/$lookup/$unwind(preserveNullAndEmptyArrays:true)/$project) and
        // guards against regressing it back to an inner join (which would silently drop the orphan order).
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        var orphanOrder = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "desc", "Orphan order" }
        };
        database.MongoDatabase.GetCollection<BsonDocument>(ordersCollection).InsertOne(orphanOrder);

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var orders = db.Orders.Include(o => o.Customer).ToList();

        // All four orders (three with a customer + the orphan) must be returned (left-outer, not inner).
        Assert.Equal(4, orders.Count);
        var orphan = Assert.Single(orders, o => o.OrderDescription == "Orphan order");
        Assert.Null(orphan.Customer);
        Assert.All(orders.Where(o => o.OrderDescription != "Orphan order"), o => Assert.NotNull(o.Customer));
    }
#endif

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
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
#endif

#if !EF8 && !EF9
    [Fact]
    public void Include_multi_join_then_where_composed_after_is_not_silently_dropped()
    {
        // EF-369 repro: a multi-hop reference Include chain (two reference joins) flattens to forced-unwind
        // $lookup stages (see MongoQueryableMethodTranslatingExpressionVisitor's isSecondOrLaterJoin path).
        // A Where composed AFTER the Include chain reads through the joined-in navigation properties, which
        // the provider does not yet rewrite to read the flattened $lookup fields (TODO EF-317). It must fail
        // loudly (translation failure) rather than silently drop the predicate and return every order.
        var (ordersCollection, customersCollection, regionsCollection) = SetupOrdersCustomersAndRegions();

        using var db = new OrderCustomerRegionDbContext(database, ordersCollection, customersCollection, regionsCollection);

        Assert.Throws<InvalidOperationException>(() =>
            db.Orders
                .Include(o => o.Customer)
                    .ThenInclude(c => c.Region)
                .Where(o => o.Customer.Region.RegionName == "West")
                .ToList());
    }

    [Fact]
    public void Include_multi_join_then_orderby_composed_after_is_not_silently_dropped()
    {
        // Same EF-369 gap for OrderBy: a sort key selector reaching through the joined-in navigation
        // properties composed after a multi-hop reference Include chain must also fail loudly rather than
        // silently sort against the wrong (un-joined) source.
        var (ordersCollection, customersCollection, regionsCollection) = SetupOrdersCustomersAndRegions();

        using var db = new OrderCustomerRegionDbContext(database, ordersCollection, customersCollection, regionsCollection);

        Assert.Throws<InvalidOperationException>(() =>
            db.Orders
                .Include(o => o.Customer)
                    .ThenInclude(c => c.Region)
                .OrderBy(o => o.Customer.Region.RegionName)
                .ToList());
    }

    [Fact]
    public void Include_multi_join_then_root_only_where_and_orderby_are_reattached_correctly()
    {
        // A Where/OrderBy composed after a multi-hop Include chain that only reads a property of the root
        // entity (never crossing into the joined-in Customer/Region data) doesn't need any $lookup field -
        // it must be correctly reattached to the un-joined base source and actually applied, not silently
        // dropped (which would happen to look like it "worked" if left unverified, since dropping a no-op
        // filter/sort is indistinguishable from a correctly-applied one unless the assertions are specific
        // enough to catch it).
        var (ordersCollection, customersCollection, regionsCollection) = SetupOrdersCustomersAndRegions();

        using var db = new OrderCustomerRegionDbContext(database, ordersCollection, customersCollection, regionsCollection);
        var orders = db.Orders
            .Include(o => o.Customer)
                .ThenInclude(c => c.Region)
            .Where(o => o.OrderDescription != "Order 1")
            .OrderByDescending(o => o.OrderDescription)
            .ToList();

        Assert.Equal(["Order 3", "Order 2"], orders.Select(o => o.OrderDescription).ToArray());
        Assert.All(orders, o => Assert.NotNull(o.Customer?.Region));
    }

    [Fact]
    public void Include_multi_join_then_skip_take_composed_after_still_works()
    {
        // Skip/Take carry no lambda over the joined shape, so unlike Where/OrderBy above they can be
        // (and must continue to be) safely reattached to the un-joined base source instead of being
        // silently discarded.
        var (ordersCollection, customersCollection, regionsCollection) = SetupOrdersCustomersAndRegions();

        using var db = new OrderCustomerRegionDbContext(database, ordersCollection, customersCollection, regionsCollection);
        var orders = db.Orders
            .Include(o => o.Customer)
                .ThenInclude(c => c.Region)
            .OrderBy(o => o.OrderDescription)
            .Skip(1)
            .Take(2)
            .ToList();

        Assert.Equal(["Order 2", "Order 3"], orders.Select(o => o.OrderDescription).ToArray());
        Assert.All(orders, o => Assert.NotNull(o.Customer?.Region));
    }
#endif

#if !EF8 && !EF9
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
#endif

    [Fact]
    public void Filtered_collection_include_predicate_is_not_silently_dropped()
    {
        // A user filtered-Include predicate (.Include(c => c.Orders.Where(...))) lowers to a Where inside
        // the collection subquery. The provider does not yet translate that predicate into the $lookup
        // sub-pipeline $match. It must fail loudly (translation failure) rather than silently drop the
        // predicate and return ALL of the customer's orders.
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        Assert.Throws<InvalidOperationException>(() =>
            db.Customers
                .Include(c => c.Orders.Where(o => o.OrderDescription == "Order 1"))
                .First(c => c.FullName == "Alice"));
    }

    [Fact]
    public void Query_filter_on_collection_include_target_is_not_silently_dropped()
    {
        // A HasQueryFilter on the dependent entity (e.g. soft-delete / multi-tenant) also lowers to a Where
        // inside the collection-Include subquery. The provider does not yet translate it into the $lookup
        // sub-pipeline $match, so it must fail loudly rather than silently bypass the filter and return
        // soft-deleted rows.
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new SoftDeleteOrderCustomerDbContext(database, ordersCollection, customersCollection);

        Assert.Throws<InvalidOperationException>(() =>
            db.Customers
                .Include(c => c.Orders)
                .First(c => c.FullName == "Alice"));
    }

    // BSON uses: desc, cust_id, region_id for Orders/Customers; region_name for Regions.
    // C# uses:   OrderDescription, CustomerId, RegionId for Orders/Customers; RegionName for Regions.
    private (string ordersCollection, string customersCollection, string regionsCollection) SetupOrdersCustomersAndRegions()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("MultiJoinCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("MultiJoinOrders") + Guid.NewGuid().ToString("N")[..8];
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName("MultiJoinRegions") + Guid.NewGuid().ToString("N")[..8];

        var westRegionId = ObjectId.GenerateNewId();
        var eastRegionId = ObjectId.GenerateNewId();

        var regions = database.MongoDatabase.GetCollection<BsonDocument>(regionsName);
        regions.InsertMany([
            new BsonDocument { { "_id", westRegionId }, { "region_name", "West" } },
            new BsonDocument { { "_id", eastRegionId }, { "region_name", "East" } }
        ]);

        var westCustomerId = ObjectId.GenerateNewId();
        var eastCustomerId = ObjectId.GenerateNewId();

        var customers = database.MongoDatabase.GetCollection<BsonDocument>(customersName);
        customers.InsertMany([
            new BsonDocument { { "_id", westCustomerId }, { "name", "Alice" }, { "region_id", westRegionId } },
            new BsonDocument { { "_id", eastCustomerId }, { "name", "Bob" }, { "region_id", eastRegionId } }
        ]);

        var orders = database.MongoDatabase.GetCollection<BsonDocument>(ordersName);
        orders.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", westCustomerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", westCustomerId } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 3" }, { "cust_id", eastCustomerId } }
        ]);

        return (ordersName, customersName, regionsName);
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
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
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

    // Same model as OrderCustomerDbContext but with a soft-delete HasQueryFilter on the dependent (Order).
    class SoftDeleteOrderCustomerDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _customersCollection;

        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }

        public SoftDeleteOrderCustomerDbContext(
            TemporaryDatabaseFixture database,
            string ordersCollection,
            string customersCollection)
            : base(new DbContextOptionsBuilder<SoftDeleteOrderCustomerDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
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
                // Soft-delete-style query filter on the dependent, expressed over a mapped property so
                // materialization itself is unaffected — the only difference vs the plain model is the
                // extra Where lowered into the collection-Include subquery.
                b.HasQueryFilter(o => o.OrderDescription != "tombstone");
            });
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }

#if !EF8 && !EF9
    class MultiJoinRegion
    {
        public ObjectId _id { get; set; }
        public string RegionName { get; set; }
    }

    class MultiJoinCustomer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public ObjectId? RegionId { get; set; }
        public MultiJoinRegion Region { get; set; }
    }

    class MultiJoinOrder
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public MultiJoinCustomer Customer { get; set; }
    }

    class OrderCustomerRegionDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _customersCollection;
        private readonly string _regionsCollection;

        public DbSet<MultiJoinOrder> Orders { get; set; }
        public DbSet<MultiJoinCustomer> Customers { get; set; }
        public DbSet<MultiJoinRegion> Regions { get; set; }

        public OrderCustomerRegionDbContext(
            TemporaryDatabaseFixture database,
            string ordersCollection,
            string customersCollection,
            string regionsCollection)
            : base(new DbContextOptionsBuilder<OrderCustomerRegionDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
            _regionsCollection = regionsCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<MultiJoinRegion>(b =>
            {
                b.ToCollection(_regionsCollection);
                b.Property(r => r.RegionName).HasElementName("region_name");
            });

            modelBuilder.Entity<MultiJoinCustomer>(b =>
            {
                b.ToCollection(_customersCollection);
                b.Property(c => c.FullName).HasElementName("name");
                b.Property(c => c.RegionId).HasElementName("region_id");
                b.HasOne(c => c.Region).WithMany().HasForeignKey(c => c.RegionId);
            });

            modelBuilder.Entity<MultiJoinOrder>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
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
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
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
#endif
}
