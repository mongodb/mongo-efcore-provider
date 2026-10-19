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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A projected comparison of a reference-collection <c>Count</c> against a constant (<c>c.Orders.Count &gt; 0</c>): the
/// $lookup size is compared server-side. Customers with no, one and several orders (and a dangling FK on an order) pin
/// every operator.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCollectionCountComparisonTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId? CustomerId { get; set; }
    }

    [Theory]
#if EF8
    // EF9+ rewrites `Count > 0` / `Count != 0` to Any(), which no mode translates (EF-216).
    [InlineData("gt0", new[] { "none:False", "one:True", "two:True" })]
    [InlineData("ne0", new[] { "none:False", "one:True", "two:True" })]
#endif
    [InlineData("eq0", new[] { "none:True", "one:False", "two:False" })]
    [InlineData("gte2", new[] { "none:False", "one:False", "two:True" })]
    [InlineData("lt2", new[] { "none:True", "one:True", "two:False" })]
    [InlineData("lte1", new[] { "none:True", "one:True", "two:False" })]
    public void Count_comparison_leaf_matches_the_stored_orders(string shape, string[] expected)
    {
        var (customers, orders) = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(customers, orders, mode);
                var q = db.Set<Customer>().OrderBy(c => c.Name);
                return (shape switch
                    {
                        "gt0" => q.Select(c => new { c.Name, V = c.Orders.Count > 0 }),
                        "eq0" => q.Select(c => new { c.Name, V = c.Orders.Count == 0 }),
                        "ne0" => q.Select(c => new { c.Name, V = c.Orders.Count != 0 }),
                        "gte2" => q.Select(c => new { c.Name, V = c.Orders.Count >= 2 }),
                        "lt2" => q.Select(c => new { c.Name, V = c.Orders.Count < 2 }),
                        _ => q.Select(c => new { c.Name, V = c.Orders.Count <= 1 })
                    })
                    .AsEnumerable().OrderBy(x => x.Name == "none" ? 0 : x.Name == "one" ? 1 : 2)
                    .Select(x => $"{x.Name}:{x.V}").ToList();
            },
            [.. expected]);
    }

    private SingleContext Create(string customers, string orders, MongoQueryMode mode)
    {
        var builder = new DbContextOptionsBuilder<SingleContext>()
            .UseMongoDB(
                database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new SingleContext(builder.Options, customers, orders);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class SingleContext(DbContextOptions<SingleContext> options, string customers, string orders) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Order>().ToCollection(orders);
            mb.Entity<Customer>(b =>
            {
                b.ToCollection(customers);
                b.HasMany(c => c.Orders).WithOne().HasForeignKey(o => o.CustomerId);
            });
        }
    }

    private (string Customers, string Orders) Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customers = TemporaryDatabaseFixtureBase.CreateCollectionName("ccCustomers") + suffix;
        var orders = TemporaryDatabaseFixtureBase.CreateCollectionName("ccOrders") + suffix;
        var none = ObjectId.GenerateNewId();
        var one = ObjectId.GenerateNewId();
        var two = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(customers).InsertMany(
        [
            new BsonDocument { { "_id", none }, { "Name", "none" } },
            new BsonDocument { { "_id", one }, { "Name", "one" } },
            new BsonDocument { { "_id", two }, { "Name", "two" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(orders).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", one } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", two } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", two } },
            // Dangling and null FKs belong to nobody.
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", ObjectId.GenerateNewId() } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "CustomerId", BsonNull.Value } }
        ]);
        return (customers, orders);
    }
}
