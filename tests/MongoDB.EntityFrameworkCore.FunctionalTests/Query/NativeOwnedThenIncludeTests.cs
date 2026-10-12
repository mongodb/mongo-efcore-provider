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
/// <c>Include(o =&gt; o.Buyer).ThenInclude(b =&gt; b.Address).ThenInclude(a =&gt; a.Region)</c>: a reference declared on an owned
/// type, reached through a single-reference owned hop. The Region <c>$lookup</c> reads its key at
/// <c>_lookup_Buyer.Address.RegionId</c>, which the confirm step verifies. Every level that can be absent (no buyer, no
/// address, no region key, a dangling region key) must still return its row in every mode.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeOwnedThenIncludeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Order
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? BuyerId { get; set; }
        public Buyer? Buyer { get; set; }
    }

    public class Buyer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Address? Address { get; set; }
    }

    public class Address
    {
        public string City { get; set; } = "";
        public ObjectId? RegionId { get; set; }
        public Region? Region { get; set; }
    }

    public class Region
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class FreshModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;

        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class Ctx(TemporaryDatabaseFixture database, string orders, string buyers, string regions, MongoQueryMode mode)
        : DbContext(new DbContextOptionsBuilder<Ctx>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, FreshModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Buyer> Buyers { get; set; } = null!;
        public DbSet<Region> Regions { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(orders);
                b.HasOne(o => o.Buyer).WithMany().HasForeignKey(o => o.BuyerId);
            });
            modelBuilder.Entity<Buyer>(b =>
            {
                b.ToCollection(buyers);
                b.OwnsOne(x => x.Address, a => a.HasOne(x => x.Region).WithMany().HasForeignKey(x => x.RegionId));
            });
            modelBuilder.Entity<Region>(b => b.ToCollection(regions));
        }
    }

    [Fact]
    public void Every_level_that_can_be_absent_keeps_its_row_in_every_mode()
    {
        var (orders, buyers, regions) = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = new Ctx(database, orders, buyers, regions, mode);
                return db.Orders.AsNoTracking()
                    .Include(o => o.Buyer).ThenInclude(b => b.Address).ThenInclude(a => a.Region)
                    .AsEnumerable()
                    .OrderBy(o => o.Name)
                    .Select(o => $"{o.Name}:{o.Buyer?.Name ?? "-"}:{o.Buyer?.Address?.City ?? "-"}:{o.Buyer?.Address?.Region?.Name ?? "-"}")
                    .ToList();
            },
            [
                "dangling:Dave:Rome:-",
                "full:Alice:London:Europe",
                "noAddress:Carol:-:-",
                "noBuyer:-:-:-",
                "noRegionKey:Bob:Paris:-"
            ]);
    }

    private (string Orders, string Buyers, string Regions) Seed()
    {
        string Name(string stem) => TemporaryDatabaseFixtureBase.CreateCollectionName(stem) + Guid.NewGuid().ToString("N")[..8];
        var orders = Name("tioorders");
        var buyers = Name("tiobuyers");
        var regions = Name("tioregions");

        var europe = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(regions).InsertOne(
            new BsonDocument { { "_id", europe }, { "Name", "Europe" } });

        var alice = ObjectId.GenerateNewId();
        var bob = ObjectId.GenerateNewId();
        var carol = ObjectId.GenerateNewId();
        var dave = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(buyers).InsertMany(
        [
            new BsonDocument { { "_id", alice }, { "Name", "Alice" }, { "Address", new BsonDocument { { "City", "London" }, { "RegionId", europe } } } },
            new BsonDocument { { "_id", bob }, { "Name", "Bob" }, { "Address", new BsonDocument { { "City", "Paris" } } } },
            new BsonDocument { { "_id", carol }, { "Name", "Carol" } },
            new BsonDocument
            {
                { "_id", dave }, { "Name", "Dave" },
                { "Address", new BsonDocument { { "City", "Rome" }, { "RegionId", ObjectId.GenerateNewId() } } }
            }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(orders).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "full" }, { "BuyerId", alice } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "noRegionKey" }, { "BuyerId", bob } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "noAddress" }, { "BuyerId", carol } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "dangling" }, { "BuyerId", dave } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "noBuyer" } }
        ]);

        return (orders, buyers, regions);
    }
}
