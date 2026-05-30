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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class IncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private IMongoDatabase MongoDatabase => database.MongoDatabase;

    [Fact]
    public void Include_reference_dependent_to_principal_materializes()
    {
        const string testName = nameof(Include_reference_dependent_to_principal_materializes);
        // Stage 2: dependent → principal reference Include. EF nav-expansion
        // rewrites Orders.Include(o => o.Customer) into a Queryable.Join + Select
        // wrapping an IncludeExpression; MongoQueryTranslationPreprocessor's
        // IncludeJoinUnwrapper lifts that back to a plain
        // Select(p => IncludeExpression(p, default(TInner), nav)), after which
        // the Stage 1 loader infrastructure (now extended with a reference
        // branch) materializes the related principal via a per-dependent
        // sub-query.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .OrderBy(o => o.Id)
            .Include(o => o.Customer)
            .ToList();

        Assert.Equal(3, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.Equal("Alfreds", orders[0].Customer.Name);
        Assert.Equal("Alfreds", orders[1].Customer.Name);
        Assert.Equal("Ana Trujillo", orders[2].Customer.Name);
        // Identity resolution: orders 0 and 1 share the same Customer instance.
        Assert.Same(orders[0].Customer, orders[1].Customer);
    }

    [Fact]
    public void Include_collection_principal_to_dependents_materializes()
    {
        const string testName = nameof(Include_collection_principal_to_dependents_materializes);
        // Stage 1: cross-collection collection Include is implemented via a
        // fan-out sub-query against the related collection.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders)
            .ToList();

        Assert.Equal(2, customers.Count);
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(2, alfki.Orders.Count);
        Assert.Single(anatr.Orders);
        Assert.All(alfki.Orders, o => Assert.Same(alfki, o.Customer));
        Assert.Same(anatr, anatr.Orders.Single().Customer);
    }

    [Fact]
    public void Collection_include_materializes_regardless_of_strategy()
    {
        const string testName = nameof(Collection_include_materializes_regardless_of_strategy);
        // Strategy-invariance anchor for EF-117 hybrid work: this test must
        // keep passing when the collection Include is later routed through
        // server-side $lookup instead of the current fan-out sub-query loader.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders)
            .ToList();

        Assert.NotEmpty(customers);
        Assert.All(customers, c => Assert.NotNull(c.Orders));
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(2, alfki.Orders.Count);
        Assert.Single(anatr.Orders);
    }

    [Fact]
    public void Include_collection_emits_single_lookup_query_and_materializes()
    {
        const string testName = nameof(Include_collection_emits_single_lookup_query_and_materializes);
        // EF-117 Task 2.2: a top-level principal→dependent collection Include must
        // run as a SINGLE server-side $lookup (into the `_lookup_<Nav>` field) rather
        // than the client-side fan-out (one sub-query per principal).
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(
            new Customer { Id = "alfki", Name = "Alfreds" },
            new Customer { Id = "anatr", Name = "Ana Trujillo" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" },
            new Order { Id = "o3", CustomerId = "anatr" });
        seed.SaveChanges();

        List<string> logs = [];
        using var db = new CustomerOrderContext(MongoDatabase, testName, logs.Add);
        var customers = db.Customers
            .OrderBy(c => c.Id)
            .Include(c => c.Orders)
            .ToList();

        // Materialization: each principal's dependents come back from the $lookup array.
        Assert.Equal(2, customers.Count);
        var alfki = customers.Single(c => c.Id == "alfki");
        var anatr = customers.Single(c => c.Id == "anatr");
        Assert.Equal(2, alfki.Orders.Count);
        Assert.Single(anatr.Orders);

        // Exactly one MQL query was executed, and it carries a $lookup into _lookup_Orders.
        var mqlQueries = logs.Where(l => l.Contains("Executed MQL query")).ToList();
        Assert.Single(mqlQueries);
        var lookupQuery = mqlQueries[0];
        Assert.Contains("$lookup", lookupQuery);
        Assert.Contains("\"as\" : \"_lookup_Orders\"", lookupQuery);
        // No fan-out sub-queries against the Orders collection (would be a second query).
        Assert.DoesNotContain(logs, l => l.Contains("Executed MQL query") && l != lookupQuery);
    }

    [Fact]
    public void ThenInclude_chain_materializes()
    {
        const string testName = nameof(ThenInclude_chain_materializes);
        // Stage 3: a ThenInclude chain. The outer collection Include
        // (Customer.Orders) is materialized via the fan-out loader; the loader
        // extracts the chained ThenInclude path (Items) from the outer's
        // NavigationExpression and applies it as a recursive .Include(path) on
        // the sub-query, so the inner collection is loaded too.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1" },
            new ThenIncludeItem { Id = "i2", OrderId = "o1" });
        seed.SaveChanges();

        using var db = new ThenIncludeContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ThenInclude(o => o.Items)
            .ToList();

        var alfki = Assert.Single(customers);
        var order = Assert.Single(alfki.Orders);
        Assert.Equal(2, order.Items.Count);
        Assert.All(order.Items, i => Assert.Same(order, i.Order));
    }

    [Fact]
    public void Include_collection_as_no_tracking_materializes()
    {
        const string testName = nameof(Include_collection_as_no_tracking_materializes);
        // Stage 4: AsNoTracking on the outer query should still load related
        // collections — the loader has to propagate the outer's per-query
        // tracking behavior to its sub-query, otherwise the related entities
        // get attached to the DbContext anyway.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .AsNoTracking()
            .Include(c => c.Orders)
            .ToList();

        var alfki = Assert.Single(customers);
        Assert.Equal(2, alfki.Orders.Count);
        Assert.All(alfki.Orders, o => Assert.Same(alfki, o.Customer));
        // Nothing should be tracked.
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Include_reference_as_no_tracking_materializes()
    {
        const string testName = nameof(Include_reference_as_no_tracking_materializes);
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .AsNoTracking()
            .Include(o => o.Customer)
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.All(orders, o => Assert.Equal("Alfreds", o.Customer.Name));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Include_collection_no_tracking_with_identity_resolution_materializes_without_tracking()
    {
        const string testName = nameof(Include_collection_no_tracking_with_identity_resolution_materializes_without_tracking);
        // Stage 4: AsNoTrackingWithIdentityResolution propagates to the
        // include sub-query so no entities get tracked. Cross-query identity
        // resolution (two Orders pointing to the same Customer resolve to a
        // single Customer instance) is a known limitation of the fan-out
        // implementation: each sub-query has its own materialization scope.
        // Tracking-mode TrackAll DOES dedupe via the DbContext state manager
        // — see Include_reference_dependent_to_principal_materializes.
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new Order { Id = "o1", CustomerId = "alfki" },
            new Order { Id = "o2", CustomerId = "alfki" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .AsNoTrackingWithIdentityResolution()
            .Include(o => o.Customer)
            .ToList();

        Assert.Equal(2, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Customer));
        Assert.All(orders, o => Assert.Equal("Alfreds", o.Customer.Name));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Include_collection_with_no_matching_dependents_returns_empty_collection()
    {
        const string testName = nameof(Include_collection_with_no_matching_dependents_returns_empty_collection);
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new Customer { Id = "lonely", Name = "No Orders" });
        // No Orders for this customer.
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ToList();

        var lonely = Assert.Single(customers);
        Assert.NotNull(lonely.Orders);
        Assert.Empty(lonely.Orders);
    }

    [Fact]
    public void Include_reference_with_missing_principal_leaves_navigation_null()
    {
        const string testName = nameof(Include_reference_with_missing_principal_leaves_navigation_null);
        using var seed = new CustomerOrderContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        // Order with a CustomerId pointing at a Customer that doesn't exist —
        // dangling FK. The Include should leave Customer null rather than throw.
        seed.Orders.AddRange(new Order { Id = "o-orphan", CustomerId = "ghost" });
        seed.SaveChanges();

        using var db = new CustomerOrderContext(MongoDatabase, testName);
        var orders = db.Orders
            .Include(o => o.Customer)
            .ToList();

        var orphan = Assert.Single(orders);
        Assert.Null(orphan.Customer);
    }

    [Fact]
    public void Include_collection_then_include_collection_then_include_reference_materializes()
    {
        const string testName = nameof(Include_collection_then_include_collection_then_include_reference_materializes);
        // Stage 3 regression check — Customer.Orders.Items.Product is a 3-level
        // chain ending in a reference, mirroring the Northwind spec test
        // Include_collection_then_include_collection_then_include_reference
        // (Customer.Orders.OrderDetails.Product). Verifies our recursive
        // Include(path) handles reference-at-end-of-chain correctly.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Products.AddRange(
            new ThenIncludeProduct { Id = "p1", Name = "Chai" },
            new ThenIncludeProduct { Id = "p2", Name = "Chang" });
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1", ProductId = "p1" },
            new ThenIncludeItem { Id = "i2", OrderId = "o1", ProductId = "p2" });
        seed.SaveChanges();

        using var db = new ThenIncludeContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ThenInclude(o => o.Items)
            .ThenInclude(i => i.Product)
            .ToList();

        var alfki = Assert.Single(customers);
        var order = Assert.Single(alfki.Orders);
        Assert.Equal(2, order.Items.Count);
        Assert.All(order.Items, i => Assert.NotNull(i.Product));
        Assert.Equal(new[] { "Chai", "Chang" }, order.Items.Select(i => i.Product!.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Include_skip_navigation_throws_not_supported()
    {
        // EF-117's scope explicitly excludes many-to-many (skip navigations).
        // This exception message ships as the final behavior for the M2M case.
        using var db = new PostTagContext(MongoDatabase, nameof(Include_skip_navigation_throws_not_supported));

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Posts.Include(p => p.Tags).ToList());

        Assert.Contains("many-to-many", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    private class Customer
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<Order> Orders { get; set; } = [];
    }

    private class Order
    {
        public string Id { get; set; } = null!;
        public string CustomerId { get; set; } = null!;
        public Customer Customer { get; set; } = null!;
    }

    private class CustomerOrderContext(IMongoDatabase mongoDatabase, string suffix, Action<string>? log = null) : DbContext
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            if (log != null)
            {
                optionsBuilder.LogTo(log).EnableSensitiveDataLogging();
            }
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Customer>().ToCollection($"ef117_{suffix}_customers");
            mb.Entity<Order>().ToCollection($"ef117_{suffix}_orders");
            mb.Entity<Customer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);
        }
    }

    private class ThenIncludeCustomer
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<ThenIncludeOrder> Orders { get; set; } = [];
    }

    private class ThenIncludeOrder
    {
        public string Id { get; set; } = null!;
        public string CustomerId { get; set; } = null!;
        public ThenIncludeCustomer Customer { get; set; } = null!;
        public List<ThenIncludeItem> Items { get; set; } = [];
    }

    private class ThenIncludeItem
    {
        public string Id { get; set; } = null!;
        public string OrderId { get; set; } = null!;
        public ThenIncludeOrder Order { get; set; } = null!;
        public string? ProductId { get; set; }
        public ThenIncludeProduct? Product { get; set; }
    }

    private class ThenIncludeProduct
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
    }

    private class ThenIncludeContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
    {
        public DbSet<ThenIncludeCustomer> Customers { get; set; } = null!;
        public DbSet<ThenIncludeOrder> Orders { get; set; } = null!;
        public DbSet<ThenIncludeItem> Items { get; set; } = null!;
        public DbSet<ThenIncludeProduct> Products { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<ThenIncludeCustomer>().ToCollection($"ef117_{suffix}_customers");
            mb.Entity<ThenIncludeOrder>().ToCollection($"ef117_{suffix}_orders");
            mb.Entity<ThenIncludeItem>().ToCollection($"ef117_{suffix}_items");
            mb.Entity<ThenIncludeProduct>().ToCollection($"ef117_{suffix}_products");
            mb.Entity<ThenIncludeCustomer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);
            mb.Entity<ThenIncludeOrder>()
                .HasMany(o => o.Items)
                .WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId);
            mb.Entity<ThenIncludeItem>()
                .HasOne(i => i.Product)
                .WithMany()
                .HasForeignKey(i => i.ProductId);
        }
    }

    private class Post
    {
        public string Id { get; set; } = null!;
        public string Title { get; set; } = null!;
        public List<Tag> Tags { get; set; } = [];
    }

    private class Tag
    {
        public string Id { get; set; } = null!;
        public string Name { get; set; } = null!;
        public List<Post> Posts { get; set; } = [];
    }

    private class PostTagContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
    {
        public DbSet<Post> Posts { get; set; } = null!;
        public DbSet<Tag> Tags { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Post>().ToCollection($"ef117_{suffix}_posts");
            mb.Entity<Tag>().ToCollection($"ef117_{suffix}_tags");
            mb.Entity<Post>().HasMany(p => p.Tags).WithMany(t => t.Posts);
        }
    }
}
