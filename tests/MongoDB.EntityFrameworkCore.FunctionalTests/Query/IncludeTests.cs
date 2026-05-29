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
    public void Include_reference_dependent_to_principal_throws_pending()
    {
        // Stage 0: dependent → principal reference Include reaches EF Core's
        // navigation-expansion which currently produces a sub-Select shape the
        // MongoDB provider can't translate. Stage 1 of EF-117 will route this
        // through the cross-collection Include path and the assertion below
        // flips to a materialization assertion.
        using var db = new CustomerOrderContext(MongoDatabase);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Orders.Include(o => o.Customer).ToList());

        Assert.Contains("could not be translated", ex.Message);
    }

    [Fact]
    public void Include_collection_principal_to_dependents_throws_pending()
    {
        // Stage 0: cross-collection collection Include is recognised by the
        // provider's projection-binding visitor and rejected with the legacy
        // EF-117 message (preserved so existing specification-test overrides
        // remain green). Stage 2 of EF-117 lands the implementation.
        using var db = new CustomerOrderContext(MongoDatabase);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Customers.Include(c => c.Orders).ToList());

        Assert.Contains("Including navigation 'Navigation' is not supported", ex.Message);
        Assert.Contains("EF-117", ex.Message);
        Assert.Contains("Customer.Orders", ex.Message);
    }

    [Fact]
    public void ThenInclude_chain_throws_pending()
    {
        // Stage 0: a Customer → Order → OrderDetail ThenInclude chain hits the
        // same cross-collection rejection. Stage 3 of EF-117 lands the
        // implementation of multi-level chains.
        using var db = new ThenIncludeContext(MongoDatabase);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Customers
                .Include(c => c.Orders)
                .ThenInclude(o => o.Items)
                .ToList());

        Assert.Contains("Including navigation 'Navigation' is not supported", ex.Message);
        Assert.Contains("EF-117", ex.Message);
    }

    [Fact]
    public void Include_skip_navigation_throws_not_supported()
    {
        // EF-117's scope explicitly excludes many-to-many (skip navigations).
        // This exception message ships as the final behavior for the M2M case.
        using var db = new PostTagContext(MongoDatabase);

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

    private class CustomerOrderContext(IMongoDatabase mongoDatabase) : DbContext
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Customer>().ToCollection("ef117_customers");
            mb.Entity<Order>().ToCollection("ef117_orders");
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
    }

    private class ThenIncludeContext(IMongoDatabase mongoDatabase) : DbContext
    {
        public DbSet<ThenIncludeCustomer> Customers { get; set; } = null!;
        public DbSet<ThenIncludeOrder> Orders { get; set; } = null!;
        public DbSet<ThenIncludeItem> Items { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<ThenIncludeCustomer>().ToCollection("ef117_ti_customers");
            mb.Entity<ThenIncludeOrder>().ToCollection("ef117_ti_orders");
            mb.Entity<ThenIncludeItem>().ToCollection("ef117_ti_items");
            mb.Entity<ThenIncludeCustomer>()
                .HasMany(c => c.Orders)
                .WithOne(o => o.Customer)
                .HasForeignKey(o => o.CustomerId);
            mb.Entity<ThenIncludeOrder>()
                .HasMany(o => o.Items)
                .WithOne(i => i.Order)
                .HasForeignKey(i => i.OrderId);
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

    private class PostTagContext(IMongoDatabase mongoDatabase) : DbContext
    {
        public DbSet<Post> Posts { get; set; } = null!;
        public DbSet<Tag> Tags { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => base.OnConfiguring(optionsBuilder
                .UseMongoDB(mongoDatabase.Client, mongoDatabase.DatabaseNamespace.DatabaseName)
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Entity<Post>().ToCollection("ef117_posts");
            mb.Entity<Tag>().ToCollection("ef117_tags");
            mb.Entity<Post>().HasMany(p => p.Tags).WithMany(t => t.Posts);
        }
    }
}
