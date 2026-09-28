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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native GroupBy composed on a join scope (join-then-group): a reference navigation, an explicit/query-syntax
/// join, a GroupJoin/DefaultIfEmpty left join, a self-join and a two-level chain. Each shape asserts
/// NativeOnly == DriverLinq == a hand-computed expectation over a seed that includes a dangling required FK, a
/// null and a dangling optional FK, and an all-null inner value. Group-then-join is a different ordering, still
/// hard-declined (see NativeGroupByTests.GroupBy_combined_with_Join_*).
/// </summary>
public class NativeGroupByOverJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Owners: Alice (North, Rank 7), Bob (South, Rank null), Cara (North, Rank 3).
    // Orders (Total, Owner, Reviewer, Region):
    //   o1 10 Alice  Bob       North
    //   o2 20 Alice  (null)    North
    //   o3 30 Bob    Alice     South
    //   o4  5 Cara   dangling  North
    //   o5  1 dangling (null)  East     <- dropped by any required Owner join; lowest Total, so first by Total
    // Lines (Order, Sku, Quantity): l1 o1 A 1, l2 o1 B 2, l3 o3 A 4, l4 o5 A 8 (o5's owner dangles).
    private static Seed CreateSeed()
    {
        var alice = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 };
        var bob = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = null };
        var cara = new Owner { Id = ObjectId.GenerateNewId(), Name = "Cara", Region = "North", Rank = 3 };

        var o1 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, ReviewerId = bob.Id, Total = 10m, Region = "North" };
        var o2 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, ReviewerId = null, Total = 20m, Region = "North" };
        var o3 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = bob.Id, ReviewerId = alice.Id, Total = 30m, Region = "South" };
        var o4 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = cara.Id, ReviewerId = ObjectId.GenerateNewId(), Total = 5m, Region = "North" };
        var o5 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), ReviewerId = null, Total = 1m, Region = "East" };

        var lines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o1.Id, Sku = "A", Quantity = 1 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o1.Id, Sku = "B", Quantity = 2 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o3.Id, Sku = "A", Quantity = 4 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = o5.Id, Sku = "A", Quantity = 8 },
        };

        return new Seed([alice, bob, cara], [o1, o2, o3, o4, o5], lines);
    }

    [Fact]
    public void Required_navigation_key_drops_dangling_rows()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Required_navigation_key_drops_dangling_rows) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count(), Total = g.Sum(o => o.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count, x.Total)).ToList();
        });

        Assert.Equal([("North", 3, 35m), ("South", 1, 30m)], result);
    }

    [Fact]
    public void Inner_side_accumulator_over_all_null_values_is_null()
    {
        // An explicit join, so the accumulator reads the grouped join's Inner side. (Spelled as a navigation inside
        // the aggregate, EF re-expands it as a second join per group: see
        // Navigation_dereferenced_inside_an_accumulator_declines_cleanly.)
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Inner_side_accumulator_over_all_null_values_is_null) + mode);
            return (from o in db.Orders
                    join w in db.Owners on o.OwnerId equals w.Id
                    group w by w.Region into g
                    select new { g.Key, MaxRank = g.Max(x => x.Rank) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.MaxRank)).ToList();
        });

        Assert.Equal([("North", (int?)7), ("South", (int?)null)], result);
    }

    [Fact]
    public void Navigation_dereferenced_inside_an_accumulator_declines_cleanly()
    {
        // EF nav-expands `o.Owner.Rank` inside the aggregate as a correlated Join over the group's elements
        // (g.AsQueryable().Join(Owners, ...).Max(e => e.Inner.Rank)), not through the GroupBy's own join, so the
        // accumulator isn't a translatable operand and join mode must not half-confirm. Native only: the driver-LINQ
        // fallback can't translate that nested Join either (ExpressionNotSupportedException, pre-existing), so
        // there is no oracle for DeclinesCleanly.
        var seed = CreateSeed();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Navigation_dereferenced_inside_an_accumulator_declines_cleanly));
            return db.Orders
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, MaxRank = g.Max(o => o.Owner!.Rank) })
                .ToList();
        });
    }

    [Fact]
    public void Optional_navigation_key_groups_unmatched_rows_under_a_null_key()
    {
        // Reviewer is optional (nullable FK): nav-expansion emits a left join. o2/o5 have no reviewer and o4's
        // dangles; all three stay and group under a null key.
        var seed = CreateSeed();
        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Optional_navigation_key_groups_unmatched_rows_under_a_null_key) + mode);
            return db.Orders
                .GroupBy(o => o.Reviewer!.Name)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([((string?)null, 3), ("Alice", 1), ("Bob", 1)], result);
    }

    [Fact]
    public void Query_syntax_left_join_group_counts_the_unmatched_outer_row()
    {
        // GroupJoin + DefaultIfEmpty, grouped by the outer side (the GroupJoin_GroupBy_Aggregate shape).
        // Every owner has an order, so add an order-less owner for this test.
        var seed = CreateSeed();
        var dora = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dora", Region = "West", Rank = 1 };
        seed = seed with { Owners = [..seed.Owners, dora] };

        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Query_syntax_left_join_group_counts_the_unmatched_outer_row) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into grouping
                    from o in grouping.DefaultIfEmpty()
                    group o by w.Name into g
                    select new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("Alice", 2), ("Bob", 1), ("Cara", 1), ("Dora", 1)], result);
    }

    [Fact]
    public void Non_nullable_accumulator_over_an_unmatched_left_join_side_does_not_answer_default()
    {
        // Dora has no orders, so her group's only element is the DefaultIfEmpty null: Max over x.Total has no value.
        // LINQ-to-Objects throws (NullReferenceException) and driver-LINQ fails to deserialize null into decimal. A
        // native $max would answer null, read back as 0m: a plausible, wrong value. Native must decline instead.
        var seed = CreateSeed();
        var dora = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dora", Region = "West", Rank = 1 };
        seed = seed with { Owners = [..seed.Owners, dora] };

        List<(string, decimal)> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(seed, mode, nameof(Non_nullable_accumulator_over_an_unmatched_left_join_side_does_not_answer_default) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into grouping
                    from o in grouping.DefaultIfEmpty()
                    group o by w.Name into g
                    select new { g.Key, Max = g.Max(x => x.Total) })
                .AsEnumerable().Select(x => (x.Key, x.Max)).ToList();
        }

        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        // Native falls back to driver-LINQ, which throws rather than answering 0 for Dora.
        Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.Native));
    }

    [Fact]
    public void Nullable_accumulator_over_an_unmatched_left_join_side_is_null()
    {
        // The nullable spelling of the test above has a correct answer (EF's null for an all-null Max), so it stays
        // native: Alice max(10, 20), Bob 30, Cara 5, Dora null.
        var seed = CreateSeed();
        var dora = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dora", Region = "West", Rank = 1 };
        seed = seed with { Owners = [..seed.Owners, dora] };

        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Nullable_accumulator_over_an_unmatched_left_join_side_is_null) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into grouping
                    from o in grouping.DefaultIfEmpty()
                    group o by w.Name into g
                    select new { g.Key, Max = g.Max(x => (decimal?)x.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Max)).ToList();
        });

        Assert.Equal([("Alice", (decimal?)20m), ("Bob", 30m), ("Cara", 5m), ("Dora", null)], result);
    }

    [Fact]
    public void Non_nullable_key_over_an_unmatched_left_join_side_declines()
    {
        // Dora has no orders, so her row's o is the DefaultIfEmpty null and o.Total has no value: its $group _id would
        // be missing and read back as 0m, a plausible, wrong group. LINQ-to-Objects throws (NullReferenceException) and
        // driver-LINQ fails to deserialize the null key (FormatException). Native must decline, never answer 0.
        var seed = CreateSeedWithOrderlessOwner();

        List<(decimal, int)> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(seed, mode, nameof(Non_nullable_key_over_an_unmatched_left_join_side_declines) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by o.Total into g
                    select new { g.Key, Count = g.Count() })
                .AsEnumerable().Select(x => (x.Key, x.Count)).ToList();
        }

        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        // Native falls back to driver-LINQ, which throws rather than inventing a 0 group.
        Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.Native));
    }

    [Fact]
    public void Composite_key_with_a_non_nullable_unmatched_left_join_part_declines()
    {
        // As above, for one part of a composite key. Native-only: driver-LINQ answers (Dora, 0) for the unmatched row
        // (pre-existing), so it is no oracle here.
        var seed = CreateSeedWithOrderlessOwner();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Composite_key_with_a_non_nullable_unmatched_left_join_part_declines));
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by new { w.Name, o.Total } into g
                    select new { g.Key.Name, g.Key.Total, Count = g.Count() })
                .ToList();
        });
    }

    [Fact]
    public void Nullable_key_over_an_unmatched_left_join_side_groups_under_null()
    {
        // The nullable spelling has a correct answer: Dora's unmatched row groups under a null key; every order's
        // Total is distinct (10, 20, 30, 5), so each other group has one row.
        var seed = CreateSeedWithOrderlessOwner();
        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Nullable_key_over_an_unmatched_left_join_side_groups_under_null) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by (decimal?)o.Total into g
                    select new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([((decimal?)null, 1), (5m, 1), (10m, 1), (20m, 1), (30m, 1)], result);
    }

    [Fact]
    public void Projected_distinct_count_after_an_inner_side_where()
    {
        // The projected-Distinct branch of MongoSelectDefinition.ActiveOps now also outranks a confirmed join's Inner
        // access. Filtered rows: o1, o2 (Alice, North), o3 (Bob, South); distinct regions {North, South}.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Projected_distinct_count_after_an_inner_side_where) + mode);
            return new List<int>
            {
                db.Orders.Where(o => o.Owner!.Name != "Cara").Select(o => o.Owner!.Region).Distinct().Count()
            };
        });

        Assert.Equal([2], result);
    }

    // A condition over a left join's possibly-unmatched inner side: for Dora (no orders) x is the DefaultIfEmpty null,
    // and EF's null semantics make `x.Total < 15` false, so the hand answer for Dora is 0 (Alice: o1 10 -> 1; Bob: o3
    // 30 -> 0; Cara: o4 5 -> 1). In $expr, null orders below every value, so `null < 15` is TRUE and a native $cond
    // would count Dora as 1. Native must decline. Driver-LINQ is no oracle: it also answers 1 on EF10 and throws on
    // EF8/EF9.
    [Theory]
    [InlineData("Count(pred)")]
    [InlineData("Count(flipped pred)")]
    [InlineData("Count(negated pred)")]
    [InlineData("Count(<=)")]
    [InlineData("Sum(conditional)")]
    [InlineData("Where(pred).Count()")]
    [InlineData("Where(pred).Sum()")]
    public void Accumulator_condition_over_an_unmatched_left_join_side_declines(string shape)
    {
        var seed = CreateSeedWithOrderlessOwner();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Accumulator_condition_over_an_unmatched_left_join_side_declines) + shape.Length);
            var query = shape switch
            {
                "Count(pred)" => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = g.Count(x => x.Total < 15) }),
                "Count(flipped pred)" => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = g.Count(x => 15 > x.Total) }),
                "Count(negated pred)" => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = g.Count(x => !(x.Total >= 15)) }),
                "Count(<=)" => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = g.Count(x => x.Total <= 10) }),
                "Sum(conditional)" => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = g.Sum(x => x.Total < 15 ? 1 : 0) }),
                "Where(pred).Count()" => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = g.Where(x => x.Total < 15).Count() }),
                _ => (from w in db.Owners join o in db.Orders on w.Id equals o.OwnerId into gj from o in gj.DefaultIfEmpty()
                    group o by w.Name into g select new { g.Key, N = (int)g.Where(x => x.Total < 15).Sum(x => x.Total) }),
            };
            return query.AsEnumerable().Select(x => (x.Key, x.N)).ToList();
        });
    }

    [Fact]
    public void Key_only_accumulator_condition_over_a_left_join_declines()
    {
        // A key-only condition reads the key part's raw per-row value. Grouped by (decimal?)o.Total, Dora's group has a
        // null key, and EF's null semantics make `g.Key < 15` false (hand answer 0), but $expr's `null < 15` is true.
        var seed = CreateSeedWithOrderlessOwner();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Key_only_accumulator_condition_over_a_left_join_declines));
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by (decimal?)o.Total into g
                    select new { g.Key, N = g.Count(x => g.Key < 15) })
                .ToList();
        });
    }

    [Fact]
    public void Accumulator_condition_over_the_outer_side_of_a_left_join_stays_native()
    {
        // The outer side is never unmatched. Elements are owners (w), one per joined row: Alice x2 (Rank 7), Bob
        // (null), Cara (3), Dora (1, her DefaultIfEmpty row). Rank > 2: Alice 2, Bob 0, Cara 1, Dora 0.
        var seed = CreateSeedWithOrderlessOwner();
        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Accumulator_condition_over_the_outer_side_of_a_left_join_stays_native) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by w.Name into g
                    select new { g.Key, N = g.Count(x => x.Rank > 2) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.N)).ToList();
        });

        Assert.Equal([("Alice", 2), ("Bob", 0), ("Cara", 1), ("Dora", 0)], result);
    }

    [Fact]
    public void Accumulator_condition_over_an_inner_join_side_stays_native()
    {
        // An inner join never has an unmatched inner. Rows: o1/o2 (Alice, 7), o3 (Bob, null), o4 (Cara, 3); o5 is
        // dropped. Rank > 2 per order Region: North (o1, o2, o4) 3, South (o3) 0.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Accumulator_condition_over_an_inner_join_side_stays_native) + mode);
            return (from o in db.Orders
                    join w in db.Owners on o.OwnerId equals w.Id
                    group w by o.Region into g
                    select new { g.Key, N = g.Count(x => x.Rank > 2) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.N)).ToList();
        });

        Assert.Equal([("North", 3), ("South", 0)], result);
    }

    [Fact]
    public void One_to_many_join_multiplies_rows_before_grouping()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(One_to_many_join_multiplies_rows_before_grouping) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId
                    group o by w.Region into g
                    select new { g.Key, Count = g.Count(), Total = g.Sum(x => x.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count, x.Total)).ToList();
        });

        Assert.Equal([("North", 3, 35m), ("South", 1, 30m)], result);
    }

    [Fact]
    public void Self_join_groups_by_the_outer_and_aggregates_the_inner()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Self_join_groups_by_the_outer_and_aggregates_the_inner) + mode);
            return (from o in db.Orders
                    join o2 in db.Orders on o.Id equals o2.Id
                    group o2 by o.Region into g
                    select new { g.Key, Max = g.Max(x => x.Total) })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Max)).ToList();
        });

        Assert.Equal([("East", 1m), ("North", 20m), ("South", 30m)], result);
    }

    [Fact]
    public void Composite_key_spanning_both_scopes()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Composite_key_spanning_both_scopes) + mode);
            return db.Orders
                .GroupBy(o => new { o.Region, OwnerName = o.Owner!.Name })
                .Select(g => new { g.Key.Region, g.Key.OwnerName, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Region).ThenBy(x => x.OwnerName)
                .Select(x => (x.Region, x.OwnerName, x.Count)).ToList();
        });

        Assert.Equal([("North", "Alice", 2), ("North", "Cara", 1), ("South", "Bob", 1)], result);
    }

    [Fact]
    public void Two_level_chain_key()
    {
        // OrderLine -> Order (required) -> Owner (required): l4's order has a dangling owner, so it is dropped.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Two_level_chain_key) + mode);
            return db.OrderLines
                .GroupBy(l => new { l.Order!.Owner!.Region, l.Sku })
                .Select(g => new { g.Key.Region, g.Key.Sku, Quantity = g.Sum(l => l.Quantity) })
                .AsEnumerable().OrderBy(x => x.Region).ThenBy(x => x.Sku)
                .Select(x => (x.Region, x.Sku, x.Quantity)).ToList();
        });

        Assert.Equal([("North", "A", 1), ("North", "B", 2), ("South", "A", 4)], result);
    }

    [Fact]
    public void Terminal_count_over_a_navigation_group()
    {
        var seed = CreateSeed();
        var counts = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Terminal_count_over_a_navigation_group) + mode);
            return new List<int> { db.Orders.GroupBy(o => o.Owner!.Region).Count() };
        });

        Assert.Equal([2], counts);
    }

    [Fact]
    public void Having_and_ordering_over_a_navigation_group()
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Having_and_ordering_over_a_navigation_group) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Name)
                .Where(g => g.Count() > 0)
                .OrderByDescending(g => g.Sum(o => o.Total))
                .ThenBy(g => g.Key) // Alice and Bob tie on 30: without a tie-break, parity would compare tie order
                .Select(g => new { g.Key, Total = g.Sum(o => o.Total) })
                .AsEnumerable()
                .Select(x => (x.Key, x.Total)).ToList();
        });

        Assert.Equal([("Alice", 30m), ("Bob", 30m), ("Cara", 5m)], result);
    }

    [Fact]
    public void Inner_side_where_before_a_navigation_group()
    {
        // The Where reaches the join's Inner side (JoinInnerAccessConfirmed, its $match in PostJoinOps after the
        // $lookup), then the GroupBy confirms the same join. Cara's o4 is filtered out; o5's owner dangles.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Inner_side_where_before_a_navigation_group) + mode);
            return db.Orders
                .Where(o => o.Owner!.Name != "Cara")
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("North", 2), ("South", 1)], result);
    }

    // Post-group terminal aggregates after an inner-side Where: the Where sets JoinInnerAccessConfirmed, and the
    // aggregate's predicate $match must still land after the $group (PostGroupOps), not in PostJoinOps before it,
    // where the flattened Count alias doesn't exist yet. Filtered rows: o1, o2 (Alice, North), o3 (Bob, South);
    // o4 is Cara's and o5's owner dangles. So North = 2, South = 1.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(6, false)]
    public void All_over_a_navigation_group_after_an_inner_side_where(int minCount, bool expected)
    {
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(All_over_a_navigation_group_after_an_inner_side_where) + minCount + mode);
            return new List<bool>
            {
                db.Orders
                    .Where(o => o.Owner!.Name != "Cara")
                    .GroupBy(o => o.Owner!.Region)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .All(x => x.Count >= minCount)
            };
        });

        Assert.Equal([expected], result);
    }

    [Theory]
    [InlineData(1, true, 1)]
    [InlineData(2, false, 0)]
    public void Any_and_Count_over_a_navigation_group_after_an_inner_side_where_decline_cleanly(int moreThan, bool expectedAny, int expectedCount)
    {
        // EF rewrites Any(pred)/Count(pred) to a post-group Where, which declines (see
        // Post_group_where_after_a_confirmed_join_group_declines_cleanly), so these don't reach the All path. If a
        // later slice makes them native, flip to NativeAndParity.
        var seed = CreateSeed();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Any_and_Count_over_a_navigation_group_after_an_inner_side_where_decline_cleanly) + moreThan + mode);
            var groups = db.Orders
                .Where(o => o.Owner!.Name != "Cara")
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() });
            return new List<(bool, int)> { (groups.Any(x => x.Count > moreThan), groups.Count(x => x.Count > moreThan)) };
        });

        Assert.Equal([(expectedAny, expectedCount)], result);
    }

    [Fact]
    public void Ordering_after_an_inner_side_where_and_a_navigation_group_declines_cleanly()
    {
        // JoinInnerAccessConfirmed is set by the Where; MongoSelectDefinition.ActiveOps must route a post-group op to
        // PostGroupOps (after $group), not PostJoinOps (see All_over_a_navigation_group_after_an_inner_side_where).
        // A post-group OrderBy declines today; if a later slice makes it native, flip this to NativeAndParity.
        var seed = CreateSeed();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Ordering_after_an_inner_side_where_and_a_navigation_group_declines_cleanly) + mode);
            return db.Orders
                .Where(o => o.Owner!.Name != "Cara")
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .AsEnumerable()
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("North", 2), ("South", 1)], result);
    }

    [Fact]
    public void Paging_before_a_row_preserving_left_join_group_stays_native()
    {
        // Reviewer is a left-outer reference nav (1:1), so Take(3) commutes with the $lookup:
        // first 3 by Total = o5 (no reviewer), o4 (dangling reviewer), o1 (Bob).
        var seed = CreateSeed();
        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Paging_before_a_row_preserving_left_join_group_stays_native) + mode);
            return db.Orders
                .OrderBy(o => o.Total).Take(3)
                .GroupBy(o => o.Reviewer!.Name)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([((string?)null, 2), ("Bob", 1)], result);
    }

    [Fact]
    public void Paging_before_a_required_navigation_group_declines_cleanly()
    {
        // Review Focus #2. Correct answer: Take(3) by Total = o5, o4, o1, and the inner join drops o5 (dangling
        // owner), so North = 2. Deferring the Take past the $lookup would page the joined rows (o4, o1, o2) and
        // answer North = 3, silently. So this must decline, never go native.
        var seed = CreateSeed();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Paging_before_a_required_navigation_group_declines_cleanly));
            return db.Orders
                .OrderBy(o => o.Total).Take(3)
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList();
        });
    }

    [Fact]
    public void Paging_before_a_two_level_chain_group_declines_cleanly()
    {
        var seed = CreateSeed();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Paging_before_a_two_level_chain_group_declines_cleanly));
            return db.OrderLines
                .OrderBy(l => l.Quantity).Take(2)
                .GroupBy(l => l.Order!.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToList();
        });
    }

    [Fact]
    public void GroupBy_over_a_join_projecting_only_the_inner_side_resolves_through_the_inner_scope()
    {
        // Review Focus #4. Nav-expansion folds the `(o, w) => w` result selector into the GroupBy, so the key
        // arrives as `ti => ti.Inner.Region` over the TransparentIdentifier and resolves to the Owner's Region
        // through the join scope. Order also has a Region (o5 is "East"), so resolving `Region` by name against the
        // root would answer differently: this pins that it doesn't.
        var seed = CreateSeed();
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(GroupBy_over_a_join_projecting_only_the_inner_side_resolves_through_the_inner_scope) + mode);
            return db.Orders
                .Join(db.Owners, o => o.OwnerId, w => w.Id, (o, w) => w)
                .GroupBy(w => w.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("North", 3), ("South", 1)], result);
    }

    [Fact]
    public void Post_group_where_after_a_confirmed_join_group_declines_cleanly()
    {
        // Review Focus #5: the grouped Select confirms the join (registering its $lookup), and then the post-group
        // Where declines. The fallback must still be correct with the confirmed (flat _lookup_) document shape.
        // When a later slice makes post-GroupBy Where native, this will throw from DeclinesCleanly's NativeOnly
        // half. That is the signal to flip it to NativeAndParity.
        var seed = CreateSeed();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Post_group_where_after_a_confirmed_join_group_declines_cleanly) + mode);
            return db.Orders
                .GroupBy(o => o.Owner!.Region)
                .Select(g => new { g.Key, Count = g.Count() })
                .Where(x => x.Count > 1)
                .AsEnumerable()
                .Select(x => (x.Key, x.Count)).ToList();
        });

        Assert.Equal([("North", 3)], result);
    }

    // In the aggregation dialect null orders below every value, so a bare $lt/$lte over a possibly-null group operand
    // answers true where C# lifted semantics answer false. The tests below pin the guarded comparisons over a left
    // join, against hand-computed expectations. Owners: Alice (orders 10, 20; Rank 7, North), Bob (30; null, South),
    // Cara (5; 3, North), Dora (no orders; 1, West).

    [Fact]
    public void Having_less_than_over_a_nullable_accumulator_of_an_unmatched_side_excludes_the_group()
    {
        // The P04 shape. Dora's Max is null (no orders): null < 100 is false. Hand oracle only: driver-LINQ can't
        // translate this left join on any version.
        var seed = CreateSeedWithOrderlessOwner();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Having_less_than_over_a_nullable_accumulator_of_an_unmatched_side_excludes_the_group));

        var count = (from w in db.Owners
                     join o in db.Orders on w.Id equals o.OwnerId into gj
                     from o in gj.DefaultIfEmpty()
                     group o by w.Name)
            .Where(g => g.Max(x => (decimal?)x.Total) < 100)
            .Count();

        Assert.Equal(3, count);
    }

    [Fact]
    public void Group_key_with_a_condition_over_an_unmatched_side_declines()
    {
        // Dora's o.Total has no value, so `o.Total < 15` would be decided by $expr's null ordering (true), filing her
        // under "cheap"; the correct answer (lifted null < 15 is false) is "dear": cheap = {10, 5}, dear = {20, 30,
        // Dora}. The operand is a non-nullable decimal, so no CLR-type null guard applies: native must decline.
        var seed = CreateSeedWithOrderlessOwner();
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
        {
            using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
                nameof(Group_key_with_a_condition_over_an_unmatched_side_declines));
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by (o.Total < 15 ? "cheap" : "dear") into g
                    select new { g.Key, C = g.Count() })
                .ToList();
        });
    }

    [Fact]
    public void Having_less_than_over_a_nullable_key_of_an_unmatched_side_excludes_the_null_key()
    {
        var seed = CreateSeedWithOrderlessOwner();
        var result = LeftJoinNativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode, nameof(Having_less_than_over_a_nullable_key_of_an_unmatched_side_excludes_the_null_key) + mode);
            return (from w in db.Owners
                    join o in db.Orders on w.Id equals o.OwnerId into gj
                    from o in gj.DefaultIfEmpty()
                    group w by (decimal?)o.Total into g
                    where g.Key < 15
                    select new { g.Key, C = g.Count() })
                .AsEnumerable().OrderBy(x => x.Key)
                .Select(x => (x.Key, x.C)).ToList();
        });

        Assert.Equal([((decimal?)5m, 1), (10m, 1)], result);
    }

    [Fact]
    public void Accumulator_condition_over_a_nullable_outer_property_of_a_left_join_does_not_count_null()
    {
        // North: Alice x2 (7), Cara (3) -> 1. South: Bob (null) -> 0 (null < 5 is false). West: Dora (1) -> 1.
        // Hand oracle only: EF10's driver-LINQ counts Bob (South 1), and EF8/EF9's can't translate the join.
        var seed = CreateSeedWithOrderlessOwner();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Accumulator_condition_over_a_nullable_outer_property_of_a_left_join_does_not_count_null));
        var result = (from w in db.Owners
                      join o in db.Orders on w.Id equals o.OwnerId into gj
                      from o in gj.DefaultIfEmpty()
                      group w by w.Region into g
                      select new { g.Key, C = g.Count(x => x.Rank < 5) })
            .AsEnumerable().OrderBy(x => x.Key)
            .Select(x => (x.Key, x.C)).ToList();

        Assert.Equal([("North", 1), ("South", 0), ("West", 1)], result);
    }

    [Fact]
    public void Projection_ternary_over_a_nullable_key_of_an_unmatched_side_treats_null_as_not_less()
    {
        // Hand oracle only: EF10's driver-LINQ answers 1 for the null key, and EF8/EF9's can't translate the join.
        var seed = CreateSeedWithOrderlessOwner();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Projection_ternary_over_a_nullable_key_of_an_unmatched_side_treats_null_as_not_less));
        var result = (from w in db.Owners
                      join o in db.Orders on w.Id equals o.OwnerId into gj
                      from o in gj.DefaultIfEmpty()
                      group w by (decimal?)o.Total into g
                      select new { g.Key, F = g.Key < 15 ? 1 : 0 })
            .AsEnumerable().OrderBy(x => x.Key)
            .Select(x => (x.Key, x.F)).ToList();

        Assert.Equal([((decimal?)null, 0), (5m, 1), (10m, 1), (20m, 0), (30m, 0)], result);
    }

    [Fact]
    public void Composite_key_with_a_nullable_part_of_an_unmatched_side_treats_the_missing_part_as_null()
    {
        // Probe 4b. Dora's (decimal?)o.Total is missing, so $group omits it from _id; C# reads it as null.
        // Hand oracle only: EF8/EF9 driver-LINQ can't translate the join, and it can't translate the projection
        // on any version.
        var seed = CreateSeedWithOrderlessOwner();
        var name = nameof(Composite_key_with_a_nullable_part_of_an_unmatched_side_treats_the_missing_part_as_null);

        using (var db = CreateContext(seed, MongoQueryMode.NativeOnly, name + "W"))
        {
            var nullPart = (from w in db.Owners
                            join o in db.Orders on w.Id equals o.OwnerId into gj
                            from o in gj.DefaultIfEmpty()
                            group w by new { w.Name, T = (decimal?)o.Total } into g
                            where g.Key.T == null
                            select new { g.Key.Name, C = g.Count() })
                .AsEnumerable().Select(x => (x.Name, x.C)).ToList();
            Assert.Equal([("Dora", 1)], nullPart);
        }

        using (var db = CreateContext(seed, MongoQueryMode.NativeOnly, name + "N"))
        {
            var notNull = (from w in db.Owners
                           join o in db.Orders on w.Id equals o.OwnerId into gj
                           from o in gj.DefaultIfEmpty()
                           group w by new { w.Name, T = (decimal?)o.Total } into g
                           where g.Key.T != null
                           select new { g.Key.Name, C = g.Count() })
                .AsEnumerable().Select(x => x.Name).OrderBy(n => n).ToList();
            Assert.Equal(["Alice", "Alice", "Bob", "Cara"], notNull);
        }

        using (var db = CreateContext(seed, MongoQueryMode.NativeOnly, name + "P"))
        {
            var projected = (from w in db.Owners
                             join o in db.Orders on w.Id equals o.OwnerId into gj
                             from o in gj.DefaultIfEmpty()
                             group w by new { w.Name, T = (decimal?)o.Total } into g
                             select new { g.Key.Name, g.Key.T, IsNull = g.Key.T == null ? 1 : 0, Less = g.Key.T < 15 ? 1 : 0 })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.T)
                .Select(x => (x.Name, x.T, x.IsNull, x.Less)).ToList();
            Assert.Equal(
                [("Alice", (decimal?)10m, 0, 1), ("Alice", 20m, 0, 0), ("Bob", 30m, 0, 0), ("Cara", 5m, 0, 1), ("Dora", null, 1, 0)],
                projected);
        }

        using (var db = CreateContext(seed, MongoQueryMode.NativeOnly, name + "L"))
        {
            var less = (from w in db.Owners
                        join o in db.Orders on w.Id equals o.OwnerId into gj
                        from o in gj.DefaultIfEmpty()
                        group w by new { w.Name, T = (decimal?)o.Total } into g
                        where g.Key.T < 15
                        select new { g.Key.Name, C = g.Count() })
                .AsEnumerable().Select(x => x.Name).OrderBy(n => n).ToList();
            Assert.Equal(["Alice", "Cara"], less);
        }
    }

    // EF8/EF9 nav-expand these left joins to a shape that explicit DriverLinq can't translate under a GroupBy (it
    // threw before native GroupBy-over-join existed too: ArgumentException "Property 'Key' is not defined for type
    // IGrouping", now ExpressionNotSupportedException from the driver's Join translator). With no driver-LINQ
    // oracle there, NativeOnly is checked against the hand-computed expectation alone.
    private static List<T> LeftJoinNativeAndParity<T>(Func<MongoQueryMode, List<T>> run)
