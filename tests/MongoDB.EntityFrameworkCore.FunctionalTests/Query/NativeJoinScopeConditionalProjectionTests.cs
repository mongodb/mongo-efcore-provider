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
/// Differential test (real DB) for the native join-scope nav-null-check conditional projection: a bare
/// <c>Select</c> body that is a ternary null-checking a join scope's Inner side —
/// <c>o =&gt; o.Customer != null ? o.Customer.Name : "&lt;none&gt;"</c> — against an in-memory LINQ oracle,
/// including a dangling FK where the null check must fire.
/// </summary>
/// <remarks>
/// The else branch is a non-null sentinel on purpose. EF Core's <c>NullCheckRemovingExpressionVisitor</c>
/// (run in preprocessing for every provider) collapses <c>caller != null ? caller.Member : null</c> to a bare
/// <c>caller.Member</c>, so a typed-null else branch never reaches
/// <c>NativeJoinScopeProjectionBinder.TryBindConditionalProjection</c>; that collapsed shape is handled by the
/// bare-scalar-leaf arm in <c>MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect</c>. See
/// <c>NativeJoinScopeProjectionBinderTests.Binds_a_bare_nav_null_check_ternary_over_a_left_join</c> for the unit pin.
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
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_nav_null_check_ternary_over_a_reference_join_matches_oracle(MongoQueryMode mode)
    {
        var (ordersName, customersName, regionsName) =
            CreateCollectionNames(nameof(Bare_nav_null_check_ternary_over_a_reference_join_matches_oracle));

        var matchedCustomerId = ObjectId.GenerateNewId();
        var matchedOrderId = ObjectId.GenerateNewId();
        var unmatchedOrderId = ObjectId.GenerateNewId();
        // A dangling FK, never inserted into Customers, so the $lookup finds no match (not merely a null
        // CustomerId).
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

        // Independent oracle: a LINQ-to-Objects left-outer join applying the same ternary.
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

    // DriverLinq included: the flattened left-outer $unwind leaves the joined field MISSING, which the driver's
    // `$ne: [field, null]` treats as non-null; the bridge normalizes missing -> null after a preserved forced $unwind.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
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

        // Two-level chain: level 1 (Order -> Customer) is a plain Join, level 2 (Customer -> Region) a LeftJoin;
        // the null check targets the second level's Inner side.
        Func<System.Collections.Generic.List<string?>> runQuery = () => db.Set<Order>()
            .Join(db.Set<Customer>(), o => o.CustomerId, c => c.Id, (o, c) => new { o, c })
            .GroupJoin(db.Set<Region>(), x => x.c.RegionId, r => r.Id, (x, rs) => new { x.o, x.c, rs })
            .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, x.c, r })
            .OrderBy(x => x.o.OrderNo)
            .Select(x => x.r != null ? x.r.Name : NoneSentinel)
            .ToList();

        // TryBindConditionalProjection is depth-agnostic, so this goes native and the second level's null check
        // fires for the dangling-region row.
        var actual = runQuery();

        // Independent in-memory oracle.
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

        // Independent in-memory oracle.
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
        // "<none>" sorts before "Bravo" ('<' is 60, 'B' is 66), so the two unmatched orders come first, ordered by
        // OrderNo.
        Assert.Equal([2, 3, 1], actual);
    }

    // A bare entity null check (`o.Customer == null`, EF Core's Select_entity_compared_to_null) over an optional
    // reference navigation, which nav-expansion leaves as `ti.Inner == null` over a left-outer join. Both unmatched
    // shapes must answer "null": a null FK and a dangling FK whose customer document is absent. The hand oracle pins
    // the values; NativeAndParity pins that the query went native and that driver-LINQ agrees.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bare_entity_null_check_over_a_reference_join_goes_native(bool isNotNull)
    {
        var (ordersName, customersName, regionsName) = SeedNullCheckRows(
            nameof(Bare_entity_null_check_over_a_reference_join_goes_native) + isNotNull);

        var actual = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
            var query = db.Set<Order>().OrderBy(o => o.OrderNo);
            return isNotNull
                ? query.Select(o => o.Customer != null).ToList()
                : query.Select(o => o.Customer == null).ToList();
        });

        // OrderNo 1 matched, 2 null FK, 3 dangling FK.
        Assert.Equal(isNotNull ? new[] { true, false, false } : new[] { false, true, true }, actual);
    }

    // The same check as one member of a wrapped projection, beside an outer-side scalar.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrapped_entity_null_check_over_a_reference_join_goes_native(bool isNotNull)
    {
        var (ordersName, customersName, regionsName) = SeedNullCheckRows(
            nameof(Wrapped_entity_null_check_over_a_reference_join_goes_native) + isNotNull);

        var actual = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
            var query = db.Set<Order>().OrderBy(o => o.OrderNo);
            return isNotNull
                ? query.Select(o => new { o.OrderNo, Check = o.Customer != null }).AsEnumerable()
                    .Select(x => x.OrderNo + ":" + x.Check).ToList()
                : query.Select(o => new { o.OrderNo, Check = o.Customer == null }).AsEnumerable()
                    .Select(x => x.OrderNo + ":" + x.Check).ToList();
        });

        Assert.Equal(
            isNotNull ? new[] { "1:True", "2:False", "3:False" } : new[] { "1:False", "2:True", "3:True" },
            actual);
    }

    // A two-hop optional navigation (`o.Customer.Region == null`) checks the SECOND level's Inner side. Every order
    // whose customer is unmatched (null or dangling FK) has no region either.
    [Fact]
    public void Bare_entity_null_check_over_a_two_level_chain_goes_native()
    {
        var (ordersName, customersName, regionsName) = SeedNullCheckRows(
            nameof(Bare_entity_null_check_over_a_two_level_chain_goes_native));

        var actual = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
            return db.Set<Order>().OrderBy(o => o.OrderNo).Select(o => o.Customer!.Region == null).ToList();
        });

        // OrderNo 1's customer has a matched region; 2 and 3 have no customer, hence no region.
        Assert.Equal(new[] { false, true, true }, actual);
    }

    [Fact]
    public void Wrapped_entity_null_check_over_a_two_level_chain_goes_native()
    {
        var (ordersName, customersName, regionsName) = SeedNullCheckRows(
            nameof(Wrapped_entity_null_check_over_a_two_level_chain_goes_native));

        var actual = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
            return db.Set<Order>().OrderBy(o => o.OrderNo)
                .Select(o => new { o.OrderNo, NoCustomer = o.Customer == null, NoRegion = o.Customer!.Region == null })
                .AsEnumerable().Select(x => x.OrderNo + ":" + x.NoCustomer + ":" + x.NoRegion).ToList();
        });

        Assert.Equal(new[] { "1:False:False", "2:True:True", "3:True:True" }, actual);
    }

    // Beside a whole-entity member the projection must be read from whole documents, which a computed null-check leaf
    // is not; the binder declines the whole projection and the fallback answers.
    [Fact]
    public void Wrapped_entity_null_check_beside_a_whole_entity_declines()
    {
        var (ordersName, customersName, regionsName) = SeedNullCheckRows(
            nameof(Wrapped_entity_null_check_beside_a_whole_entity_declines));

        var actual = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
            return db.Set<Order>().OrderBy(o => o.OrderNo)
                .Select(o => new { Order = o, NoCustomer = o.Customer == null })
                .AsEnumerable().Select(x => x.Order.OrderNo + ":" + x.NoCustomer).ToList();
        });

        Assert.Equal(new[] { "1:False", "2:True", "3:True" }, actual);
    }

    // An INNER join's Inner side is never null, so the check is constant; the shared gate declines it (as for the
    // ternary and Where arms) rather than render a constant. The single matched row answers `false` for `== null`.
    // The decline is conservative: with the left-outer guard removed the native query still answers correctly (the
    // inner $unwind drops unmatched rows first), so this test, not a wrong result, is what pins that guard.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bare_entity_null_check_over_an_inner_join_declines(bool isNotNull)
    {
        var (ordersName, customersName, regionsName) = SeedNullCheckRows(
            nameof(Bare_entity_null_check_over_an_inner_join_declines) + isNotNull);

        var actual = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
            var joined = db.Set<Order>()
                .Join(db.Set<Customer>(), o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .OrderBy(x => x.o.OrderNo);
            return isNotNull
                ? joined.Select(x => x.c != null).ToList()
                : joined.Select(x => x.c == null).ToList();
        });

        Assert.Equal(new[] { isNotNull }, actual);
    }

    // Seeds OrderNo 1 (customer matched, region matched), 2 (null CustomerId) and 3 (dangling CustomerId: the customer
    // document is never inserted).
    private (string Orders, string Customers, string Regions) SeedNullCheckRows(string testName)
    {
        var names = CreateCollectionNames(testName);
        var regionId = ObjectId.GenerateNewId();
        var customerId = ObjectId.GenerateNewId();

        using var seed = new JoinScopeDbContext(database, names.Orders, names.Customers, names.Regions, MongoQueryMode.DriverLinq);
        seed.Set<Region>().Add(new Region { Id = regionId, Name = "Western Europe" });
        seed.Set<Customer>().Add(new Customer { Id = customerId, Name = "Alfreds", RegionId = regionId });
        seed.Set<Order>().AddRange(
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 1, CustomerId = customerId },
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 2, CustomerId = null },
            new Order { Id = ObjectId.GenerateNewId(), OrderNo = 3, CustomerId = ObjectId.GenerateNewId() });
        seed.SaveChanges();
        return names;
    }

