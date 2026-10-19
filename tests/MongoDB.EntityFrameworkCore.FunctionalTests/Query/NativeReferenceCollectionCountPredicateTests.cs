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
/// EF-322: an unfiltered reference-collection-navigation Count/LongCount, compared against a value inside a
/// Where predicate, translates natively via a $lookup + $size — the same machinery
/// NativeProjectionBinder.TryTranslateProjectedCollectionCount already proved correct for the Select-leaf case,
/// reused here from the Where-predicate call site. See
/// docs/superpowers/specs/2026-09-27-native-reference-collection-count-predicate-design.md.
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

    // A genuinely user-filtered `Where(pred).Count()` — as opposed to every other test in this file, which
    // is the bare FK-correlation shape (`o.Orders.Where(ord => ord.OwnerId == o.Id).Count()`, i.e. what
    // `o.Orders.Count` desugars to) — must still decline cleanly, not be silently matched by the bare-count
    // binder. This shape IS routed through NativeCorrelationMatcher.TryMatchReferenceCollectionCountNavigation,
    // but declines inside it: EF's nav-expansion produces the FK-correlation conjunct ANDed with the user's
    // real filter (`ord.OwnerId == o.Id && ord.Total > 15m`), and TryMatchCorrelatedCollection's call to
    // TryGetCorrelationEqualitySides rejects that AndAlso shape — it only recognizes the bare FK-equality
    // correlation on its own, not one combined with an extra conjunct.
    [Fact]
    public void Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuinely_filtered_Where_Count_still_declines_cleanly_under_NativeOnly));

        Assert.Throws<MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeTranslationNotSupportedException>(
            () => db.Owners.Where(o => o.Orders.Where(ord => ord.Total > 15m).Count() > 0).ToList());
    }

    // Simple baseline differential: native result vs. an in-memory/driver-LINQ oracle across a spread of
    // related-row counts (zero, one, many) — deliberately NOT combined with any other operator (Union,
    // Distinct, Join, Include, …), unlike the elaborate differential tests below which each pin a specific
    // composition bug.
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

    // ── EF-322 final review (Critical 1/2): regression coverage for the retroactive Route decline ──
    //
    // The Where(Count>N) predicate binder registers its own $lookup eagerly, before it's known whether a
    // LATER set operation (Union/Concat), a projected Distinct, or a genuine Join will also attach to this
    // select. Each of those is lowered by a MongoSelectLowerer branch that does not flush PostJoinOps at the
    // point this predicate's $match needs it, so composing this predicate with any of them must NOT go native
    // — MongoSelectDefinition.Route retroactively declines the whole combination once the shape is fully
    // known, falling back to driver-LINQ (which already supports this predicate, just not natively) under the
    // default MongoQueryMode.Native, and throwing (a clean decline, not silently wrong data) under NativeOnly.
    // Every test below asserts against an in-memory LINQ oracle, not just "NativeOnly succeeds/throws".
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

        // The exact exception type/message is not contract (Query/AGENTS.md) — what matters is that this
        // combination is declined cleanly (and therefore never silently returns wrong data), not which
        // specific exception surfaces. AssertDeclinesCleanly asserts an exception is thrown AND that it is
        // not one of the deserializer-crash types (InvalidCastException/FormatException/OverflowException).
        AssertDeclinesCleanly(() =>
            db.Owners.Where(o => o.Orders.Count > 1).OrderBy(o => o.Name).Take(1)
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
                .ToList());
    }

    // ── EF-322 final review (round 2): two more bugs found in the ORIGINAL Task 2 commit (014b8da3), not
    // introduced by round 1's fix, and not caught by round 1's own review — both closed here.
    //
    // NEW Critical: the predicate on a JOIN'S INNER side. NativeCorrelationMatcher's binder records its
    // predicate into MongoSelectDefinition.PostJoinOps on the INNER MongoQueryExpression (the one representing
    // `Owners.Where(o => o.Orders.Count > 1)` used as `Join`'s second argument) — but
    // MongoSelectDefinition.IsBareCollectionScan, the signal TranslateJoinCore uses to decide whether an inner
    // side is "the whole target collection and nothing else", never checked PostJoinOps. So the inner read as
    // bare, its predicate silently discarded, in EVERY MongoQueryMode — including an explicit DriverLinq,
    // because IsBareCollectionScan also decides whether MarkSawNonBareJoinInner fires, which is what routes a
    // genuinely filtered inner join to the SAME clean, universal decline a plain (non-count) filtered inner
    // already gets. Fixed by adding `_postJoinOps.Count == 0` to IsBareCollectionScan.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Count_predicate_on_join_inner_side_declines_cleanly_in_every_mode(MongoQueryMode mode)
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Count_predicate_on_join_inner_side_declines_cleanly_in_every_mode) + mode);

        // Must decline (never silently drop the filter) in every mode, exactly like the pre-existing
        // non-count-predicate control case (`o.Name != "Carol"`) already does — proving this isn't a
        // native-only concern, but a shape that was never safe to admit as a join's bare inner side.
        AssertDeclinesCleanly(() =>
            db.Orders.Join(
                    db.Owners.Where(o => o.Orders.Count > 1),
                    r => r.OwnerId, o => o.Id, (r, o) => new { o.Name, r.Total })
                .ToList());
    }

    // NEW Important: a correlated SelectMany composed after the count predicate. Before this fix, the
    // predicate's own $lookup could land on the same document path as the SelectMany's own $lookup/$unwind
    // field, corrupting the read side with a BSON-type mismatch (InvalidCastException/FormatException) instead
    // of declining cleanly — this shape was already unsupported in every mode before Task 2 (Query/AGENTS.md's
    // "No driver-LINQ oracle for some shapes: SelectMany over a reference collection ... hard-fail in every
    // MongoQueryMode"), so the fix only needed to restore that existing clean decline, not invent new support.
    // Fixed by adding `_unwindSources.Count > 0` as a fourth disqualifying condition in Route's retroactive
    // decline, alongside SetOperation/Grouping/JoinScope from round 1.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Count_predicate_before_correlated_SelectMany_declines_cleanly_in_every_mode(MongoQueryMode mode)
    {
        var seed = SeedThreeOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Count_predicate_before_correlated_SelectMany_declines_cleanly_in_every_mode) + mode);

        // A bare Assert.ThrowsAny<Exception> is NOT enough here: the bug this guards is that the query used to
        // throw a BSON-deserialize CRASH (InvalidCastException: BsonDecimal128 -> BsonInt32, or a FormatException
        // from a scalar/ObjectId serializer reading an array) instead of the clean
        // InvalidOperationException/NativeTranslationNotSupportedException decline it threw before Task 2 — and
        // ThrowsAny is satisfied by either. AssertDeclinesCleanly asserts an exception is thrown AND that it is
        // not one of the deserializer-crash types.
        AssertDeclinesCleanly(() =>
            (from o in db.Owners.Where(o => o.Orders.Count > 1)
             from r in db.Orders.Where(r => r.OwnerId == o.Id)
             select new { o.Name, r.Total })
            .ToList());
    }

    // ── EF-322 final review (round 3): a paged/filtered Include on the SAME navigation the count predicate
    // targets used to corrupt the predicate's own count, in EVERY mode including explicit DriverLinq.
    //
    // NEW Critical mechanism (structurally different from rounds 1-2, which were both fixable by adding a
    // condition to the retroactive Route-fallback check): this predicate's binder registers a BARE
    // (whole-array) `_lookup_<Nav>` lookup at Where-translation time via
    // NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup. The Include for the SAME navigation is
    // bound LATER, in a completely different visitor pass (MongoProjectionBindingExpressionVisitor), and
    // registers its OWN lookup for the SAME alias, carrying a paged sub-pipeline (the OrderBy/Skip/Take).
    // MongoQueryExpression.AddLookup's existing, general-purpose bare-then-pipelined merge (used by many other
    // features — Include, ThenInclude, joins — NOT changed by this fix) then merges the Include's pipeline
    // INTO the predicate's own bare entry, so the predicate's own $size ends up reading the PAGED array, not
    // the true unfiltered count. This happens at registration/merge time, before MongoQueryMode is ever
    // consulted — so an explicit DriverLinq query was ALSO wrong, not just Native/NativeOnly, and NativeOnly
    // did not even throw (the corrupted pipeline "succeeded", just with wrong data). Fixed by flagging the
    // predicate's own bare lookup (LookupExpression.IsBareCountSizeSource) and widening the EXISTING
    // Include-vs-join alias-collision check in MongoProjectionBindingExpressionVisitor's IncludeExpression
    // case to also reroute a PAGED Include away from a flagged bare count-size source at the same alias
    // (leaving the predicate's own alias, and AddLookup's general merge behavior, untouched) — see
    // NativeCorrelationMatcher.TryBuildReferenceCollectionCountLookup's and
    // LookupExpression.IsBareCountSizeSource's own remarks. (An earlier attempt gave the predicate's own
    // lookup a private alias instead; that was reverted after it broke an existing invariant — see the task
    // report's round-3 section.)
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

        // Oracle: only owners whose TRUE (unfiltered) order count exceeds 1 survive the predicate — Alice (2)
        // and Carol (3), never Bob (1) or Dave (0) — each carrying only the ONE cheapest order the Include
        // itself asked for. Before this fix, the predicate's own $size read the ALREADY-Take(1)'d array, so
        // EVERY owner with at least one order (wrongly) satisfied `Count > 1` as `1 > 1` = false for all of
        // them, silently returning zero rows in every mode.
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

    // The self-referencing-entity equivalent of the same collision: the navigation and the entity it's
    // included/counted on are the SAME type (Node.Children : List<Node>), proving the fix isn't accidentally
    // relying on Owner/Order being two distinct entity types.
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

        // Only rootA has more than one child (2); rootB has exactly one (never satisfies Count > 1), and the
        // children themselves have none. rootA's Include should load only its cheapest-ordered child (childA2,
        // Order == 1) — before the fix, the predicate's own $size read the already-Take(1)'d array, so no root
        // ever satisfied `Count > 1` and this returned zero rows in every mode.
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
    /// Asserts <paramref name="query"/> throws — but not one of the BSON-deserializer-crash exception types
    /// (<see cref="InvalidCastException"/>/<see cref="FormatException"/>/<see cref="OverflowException"/>) that
    /// signal a native/fallback pipeline was silently built wrong (a document-path collision) rather than the
    /// query being cleanly declined. The exact exception TYPE among the "clean decline" family is not contract
    /// (Query/AGENTS.md) — only that it isn't one of these.
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

    // Alice (2 orders) and Carol (3 orders) both have Orders.Count > 1; Bob (1 order) does not — gives the
    // set-op/Distinct/Join regression tests more than one matching row to actually exercise ordering/dedup
    // against, unlike the two-owner seed above (where only one row ever satisfies the predicate).
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

    // Alice=2 orders, Bob=1, Carol=3, Dave=0 — matches the reviewer's round-3 repro data exactly, and gives
    // the Include+Count-predicate collision tests two owners that should survive `Count > 1` (Alice, Carol),
    // each with more than one order so a `.Take(1)`/`.Skip(1)` Include actually narrows what gets loaded.
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
