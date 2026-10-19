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
/// An unfiltered reference-collection-navigation Count/LongCount compared inside a Where predicate translates
/// natively via $lookup + $size, reusing the machinery of NativeProjectionBinder.TryTranslateProjectedCollectionCount.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferenceCollectionCountPredicateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Bare_Count_comparison_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Bare_Count_comparison_goes_native_under_NativeOnly));

        var result = db.Owners.Where(o => o.Orders.Count > 1).ToList();

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Name);
    }

    [Fact]
    public void LongCount_comparison_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(LongCount_comparison_goes_native_under_NativeOnly));

        var result = db.Owners.Where(o => o.Orders.LongCount() > 1).ToList();

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Name);
    }

    [Fact]
    public void Count_on_the_right_hand_side_of_the_comparison_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Count_on_the_right_hand_side_of_the_comparison_goes_native_under_NativeOnly));

        var result = db.Owners.Where(o => 1 < o.Orders.Count).ToList();

        Assert.Single(result);
        Assert.Equal("Alice", result[0].Name);
    }

    [Fact]
    public void Zero_related_rows_reads_as_zero_under_NativeOnly()
    {
        var ownerWithNoOrders = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol" };
        var seed = new Seed([ownerWithNoOrders], []);
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Zero_related_rows_reads_as_zero_under_NativeOnly));

        var result = db.Owners.Where(o => o.Orders.Count == 0).ToList();

        Assert.Single(result);
        Assert.Equal("Carol", result[0].Name);
    }

    // A genuinely user-filtered `Where(pred).Count()` must decline, not be matched as a bare count. Nav-expansion
    // ANDs the FK correlation with the user's filter, and TryGetCorrelationEqualitySides only recognizes the bare
    // FK equality.
    [Fact]
    public void Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly));

        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => db.Owners.Where(o => o.Orders.Where(ord => ord.Total > 15m).Count() > 0).ToList());
    }

    // Baseline differential across zero/one/many related rows, with no other operator composed.
    [Fact]
    public void Differential_native_result_matches_in_memory_oracle_across_related_row_counts()
    {
        var ownerWithNoOrders = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol" };
        var seed = SeedOwnersAndOrders();
        var allOwners = seed.Owners.Append(ownerWithNoOrders).ToArray();
        var combinedSeed = new Seed(allOwners, seed.Orders);

        using var native = CreateContext(combinedSeed, MongoQueryMode.NativeOnly,
            nameof(Differential_native_result_matches_in_memory_oracle_across_related_row_counts) + "Native");
        using var driverLinq = CreateContext(combinedSeed, MongoQueryMode.DriverLinq,
            nameof(Differential_native_result_matches_in_memory_oracle_across_related_row_counts) + "DriverLinq");

        var nativeNames = native.Owners.Where(o => o.Orders.Count >= 1).Select(o => o.Name).OrderBy(n => n).ToList();
        var oracleNames = driverLinq.Owners.Where(o => o.Orders.Count >= 1).Select(o => o.Name).OrderBy(n => n).ToList();

        Assert.Equal(oracleNames, nativeNames);
    }

    // ── Retroactive Route decline ────────────────────────────────────────────────────────────────────
    //
    // The predicate binder registers its $lookup eagerly, before a later Union/Concat, projected Distinct or Join is
    // known. Those MongoSelectLowerer branches don't flush PostJoinOps where this $match needs it, so
    // MongoSelectDefinition.Route declines the combination: driver-LINQ fallback under Native, a clean throw under
    // NativeOnly. Each test checks against an in-memory oracle.
    [Fact]
    public void Union_after_count_predicate_falls_back_to_correct_data_under_Native()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var native = CreateContext(seed, MongoQueryMode.Native, nameof(Union_after_count_predicate_falls_back_to_correct_data_under_Native));

        var result = native.Owners.Union(native.Owners).Where(o => o.Orders.Count > 1).ToList();
        var oracle = InMemoryOracle(seed, o => o.Orders.Count > 1);

        AssertSameOwnersByName(oracle, result);
    }

    [Fact]
    public void Union_after_count_predicate_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Union_after_count_predicate_declines_cleanly_under_NativeOnly));

        Assert.ThrowsAny<Exception>(() => db.Owners.Union(db.Owners).Where(o => o.Orders.Count > 1).ToList());
    }

    [Fact]
    public void Concat_after_count_predicate_falls_back_to_correct_data_under_Native()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var native = CreateContext(seed, MongoQueryMode.Native, nameof(Concat_after_count_predicate_falls_back_to_correct_data_under_Native));

        var result = native.Owners.Where(o => o.Orders.Count > 1).Concat(native.Owners.Where(o => o.Name == "Bob")).ToList();
        var oracle = InMemoryOracle(seed, o => o.Orders.Count > 1)
            .Concat(seed.Owners.Where(o => o.Name == "Bob"))
            .ToList();

        AssertSameOwnersByName(oracle, result);
    }

    [Fact]
    public void Count_predicate_before_projected_Union_falls_back_to_correct_data_under_Native()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Count_predicate_before_projected_Union_falls_back_to_correct_data_under_Native));

        var result = native.Owners.Where(o => o.Orders.Count > 1).Select(o => new { o.Name })
            .Union(native.Owners.Where(o => o.Name == "Zed").Select(o => new { o.Name }))
            .ToList();

        var oracleNames = InMemoryOracle(seed, o => o.Orders.Count > 1).Select(o => o.Name)
            .Union(seed.Owners.Where(o => o.Name == "Zed").Select(o => o.Name))
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(oracleNames, result.Select(r => r.Name).OrderBy(n => n).ToList());
    }

    [Fact]
    public void Count_predicate_before_projected_Union_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Count_predicate_before_projected_Union_declines_cleanly_under_NativeOnly));

        Assert.ThrowsAny<Exception>(() =>
            db.Owners.Where(o => o.Orders.Count > 1).Select(o => new { o.Name })
                .Union(db.Owners.Where(o => o.Name == "Zed").Select(o => new { o.Name }))
                .ToList());
    }

    [Fact]
    public void Count_predicate_before_projected_Distinct_OrderBy_Take_falls_back_to_correct_data_under_Native()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Count_predicate_before_projected_Distinct_OrderBy_Take_falls_back_to_correct_data_under_Native));

        var result = native.Owners.Where(o => o.Orders.Count > 1).Select(o => new { N = o.Name })
            .Distinct().OrderByDescending(x => x.N).Take(1).ToList();

        var oracle = InMemoryOracle(seed, o => o.Orders.Count > 1).Select(o => o.Name)
            .Distinct().OrderByDescending(n => n).Take(1).ToList();

        Assert.Equal(oracle, result.Select(r => r.N).ToList());
    }

    [Fact]
    public void Count_predicate_before_projected_Distinct_Where_falls_back_to_correct_data_under_Native()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Count_predicate_before_projected_Distinct_Where_falls_back_to_correct_data_under_Native));

        var result = native.Owners.Where(o => o.Orders.Count > 1).Select(o => new { N = o.Name })
            .Distinct().Where(x => x.N != "Alice").ToList();

        var oracle = InMemoryOracle(seed, o => o.Orders.Count > 1).Select(o => o.Name)
            .Distinct().Where(n => n != "Alice").OrderBy(n => n).ToList();

        Assert.Equal(oracle, result.Select(r => r.N).OrderBy(n => n).ToList());
    }

    [Fact]
    public void Count_predicate_before_projected_Distinct_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Count_predicate_before_projected_Distinct_declines_cleanly_under_NativeOnly));

        Assert.ThrowsAny<Exception>(() =>
            db.Owners.Where(o => o.Orders.Count > 1).Select(o => new { N = o.Name })
                .Distinct().OrderByDescending(x => x.N).Take(1).ToList());
    }

    [Fact]
    public void Count_predicate_before_a_later_Join_falls_back_to_correct_data_under_Native()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Count_predicate_before_a_later_Join_falls_back_to_correct_data_under_Native));

        var result = native.Owners.Where(o => o.Orders.Count > 1).OrderBy(o => o.Name).Take(1)
            .Join(native.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .ToList();

        var expectedOwner = InMemoryOracle(seed, o => o.Orders.Count > 1).OrderBy(o => o.Name).First();
        var oracle = seed.Orders.Where(r => r.OwnerId == expectedOwner.Id)
            .Select(r => (expectedOwner.Name, r.Total))
            .OrderBy(x => x.Total)
            .ToList();

        Assert.Equal(oracle, result.Select(r => (r.Name, r.Total)).OrderBy(x => x.Total).ToList());
    }

    [Fact]
    public void Count_predicate_before_a_later_Join_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Count_predicate_before_a_later_Join_declines_cleanly_under_NativeOnly));

        // The exception type isn't contract; AssertDeclinesCleanly checks it isn't a deserializer crash.
        AssertDeclinesCleanly(() =>
            db.Owners.Where(o => o.Orders.Count > 1).OrderBy(o => o.Name).Take(1)
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
                .ToList());
    }

    // ── Count predicate on a join's inner side ──────────────────────────────────────────────────────
    //
    // The predicate lives in the inner MongoSelectDefinition.PostJoinOps, so IsBareCollectionScan must check
    // PostJoinOps; otherwise the inner reads as bare and the filter is silently dropped in every mode (including
    // DriverLinq, since IsBareCollectionScan also gates MarkSawNonBareJoinInner).
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Count_predicate_on_join_inner_side_declines_cleanly_in_every_mode(MongoQueryMode mode)
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Count_predicate_on_join_inner_side_declines_cleanly_in_every_mode) + mode);

        // Must decline in every mode, like the non-count control case (`o.Name != "Carol"`).
        AssertDeclinesCleanly(() =>
            db.Orders.Join(
                    db.Owners.Where(o => o.Orders.Count > 1),
                    r => r.OwnerId, o => o.Id, (r, o) => new { o.Name, r.Total })
                .ToList());
    }

    // Correlated SelectMany after the count predicate: the predicate's $lookup could share a document path with
    // the SelectMany's $lookup/$unwind, corrupting reads with a BSON type mismatch. This shape is unsupported in
    // every mode, so Route declines when `_unwindSources.Count > 0`.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Count_predicate_before_correlated_SelectMany_declines_cleanly_in_every_mode(MongoQueryMode mode)
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Count_predicate_before_correlated_SelectMany_declines_cleanly_in_every_mode) + mode);

        // ThrowsAny isn't enough: the hazard is a BSON-deserialize crash (InvalidCastException / FormatException)
        // instead of a clean decline, and ThrowsAny accepts either.
        AssertDeclinesCleanly(() =>
            (from o in db.Owners.Where(o => o.Orders.Count > 1)
             from r in db.Orders.Where(r => r.OwnerId == o.Id)
             select new { o.Name, r.Total })
            .ToList());
    }

    // ── Paged Include on the same navigation as the count predicate ───────────────────────────────────
    //
    // The predicate registers a bare `_lookup_<Nav>` (NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup);
    // the Include later registers a paged lookup for the same alias. MongoQueryExpression.AddLookup's bare-then-
    // pipelined merge would fold the paging into the predicate's lookup, so $size reads the paged array — silently
    // wrong in every mode, since the merge happens before MongoQueryMode is consulted. The predicate's lookup is
    // flagged LookupExpression.IsBareCountSizeSource and MongoProjectionBindingExpressionVisitor reroutes a paged
    // Include away from it.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_ordered_Take_on_same_nav_as_count_predicate_returns_correct_data(MongoQueryMode mode)
    {
        var seed = SeedFourOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Include_ordered_Take_on_same_nav_as_count_predicate_returns_correct_data) + mode);

        var result = db.Owners
            .Include(o => o.Orders.OrderBy(r => r.Total).Take(1))
            .Where(o => o.Orders.Count > 1)
            .ToList();

        // Only owners whose unfiltered count exceeds 1 survive (Alice 2, Carol 3), each with just the one cheapest
        // order the Include asked for. A $size over the Take(1)'d array would return zero rows.
        AssertOwnersWithLoadedOrders(
            result,
            ("Alice", new[] { 10m }),
            ("Carol", new[] { 40m }));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_ordered_Skip_on_same_nav_as_count_predicate_returns_correct_data(MongoQueryMode mode)
    {
        var seed = SeedFourOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Include_ordered_Skip_on_same_nav_as_count_predicate_returns_correct_data) + mode);

        var result = db.Owners
            .Include(o => o.Orders.OrderBy(r => r.Total).Skip(1))
            .Where(o => o.Orders.Count > 1)
            .ToList();

        AssertOwnersWithLoadedOrders(
            result,
            ("Alice", new[] { 20m }),
            ("Carol", new[] { 50m, 60m }));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_ordered_Skip_Take_on_same_nav_as_count_predicate_returns_correct_data(MongoQueryMode mode)
    {
        var seed = SeedFourOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Include_ordered_Skip_Take_on_same_nav_as_count_predicate_returns_correct_data) + mode);

        var result = db.Owners
            .Include(o => o.Orders.OrderBy(r => r.Total).Skip(1).Take(1))
            .Where(o => o.Orders.Count > 1)
            .ToList();

        AssertOwnersWithLoadedOrders(
            result,
            ("Alice", new[] { 20m }),
            ("Carol", new[] { 50m }));
    }

    private static void AssertOwnersWithLoadedOrders(
        List<Owner> actual, params (string Name, decimal[] OrderTotals)[] expected)
    {
        var actualByName = actual.ToDictionary(o => o.Name, o => o.Orders.Select(r => r.Total).OrderBy(t => t).ToList());
        var expectedByName = expected.ToDictionary(e => e.Name, e => e.OrderTotals.OrderBy(t => t).ToList());

        Assert.Equal(expectedByName.Keys.OrderBy(n => n), actualByName.Keys.OrderBy(n => n));
        foreach (var name in expectedByName.Keys)
            Assert.Equal(expectedByName[name], actualByName[name]);
    }

    // Self-referencing variant (Node.Children : List<Node>): the fix mustn't rely on distinct entity types.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Include_ordered_Take_on_same_self_referencing_nav_as_count_predicate_returns_correct_data(MongoQueryMode mode)
    {
        var rootA = new Node { Id = ObjectId.GenerateNewId(), Order = 0 };
        var rootB = new Node { Id = ObjectId.GenerateNewId(), Order = 0 };
        var childA1 = new Node { Id = ObjectId.GenerateNewId(), Order = 2, ParentId = rootA.Id };
        var childA2 = new Node { Id = ObjectId.GenerateNewId(), Order = 1, ParentId = rootA.Id };
        var childB1 = new Node { Id = ObjectId.GenerateNewId(), Order = 5, ParentId = rootB.Id };

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodesName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Include_ordered_Take_on_same_self_referencing_nav_as_count_predicate_returns_correct_data) + mode) + suffix;
        database.MongoDatabase.GetCollection<Node>(nodesName)
            .InsertMany([rootA, rootB, childA1, childA2, childB1]);

        using var db = new NodeTestDbContext(database, nodesName, mode);

        var result = db.Nodes
            .Include(n => n.Children.OrderBy(c => c.Order).Take(1))
            .Where(n => n.Children.Count > 1)
            .ToList();

        // Only rootA has more than one child; its Include should load only childA2 (Order == 1).
        var root = Assert.Single(result);
        Assert.Equal(rootA.Id, root.Id);
        var loadedChild = Assert.Single(root.Children);
        Assert.Equal(childA2.Id, loadedChild.Id);
    }

    public class Node
    {
        public ObjectId Id { get; set; }
        public int Order { get; set; }
        public ObjectId? ParentId { get; set; }
        public Node? Parent { get; set; }
        public List<Node> Children { get; set; } = [];
    }

    private sealed class NodeTestDbContext : DbContext
    {
        private readonly string _nodesCollection;

        public DbSet<Node> Nodes { get; set; } = null!;

        public NodeTestDbContext(TemporaryDatabaseFixture database, string nodesCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode))
        {
            _nodesCollection = nodesCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var optionsBuilder = new DbContextOptionsBuilder<NodeTestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Node>(b =>
            {
                b.ToCollection(_nodesCollection);
                b.HasMany(n => n.Children).WithOne(n => n.Parent).HasForeignKey(n => n.ParentId);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// Asserts <paramref name="query"/> throws, but not a BSON-deserializer crash type
    /// (<see cref="InvalidCastException"/>/<see cref="FormatException"/>/<see cref="OverflowException"/>), which
    /// signals a wrongly built pipeline rather than a clean decline. The exact decline type isn't contract.
    /// </summary>
    private static void AssertDeclinesCleanly(Action query)
    {
        var ex = Assert.ThrowsAny<Exception>(query);
        Assert.False(
            ex is InvalidCastException or FormatException or OverflowException,
            $"Expected a clean decline, got a deserializer-crash exception instead: {ex.GetType().FullName}: {ex.Message}");
    }

    private static List<Owner> InMemoryOracle(Seed seed, Func<OwnerWithOrders, bool> predicate)
        => seed.Owners
            .Select(o => new OwnerWithOrders(o, seed.Orders.Where(r => r.OwnerId == o.Id).ToList()))
            .Where(predicate)
            .Select(x => x.Owner)
            .ToList();

    private sealed record OwnerWithOrders(Owner Owner, List<Order> Orders)
    {
        public string Name => Owner.Name;
    }

    private static void AssertSameOwnersByName(IEnumerable<Owner> expected, IEnumerable<Owner> actual)
        => Assert.Equal(
            expected.Select(o => o.Name).OrderBy(n => n).ToList(),
            actual.Select(o => o.Name).OrderBy(n => n).ToList());

    private sealed record Seed(Owner[] Owners, Order[] Orders);

    private static Seed SeedOwnersAndOrders()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob" };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m },
        };

        return new Seed([ownerA, ownerB], orders);
    }

    // Alice (2) and Carol (3) satisfy Orders.Count > 1, Bob (1) doesn't: more than one matching row to exercise
    // ordering/dedup.
    private static Seed SeedThreeOwnersAndOrders()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob" };
        var ownerC = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol" };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerC.Id, Total = 40m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerC.Id, Total = 50m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerC.Id, Total = 60m },
        };

        return new Seed([ownerA, ownerB, ownerC], orders);
    }

    // Alice=2, Bob=1, Carol=3, Dave=0: two survivors of `Count > 1`, each with enough orders that a
    // `.Take(1)`/`.Skip(1)` Include actually narrows what's loaded.
    private static Seed SeedFourOwnersAndOrders()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob" };
        var ownerC = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol" };
        var ownerD = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dave" };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerC.Id, Total = 40m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerC.Id, Total = 50m },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerC.Id, Total = 60m },
        };

        return new Seed([ownerA, ownerB, ownerC, ownerD], orders);
    }

    private CountPredicateTestDbContext CreateContext(Seed seed, MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Owners" + suffix;
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Orders" + suffix;

        if (seed.Owners.Length > 0)
            database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany(seed.Owners);
        if (seed.Orders.Length > 0)
            database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(seed.Orders);

        return new CountPredicateTestDbContext(database, ownersName, ordersName, mode);
    }

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public decimal Total { get; set; }
    }

    private sealed class CountPredicateTestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _ordersCollection;

        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;

        public CountPredicateTestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode))
        {
            _ownersCollection = ownersCollection;
            _ordersCollection = ordersCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var optionsBuilder = new DbContextOptionsBuilder<CountPredicateTestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Orders).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<Order>(b => b.ToCollection(_ordersCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