#if !EF8 && !EF9
    // Two-level chains spelled with EF10's LeftJoin operator (the GroupJoin/SelectMany/DefaultIfEmpty spelling is
    // above). Seed rows exercise every unmatched shape: a matched region, a dangling RegionId, a null RegionId,
    // and (for the left-left chains) an order whose customer is dangling, so level 1 is unmatched too. Every
    // unmatched row must read the ternary's ELSE value, never a bare null or a default. The DriverLinq rows guard
    // the fallback's missing -> null normalization after a preserved forced $unwind
    // (MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs): with it disabled, every explicit-LeftJoin DriverLinq
    // row returns null for the unmatched rows.
    [Theory]
    [InlineData(MongoQueryMode.Native, "JoinThenLeftJoinBareTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "JoinThenLeftJoinBareTernary")]
    [InlineData(MongoQueryMode.Native, "LeftJoinThenLeftJoinBareTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "LeftJoinThenLeftJoinBareTernary")]
    [InlineData(MongoQueryMode.Native, "LeftJoinThenLeftJoinFirstLevelTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "LeftJoinThenLeftJoinFirstLevelTernary")]
    [InlineData(MongoQueryMode.Native, "LeftJoinThenLeftJoinWrappedTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "LeftJoinThenLeftJoinWrappedTernary")]
    [InlineData(MongoQueryMode.Native, "LeftJoinThenLeftJoinResultSelectorTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "LeftJoinThenLeftJoinResultSelectorTernary")]
    [InlineData(MongoQueryMode.Native, "JoinThenLeftJoinResultSelectorTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "JoinThenLeftJoinResultSelectorTernary")]
    [InlineData(MongoQueryMode.Native, "TwoHopNavigationTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "TwoHopNavigationTernary")]
    [InlineData(MongoQueryMode.Native, "TwoHopNavigationWrappedTernary")]
    [InlineData(MongoQueryMode.DriverLinq, "TwoHopNavigationWrappedTernary")]
    public void Nav_null_check_ternary_over_a_two_level_LeftJoin_chain_matches_oracle(MongoQueryMode mode, string shape)
    {
        var (ordersName, customersName, regionsName) =
            CreateCollectionNames(nameof(Nav_null_check_ternary_over_a_two_level_LeftJoin_chain_matches_oracle) + shape);

        var matchedRegionId = ObjectId.GenerateNewId();
        var danglingRegionId = ObjectId.GenerateNewId();
        var danglingCustomerId = ObjectId.GenerateNewId();
        var matchedCustomerId = ObjectId.GenerateNewId();
        var danglingRegionCustomerId = ObjectId.GenerateNewId();
        var nullRegionCustomerId = ObjectId.GenerateNewId();

        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Region>().Add(new Region { Id = matchedRegionId, Name = "Western Europe" });
            seed.Set<Customer>().AddRange(
                new Customer { Id = matchedCustomerId, Name = "Alfreds", RegionId = matchedRegionId },
                new Customer { Id = danglingRegionCustomerId, Name = "Blauer", RegionId = danglingRegionId },
                new Customer { Id = nullRegionCustomerId, Name = "Chop-suey", RegionId = null });
            seed.Set<Order>().AddRange(
                new Order { Id = ObjectId.GenerateNewId(), OrderNo = 1, CustomerId = matchedCustomerId },
                new Order { Id = ObjectId.GenerateNewId(), OrderNo = 2, CustomerId = danglingRegionCustomerId },
                new Order { Id = ObjectId.GenerateNewId(), OrderNo = 3, CustomerId = nullRegionCustomerId },
                new Order { Id = ObjectId.GenerateNewId(), OrderNo = 4, CustomerId = danglingCustomerId });
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, regionsName, mode);
        var actual = RunTwoLevelLeftJoinTernary(db.Set<Order>(), db.Set<Customer>(), db.Set<Region>(), shape);

        using var oracleDb = new JoinScopeDbContext(database, ordersName, customersName, regionsName, MongoQueryMode.DriverLinq);
        var expected = RunTwoLevelLeftJoinTernaryOracle(
            oracleDb.Set<Order>().AsNoTracking().ToList(),
            oracleDb.Set<Customer>().AsNoTracking().ToList(),
            oracleDb.Set<Region>().AsNoTracking().ToList(),
            shape);

        Assert.Contains(expected, e => e.Contains(NoneSentinel, StringComparison.Ordinal));
        Assert.Equal(expected, actual);
    }

    private static System.Collections.Generic.List<string> RunTwoLevelLeftJoinTernary(
        IQueryable<Order> orders, IQueryable<Customer> customers, IQueryable<Region> regions, string shape)
        => shape switch
        {
            "JoinThenLeftJoinBareTernary" => orders
                .Join(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .LeftJoin(regions, x => x.c.RegionId, r => (ObjectId?)r.Id, (x, r) => new { x.o, x.c, r })
                .OrderBy(x => x.o.OrderNo)
                .Select(x => x.r != null ? x.r.Name : NoneSentinel)
                .AsEnumerable().Select(s => s ?? "<null>").ToList(),
            "LeftJoinThenLeftJoinBareTernary" => orders
                .LeftJoin(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .LeftJoin(regions, x => x.c.RegionId, r => (ObjectId?)r.Id, (x, r) => new { x.o, x.c, r })
                .OrderBy(x => x.o.OrderNo)
                .Select(x => x.r != null ? x.r.Name : NoneSentinel)
                .AsEnumerable().Select(s => s ?? "<null>").ToList(),
            "LeftJoinThenLeftJoinFirstLevelTernary" => orders
                .LeftJoin(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .LeftJoin(regions, x => x.c.RegionId, r => (ObjectId?)r.Id, (x, r) => new { x.o, x.c, r })
                .OrderBy(x => x.o.OrderNo)
                .Select(x => x.c != null ? x.c.Name : NoneSentinel)
                .AsEnumerable().Select(s => s ?? "<null>").ToList(),
            "LeftJoinThenLeftJoinWrappedTernary" => orders
                .LeftJoin(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .LeftJoin(regions, x => x.c.RegionId, r => (ObjectId?)r.Id, (x, r) => new { x.o, x.c, r })
                .OrderBy(x => x.o.OrderNo)
                .Select(x => new { x.o.OrderNo, Region = x.r != null ? x.r.Name : NoneSentinel })
                .AsEnumerable().Select(x => x.OrderNo + ":" + (x.Region ?? "<null>")).ToList(),
            "LeftJoinThenLeftJoinResultSelectorTernary" => orders
                .LeftJoin(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .LeftJoin(regions, x => x.c.RegionId, r => (ObjectId?)r.Id,
                    (x, r) => new { x.o.OrderNo, Region = r != null ? r.Name : NoneSentinel })
                .OrderBy(x => x.OrderNo)
                .AsEnumerable().Select(x => x.OrderNo + ":" + (x.Region ?? "<null>")).ToList(),
            "JoinThenLeftJoinResultSelectorTernary" => orders
                .Join(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
                .LeftJoin(regions, x => x.c.RegionId, r => (ObjectId?)r.Id,
                    (x, r) => new { x.o.OrderNo, Region = r != null ? r.Name : NoneSentinel })
                .OrderBy(x => x.OrderNo)
                .AsEnumerable().Select(x => x.OrderNo + ":" + (x.Region ?? "<null>")).ToList(),
            // The same two left-outer levels reached through optional reference navigations, which EF expands
            // into the LeftJoin chain itself.
            "TwoHopNavigationTernary" => orders
                .OrderBy(o => o.OrderNo)
                .Select(o => o.Customer!.Region != null ? o.Customer.Region.Name : NoneSentinel)
                .AsEnumerable().Select(s => s ?? "<null>").ToList(),
            "TwoHopNavigationWrappedTernary" => orders
                .OrderBy(o => o.OrderNo)
                .Select(o => new { o.OrderNo, Region = o.Customer!.Region != null ? o.Customer.Region.Name : NoneSentinel })
                .AsEnumerable().Select(x => x.OrderNo + ":" + (x.Region ?? "<null>")).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

    // LINQ-to-Objects spelling of the shapes above: the same operators, with `?.` where the database query's
    // null-propagating member access would dereference an unmatched level-1 row in memory.
    private static System.Collections.Generic.List<string> RunTwoLevelLeftJoinTernaryOracle(
        System.Collections.Generic.List<Order> orders, System.Collections.Generic.List<Customer> customers,
        System.Collections.Generic.List<Region> regions, string shape)
    {
        var innerFirst = orders
            .Join(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c = (Customer?)c })
            .LeftJoin(regions, x => x.c?.RegionId, r => (ObjectId?)r.Id, (x, r) => new { x.o, x.c, r })
            .OrderBy(x => x.o.OrderNo)
            .ToList();
        var leftFirst = orders
            .LeftJoin(customers, o => o.CustomerId, c => (ObjectId?)c.Id, (o, c) => new { o, c })
            .LeftJoin(regions, x => x.c?.RegionId, r => (ObjectId?)r.Id, (x, r) => new { x.o, x.c, r })
            .OrderBy(x => x.o.OrderNo)
            .ToList();

        return shape switch
        {
            "JoinThenLeftJoinBareTernary" => innerFirst.Select(x => x.r != null ? x.r.Name! : NoneSentinel).ToList(),
            "LeftJoinThenLeftJoinBareTernary" or "TwoHopNavigationTernary" => leftFirst.Select(x => x.r != null ? x.r.Name! : NoneSentinel).ToList(),
            "LeftJoinThenLeftJoinFirstLevelTernary" => leftFirst.Select(x => x.c != null ? x.c.Name! : NoneSentinel).ToList(),
            "LeftJoinThenLeftJoinWrappedTernary" or "LeftJoinThenLeftJoinResultSelectorTernary" or "TwoHopNavigationWrappedTernary"
                => leftFirst.Select(x => x.o.OrderNo + ":" + (x.r != null ? x.r.Name : NoneSentinel)).ToList(),
            "JoinThenLeftJoinResultSelectorTernary"
                => innerFirst.Select(x => x.o.OrderNo + ":" + (x.r != null ? x.r.Name : NoneSentinel)).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }
#endif

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
