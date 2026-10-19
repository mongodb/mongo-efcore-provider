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
/// Real-database tests for filters through a multi-hop reference navigation (<c>o.Customer.Region.Name</c>,
/// <c>e.Manager.Manager</c>). Nav-expansion turns these into a chained join scope, filtered by
/// <c>NativeSlotPopulator</c>'s chained Where arm and confirmed by <c>TranslateSelect</c>'s chained root-leaf arm.
/// Each test asserts NativeOnly equals driver-LINQ AND equals hand-computed rows, over missing, null and dangling
/// references at each hop.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeChainedReferenceNavigationFilterTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public int OrderNo { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string? Name { get; set; }
        public ObjectId? RegionId { get; set; }
        public Region? Region { get; set; }
    }

    private class Region
    {
        public ObjectId Id { get; set; }
        public string? Name { get; set; }
    }

    private class Employee
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? ManagerId { get; set; }
        public Employee? Manager { get; set; }
    }

    // a comparison on the second hop excludes rows whose first or second hop is missing.
    [Fact]
    public void Equality_on_second_hop_matches_only_fully_joined_rows()
        => Assert.Equal([1], RunOrders(q => q.Where(o => o.Customer!.Region!.Name == "North")));

    // EF null semantics — a missing hop makes Name null, and null != "North".
    [Fact]
    public void Inequality_on_second_hop_includes_rows_with_a_missing_hop()
        => Assert.Equal([2, 3, 4, 5], RunOrders(q => q.Where(o => o.Customer!.Region!.Name != "North")));

    // null FK, dangling FK, dangling first hop and null first hop all read as a null second hop.
    [Fact]
    public void Null_check_on_second_hop_treats_null_and_dangling_alike()
        => Assert.Equal([2, 3, 4, 5], RunOrders(q => q.Where(o => o.Customer!.Region == null)));

    [Fact]
    public void Not_null_check_on_second_hop_matches_only_resolved_rows()
        => Assert.Equal([1], RunOrders(q => q.Where(o => o.Customer!.Region != null)));

    [Fact]
    public void Conjunction_of_root_and_second_hop_filters()
        => Assert.Equal([2, 3], RunOrders(q => q.Where(o => o.OrderNo < 4 && o.Customer!.Region == null)));

    [Fact]
    public void Local_collection_contains_over_second_hop()
        => Assert.Equal(
            [1], RunOrders(q => q.Where(o => new[] { "North", "South" }.Contains(o.Customer!.Region!.Name))));

    [Fact]
    public void Ordering_and_paging_after_a_second_hop_filter()
        => Assert.Equal(
            [4, 5], RunOrders(q => q.Where(o => o.Customer!.Region == null).OrderByDescending(o => o.OrderNo).Take(2)));

    // Paging recorded into PostJoinOps (after a first-hop filter) ahead of a SECOND hop's join: that later join is a
    // left-outer reference navigation, which neither drops nor multiplies rows, so paging after its $unwind keeps the
    // same rows and the chain stays native. First-hop filter keeps orders 1-3; the first two are 1 and 2, of which
    // only 2 has no region.
    [Fact]
    public void Paging_after_a_first_hop_filter_then_a_second_hop_filter()
        => Assert.Equal(
            [2],
            RunOrders(q => q.Where(o => o.Customer!.Name != null).OrderBy(o => o.OrderNo).Take(2)
                .Where(o => o.Customer!.Region == null)));

    // a comparison spanning two hops has no single-scope translation; must decline, not misroute.
    [Fact]
    public void Comparison_spanning_two_hops_declines_cleanly()
    {
        var (orders, customers, regions, employees) = CreateCollectionNames(nameof(Comparison_spanning_two_hops_declines_cleanly));
        SeedOrders(orders, customers, regions, employees);

        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = new ChainDbContext(database, orders, customers, regions, employees, mode);
            return db.Set<Order>().Where(o => o.Customer!.Name == o.Customer!.Region!.Name)
                .AsEnumerable().Select(o => o.OrderNo).OrderBy(n => n).ToList();
        });

        // EF C# null semantics: orders 4 and 5 have no Customer, so both sides are null and null == null holds.
        Assert.Equal([1, 4, 5], result);
    }

    // paging written BEFORE the second-hop filter must page first. Asserts results only (native or
    // fallback both acceptable); first three orders are 1,2,3, of which 2 and 3 have no region.
    [Fact]
    public void Paging_before_a_second_hop_filter_pages_first()
    {
        var (orders, customers, regions, employees) = CreateCollectionNames(nameof(Paging_before_a_second_hop_filter_pages_first));
        SeedOrders(orders, customers, regions, employees);

        List<int> Run(MongoQueryMode mode)
        {
            using var db = new ChainDbContext(database, orders, customers, regions, employees, mode);
            return db.Set<Order>().OrderBy(o => o.OrderNo).Take(3)
                .Where(o => o.Customer!.Region == null)
                .AsEnumerable().Select(o => o.OrderNo).OrderBy(n => n).ToList();
        }

        Assert.Equal([2, 3], Run(MongoQueryMode.Native));
        Assert.Equal([2, 3], Run(MongoQueryMode.DriverLinq));
    }

    // a self-referencing chain must test the SECOND level's own $lookup alias.
    [Fact]
    public void Self_referencing_second_hop_null_check()
        => Assert.Equal(
            ["Boss", "Mid", "Orphan"], RunEmployees(q => q.Where(e => e.Manager!.Manager == null)));

    [Fact]
    public void Self_referencing_second_hop_comparison()
        => Assert.Equal(["Low"], RunEmployees(q => q.Where(e => e.Manager!.Manager!.Name == "Boss")));

    private List<int> RunOrders(Func<IQueryable<Order>, IQueryable<Order>> query, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var (orders, customers, regions, employees) = CreateCollectionNames(testName);
        SeedOrders(orders, customers, regions, employees);

        // NativeOnly proves it went native; parity with DriverLinq plus the explicit expected rows (in the caller)
        // prove it's correct. Whole entities are materialized and projected client-side: a server-side scalar
        // Select over a chained scope is a different (still depth-1-only) arm, and would decline regardless of the
        // filter under test. Sorted client-side so the comparison doesn't depend on pipeline order.
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = new ChainDbContext(database, orders, customers, regions, employees, mode);
            return query(db.Set<Order>()).AsEnumerable().Select(o => o.OrderNo).OrderBy(n => n).ToList();
        });
    }

    private List<string> RunEmployees(Func<IQueryable<Employee>, IQueryable<Employee>> query, [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        var (orders, customers, regions, employees) = CreateCollectionNames(testName);

        using (var seed = new ChainDbContext(database, orders, customers, regions, employees, MongoQueryMode.DriverLinq))
        {
            var boss = new Employee { Id = ObjectId.GenerateNewId(), Name = "Boss" };
            var mid = new Employee { Id = ObjectId.GenerateNewId(), Name = "Mid", ManagerId = boss.Id };
            var low = new Employee { Id = ObjectId.GenerateNewId(), Name = "Low", ManagerId = mid.Id };
            var orphan = new Employee { Id = ObjectId.GenerateNewId(), Name = "Orphan", ManagerId = ObjectId.GenerateNewId() };
            seed.Set<Employee>().AddRange(boss, mid, low, orphan);
            seed.SaveChanges();
        }

        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = new ChainDbContext(database, orders, customers, regions, employees, mode);
            return query(db.Set<Employee>()).AsEnumerable().Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        });
    }

    private void SeedOrders(string orders, string customers, string regions, string employees)
    {
        using var seed = new ChainDbContext(database, orders, customers, regions, employees, MongoQueryMode.DriverLinq);

        var north = new Region { Id = ObjectId.GenerateNewId(), Name = "North" };
        var cA = new Customer { Id = ObjectId.GenerateNewId(), Name = "North", RegionId = north.Id };
        var cB = new Customer { Id = ObjectId.GenerateNewId(), Name = "Bravo", RegionId = null };
        var cC = new Customer { Id = ObjectId.GenerateNewId(), Name = "Charlie", RegionId = ObjectId.GenerateNewId() };

        seed.Set<Region>().Add(north);
        seed.Set<Customer>().AddRange(cA, cB, cC);
        seed.Set<Order>().AddRange(
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 1, CustomerId = cA.Id },
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 2, CustomerId = cB.Id },
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 3, CustomerId = cC.Id },
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 4, CustomerId = ObjectId.GenerateNewId() },
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 5, CustomerId = null });
        seed.SaveChanges();
    }

    private static (string Orders, string Customers, string Regions, string Employees) CreateCollectionNames(string testName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var baseName = TemporaryDatabaseFixtureBase.CreateCollectionName(testName);
        return (baseName + "O" + suffix, baseName + "C" + suffix, baseName + "R" + suffix, baseName + "E" + suffix);
    }

    private class ChainDbContext : DbContext
    {
        private readonly string _orders;
        private readonly string _customers;
        private readonly string _regions;
        private readonly string _employees;

        public ChainDbContext(
            TemporaryDatabaseFixture database, string orders, string customers, string regions, string employees,
            MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<ChainDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _orders = orders;
            _customers = customers;
            _regions = regions;
            _employees = employees;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Region>().ToCollection(_regions);
            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(_customers);
                b.HasOne(c => c.Region).WithMany().HasForeignKey(c => c.RegionId).IsRequired(false);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_orders);
                b.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId).IsRequired(false);
            });
            modelBuilder.Entity<Employee>(b =>
            {
                b.ToCollection(_employees);
                b.HasOne(e => e.Manager).WithMany().HasForeignKey(e => e.ManagerId).IsRequired(false);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
