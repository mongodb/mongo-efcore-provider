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
/// EF-352: the shadow-via-<c>EF.Property</c>-in-join-projection defect also occurs in the FLAT
/// (<c>$lookup</c> + <c>$unwind</c>) cross-collection mode, which is triggered by a SECOND
/// cross-collection join (more than one inner collection). Flat mode sets
/// <c>UsesDriverJoinFields = false</c> and places each joined document under its own root-level
/// <c>_lookup_&lt;Navigation&gt;</c> field instead of <c>_inner</c>, so the shaper must read the joined
/// entity's scalars from that field. Translates and reproduces on all supported EF versions, so this
/// is not version-guarded.
/// </summary>
[XUnitCollection("QueryTests")]
public class ShadowPropertyFlatJoinProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Shadow_property_via_EF_Property_in_flat_multi_join_projection_materializes_value()
    {
        var (customersName, ordersName, itemsName) = Seed();

        using var db = new FlatJoinDbContext(database, customersName, ordersName, itemsName);

        // The root predicate is applied BEFORE the joins so that it lands on the root collection and
        // survives as a leading $match, keeping this test independent of whether a Where placed AFTER
        // the joins can be reattached (it usually can now - see EF-X024 in docs/failing-spec-tests.md for
        // the one remaining ambiguous-sibling-navigation shape that still gets rejected).
        var query =
            from c in db.Customers.Where(c => c.CustomerId == "ALFKI")
            join o in db.Orders on c.CustomerId equals o.CustomerId
            join i in db.Items on o.OrderId equals i.OrderId
            select new
            {
                o,
                i,
                Shadow = EF.Property<DateTime?>(o, "OrderDate")
            };

        var results = query.ToList();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.NotNull(r.o));
        Assert.All(results, r => Assert.NotNull(r.i));
        Assert.All(results, r => Assert.NotNull(r.Shadow));
    }

    private (string customersName, string ordersName, string itemsName) Seed()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("FlatCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("FlatOrders") + Guid.NewGuid().ToString("N")[..8];
        var itemsName = TemporaryDatabaseFixtureBase.CreateCollectionName("FlatItems") + Guid.NewGuid().ToString("N")[..8];

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertMany([
            new BsonDocument { { "_id", "ALFKI" }, { "name", "Alfreds" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", 10643 }, { "cust_id", "ALFKI" }, { "OrderDate", new DateTime(1997, 8, 25, 0, 0, 0, DateTimeKind.Utc) } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(itemsName).InsertMany([
            new BsonDocument { { "_id", 1 }, { "ord_id", 10643 }, { "sku", "A" } },
            new BsonDocument { { "_id", 2 }, { "ord_id", 10643 }, { "sku", "B" } }
        ]);

        return (customersName, ordersName, itemsName);
    }

    class FjCustomer
    {
        public string CustomerId { get; set; }
        public string Name { get; set; }
        public List<FjOrder> Orders { get; set; }
    }

    class FjOrder
    {
        public int OrderId { get; set; }
        public string CustomerId { get; set; }
        public FjCustomer Customer { get; set; }
        public List<FjItem> Items { get; set; }
    }

    class FjItem
    {
        public int ItemId { get; set; }
        public int OrderId { get; set; }
        public FjOrder Order { get; set; }
        public string Sku { get; set; }
    }

    class FlatJoinDbContext(TemporaryDatabaseFixture database, string customersCollection, string ordersCollection, string itemsCollection)
        : DbContext(new DbContextOptionsBuilder<FlatJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<FjCustomer> Customers { get; set; }
        public DbSet<FjOrder> Orders { get; set; }
        public DbSet<FjItem> Items { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<FjCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.HasKey(c => c.CustomerId);
                b.Property(c => c.CustomerId).HasElementName("_id");
                b.Property(c => c.Name).HasElementName("name");
                b.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<FjOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.HasKey(o => o.OrderId);
                b.Property(o => o.OrderId).HasElementName("_id");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                b.Property<DateTime?>("OrderDate").HasElementName("OrderDate");
                b.HasMany(o => o.Items).WithOne(i => i.Order).HasForeignKey(i => i.OrderId);
            });

            modelBuilder.Entity<FjItem>(b =>
            {
                b.ToCollection(itemsCollection);
                b.HasKey(i => i.ItemId);
                b.Property(i => i.ItemId).HasElementName("_id");
                b.Property(i => i.OrderId).HasElementName("ord_id");
                b.Property(i => i.Sku).HasElementName("sku");
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
