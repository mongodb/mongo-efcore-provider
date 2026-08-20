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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-380: a second-join key selector reaching through an owned/embedded navigation on an
/// already-joined intermediate (e.g. <c>Order.Buyer.Address.RegionId</c>) used to emit an unscoped
/// <c>$lookup</c> localField ("RegionId" instead of "_lookup_Buyer.Address.RegionId"), silently
/// matching nothing and leaving <c>Region</c> null despite matching data existing.
/// </summary>
[XUnitCollection("QueryTests")]
public class ThenIncludeThroughOwnedNavigationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void ThenInclude_reaching_through_an_owned_navigation_after_a_prior_join_resolves_the_nested_reference()
    {
        var (ordersName, buyersName, regionsName) = Seed();
        var logs = new List<string>();

        using var db = new OwnedNavigationJoinDbContext(database, ordersName, buyersName, regionsName, logs.Add);

        var order = db.JOrders
            .Include(o => o.Buyer)
            .ThenInclude(b => b.Address)
            .ThenInclude(a => a.Region)
            .Single();

        Assert.NotNull(order.Buyer);
        Assert.NotNull(order.Buyer.Address);
        Assert.Equal("London", order.Buyer.Address.City);
        Assert.NotNull(order.Buyer.Address.Region);
        Assert.Equal("Europe", order.Buyer.Address.Region.Name);

        // Scoped under the Buyer lookup alias AND the embedded Address path, not a bare "RegionId".
        Assert.Contains(logs, l => l.Contains("\"localField\" : \"_lookup_Buyer.Address.RegionId\""));
    }

    /// <summary>
    /// Sibling owned navigations sharing a CLR type (<c>ShippingAddress</c> / <c>BillingAddress</c>, both
    /// <c>J2Address</c>) must resolve via the real navigation graph, not a CLR-type guess — otherwise
    /// owner resolution could pick the sibling with no <c>Region</c> relationship, dropping the $lookup.
    /// </summary>
    [Fact]
    public void ThenInclude_through_one_of_two_sibling_owned_navigations_sharing_a_clr_type_resolves_the_correct_one()
    {
        var (ordersName, buyersName, regionsName) = Seed2();
        var logs = new List<string>();

        using var db = new SiblingOwnedNavigationJoinDbContext(database, ordersName, buyersName, regionsName, logs.Add);

        var order = db.J2Orders
            .Include(o => o.Buyer)
            .ThenInclude(b => b.ShippingAddress)
            .ThenInclude(a => a.Region)
            .Single();

        Assert.NotNull(order.Buyer);
        Assert.NotNull(order.Buyer.ShippingAddress);
        Assert.Equal("London", order.Buyer.ShippingAddress.City);
        Assert.NotNull(order.Buyer.ShippingAddress.Region);
        Assert.Equal("Europe", order.Buyer.ShippingAddress.Region.Name);

        Assert.Contains(logs, l => l.Contains("\"localField\" : \"_lookup_Buyer.ShippingAddress.RegionId\""));
    }

    private (string ordersName, string buyersName, string regionsName) Seed2()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("J2Orders") + Guid.NewGuid().ToString("N")[..8];
        var buyersName = TemporaryDatabaseFixtureBase.CreateCollectionName("J2Buyers") + Guid.NewGuid().ToString("N")[..8];
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName("J2Regions") + Guid.NewGuid().ToString("N")[..8];

        var buyerId = ObjectId.GenerateNewId();
        var regionId = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(regionsName).InsertOne(
            new BsonDocument { { "_id", regionId }, { "Name", "Europe" } });
        database.MongoDatabase.GetCollection<BsonDocument>(buyersName).InsertOne(
            new BsonDocument
            {
                { "_id", buyerId },
                { "Name", "Alice" },
                { "ShippingAddress", new BsonDocument { { "City", "London" }, { "RegionId", regionId } } },
                { "BillingAddress", new BsonDocument { { "City", "Paris" } } }
            });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Desc", "Order 1" }, { "BuyerId", buyerId } });

        return (ordersName, buyersName, regionsName);
    }

    class J2Order
    {
        public ObjectId _id { get; set; }
        public string Desc { get; set; }
        public ObjectId? BuyerId { get; set; }
        public J2Buyer Buyer { get; set; }
    }

    class J2Buyer
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
        public J2Address ShippingAddress { get; set; }
        public J2Address BillingAddress { get; set; }
    }

    class J2Address
    {
        public string City { get; set; }
        public ObjectId? RegionId { get; set; }
        public J2Region Region { get; set; }
    }

    class J2Region
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
    }

    class SiblingOwnedNavigationJoinDbContext(
        TemporaryDatabaseFixture database, string ordersCollection, string buyersCollection, string regionsCollection,
        Action<string>? logAction = null)
        : DbContext(new DbContextOptionsBuilder<SiblingOwnedNavigationJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .LogTo(l => logAction?.Invoke(l))
            .EnableSensitiveDataLogging()
            .Options)
    {
        public DbSet<J2Order> J2Orders { get; set; }
        public DbSet<J2Buyer> J2Buyers { get; set; }
        public DbSet<J2Region> J2Regions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<J2Order>(b =>
            {
                b.ToCollection(ordersCollection);
                b.HasOne(o => o.Buyer).WithMany().HasForeignKey(o => o.BuyerId);
            });

            modelBuilder.Entity<J2Buyer>(b =>
            {
                b.ToCollection(buyersCollection);
                b.OwnsOne(c => c.ShippingAddress, a =>
                {
                    a.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId);
                });
                // Sibling owned navigation, same CLR type (J2Address), but WITHOUT the Region relationship.
                b.OwnsOne(c => c.BillingAddress, a =>
                {
                    a.Ignore(x => x.Region);
                });
            });

            modelBuilder.Entity<J2Region>(b =>
            {
                b.ToCollection(regionsCollection);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private (string ordersName, string buyersName, string regionsName) Seed()
    {
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("JOrders") + Guid.NewGuid().ToString("N")[..8];
        var buyersName = TemporaryDatabaseFixtureBase.CreateCollectionName("JBuyers") + Guid.NewGuid().ToString("N")[..8];
        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName("JRegions") + Guid.NewGuid().ToString("N")[..8];

        var buyerId = ObjectId.GenerateNewId();
        var regionId = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(regionsName).InsertOne(
            new BsonDocument { { "_id", regionId }, { "Name", "Europe" } });
        database.MongoDatabase.GetCollection<BsonDocument>(buyersName).InsertOne(
            new BsonDocument
            {
                { "_id", buyerId },
                { "Name", "Alice" },
                { "Address", new BsonDocument { { "City", "London" }, { "RegionId", regionId } } }
            });
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertOne(
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Desc", "Order 1" }, { "BuyerId", buyerId } });

        return (ordersName, buyersName, regionsName);
    }

    class JOrder
    {
        public ObjectId _id { get; set; }
        public string Desc { get; set; }
        public ObjectId? BuyerId { get; set; }
        public JBuyer Buyer { get; set; }
    }

    class JBuyer
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
        public JAddress Address { get; set; }
    }

    class JAddress
    {
        public string City { get; set; }
        public ObjectId? RegionId { get; set; }
        public JRegion Region { get; set; }
    }

    class JRegion
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
    }

    class OwnedNavigationJoinDbContext(
        TemporaryDatabaseFixture database, string ordersCollection, string buyersCollection, string regionsCollection,
        Action<string>? logAction = null)
        : DbContext(new DbContextOptionsBuilder<OwnedNavigationJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .LogTo(l => logAction?.Invoke(l))
            .EnableSensitiveDataLogging()
            .Options)
    {
        public DbSet<JOrder> JOrders { get; set; }
        public DbSet<JBuyer> JBuyers { get; set; }
        public DbSet<JRegion> JRegions { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<JOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.HasOne(o => o.Buyer).WithMany().HasForeignKey(o => o.BuyerId);
            });

            modelBuilder.Entity<JBuyer>(b =>
            {
                b.ToCollection(buyersCollection);
                b.OwnsOne(c => c.Address, a =>
                {
                    a.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId);
                });
            });

            modelBuilder.Entity<JRegion>(b =>
            {
                b.ToCollection(regionsCollection);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
#endif
