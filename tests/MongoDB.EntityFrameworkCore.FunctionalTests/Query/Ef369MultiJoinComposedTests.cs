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
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-369: a user-composed operator (Where/OrderBy/Skip/Take/Select) landing outside a MULTI-join
/// Include chain was discarded by StripJoinForLookup, silently returning unfiltered results.
/// These tests assert ROW COUNTS, not MQL — the wrong MQL is indistinguishable from a legitimately
/// unfiltered query.
/// </summary>
[XUnitCollection("QueryTests")]
public class Ef369MultiJoinComposedTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
#if !EF8 && !EF9
    // ---- SHAPE 1..: composed Where over a ThenInclude (the reported bug) ----
    [Fact]
    public void ThenInclude_with_composed_nav_Where()
    {
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .ThenInclude(c => c.Region)
            .Where(o => o.Customer.Region.RegionName == "West")
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, o => Assert.Equal("West", o.Customer.Region.RegionName));
    }

    [Fact]
    public void Two_reference_Includes_with_composed_nav_Where()
    {
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Shipper)
            .Where(o => o.Customer.FullName == "Alice")
            .ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Two_reference_Includes_with_composed_root_Where()
    {
        // Control that is correct on main: nav-expansion hoists a root predicate ahead of the join.
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .Include(o => o.Shipper)
            .Where(o => o.OrderDescription == "Order 1")
            .ToList();

        Assert.Single(result);
    }

    [Fact]
    public void Single_Include_with_composed_nav_Where()
    {
        // Control that is correct on main: only one join, so the flat-lookup path is not selected.
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .Where(o => o.Customer.FullName == "Alice")
            .ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ThenInclude_with_composed_OrderBy()
    {
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .ThenInclude(c => c.Region)
            .OrderBy(o => o.OrderDescription)
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(["Order 1", "Order 2", "Order 3"], result.Select(o => o.OrderDescription));
    }

    [Fact]
    public void ThenInclude_with_composed_OrderBy_and_Where()
    {
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .ThenInclude(c => c.Region)
            .Where(o => o.Customer.Region.RegionName == "West")
            .OrderByDescending(o => o.OrderDescription)
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(["Order 2", "Order 1"], result.Select(o => o.OrderDescription));
    }

    [Fact]
    public void ThenInclude_with_composed_Skip_Take()
    {
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .ThenInclude(c => c.Region)
            .OrderBy(o => o.OrderDescription)
            .Skip(1)
            .Take(1)
            .ToList();

        Assert.Single(result);
        Assert.Equal("Order 2", result[0].OrderDescription);
    }

    [Fact]
    public void ThenInclude_with_composed_projection_Select()
    {
        using var db = Setup();
        var result = db.Orders
            .Include(o => o.Customer)
            .ThenInclude(c => c.Region)
            .Where(o => o.Customer.Region.RegionName == "West")
            .Select(o => o.OrderDescription)
            .ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ThenInclude_with_composed_Count()
    {
        using var db = Setup();
        var count = db.Orders
            .Include(o => o.Customer)
            .ThenInclude(c => c.Region)
            .Count(o => o.Customer.Region.RegionName == "West");

        Assert.Equal(2, count);
    }
#endif

    [Fact]
    public void Explicit_Join_still_works()
    {
        using var db = Setup();
        var result = db.Orders
            .Join(db.Customers, o => o.CustomerId, c => c._id, (o, c) => new { o.OrderDescription, c.FullName })
            .ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Count(r => r.FullName == "Alice"));
    }

    [Fact]
    public void Explicit_Join_with_composed_Where_still_works()
    {
        using var db = Setup();
        var result = db.Orders
            .Join(db.Customers, o => o.CustomerId, c => c._id, (o, c) => new { o.OrderDescription, c.FullName })
            .Where(x => x.FullName == "Alice")
            .ToList();

        Assert.Equal(2, result.Count);
    }


    private OrderDbContext Setup()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef369Regions") + suffix;
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef369Customers") + suffix;
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef369Orders") + suffix;
        var shippersName = TemporaryDatabaseFixtureBase.CreateCollectionName("Ef369Shippers") + suffix;

        var west = ObjectId.GenerateNewId();
        var east = ObjectId.GenerateNewId();
        var alice = ObjectId.GenerateNewId();
        var bob = ObjectId.GenerateNewId();
        var ship1 = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(regionsName).InsertMany([
            new BsonDocument { { "_id", west }, { "rname", "West" } },
            new BsonDocument { { "_id", east }, { "rname", "East" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertMany([
            new BsonDocument { { "_id", alice }, { "name", "Alice" }, { "region_id", west } },
            new BsonDocument { { "_id", bob }, { "name", "Bob" }, { "region_id", east } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(shippersName).InsertMany([
            new BsonDocument { { "_id", ship1 }, { "sname", "Speedy" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 1" }, { "cust_id", alice }, { "ship_id", ship1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 2" }, { "cust_id", alice }, { "ship_id", ship1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "desc", "Order 3" }, { "cust_id", bob }, { "ship_id", ship1 } }
        ]);

        return new OrderDbContext(database, ordersName, customersName, regionsName, shippersName);
    }

    public class Region
    {
        public ObjectId _id { get; set; }
        public string RegionName { get; set; }
        public List<Customer> Customers { get; set; }
    }

    public class Customer
    {
        public ObjectId _id { get; set; }
        public string FullName { get; set; }
        public ObjectId? RegionId { get; set; }
        public Region Region { get; set; }
        public List<Order> Orders { get; set; }
    }

    public class Shipper
    {
        public ObjectId _id { get; set; }
        public string ShipperName { get; set; }
        public List<Order> Orders { get; set; }
    }

    public class Order
    {
        public ObjectId _id { get; set; }
        public string OrderDescription { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer Customer { get; set; }
        public ObjectId? ShipperId { get; set; }
        public Shipper Shipper { get; set; }
    }

    public class OrderDbContext : DbContext
    {
        private readonly string _orders;
        private readonly string _customers;
        private readonly string _regions;
        private readonly string _shippers;

        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Region> Regions { get; set; }
        public DbSet<Shipper> Shippers { get; set; }

        public OrderDbContext(
            TemporaryDatabaseFixture db, string orders, string customers, string regions, string shippers)
            : base(new DbContextOptionsBuilder<OrderDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _orders = orders;
            _customers = customers;
            _regions = regions;
            _shippers = shippers;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Region>(b =>
            {
                b.ToCollection(_regions);
                b.Property(r => r.RegionName).HasElementName("rname");
            });

            modelBuilder.Entity<Shipper>(b =>
            {
                b.ToCollection(_shippers);
                b.Property(s => s.ShipperName).HasElementName("sname");
            });

            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(_customers);
                b.Property(c => c.FullName).HasElementName("name");
                b.Property(c => c.RegionId).HasElementName("region_id");
                b.HasOne(c => c.Region).WithMany(r => r.Customers).HasForeignKey(c => c.RegionId);
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_orders);
                b.Property(o => o.OrderDescription).HasElementName("desc");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.Property(o => o.ShipperId).HasElementName("ship_id");
                b.HasOne(o => o.Shipper).WithMany(s => s.Orders).HasForeignKey(o => o.ShipperId);
            });
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
