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
/// EF-352: reading a shadow property via <c>EF.Property&lt;T&gt;(...)</c> inside a client-side
/// cross-collection join projection must materialise the stored value, not null. The explicit
/// LINQ <c>join</c> translates on all supported EF versions (EF8/EF9/EF10), and the defect
/// reproduced on all three, so these tests are not version-guarded.
///
/// EF Core also wraps the entity argument of <c>EF.Property&lt;T&gt;(object entity, string name)</c>
/// in a boxing <see cref="System.Linq.Expressions.ExpressionType.Convert"/> when the shaper's static
/// type is the entity's CLR type rather than <see cref="object"/> (e.g. the fluent <c>Join</c> method
/// syntax below) — the binder must see through that convert to recognise the call as a joined-shaper
/// property access, or the read silently falls through to the root document and materializes as
/// <see langword="null"/>.
/// </summary>
[XUnitCollection("QueryTests")]
public class ShadowPropertyJoinProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Shadow_property_via_EF_Property_in_join_projection_materializes_value()
    {
        var (ordersName, customersName) = Seed();

        using var db = new JoinDbContext(database, ordersName, customersName);

        var query =
            from c in db.Customers
            join o1 in
                (from o2 in db.Orders
                 orderby o2.OrderId
                 select new { o2 }) on c.CustomerId equals o1.o2.CustomerId
            where EF.Property<string>(o1.o2, "CustomerId") == "ALFKI"
            select new
            {
                o1,
                o1.o2,
                Shadow = EF.Property<DateTime?>(o1.o2, "OrderDate")
            };

        var results = query.ToList();

        Assert.NotEmpty(results);
        // The joined order entity itself materialises fine...
        Assert.All(results, r => Assert.NotNull(r.o2));
        // ...but the shadow property read through EF.Property must not come back null.
        Assert.All(results, r => Assert.NotNull(r.Shadow));
        Assert.Contains(results, r => r.Shadow == new DateTime(1997, 8, 25));
    }

    [Fact]
    public void Shadow_property_via_EF_Property_in_direct_join_projection_materializes_value()
    {
        var (ordersName, customersName) = Seed();

        using var db = new JoinDbContext(database, ordersName, customersName);

        var query =
            from c in db.Customers
            join o in db.Orders on c.CustomerId equals o.CustomerId
            where c.CustomerId == "ALFKI"
            select new
            {
                o,
                Shadow = EF.Property<DateTime?>(o, "OrderDate")
            };

        var results = query.ToList();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.NotNull(r.o));
        Assert.All(results, r => Assert.NotNull(r.Shadow));
    }

    [Fact]
    public void Join_mixed_projection_reads_shadow_property_via_ef_property()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var ordersColl = nameof(Join_mixed_projection_reads_shadow_property_via_ef_property) + "_Orders" + suffix;
        var customersColl = nameof(Join_mixed_projection_reads_shadow_property_via_ef_property) + "_Customers" + suffix;
        var client = database.MongoDatabase.Client;
        var dbName = database.MongoDatabase.DatabaseNamespace.DatabaseName;

        var customerId = ObjectId.GenerateNewId();
        var orderId = ObjectId.GenerateNewId();

        using (var db = new FluentJoinContext(client, dbName, ordersColl, customersColl))
        {
            var customer = new FluentCustomer { Id = customerId, Name = "Alice" };
            db.Customers.Add(customer);
            db.Entry(customer).Property<string>("City").CurrentValue = "NYC";
            db.Orders.Add(new FluentOrder { Id = orderId, Description = "Order 1", CustomerId = customerId });
            db.SaveChanges();
        }

        using var db2 = new FluentJoinContext(client, dbName, ordersColl, customersColl);
        var result = db2.Orders.AsNoTracking()
            .Join(db2.Customers, o => o.CustomerId, c => c.Id, (o, c) => new
            {
                o,
                c,
                City = EF.Property<string>(c, "City")
            })
            .Where(x => x.o.Id == orderId)
            .Single();

        Assert.Equal("NYC", result.City);
        Assert.Equal("Alice", result.c.Name);
    }

    private (string ordersName, string customersName) Seed()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ShadowJoinCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ShadowJoinOrders") + Guid.NewGuid().ToString("N")[..8];

        database.MongoDatabase.GetCollection<BsonDocument>(customersName).InsertMany([
            new BsonDocument { { "_id", "ALFKI" }, { "name", "Alfreds" } },
            new BsonDocument { { "_id", "ANATR" }, { "name", "Ana Trujillo" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", 10643 }, { "cust_id", "ALFKI" }, { "OrderDate", new DateTime(1997, 8, 25, 0, 0, 0, DateTimeKind.Utc) } },
            new BsonDocument { { "_id", 10692 }, { "cust_id", "ALFKI" }, { "OrderDate", new DateTime(1997, 10, 3, 0, 0, 0, DateTimeKind.Utc) } },
            new BsonDocument { { "_id", 10308 }, { "cust_id", "ANATR" }, { "OrderDate", new DateTime(1996, 9, 18, 0, 0, 0, DateTimeKind.Utc) } }
        ]);

        return (ordersName, customersName);
    }

    class JCustomer
    {
        public string CustomerId { get; set; }
        public string Name { get; set; }
    }

    class JOrder
    {
        public int OrderId { get; set; }
        public string CustomerId { get; set; }
    }

    class JoinDbContext(TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
        : DbContext(new DbContextOptionsBuilder<JoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<JOrder> Orders { get; set; }
        public DbSet<JCustomer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<JCustomer>(b =>
            {
                b.ToCollection(customersCollection);
                b.HasKey(c => c.CustomerId);
                b.Property(c => c.CustomerId).HasElementName("_id");
                b.Property(c => c.Name).HasElementName("name");
            });

            modelBuilder.Entity<JOrder>(b =>
            {
                b.ToCollection(ordersCollection);
                b.HasKey(o => o.OrderId);
                b.Property(o => o.OrderId).HasElementName("_id");
                b.Property(o => o.CustomerId).HasElementName("cust_id");
                // OrderDate is a SHADOW property (no CLR member on JOrder).
                b.Property<DateTime?>("OrderDate").HasElementName("OrderDate");
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    public class FluentOrder
    {
        public ObjectId Id { get; set; }
        public string Description { get; set; } = null!;
        public ObjectId CustomerId { get; set; }
    }

    public class FluentCustomer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = null!;
        // "City" is a shadow property — configured only via the ModelBuilder below, no CLR member —
        // so the only way to read it in a query is EF.Property<string>(customer, "City").
    }

    private class FluentJoinContext(IMongoClient client, string dbName, string ordersColl, string customersColl) : DbContext
    {
        public DbSet<FluentOrder> Orders { get; set; } = null!;
        public DbSet<FluentCustomer> Customers { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB(client, dbName)
                .ConfigureWarnings(w =>
                    w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<FluentOrder>().ToCollection(ordersColl);
            mb.Entity<FluentCustomer>(b =>
            {
                b.ToCollection(customersColl);
                b.Property<string>("City");
            });
        }
    }
}
