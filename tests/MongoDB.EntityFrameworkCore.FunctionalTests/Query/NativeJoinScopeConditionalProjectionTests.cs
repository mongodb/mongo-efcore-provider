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
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Differential-correctness functional test (real DB) for the native join-scope nav-null-check conditional
/// projection (EF-322; <c>docs/superpowers/specs/2026-09-23-native-join-scope-nav-null-conditional-projection-design.md</c>):
/// a BARE (non-wrapped) <c>Select</c> body that is exactly a ternary null-checking a join scope's Inner side and
/// dereferencing it — <c>o =&gt; o.Customer != null ? o.Customer.Name : "&lt;none&gt;"</c> — matched here against an
/// in-memory LINQ oracle over the same (Order, Customer?) left-outer pairing, including the UNMATCHED-FK
/// (dangling reference, no matching Customer document) case where the native null-check must actually fire.
/// </summary>
/// <remarks>
/// Mirrors <see cref="NativeJoinScopeNestedProjectionTests"/>'s structure (model, <c>TestServer</c>/
/// <c>TemporaryDatabaseFixture</c> fixture, per-test unique collection names, an explicit
/// <see cref="MongoQueryMode"/>-parameterized <c>[Theory]</c> asserting <c>NativeOnly</c> does not throw).
/// </remarks>
/// <remarks>
/// <b>The ELSE branch is a non-null sentinel (<c>"&lt;none&gt;"</c>), never a typed <c>null</c> literal — this is
/// LOAD-BEARING, not stylistic.</b> MEASURED: EF Core's own <c>NullCheckRemovingExpressionVisitor</c>
/// (<c>QueryTranslationPreprocessor.Process</c>, universal across every provider) collapses ANY
/// <c>caller != null ? caller.Member : null</c> ternary down to a bare <c>caller.Member</c> member access
/// BEFORE any provider-specific translation runs, whenever the ELSE branch is exactly a null constant — this
/// fires regardless of whether <c>caller</c> is a genuine EF navigation or a raw LINQ join-result member, and
/// regardless of whether the ternary was written in C# source or built by hand via
/// <see cref="System.Linq.Expressions.Expression.Condition"/> (confirmed against both). So the SPEC suite's own
/// <c>NorthwindMiscellaneousQueryMongoTest.Manual_expression_tree_typed_null_equality</c> — whose ELSE branch
/// IS a typed null, matching the exact upstream shape this ticket's design doc cites as the motivating case —
/// never reaches <c>NativeJoinScopeProjectionBinder.TryBindConditionalProjection</c> as a
/// <see cref="System.Linq.Expressions.ConditionalExpression"/> at all; it arrives already collapsed to a bare
/// <c>ti.Inner.City</c> member access, a DIFFERENT shape THIS binder does not target — that shape is instead
/// handled by the sibling bare-scalar-leaf arm added later in this plan
/// (<c>MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect</c>'s <c>Levels.Count == 1</c>-restricted
/// arm just after this one), so the gap this remark originally called out is closed. A non-null ELSE branch is
/// not subject to that collapse (the visitor's own guard requires the ELSE
/// branch to be exactly <c>ConstantExpression{Value: null}</c> for the <c>!=</c> case), so the
/// <see cref="System.Linq.Expressions.ConditionalExpression"/> this binder actually targets survives
/// preprocessing intact — exactly the shape
/// <c>NativeJoinScopeProjectionBinderTests.Binds_a_bare_nav_null_check_ternary_over_a_left_join</c> pins at the
/// unit level, and what these two tests below prove end-to-end against a real database.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeJoinScopeConditionalProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private const string NoneSentinel = "<none>";

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

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_nav_null_check_ternary_over_a_reference_join_matches_oracle(MongoQueryMode mode)
    {
        var (ordersName, customersName, regionsName) =
            CreateCollectionNames(nameof(Bare_nav_null_check_ternary_over_a_reference_join_matches_oracle));

        var matchedCustomerId = ObjectId.GenerateNewId();
        var matchedOrderId = ObjectId.GenerateNewId();
        var unmatchedOrderId = ObjectId.GenerateNewId();
        // A dangling FK: generated but NEVER inserted into the Customers collection, so the $lookup this join
        // emits genuinely finds no match for it (not merely null-CustomerId, which would be a different, less
        // interesting case — the $lookup itself still runs and fails to match).
        var danglingCustomerId = ObjectId.GenerateNewId();

        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Customer>().Add(new Customer { Id = matchedCustomerId, Name = "Alfreds" });
            seed.Set<Order>().AddRange(
                new Order { Id = matchedOrderId, OrderNo = 1, CustomerId = matchedCustomerId },     // matched
                new Order { Id = unmatchedOrderId, OrderNo = 2, CustomerId = danglingCustomerId }); // unmatched FK
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);

        var actual = db.Set<Order>()
            .OrderBy(o => o.OrderNo)
            .Select(o => o.Customer != null ? o.Customer.Name : NoneSentinel)
            .ToList();

        // The independent oracle: an ordinary LINQ-to-Objects left-outer join, sharing no code with the
        // provider, applying the SAME null-check ternary to the (Order, Customer?) pair.
        var orderSeeds = new[]
        {
            new { OrderNo = 1, CustomerId = (ObjectId?)matchedCustomerId },
            new { OrderNo = 2, CustomerId = (ObjectId?)danglingCustomerId }
        };
        var customerSeeds = new[] { new { Id = matchedCustomerId, Name = (string?)"Alfreds" } };

        var oracle = orderSeeds
            .OrderBy(o => o.OrderNo)
            .GroupJoin(customerSeeds, o => o.CustomerId, c => (ObjectId?)c.Id, (o, cs) => new { o, cs })
            .SelectMany(x => x.cs.DefaultIfEmpty(), (x, c) => c != null ? c.Name : NoneSentinel)
            .ToList();

        Assert.Equal(oracle, actual);
        Assert.Equal(2, actual.Count);
        Assert.Contains("Alfreds", actual);
        Assert.Contains(NoneSentinel, actual);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_nav_null_check_ternary_over_a_two_level_chain_matches_oracle(MongoQueryMode mode)
    {
        var (ordersName, customersName, regionsName) =
            CreateCollectionNames(nameof(Bare_nav_null_check_ternary_over_a_two_level_chain_matches_oracle));

        var matchedRegionId = ObjectId.GenerateNewId();
        var matchedCustomerId = ObjectId.GenerateNewId();
        var unmatchedCustomerId = ObjectId.GenerateNewId();
        // A dangling FK on the SECOND level: generated but never inserted into Regions.
        var danglingRegionId = ObjectId.GenerateNewId();
        var orderWithRegionId = ObjectId.GenerateNewId();
        var orderWithoutRegionId = ObjectId.GenerateNewId();

        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Region>().Add(new Region { Id = matchedRegionId, Name = "Western Europe" });
            seed.Set<Customer>().AddRange(
                new Customer { Id = matchedCustomerId, Name = "Alfreds", RegionId = matchedRegionId },
                new Customer { Id = unmatchedCustomerId, Name = "Blauer", RegionId = danglingRegionId });
            seed.Set<Order>().AddRange(
                new Order { Id = orderWithRegionId, OrderNo = 1, CustomerId = matchedCustomerId },
                new Order { Id = orderWithoutRegionId, OrderNo = 2, CustomerId = unmatchedCustomerId });
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);

        // A genuine TWO-level join chain: level 1 (Order -> Customer) is a plain (required) Join, level 2
        // (Customer -> Region) is a LeftJoin (via GroupJoin/SelectMany(DefaultIfEmpty)) — the null check under
        // test targets the SECOND level's Inner side, not the first's.
        Func<System.Collections.Generic.List<string?>> runQuery = () => db.Set<Order>()
            .Join(db.Set<Customer>(), o => o.CustomerId, c => c.Id, (o, c) => new { o, c })
            .GroupJoin(db.Set<Region>(), x => x.c.RegionId, r => r.Id, (x, rs) => new { x.o, x.c, rs })
            .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, x.c, r })
            .OrderBy(x => x.o.OrderNo)
            .Select(x => x.r != null ? x.r.Name : NoneSentinel)
            .ToList();

        // NativeJoinScopeProjectionBinder.TryBindConditionalProjection is depth-agnostic (works for any
        // scope.Levels.Count, exactly like TryBindProjection's own scalar/computed leaf arm for a chain) --
        // this genuine two-level chain goes native in BOTH modes and produces the CORRECT oracle-matching
        // result, the second level's null check firing for the dangling-region row.
        var actual = runQuery();

        // Independent in-memory oracle over the same seeded rows, sharing no code with the provider.
        var orderSeeds = new[]
        {
            new { OrderNo = 1, CustomerId = matchedCustomerId },
            new { OrderNo = 2, CustomerId = unmatchedCustomerId }
        };
        var customerSeeds = new[]
        {
            new { Id = matchedCustomerId, RegionId = (ObjectId?)matchedRegionId },
            new { Id = unmatchedCustomerId, RegionId = (ObjectId?)danglingRegionId }
        };
        var regionSeeds = new[] { new { Id = matchedRegionId, Name = (string?)"Western Europe" } };

        var oracle = orderSeeds
            .Join(customerSeeds, o => o.CustomerId, c => c.Id, (o, c) => new { o, c })
            .GroupJoin(regionSeeds, x => x.c.RegionId, r => (ObjectId?)r.Id, (x, rs) => new { x.o, x.c, rs })
            .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, r })
            .OrderBy(x => x.o.OrderNo)
            .Select(x => x.r != null ? x.r.Name : NoneSentinel)
            .ToList();

        Assert.Equal(oracle, actual);
        Assert.Equal(2, actual.Count);
        Assert.Equal("Western Europe", actual[0]);
        Assert.Equal(NoneSentinel, actual[1]);
    }

    [Fact]
    public void Bare_nav_null_check_ternary_over_a_two_level_chain_under_DriverLinq_pins_known_fallback_bug()
    {
        // EF-322 fixed this exact shape's Native/NativeOnly path (see the Theory above): it now goes native
        // and produces the correct oracle-matching result, so it no longer exercises the driver-LINQ fallback
        // bridge at all. That leaves this test as the ONLY remaining coverage of a separate, pre-existing,
        // still-unticketed data-correctness bug in that bridge for a two-level LeftJoin chain under an
        // EXPLICIT MongoQueryMode.DriverLinq: the second level's unmatched LeftJoin row comes back as a bare
        // `null` instead of running the ternary's ELSE branch (NoneSentinel). Pinned explicitly — asserting
        // the CURRENT, KNOWN-WRONG value — so this goes loudly green-then-red the moment the bridge bug is
        // fixed or changes shape, per this repo's "pin the known deviation" convention.
        var (ordersName, customersName, regionsName) =
            CreateCollectionNames(nameof(Bare_nav_null_check_ternary_over_a_two_level_chain_under_DriverLinq_pins_known_fallback_bug));

        var matchedRegionId = ObjectId.GenerateNewId();
        var matchedCustomerId = ObjectId.GenerateNewId();
        var unmatchedCustomerId = ObjectId.GenerateNewId();
        var danglingRegionId = ObjectId.GenerateNewId();
        var orderWithRegionId = ObjectId.GenerateNewId();
        var orderWithoutRegionId = ObjectId.GenerateNewId();

        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Region>().Add(new Region { Id = matchedRegionId, Name = "Western Europe" });
            seed.Set<Customer>().AddRange(
                new Customer { Id = matchedCustomerId, Name = "Alfreds", RegionId = matchedRegionId },
                new Customer { Id = unmatchedCustomerId, Name = "Blauer", RegionId = danglingRegionId });
            seed.Set<Order>().AddRange(
                new Order { Id = orderWithRegionId, OrderNo = 1, CustomerId = matchedCustomerId },
                new Order { Id = orderWithoutRegionId, OrderNo = 2, CustomerId = unmatchedCustomerId });
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq);

        var buggyActual = db.Set<Order>()
            .Join(db.Set<Customer>(), o => o.CustomerId, c => c.Id, (o, c) => new { o, c })
            .GroupJoin(db.Set<Region>(), x => x.c.RegionId, r => r.Id, (x, rs) => new { x.o, x.c, rs })
            .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, x.c, r })
            .OrderBy(x => x.o.OrderNo)
            .Select(x => x.r != null ? x.r.Name : NoneSentinel)
            .ToList();

        Assert.Equal(["Western Europe", null], buggyActual);
        Assert.NotEqual(NoneSentinel, buggyActual[1]);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void OrderBy_bare_nav_null_check_ternary_matches_oracle(MongoQueryMode mode)
    {
        var (ordersName, customersName, regionsName) =
            CreateCollectionNames(nameof(OrderBy_bare_nav_null_check_ternary_matches_oracle));

        var matchedCustomerId = ObjectId.GenerateNewId();
        var order1Id = ObjectId.GenerateNewId();
        var order2Id = ObjectId.GenerateNewId();
        var order3Id = ObjectId.GenerateNewId();
        // A dangling FK: generated but never inserted into Customers, so the $lookup finds no match.
        var danglingCustomerId = ObjectId.GenerateNewId();

        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Customer>().Add(new Customer { Id = matchedCustomerId, Name = "Bravo" });
            seed.Set<Order>().AddRange(
                new Order { Id = order1Id, OrderNo = 1, CustomerId = matchedCustomerId },
                new Order { Id = order2Id, OrderNo = 2, CustomerId = null },
                new Order { Id = order3Id, OrderNo = 3, CustomerId = danglingCustomerId });
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);

        var actual = db.Set<Order>()
            .OrderBy(o => o.Customer != null ? o.Customer.Name : NoneSentinel)
            .ThenBy(o => o.OrderNo)
            .Select(o => o.OrderNo)
            .ToList();

        // Independent in-memory oracle over the same seeded rows, sharing no code with the provider.
        var orderSeeds = new[]
        {
            new { OrderNo = 1, CustomerId = (ObjectId?)matchedCustomerId },
            new { OrderNo = 2, CustomerId = (ObjectId?)null },
            new { OrderNo = 3, CustomerId = (ObjectId?)danglingCustomerId }
        };
        var customerSeeds = new[] { new { Id = matchedCustomerId, Name = (string?)"Bravo" } };

        var oracle = orderSeeds
            .GroupJoin(customerSeeds, o => o.CustomerId, c => (ObjectId?)c.Id, (o, cs) => new { o, cs })
            .SelectMany(x => x.cs.DefaultIfEmpty(), (x, c) => new { x.o, c })
            .OrderBy(x => x.c != null ? x.c.Name : NoneSentinel)
            .ThenBy(x => x.o.OrderNo)
            .Select(x => x.o.OrderNo)
            .ToList();

        Assert.Equal(oracle, actual);
        Assert.Equal(3, actual.Count);
        // The sentinel "<none>" sorts BEFORE "Bravo" lexicographically ('<' is ASCII 60, 'B' is 66), so the
        // two unmatched orders come first, tied on the sentinel and broken by OrderNo; "Bravo" (the one
        // matched order) sorts last.
        Assert.Equal([2, 3, 1], actual);
    }

    private static (string Orders, string Customers, string Regions) CreateCollectionNames(string testName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + "O" + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + "C" + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + "R" + suffix);
    }

    private class JoinScopeDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _customersCollection;
        private readonly string _regionsCollection;

        public JoinScopeDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string customersCollection,
            string regionsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<JoinScopeDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
            _regionsCollection = regionsCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Region>().ToCollection(_regionsCollection);
            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(_customersCollection);
                b.HasOne(c => c.Region).WithMany().HasForeignKey(c => c.RegionId).IsRequired(false);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId).IsRequired(false);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
