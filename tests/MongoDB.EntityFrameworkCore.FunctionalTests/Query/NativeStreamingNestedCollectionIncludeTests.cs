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

using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A collection Include followed by a ThenInclude of a collection (<c>Include(c => c.Orders).ThenInclude(o => o.Items)</c>): each element of the
/// first array carries the second as an array of its own, which the one-pass reader reads in the element's fill loop. The whole graph must come
/// back as the DOM shaper builds it, tracked or not.
/// </summary>
public class NativeStreamingNestedCollectionIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
        public List<Tag> Tags { get; set; } = [];
    }

    private class Tag
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
        public int CustomerId { get; set; }
    }

    private class Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public int CustomerId { get; set; }
        public Customer? Customer { get; set; }
        public List<Item> Items { get; set; } = [];
    }

    private class Item
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
        public int OrderId { get; set; }
        public Order? Order { get; set; }
        public List<Note> Notes { get; set; } = [];
    }

    private class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int ItemId { get; set; }
    }

    private sealed class ChainContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Note> Notes => Set<Note>();
        public DbSet<Tag> Tags => Set<Tag>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Customer>(b =>
            {
                b.ToCollection("Customers" + suffix);
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
                b.HasMany(c => c.Tags).WithOne().HasForeignKey(t => t.CustomerId);
            });
            mb.Entity<Order>(b =>
            {
                b.ToCollection("Orders" + suffix);
                b.HasMany(o => o.Items).WithOne(i => i.Order).HasForeignKey(i => i.OrderId);
            });
            mb.Entity<Item>(b =>
            {
                b.ToCollection("Items" + suffix);
                b.HasMany(i => i.Notes).WithOne().HasForeignKey(n => n.ItemId);
            });
            mb.Entity<Note>().ToCollection("Notes" + suffix);
            mb.Entity<Tag>().ToCollection("Tags" + suffix);
        }
    }

    private ChainContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<ChainContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var seed = Db(MongoQueryMode.Native, suffix);
        seed.Customers.AddRange(
            new Customer { Id = 1, Name = "ann" },
            new Customer { Id = 2, Name = "bob" },
            new Customer { Id = 3, Name = "cy" });
        seed.Tags.AddRange(new Tag { Id = 1, Label = "vip", CustomerId = 1 }, new Tag { Id = 2, Label = "new", CustomerId = 2 });
        seed.Orders.AddRange(
            new Order { Id = 1, Code = "o1", CustomerId = 1 },
            new Order { Id = 2, Code = "o2", CustomerId = 1 },
            new Order { Id = 3, Code = "o3", CustomerId = 2 });
        seed.Items.AddRange(
            new Item { Id = 1, Sku = "a", OrderId = 1 },
            new Item { Id = 2, Sku = "b", OrderId = 1 },
            new Item { Id = 3, Sku = "c", OrderId = 2 });
        seed.Notes.AddRange(new Note { Id = 1, Text = "n1", ItemId = 1 }, new Note { Id = 2, Text = "n2", ItemId = 1 }, new Note { Id = 3, Text = "n3", ItemId = 3 });
        seed.SaveChanges();
        return suffix;
    }

    private static Type CursorRowType<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            return enumerator.GetType().GetGenericArguments()[0];
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static string Describe(Customer c)
        => $"{c.Name}:[{string.Join(",", c.Orders.OrderBy(o => o.Id).Select(o => $"{o.Code}:[{string.Join(",", o.Items.OrderBy(i => i.Id).Select(i => $"{i.Sku}<{string.Join("/", i.Notes.OrderBy(n => n.Id).Select(n => n.Text))}>"))}]"))}]"
           + $"+{string.Join("/", c.Tags.OrderBy(t => t.Id).Select(t => t.Label))}";

    [Fact]
    public void Collection_then_collection_include_streams()
    {
        var suffix = Seed();
        using var db = Db(MongoQueryMode.NativeOnly, suffix);

        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Customers.AsNoTracking().Include(c => c.Orders).ThenInclude(o => o.Items)));
        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Customers.Include(c => c.Orders).ThenInclude(o => o.Items)));
        Assert.NotEqual(
            typeof(BsonDocument),
            CursorRowType(db.Customers.AsNoTracking().Include(c => c.Orders).ThenInclude(o => o.Items).ThenInclude(i => i.Notes)));
    }

    [Fact]
    public void Sibling_collection_includes_stream()
    {
        var suffix = Seed();
        using var db = Db(MongoQueryMode.NativeOnly, suffix);

        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Customers.AsNoTracking().Include(c => c.Orders).Include(c => c.Tags)));
        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Customers.Include(c => c.Orders).Include(c => c.Tags)));
    }

    [Fact]
    public void Collection_then_collection_include_matches_driver_linq()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var untracked = db.Customers.AsNoTracking().Include(c => c.Orders).ThenInclude(o => o.Items).OrderBy(c => c.Id).AsEnumerable()
                .Select(Describe).ToList();
            var tracked = db.Customers.Include(c => c.Orders).ThenInclude(o => o.Items).OrderBy(c => c.Id).AsEnumerable()
                .Select(Describe).ToList();
            return untracked.Concat(tracked).ToList();
        });

        Assert.Equal(
            [
                "ann:[o1:[a<>,b<>],o2:[c<>]]+", "bob:[o3:[]]+", "cy:[]+",
                "ann:[o1:[a<>,b<>],o2:[c<>]]+", "bob:[o3:[]]+", "cy:[]+"
            ],
            results);
    }

    [Fact]
    public void Three_levels_of_collections_match_driver_linq()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var untracked = db.Customers.AsNoTracking()
                .Include(c => c.Orders).ThenInclude(o => o.Items).ThenInclude(i => i.Notes)
                .OrderBy(c => c.Id).AsEnumerable().Select(Describe).ToList();
            var tracked = db.Customers
                .Include(c => c.Orders).ThenInclude(o => o.Items).ThenInclude(i => i.Notes)
                .OrderBy(c => c.Id).AsEnumerable().Select(Describe).ToList();
            return untracked.Concat(tracked).ToList();
        });

        Assert.Equal(
            [
                "ann:[o1:[a<n1/n2>,b<>],o2:[c<n3>]]+", "bob:[o3:[]]+", "cy:[]+",
                "ann:[o1:[a<n1/n2>,b<>],o2:[c<n3>]]+", "bob:[o3:[]]+", "cy:[]+"
            ],
            results);
    }

    [Fact]
    public void Sibling_collection_includes_match_driver_linq()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var untracked = db.Customers.AsNoTracking().Include(c => c.Orders).Include(c => c.Tags)
                .OrderBy(c => c.Id).AsEnumerable().Select(Describe).ToList();
            var tracked = db.Customers.Include(c => c.Orders).Include(c => c.Tags)
                .OrderBy(c => c.Id).AsEnumerable().Select(Describe).ToList();
            var deep = db.Customers.AsNoTracking().Include(c => c.Orders).ThenInclude(o => o.Items).Include(c => c.Tags)
                .OrderBy(c => c.Id).AsEnumerable().Select(Describe).ToList();
            return untracked.Concat(tracked).Concat(deep).ToList();
        });

        Assert.Equal(
            [
                "ann:[o1:[],o2:[]]+vip", "bob:[o3:[]]+new", "cy:[]+",
                "ann:[o1:[],o2:[]]+vip", "bob:[o3:[]]+new", "cy:[]+",
                "ann:[o1:[a<>,b<>],o2:[c<>]]+vip", "bob:[o3:[]]+new", "cy:[]+"
            ],
            results);
    }

    [Fact]
    public void Tracked_nested_collections_are_tracked_and_fixed_up()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var customers = db.Customers.Include(c => c.Orders).ThenInclude(o => o.Items).OrderBy(c => c.Id).ToList();
            var item = customers[0].Orders.OrderBy(o => o.Id).First().Items.OrderBy(i => i.Id).First();
            return new List<string>
            {
                $"customers={db.ChangeTracker.Entries<Customer>().Count()}",
                $"orders={db.ChangeTracker.Entries<Order>().Count()}",
                $"items={db.ChangeTracker.Entries<Item>().Count()}",
                $"unchanged={db.ChangeTracker.Entries().All(e => e.State == EntityState.Unchanged)}",
                $"inverse={ReferenceEquals(item.Order, customers[0].Orders.OrderBy(o => o.Id).First())}",
                $"orderOwner={ReferenceEquals(item.Order!.Customer, customers[0])}"
            };
        });

        Assert.Equal(["customers=3", "orders=3", "items=3", "unchanged=True", "inverse=True", "orderOwner=True"], results);
    }

    [Fact]
    public void A_filtered_nested_include_matches_driver_linq()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Customers.AsNoTracking().Include(c => c.Orders).ThenInclude(o => o.Items.OrderByDescending(i => i.Id).Take(1))
                .OrderBy(c => c.Id).AsEnumerable()
                .Select(c => $"{c.Name}:[{string.Join(",", c.Orders.OrderBy(o => o.Id).Select(o => $"{o.Code}:[{string.Join("/", o.Items.Select(i => i.Sku))}]"))}]")
                .ToList();
        });

        Assert.Equal(["ann:[o1:[b],o2:[c]]", "bob:[o3:[]]", "cy:[]"], results);
    }
}
