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
    public void Include_three_hop_then_include_materializes_nested_entities()
    {
        // EF-372 regression test: a THIRD hop reference Include must prefix its $lookup localField with
        // the second hop's alias too, not just the first. Getting this wrong doesn't throw — it silently
        // returns zero rows — so assert row counts/identities rather than MQL.
        var (linesCollection, ordersCollection, buyersCollection, regionsCollection) = SetupThreeHopChain();

        using var db = new ThreeHopDbContext(database, linesCollection, ordersCollection, buyersCollection, regionsCollection);
        var lines = db.HopLines
            .Include(l => l.Order)
                .ThenInclude(o => o.Buyer)
                    .ThenInclude(b => b.Region)
            .ToList();

        Assert.Equal(4, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.NotNull(l.Order);
            Assert.NotNull(l.Order.Buyer);
            Assert.NotNull(l.Order.Buyer.Region);
        });
        Assert.Equal(2, lines.Select(l => l.Order.Buyer.Region.RegionName).Distinct().Count());
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

    [Fact]
    public void Chained_self_referencing_navigation_filter_resolves_reference_not_inverse_collection()
    {
        // StaffMember declares BOTH directions of the self-reference: Manager (reference, dependent
        // side) and DirectReports (inverse collection, principal side) - they share the same
        // IForeignKey. A chained filter through the reference nav must resolve Manager, not
        // DirectReports, for each hop: picking the inverse collection nav flips the $lookup's join
        // direction (LookupExpression branches on Navigation.IsOnDependent), silently walking the
        // relationship backwards instead of throwing.
        var staffName = TemporaryDatabaseFixtureBase.CreateCollectionName("Staff") + Guid.NewGuid().ToString("N")[..8];
        var ceoId = ObjectId.GenerateNewId();
        var vpId = ObjectId.GenerateNewId();
        var managerId = ObjectId.GenerateNewId();

        var staff = database.MongoDatabase.GetCollection<BsonDocument>(staffName);
        staff.InsertMany([
            new BsonDocument { { "_id", ceoId }, { "emp_name", "CEO" } },
            new BsonDocument { { "_id", vpId }, { "emp_name", "VP" }, { "mgr_id", ceoId } },
            new BsonDocument { { "_id", managerId }, { "emp_name", "Manager" }, { "mgr_id", vpId } }
        ]);

        using var db = new StaffDbContext(database, staffName);
        var namesWhoseGreatGrandManagerIsNull = db.Staff
            .Where(s => s.Manager.Manager.Manager == null)
            .Select(s => s.EmployeeName)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(["CEO", "Manager", "VP"], namesWhoseGreatGrandManagerIsNull);
    }
#endif

#if !EF8 && !EF9
    [Fact]
    public void Include_two_sibling_reference_navigations_to_same_target_type()
    {
        // EF-378: Root.A and Root.B both target Mid via distinct navigations. The provider used to track
        // prior joins keyed by target entity type, so the second join's type collapsed onto the first's
        // entry and was never detected as "second or later," breaking materialization.
        var midCollection = TemporaryDatabaseFixtureBase.CreateCollectionName("SibMid") + Guid.NewGuid().ToString("N")[..8];
        var rootCollection = TemporaryDatabaseFixtureBase.CreateCollectionName("SibRoot") + Guid.NewGuid().ToString("N")[..8];

        var midAId = ObjectId.GenerateNewId();
        var midBId = ObjectId.GenerateNewId();

        var mids = database.MongoDatabase.GetCollection<BsonDocument>(midCollection);
        mids.InsertMany([
            new BsonDocument { { "_id", midAId }, { "name", "MidA" } },
            new BsonDocument { { "_id", midBId }, { "name", "MidB" } }
        ]);

        var roots = database.MongoDatabase.GetCollection<BsonDocument>(rootCollection);
        roots.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "a_id", midAId },
            { "b_id", midBId }
        });

        using var db = new SibDbContext(database, rootCollection, midCollection);
        var root = db.SibRoots.Include(r => r.A).Include(r => r.B).First();

        Assert.NotNull(root.A);
        Assert.NotNull(root.B);
        // Guards against each sibling reading the wrong (or the other's) joined document.
        Assert.Equal("MidA", root.A.Name);
        Assert.Equal("MidB", root.B.Name);
    }
