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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Entities with nested parts, which the one-pass reader reads whole: an owned collection beneath an owned reference, several owned parts
/// under one, an owned element beside a navigation to another collection, a joined entity or collection element that owns parts of its own, and
/// a hierarchy reached through a lookup. Each must come back exactly as the DOM shaper built it, tracked or not.
/// </summary>
public class NativeStreamingNestedShapeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---- model -------------------------------------------------------------------------------------------

    private class Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public Shipping? Shipping { get; set; }
        public List<Line> Lines { get; set; } = [];
    }

    private class Shipping
    {
        public string Carrier { get; set; } = "";
        public Address? Address { get; set; }
        public List<Parcel> Parcels { get; set; } = [];
    }

    private class Address
    {
        public string City { get; set; } = "";
    }

    private class Parcel
    {
        public string Tag { get; set; } = "";
        public Address? Where { get; set; }
    }

    // An owned element with a navigation to another collection, which these queries do not include.
    private class Line
    {
        public string Sku { get; set; } = "";
        public int? ProductId { get; set; }
        public Product? Product { get; set; }
    }

    private class Product
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
    }

    private class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Article> Articles { get; set; } = [];
        public List<Person> Staff { get; set; } = [];
    }

    private class Article
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public int CategoryId { get; set; }
        public Category? Category { get; set; }
        public int? AuthorId { get; set; }
        public Person? Author { get; set; }
        public Stats? Stats { get; set; }
    }

    private class Stats
    {
        public int Views { get; set; }
        public List<string> Notes { get; set; } = [];
    }

    // A joined entity that owns parts of its own, and is the root of a hierarchy.
    private abstract class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Address? Home { get; set; }
        public List<Address> Past { get; set; } = [];
        public int? CategoryId { get; set; }
    }

    private class Employee : Person
    {
        public string Role { get; set; } = "";
    }

    private class Customer : Person
    {
        public int Orders { get; set; }
    }

    private sealed class NestedContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Article> Articles => Set<Article>();
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Product>().ToCollection("Products" + suffix);
            mb.Entity<Order>(b =>
            {
                b.ToCollection("Orders" + suffix);
                b.OwnsOne(o => o.Shipping, s =>
                {
                    s.OwnsOne(x => x.Address);
                    s.OwnsMany(x => x.Parcels, p => p.OwnsOne(x => x.Where));
                });
                b.OwnsMany(o => o.Lines, l => l.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId));
            });

            mb.Entity<Person>(b =>
            {
                b.ToCollection("People" + suffix);
                b.OwnsOne(p => p.Home);
                b.OwnsMany(p => p.Past);
            });
            mb.Entity<Employee>();
            mb.Entity<Customer>();

            mb.Entity<Category>(b =>
            {
                b.ToCollection("Categories" + suffix);
                b.HasMany(c => c.Staff).WithOne().HasForeignKey(p => p.CategoryId);
            });
            mb.Entity<Article>(b =>
            {
                b.ToCollection("Articles" + suffix);
                b.HasOne(a => a.Category).WithMany(c => c.Articles).HasForeignKey(a => a.CategoryId);
                b.HasOne(a => a.Author).WithMany().HasForeignKey(a => a.AuthorId);
                b.OwnsOne(a => a.Stats);
            });
        }
    }

    private NestedContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<NestedContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var seed = Db(MongoQueryMode.Native, suffix);

        seed.Products.AddRange(new Product { Id = 1, Title = "widget" }, new Product { Id = 2, Title = "gadget" });
        seed.People.AddRange(
            new Employee { Id = 1, Name = "ann", Role = "dev", CategoryId = 1, Home = new Address { City = "Oslo" }, Past = [new Address { City = "Rome" }, new Address { City = "Bonn" }] },
            new Customer { Id = 2, Name = "bob", Orders = 7, CategoryId = 1 });
        seed.Categories.AddRange(new Category { Id = 1, Name = "c1" }, new Category { Id = 2, Name = "c2" });
        seed.SaveChanges();

        seed.Orders.AddRange(
            new Order
            {
                Id = 1,
                Code = "one",
                Shipping = new Shipping
                {
                    Carrier = "air",
                    Address = new Address { City = "Oslo" },
                    Parcels = [new Parcel { Tag = "p1", Where = new Address { City = "A" } }, new Parcel { Tag = "p2" }]
                },
                Lines = [new Line { Sku = "a", ProductId = 1 }, new Line { Sku = "b" }]
            },
            new Order { Id = 2, Code = "two", Shipping = new Shipping { Carrier = "sea" } },
            new Order { Id = 3, Code = "three" });
        seed.Articles.AddRange(
            new Article { Id = 1, Title = "a1", CategoryId = 1, AuthorId = 1, Stats = new Stats { Views = 5, Notes = ["x", "y"] } },
            new Article { Id = 2, Title = "a2", CategoryId = 1, AuthorId = 2 },
            new Article { Id = 3, Title = "a3", CategoryId = 2, AuthorId = null, Stats = new Stats { Views = 9 } });
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

    private static void AssertStreams<T>(IQueryable<T> query)
        => Assert.NotEqual(typeof(BsonDocument), CursorRowType(query));

    private static string Describe(Order o)
        => $"{o.Id}:{o.Code}:{(o.Shipping == null ? "-" : $"{o.Shipping.Carrier}@{o.Shipping.Address?.City ?? "-"}"
            + $"[{string.Join(",", o.Shipping.Parcels.Select(p => p.Tag + "@" + (p.Where?.City ?? "-")))}]")}"
           + $":[{string.Join(",", o.Lines.Select(l => l.Sku))}]";

    private static string Describe(Person? p)
        => p switch
        {
            null => "-",
            Employee e => $"E:{e.Id}:{e.Name}:{e.Role}:{e.Home?.City ?? "-"}:[{string.Join(",", e.Past.Select(a => a.City))}]",
            Customer c => $"C:{c.Id}:{c.Name}:{c.Orders}:{c.Home?.City ?? "-"}:[{string.Join(",", c.Past.Select(a => a.City))}]",
            _ => "?"
        };

    // ---- owned parts beneath an owned reference ----------------------------------------------------------

    [Fact]
    public void Owned_collection_beneath_an_owned_reference_streams_and_matches_driver_linq()
    {
        var suffix = Seed();

        using (var db = Db(MongoQueryMode.NativeOnly, suffix))
        {
            AssertStreams(db.Orders.AsNoTracking());
            AssertStreams(db.Orders);
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var untracked = db.Orders.AsNoTracking().OrderBy(o => o.Id).AsEnumerable().Select(Describe).ToList();
            var tracked = db.Orders.OrderBy(o => o.Id).AsEnumerable().Select(Describe).ToList();
            return untracked.Concat(tracked).Append($"entries={db.ChangeTracker.Entries().Count()}").ToList();
        });

        var expected = new[] { "1:one:air@Oslo[p1@A,p2@-]:[a,b]", "2:two:sea@-[]:[]", "3:three:-:[]" };
        Assert.Equal(expected.Concat(expected).ToArray(), results.Take(6).ToArray());
    }

    [Fact]
    public void An_owned_element_beside_a_navigation_to_another_collection_streams()
    {
        var suffix = Seed();

        using var db = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(db.Orders.AsNoTracking());

        // The navigation is not included, so it stays null, as it did.
        var order = db.Orders.AsNoTracking().Single(o => o.Id == 1);
        Assert.All(order.Lines, l => Assert.Null(l.Product));
        Assert.Equal([1, null], order.Lines.Select(l => l.ProductId).ToArray());
    }

    // ---- a joined entity or element that owns parts -------------------------------------------------------

    [Fact]
    public void Joined_hierarchy_entity_that_owns_parts_streams_and_matches_driver_linq()
    {
        var suffix = Seed();

        using (var db = Db(MongoQueryMode.NativeOnly, suffix))
        {
            AssertStreams(db.Articles.AsNoTracking().Include(a => a.Author));
            AssertStreams(db.Articles.Include(a => a.Author));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var untracked = db.Articles.AsNoTracking().Include(a => a.Author).OrderBy(a => a.Id).AsEnumerable()
                .Select(a => $"{a.Title}={Describe(a.Author)}/{a.Stats?.Views.ToString() ?? "-"}[{string.Join("", a.Stats?.Notes ?? [])}]").ToList();
            var tracked = db.Articles.Include(a => a.Author).OrderBy(a => a.Id).AsEnumerable()
                .Select(a => $"{a.Title}={Describe(a.Author)}").ToList();
            return untracked.Concat(tracked).Append($"entries={db.ChangeTracker.Entries().Count()}").ToList();
        });

        Assert.Equal(
            [
                "a1=E:1:ann:dev:Oslo:[Rome,Bonn]/5[xy]", "a2=C:2:bob:7:-:[]/-[]", "a3=-/9[]",
                "a1=E:1:ann:dev:Oslo:[Rome,Bonn]", "a2=C:2:bob:7:-:[]", "a3=-",
            ],
            results.Take(6).ToArray());
    }

    [Fact]
    public void Collection_include_of_elements_that_own_parts_streams_and_matches_driver_linq()
    {
        var suffix = Seed();

        using (var db = Db(MongoQueryMode.NativeOnly, suffix))
        {
            AssertStreams(db.Categories.AsNoTracking().Include(c => c.Articles));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Categories.AsNoTracking().Include(c => c.Articles).OrderBy(c => c.Id).AsEnumerable()
                .Select(c => $"{c.Name}:[{string.Join(",", c.Articles.OrderBy(a => a.Id).Select(a => a.Title))}]")
                .ToList();
        });

        Assert.Equal(["c1:[a1,a2]", "c2:[a3]"], results);
    }

    [Fact]
    public void Collection_include_of_a_hierarchy_streams_and_matches_driver_linq()
    {
        var suffix = Seed();

        using (var db = Db(MongoQueryMode.NativeOnly, suffix))
        {
            AssertStreams(db.Categories.AsNoTracking().Include(c => c.Staff));
            AssertStreams(db.Categories.Include(c => c.Staff));
        }

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var untracked = db.Categories.AsNoTracking().Include(c => c.Staff).OrderBy(c => c.Id).AsEnumerable()
                .Select(c => $"{c.Name}:[{string.Join(",", c.Staff.OrderBy(p => p.Id).Select(Describe))}]").ToList();
            var tracked = db.Categories.Include(c => c.Staff).OrderBy(c => c.Id).AsEnumerable()
                .Select(c => $"{c.Name}:[{string.Join(",", c.Staff.OrderBy(p => p.Id).Select(Describe))}]").ToList();
            return untracked.Concat(tracked).Append($"people={db.ChangeTracker.Entries<Person>().Count()}").ToList();
        });

        Assert.Equal(
            [
                // EF does not include the owned parts (Home, Past) of an element of an included collection, in any mode.
                "c1:[E:1:ann:dev:-:[],C:2:bob:7:-:[]]", "c2:[]",
                "c1:[E:1:ann:dev:-:[],C:2:bob:7:-:[]]", "c2:[]",
                "people=2"
            ],
            results);
    }
}
