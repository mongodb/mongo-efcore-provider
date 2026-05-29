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
        // Stage 0/1: dependent → principal reference Include is rewritten by
        // EF nav-expansion into a Queryable.Join the provider's translator
        // doesn't support. Stage 2 of EF-117 lands the JOIN-unwrap path; the
        // assertion below flips to a materialization assertion then.
        using var db = new CustomerOrderContext(MongoDatabase, nameof(Include_reference_dependent_to_principal_throws_pending));

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Orders.Include(o => o.Customer).ToList());

        Assert.Contains("could not be translated", ex.Message);
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
    public void ThenInclude_chain_outer_collection_loads_inner_pending()
    {
        const string testName = nameof(ThenInclude_chain_outer_collection_loads_inner_pending);
        // Stage 1 limitation: a ThenInclude chain runs the outer Include via
        // the fan-out loader, but the nested ThenInclude is silently dropped
        // because the loader doesn't yet recurse. Stage 3 of EF-117 wires the
        // recursion and the assertion below flips to a materialization
        // assertion for the inner collection.
        using var seed = new ThenIncludeContext(MongoDatabase, testName);
        seed.Database.EnsureCreated();
        seed.Customers.AddRange(new ThenIncludeCustomer { Id = "alfki", Name = "Alfreds" });
        seed.Orders.AddRange(
            new ThenIncludeOrder { Id = "o1", CustomerId = "alfki" });
        seed.Items.AddRange(
            new ThenIncludeItem { Id = "i1", OrderId = "o1" });
        seed.SaveChanges();

        using var db = new ThenIncludeContext(MongoDatabase, testName);
        var customers = db.Customers
            .Include(c => c.Orders)
            .ThenInclude(o => o.Items)
            .ToList();

        var alfki = Assert.Single(customers);
        Assert.Single(alfki.Orders); // Stage 1: Orders loaded
        Assert.Empty(alfki.Orders[0].Items); // Stage 1 limit: Items not loaded yet (Stage 3 wires this)
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

    private class CustomerOrderContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
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
    }

    private class ThenIncludeContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
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
            mb.Entity<ThenIncludeCustomer>().ToCollection($"ef117_{suffix}_customers");
            mb.Entity<ThenIncludeOrder>().ToCollection($"ef117_{suffix}_orders");
            mb.Entity<ThenIncludeItem>().ToCollection($"ef117_{suffix}_items");
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

    private class PostTagContext(IMongoDatabase mongoDatabase, string suffix) : DbContext
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
            mb.Entity<Post>().ToCollection($"ef117_{suffix}_posts");
            mb.Entity<Tag>().ToCollection($"ef117_{suffix}_tags");
            mb.Entity<Post>().HasMany(p => p.Tags).WithMany(t => t.Posts);
        }
    }
}