#endif

    [Fact]
    public void Filtered_include_multi_key_order_by_then_by_applies_full_sort()
    {
        // EF-433: OrderBy(...).ThenBy(...) inside a filtered Include must produce a single $sort stage
        // with both keys, not two sequential $sort stages (the second of which would silently discard
        // the first key's ordering). Alice's orders share Priority for two of the three orders, so the
        // secondary key (Amount) only takes effect within that group if both keys are honored together.
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomersForMultiKeySort();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        var customer = db.Customers
            .Include(c => c.Orders.OrderBy(o => o.Priority).ThenBy(o => o.Amount))
            .First(c => c.FullName == "Alice");

        Assert.Equal([1, 1, 2], customer.Orders.Select(o => o.Priority));
        Assert.Equal([10, 20, 5], customer.Orders.Select(o => o.Amount));
    }

    [Fact]
    public void Filtered_include_order_by_unsupported_key_selector_is_not_silently_dropped()
    {
        // EF-433: a key selector that isn't a simple property access must fail loudly (translation
        // failure) rather than silently fall back to sorting by "_id", which would return a plausible
        // but wrong order with no error raised.
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        Assert.Throws<InvalidOperationException>(() =>
            db.Customers
                .Include(c => c.Orders.OrderBy(o => o.OrderDescription.ToUpper()))
                .First(c => c.FullName == "Alice"));
    }

    [Fact]
    public void Filtered_include_order_by_unmapped_member_key_selector_is_not_silently_dropped()
    {
        // A key selector that IS a member access, but names something that is not a mapped property of the
        // target entity (here string.Length), has no BSON element to sort on. Emitting the member name as an
        // element name would $sort on a field absent from every document, returning a plausible but arbitrary
        // order with no error raised, so this must fail loudly too.
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        Assert.Throws<InvalidOperationException>(() =>
            db.Customers
                .Include(c => c.Orders.OrderBy(o => o.OrderDescription.Length))
                .First(c => c.FullName == "Alice"));
    }

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

    // BSON uses: ord_id for Lines; buyer_id for Orders; b_name, region_id for Buyers; r_name for Regions
    // C# uses:   OrderId for Lines; BuyerId for Orders; BuyerName, RegionId for Buyers; RegionName for Regions
    private (string linesCollection, string ordersCollection, string buyersCollection, string regionsCollection) SetupThreeHopChain()
    {
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName("HopRegions") + Guid.NewGuid().ToString("N")[..8];
        var buyersName = TemporaryDatabaseFixtureBase.CreateCollectionName("HopBuyers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("HopOrders") + Guid.NewGuid().ToString("N")[..8];
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName("HopLines") + Guid.NewGuid().ToString("N")[..8];

        var regionId1 = ObjectId.GenerateNewId();
        var regionId2 = ObjectId.GenerateNewId();
        var buyerId1 = ObjectId.GenerateNewId();
        var buyerId2 = ObjectId.GenerateNewId();
        var orderId1 = ObjectId.GenerateNewId();
        var orderId2 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(regionsName).InsertMany([
            new BsonDocument { { "_id", regionId1 }, { "r_name", "East" } },
            new BsonDocument { { "_id", regionId2 }, { "r_name", "West" } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(buyersName).InsertMany([
            new BsonDocument { { "_id", buyerId1 }, { "b_name", "Buyer 1" }, { "region_id", regionId1 } },
            new BsonDocument { { "_id", buyerId2 }, { "b_name", "Buyer 2" }, { "region_id", regionId2 } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", orderId1 }, { "buyer_id", buyerId1 } },
            new BsonDocument { { "_id", orderId2 }, { "buyer_id", buyerId2 } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(linesName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ord_id", orderId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ord_id", orderId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ord_id", orderId2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ord_id", orderId2 } }
        ]);

        return (linesName, ordersName, buyersName, regionsName);
    }

    // Alice's orders: two share Priority 1 (Amounts 20 then 10, inserted out of Amount order) and one has
    // Priority 2. Sorting by Priority then Amount must yield Amount order [10, 20, 5]; sorting by Amount
    // alone (the ThenBy-collapse bug) would yield [5, 10, 20] instead.
    private (string ordersCollection, string customersCollection) SetupOrdersAndCustomersForMultiKeySort()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("IncludeCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("IncludeOrders") + Guid.NewGuid().ToString("N")[..8];

        var customerId = ObjectId.GenerateNewId();

        var customers = database.MongoDatabase.GetCollection<BsonDocument>(customersName);
        customers.InsertOne(new BsonDocument { { "_id", customerId }, { "name", "Alice" } });

        var orders = database.MongoDatabase.GetCollection<BsonDocument>(ordersName);
        orders.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order A" }, { "cust_id", customerId }, { "priority", 1 }, { "amount", 20 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order B" }, { "cust_id", customerId }, { "priority", 2 }, { "amount", 5 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order C" }, { "cust_id", customerId }, { "priority", 1 }, { "amount", 10 } }
        ]);

        return (ordersName, customersName);
    }

    class Order
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public int? Priority { get; set; }
        public int? Amount { get; set; }
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
                b.Property(o => o.Priority).HasElementName("priority");
                b.Property(o => o.Amount).HasElementName("amount");
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
    class HopRegion
    {
        public ObjectId _id { get; set; }
        public string RegionName { get; set; }
    }

    class HopBuyer
    {
        public ObjectId _id { get; set; }
        public string BuyerName { get; set; }
        public ObjectId? RegionId { get; set; }
        public HopRegion Region { get; set; }
    }

    class HopOrder
    {
        public ObjectId _id { get; set; }
        public ObjectId? BuyerId { get; set; }
        public HopBuyer Buyer { get; set; }
    }

    class HopLine
    {
        public ObjectId _id { get; set; }
        public ObjectId? OrderId { get; set; }
        public HopOrder Order { get; set; }
    }

    class ThreeHopDbContext : DbContext
    {
        private readonly string _linesCollection;
        private readonly string _ordersCollection;
        private readonly string _buyersCollection;
        private readonly string _regionsCollection;

        public DbSet<HopLine> HopLines { get; set; }

        public ThreeHopDbContext(
            TemporaryDatabaseFixture database,
            string linesCollection,
            string ordersCollection,
            string buyersCollection,
            string regionsCollection)
            : base(new DbContextOptionsBuilder<ThreeHopDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _linesCollection = linesCollection;
            _ordersCollection = ordersCollection;
            _buyersCollection = buyersCollection;
            _regionsCollection = regionsCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<HopRegion>(b =>
            {
                b.ToCollection(_regionsCollection);
                b.Property(r => r.RegionName).HasElementName("r_name");
            });

            modelBuilder.Entity<HopBuyer>(b =>
            {
                b.ToCollection(_buyersCollection);
                b.Property(x => x.BuyerName).HasElementName("b_name");
                b.Property(x => x.RegionId).HasElementName("region_id");
                b.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId);
            });

            modelBuilder.Entity<HopOrder>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.Property(x => x.BuyerId).HasElementName("buyer_id");
                b.HasOne(x => x.Buyer).WithMany().HasForeignKey(x => x.BuyerId);
            });

            modelBuilder.Entity<HopLine>(b =>
            {
                b.ToCollection(_linesCollection);
                b.Property(x => x.OrderId).HasElementName("ord_id");
                b.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId);
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

#if !EF8 && !EF9
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

#if !EF8 && !EF9
    class SibMid
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
    }

    class SibRoot
    {
        public ObjectId _id { get; set; }
        public ObjectId AId { get; set; }
        public ObjectId BId { get; set; }
        public SibMid A { get; set; }
        public SibMid B { get; set; }
    }

    class SibDbContext : DbContext
    {
        private readonly string _rootCollection;
        private readonly string _midCollection;

        public DbSet<SibRoot> SibRoots { get; set; }
        public DbSet<SibMid> SibMids { get; set; }

        public SibDbContext(TemporaryDatabaseFixture database, string rootCollection, string midCollection)
            : base(new DbContextOptionsBuilder<SibDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _rootCollection = rootCollection;
            _midCollection = midCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SibMid>(b =>
            {
                b.ToCollection(_midCollection);
                b.Property(m => m.Name).HasElementName("name");
            });

            modelBuilder.Entity<SibRoot>(b =>
            {
                b.ToCollection(_rootCollection);
                b.Property(r => r.AId).HasElementName("a_id");
                b.Property(r => r.BId).HasElementName("b_id");
                b.HasOne(r => r.A).WithMany().HasForeignKey(r => r.AId);
                b.HasOne(r => r.B).WithMany().HasForeignKey(r => r.BId);
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