#if EF8 || EF9
    {
        // Pinned, so a driver/EF change that makes DriverLinq work here is noticed and this can use full parity.
        Assert.ThrowsAny<Exception>(() => run(MongoQueryMode.DriverLinq));
        return run(MongoQueryMode.NativeOnly);
    }
#else
        => NativeModeAssert.NativeAndParity(run);
#endif

    private static Seed CreateSeedWithOrderlessOwner()
    {
        var seed = CreateSeed();
        var dora = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dora", Region = "West", Rank = 1 };
        return seed with { Owners = [..seed.Owners, dora] };
    }

    private sealed record Seed(Owner[] Owners, Order[] Orders, OrderLine[] OrderLines);

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";
        public int? Rank { get; set; }
        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public ObjectId? ReviewerId { get; set; }
        public Owner? Reviewer { get; set; }
        public decimal Total { get; set; }
        public string Region { get; set; } = "";
        public List<OrderLine> OrderLines { get; set; } = [];
    }

    public class OrderLine
    {
        public ObjectId Id { get; set; }
        public ObjectId OrderId { get; set; }
        public Order? Order { get; set; }
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    private GroupByOverJoinDbContext CreateContext(Seed seed, MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var prefix = TemporaryDatabaseFixtureBase.CreateCollectionName(name);
        var ownersName = prefix + "W" + suffix;
        var ordersName = prefix + "O" + suffix;
        var linesName = prefix + "L" + suffix;

        database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany(seed.Owners);
        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(seed.Orders);
        database.MongoDatabase.GetCollection<OrderLine>(linesName).InsertMany(seed.OrderLines);

        return new GroupByOverJoinDbContext(database, ownersName, ordersName, linesName, mode);
    }

    private sealed class GroupByOverJoinDbContext(
        TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, string linesCollection,
        MongoQueryMode mode)
        : DbContext(new DbContextOptionsBuilder<GroupByOverJoinDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<OrderLine> OrderLines { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(ownersCollection);
                b.HasMany(w => w.Orders).WithOne(o => o.Owner).HasForeignKey(o => o.OwnerId);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(ordersCollection);
                b.HasOne(o => o.Reviewer).WithMany().HasForeignKey(o => o.ReviewerId);
                b.HasMany(o => o.OrderLines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId);
            });
            modelBuilder.Entity<OrderLine>(b => b.ToCollection(linesCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
