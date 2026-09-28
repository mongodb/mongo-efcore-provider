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
/// First/FirstOrDefault/Single/Last composed after a projection through an OPTIONAL reference navigation
/// (<c>OrderBy(..).Select(p =&gt; p.Customer!.City).First()</c> — EF's Northwind <c>OfType_Select</c>). The join is a
/// left-outer reference <c>$lookup</c>, which preserves the row count, so the reducer's <c>$limit</c> is safe to emit
/// before it.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReducerAfterReferenceLookupTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Customer
    {
        public ObjectId Id { get; set; }
        public string City { get; set; } = "";
    }

    public class Purchase
    {
        public ObjectId Id { get; set; }
        public int Seq { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private sealed class Ctx(TemporaryDatabaseFixture database, string customers, string purchases, MongoQueryMode mode)
        : DbContext(BuildOptions(database, mode))
    {
        public DbSet<Customer> Customers { get; set; } = null!;
        public DbSet<Purchase> Purchases { get; set; } = null!;

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var builder = new DbContextOptionsBuilder<Ctx>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(builder).UseQueryMode(mode);
            return builder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToCollection(customers);
            modelBuilder.Entity<Purchase>(b =>
            {
                b.ToCollection(purchases);
                b.HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).IsRequired(false);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    /// <summary>Seq 1 has a NULL FK, Seq 2 a DANGLING FK, Seq 3 a matched FK ("London").</summary>
    private Func<MongoQueryMode, Ctx> Seed(string name)
    {
        var suffix = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var london = new Customer { Id = ObjectId.GenerateNewId(), City = "London" };
        database.MongoDatabase.GetCollection<Customer>("C" + suffix).InsertOne(london);
        database.MongoDatabase.GetCollection<Purchase>("P" + suffix).InsertMany(
        [
            new Purchase { Id = ObjectId.GenerateNewId(), Seq = 1, CustomerId = null },
            new Purchase { Id = ObjectId.GenerateNewId(), Seq = 2, CustomerId = ObjectId.GenerateNewId() },
            new Purchase { Id = ObjectId.GenerateNewId(), Seq = 3, CustomerId = london.Id },
        ]);
        return mode => new Ctx(database, "C" + suffix, "P" + suffix, mode);
    }

    // The first row has a null FK — First() must return its (null) City, not skip it.
    [Fact]
    public void First_over_optional_reference_nav_projection_goes_native_and_keeps_null_fk_row()
    {
        var create = Seed(nameof(First_over_optional_reference_nav_projection_goes_native_and_keeps_null_fk_row));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return new List<string?> { db.Purchases.OrderBy(p => p.Seq).Select(p => p.Customer!.City).First() };
        });
        Assert.Equal([null], result);
    }

    [Fact]
    public void FirstOrDefault_after_skip_over_dangling_fk_row_goes_native()
    {
        var create = Seed(nameof(FirstOrDefault_after_skip_over_dangling_fk_row_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return new List<string?> { db.Purchases.OrderBy(p => p.Seq).Skip(1).Select(p => p.Customer!.City).FirstOrDefault() };
        });
        Assert.Equal([null], result);
    }

    [Fact]
    public void Single_over_matched_row_goes_native()
    {
        var create = Seed(nameof(Single_over_matched_row_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return new List<string?> { db.Purchases.Where(p => p.Seq == 3).Select(p => p.Customer!.City).Single() };
        });
        Assert.Equal(["London"], result);
    }

    [Fact]
    public void Last_over_optional_reference_nav_projection_goes_native()
    {
        var create = Seed(nameof(Last_over_optional_reference_nav_projection_goes_native));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = create(mode);
            return new List<string?> { db.Purchases.OrderBy(p => p.Seq).Select(p => p.Customer!.City).Last() };
        });
        Assert.Equal(["London"], result);
    }
}
