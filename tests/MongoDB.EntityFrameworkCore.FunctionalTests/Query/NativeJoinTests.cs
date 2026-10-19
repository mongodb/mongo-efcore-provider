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
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native translation of explicit <c>Join</c>/<c>LeftJoin</c>. A join records a
/// <see cref="MongoDB.EntityFrameworkCore.Query.Expressions.MongoJoinScope"/> (metadata only); the consuming
/// operator (<c>Where</c> over the outer side, a bare or wrapped <c>Select</c>) confirms it and registers the
/// <c>$lookup</c>. Shapes that go native are asserted under <see cref="MongoQueryMode.NativeOnly"/>; the rest must
/// decline gracefully. The outer whole-entity leaf stages <c>$$ROOT</c> under its alias; the inner leaf uses the
/// join's fixed <c>$lookup</c> prefix because the read side resolves it from the navigation.
/// </summary>
public class NativeJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL), out var spyLogger);

        // Uses a shape no Select arm confirms (untranslatable `o.Name.ToUpper()`), so the driver-LINQ document shape is
        // decided purely by the join-scope recording. A confirmed body flips to the flat shape in every mode.
        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { Name = o.Name.ToUpper(), r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { Name = o.Name.ToUpper(), r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(
            expected.Select(x => (x.Name, x.Total)),
            result.Select(x => (x.Name, x.Total)));

        // An unconfirmed single join must keep the classic "_outer"/"_inner" driver-LINQ shape: recording a
        // MongoJoinScope must register nothing (a premature AddLookup would force "_lookup_<Navigation>").
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.DoesNotContain("_lookup_Order", message);
    }

    [Fact]
    public void Confirmed_join_projection_uses_the_flat_lookup_shape_under_DriverLinq_too()
    {
        // Once a Select confirms the join, the $lookup is registered at translation time, before the query mode is
        // read, so the flat "_lookup_<Navigation>" shape applies under DriverLinq too.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Confirmed_join_projection_uses_the_flat_lookup_shape_under_DriverLinq_too), out var spyLogger);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("_lookup_Orders", message);
    }

    [Fact]
    public void Genuine_two_sided_join_returns_correct_results_via_fallback()
    {
        // Mode-independent: whichever path Native picks must match the oracle.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Genuine_two_sided_join_returns_correct_results_via_fallback));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Whole_outer_entity_leaf_projection_goes_native_under_NativeOnly()
    {
        // Whole outer entity leaf plus a scalar inner leaf: the outer leaf arrives as the join's
        // StructuralTypeShaperExpression and is rebound by index; the binder's Outer arm stages $$ROOT.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_outer_entity_leaf_projection_goes_native_under_NativeOnly));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total })
            .AsEnumerable()
            .OrderBy(x => x.o.Name).ThenBy(x => x.Total)
            .Select(x => (x.o.Name, x.o.Region, x.Total))
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total })
            .OrderBy(x => x.o.Name).ThenBy(x => x.Total)
            .Select(x => (x.o.Name, x.o.Region, x.Total))
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Depth1_whole_entity_leaf_join_with_paging_goes_native_under_NativeOnly()
    {
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Depth1_whole_entity_leaf_join_with_paging_goes_native_under_NativeOnly));

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => r)
            .Skip(1).Take(1)
            .ToList();

        Assert.Single(results);
    }

    [Fact]
    public void GroupJoin_array_result_shape_still_declines_cleanly_in_NativeOnly()
    {
        // Raw GroupJoin with a collection result is unsupported in every mode (EF-436): substituting a single-entity
        // shaper for the collection in TranslateJoinCore gives a type mismatch.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(GroupJoin_array_result_shape_still_declines_cleanly_in_NativeOnly));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            db.Owners
                .GroupJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o.Name, Orders = rs })
                .ToList());

        // GroupJoin's collection result shape can't be bound; this throws during projection binding in all modes
        Assert.Contains("could not be translated", ex.Message);
    }

    [Fact]
    public void Chained_join_scalar_leaf_shapes_resolve_correctly_under_NativeOnly()
    {
        // Three-source chain (Owners -> Orders -> OrderLines) with single-scope scalar leaves goes native.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_scalar_leaf_shapes_resolve_correctly_under_NativeOnly));

        var chainResult = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, x => x.r.Id, l => l.OrderId, (x, l) => new { x.o.Name, l.Sku })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Sku)
            .ToList();

        var expectedChainResult = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(seed.OrderLines, x => x.r.Id, l => l.OrderId, (x, l) => new { x.o.Name, l.Sku })
            .OrderBy(x => x.Name).ThenBy(x => x.Sku)
            .ToList();

        Assert.NotEmpty(chainResult);
        Assert.Equal(expectedChainResult, chainResult);

        // The intermediate-Select spelling confirms the first join while Joins.Count is still 1, so AddLookup fires
        // and UsesDriverJoinFields flips before the second join runs (a wrong-data bug in
        // NorthwindJoinQueryMongoTest.GroupJoin_Where). Needs a result check on either route, hence Native.
        using var dbNative = CreateContext(seed, MongoQueryMode.Native,
            nameof(Chained_join_scalar_leaf_shapes_resolve_correctly_under_NativeOnly) + "_fallback");

        var result = dbNative.Owners
            .Join(dbNative.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.o)
            .Join(dbNative.Orders, o => o.Id, r2 => r2.OwnerId, (o, r2) => new { o.Name, r2.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.o)
            .Join(seed.Orders, o => o.Id, r2 => r2.OwnerId, (o, r2) => new { o.Name, r2.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly()
    {
        // EF elides the pending wrap Select for a bare Any(), so confirmation happens only via
        // NativeCardinalityBinder.TryBindAggregate's no-trailing-Select path.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly));

        var found = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .OrderBy(x => x.o.Id)
            .Any();

        Assert.True(found);
    }

    [Fact]
    public void Chained_join_Where_terminal_Count_goes_native_under_NativeOnly()
    {
        // Count() over an eligible chain: the $count arm of TryBindAggregate.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_Where_terminal_Count_goes_native_under_NativeOnly));

        var count = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .Count();

        Assert.True(count > 0);
    }

    [Fact]
    public void Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly()
    {
        // Every leaf is rooted at one scope: e.o.Name (0), e.r.Total (1), l.Sku (2).
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly));

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            })
            .Where(x => x.OwnerName == seed.Owners[0].Name)
            .ToList();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(seed.Owners[0].Name, r.OwnerName));
    }

    [Fact]
    public void Chained_join_scalar_leaf_projection_with_trailing_paging_goes_native_under_NativeOnly()
    {
        // Chain-scalar projection then Skip/Take (Join_Customers_Orders_Orders_Skip_Take_Same_Properties shape). EF hoists
        // Skip/Take ahead of the pending selector, so it reaches IsSingleEligibleNativeJoinScope's HasPaging branch
        // pre-confirmation and is deferred past the $lookup/$unwind.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_scalar_leaf_projection_with_trailing_paging_goes_native_under_NativeOnly));

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            })
            .Skip(1).Take(1)
            .ToList();

        // Row identity checked against an in-memory oracle over the same joins, not just the count.
        var allJoined = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(seed.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            })
            .ToList();

        Assert.Single(results);
        Assert.Contains(results[0], allJoined);
    }

    [Fact]
    public void Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly()
    {
        // EF hoists Skip/Take ahead of the pending selector, so Select.HasPaging is true at confirmation; neither join
        // is a left-outer reference nav, so PipelineOps are deferred past both $lookup/$unwind pairs.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly));

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            })
            .Skip(1).Take(2)
            .ToList();

        // Membership check against the oracle, so a wrong-row (not just wrong-count) regression is caught.
        var allJoined = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(seed.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new
            {
                OwnerName = e.o.Name,
                OrderTotal = e.r.Total,
                LineSku = l.Sku
            })
            .ToList();

        Assert.Equal(2, results.Count);
        Assert.All(results, row => Assert.Contains(row, allJoined));
    }

    [Fact]
    public void Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly()
    {
        // A dangling-OwnerId Order is inserted first, then a matched one. If paging ran before the $lookup,
        // {$limit: 1} would keep the dangling order and the join would return zero rows.
        var seed = SeedDanglingOrderFirstThenMatched();

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly));

        var result = db.Orders
            .Join(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r.Total, o.Name })
            .Skip(0).Take(1)
            .ToList();

        Assert.Single(result);
        // The dangling order must have been dropped by the join before paging.
        Assert.NotEqual(seed.Orders[0].Total, result[0].Total);
    }

    [Fact]
    public void Paging_before_a_where_reaching_a_confirmed_joins_inner_side_declines_under_NativeOnly()
    {
        // PostJoinOps (Where on a required reference nav's Inner side) and PostLookupPagingOps (deferred Skip/Take) can
        // both be populated. Page-then-filter (Skip/Take before a Where on Order.Owner) must not defer the paging, or
        // MongoSelectLowerer would emit filter-then-page and return zero rows; IsSingleEligibleNativeJoinScope declines
        // it (JoinInnerAccessConfirmed guard). No explicit Select: nav-expansion synthesizes `Select(ti => ti.Outer)`.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Paging_before_a_where_reaching_a_confirmed_joins_inner_side_declines_under_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders
                .Skip(1).Take(2)
                .Where(r => r.Owner!.Name != seed.Owners[0].Name)
                .ToList());
    }

    [Fact]
    public void Paging_after_a_dangling_required_reference_dereference_pages_the_joined_result_under_NativeOnly()
    {
        // Include_where_skip_take_projection shape (no Join operator): paging before a projection through a required
        // reference nav. With a dangling reference, native is drop-then-page (see MongoSelectDefinition.PostLookupPagingOps),
        // consistent with explicit joins.
        var seed = SeedDanglingOrderFirstThenMatched();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Paging_after_a_dangling_required_reference_dereference_pages_the_joined_result_under_NativeOnly));

        // Mirrors the spec shape: Include, Skip/Take, then a wrapped projection. A bare leaf declines for an
        // unrelated reason (a cross-collection reference needs the Include-confirmed wrapped-leaf arm).
        var result = db.Orders
            .Include(r => r.Owner)
            .Skip(0).Take(1)
            .Select(r => new { r.Owner!.Name })
            .ToList();

        // The dangling order is dropped before paging; the returned row is the matched order's owner.
        Assert.Equal([seed.Owners[0].Name], result.Select(x => x.Name));
    }

    // Outer-source paging BEFORE a 1:N join pages the OUTER sequence, so it must stay ahead of the $lookup/$unwind.
    // A navigation-less key-equality join is 1:N like a collection navigation; treating a null navigation as
    // non-multiplying deferred the Take past the $unwind (one row instead of three, silently). Differential against
    // an in-memory oracle, all three modes, inner and left-outer spellings.
    [Theory]
    [InlineData(MongoQueryMode.Native, false)]
    [InlineData(MongoQueryMode.DriverLinq, false)]
    [InlineData(MongoQueryMode.NativeOnly, false)]
    [InlineData(MongoQueryMode.Native, true)]
    [InlineData(MongoQueryMode.DriverLinq, true)]
    [InlineData(MongoQueryMode.NativeOnly, true)]
    public void Outer_paging_before_a_navigation_less_one_to_many_join_pages_the_outer_rows(MongoQueryMode mode, bool leftOuter)
    {
        var (owners, orderLines) = SeedTwoOwnersSharingThreeMatchingLines();
        using var db = CreateContext(new Seed(owners, [], orderLines), mode,
            nameof(Outer_paging_before_a_navigation_less_one_to_many_join_pages_the_outer_rows) + mode + leftOuter);

        List<(string Name, int? Quantity)> actual, expected;
        if (leftOuter)
        {
            actual = (from o in db.Owners.OrderBy(o => o.Name).Take(1)
                      join ol in db.OrderLines on o.Region equals ol.Sku into g
                      from ol in g.DefaultIfEmpty()
                      select new { o.Name, Quantity = ol != null ? (int?)ol.Quantity : null })
                .AsEnumerable().Select(x => (x.Name, x.Quantity)).ToList();
            expected = (from o in owners.OrderBy(o => o.Name).Take(1)
                        join ol in orderLines on o.Region equals ol.Sku into g
                        from ol in g.DefaultIfEmpty()
                        select (o.Name, ol != null ? (int?)ol.Quantity : null)).ToList();
        }
        else
        {
            actual = db.Owners.OrderBy(o => o.Name).Take(1)
                .Join(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { o.Name, ol.Quantity })
                .AsEnumerable().Select(x => (x.Name, (int?)x.Quantity)).ToList();
            expected = owners.OrderBy(o => o.Name).Take(1)
                .Join(orderLines, o => o.Region, ol => ol.Sku, (o, ol) => (o.Name, (int?)ol.Quantity)).ToList();
        }

        Assert.Equal(3, expected.Count);
        Assert.Equal(expected.OrderBy(x => x.Quantity), actual.OrderBy(x => x.Quantity));
    }

    // Collection-navigation sibling of the test above (resolves to Owner.Orders): paging recorded before any join is
    // already ahead of the $lookup in PipelineOps, so this goes native.
    [Fact]
    public void Outer_paging_before_a_collection_navigation_join_goes_native_and_pages_the_outer_rows()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Outer_paging_before_a_collection_navigation_join_goes_native_and_pages_the_outer_rows));

        var actual = db.Owners.OrderBy(o => o.Name).Take(1)
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable().Select(x => (x.Name, x.Total)).OrderBy(x => x.Total).ToList();
        var expected = seed.Owners.OrderBy(o => o.Name).Take(1)
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => (o.Name, r.Total))
            .OrderBy(x => x.Total).ToList();

        Assert.True(expected.Count > 1, "fixture must give the first owner more than one order");
        Assert.Equal(expected, actual);
    }

    // Paging on BOTH sides of a 1:N join records both into one PipelineOps snapshot, which can be neither kept ahead
    // of the $lookup nor deferred past it. Declines: throws under NativeOnly, correct rows via fallback under Native.
    [Fact]
    public void Paging_on_both_sides_of_a_one_to_many_join_declines_and_falls_back_correctly()
    {
        var (owners, orderLines) = SeedTwoOwnersSharingThreeMatchingLines();
        var seed = new Seed(owners, [], orderLines);

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(Paging_on_both_sides_of_a_one_to_many_join_declines_and_falls_back_correctly) + "_nativeOnly"))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnly.Owners.OrderBy(o => o.Name).Take(1)
                    .Join(nativeOnly.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { o.Name, ol.Quantity })
                    .Take(2)
                    .ToList());
        }

        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Paging_on_both_sides_of_a_one_to_many_join_declines_and_falls_back_correctly) + "_native");

        var actual = native.Owners.OrderBy(o => o.Name).Take(1)
            .Join(native.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { o.Name, ol.Quantity })
            .Take(2)
            .ToList();

        // Which two of Alice's three joined rows survive the unordered Take(2) is unspecified; that there are two, all
        // Alice's (the outer Take(1) ran first), is not.
        Assert.Equal(2, actual.Count);
        Assert.All(actual, x => Assert.Equal("Alice", x.Name));
    }

    private static (Owner[] Owners, OrderLine[] OrderLines) SeedTwoOwnersSharingThreeMatchingLines()
        => ([
                new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "MATCH" },
                new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "MATCH" },
            ],
            Enumerable.Range(1, 3).Select(i => new OrderLine
            {
                Id = ObjectId.GenerateNewId(), OrderId = ObjectId.GenerateNewId(), Sku = "MATCH", Quantity = i
            }).ToArray());

#if !EF8 && !EF9
    [Fact]
    public void Chained_join_then_LeftJoin_unmatched_row_reads_a_scalar_leaf_at_the_second_level_under_NativeOnly()
    {
        // Chain-depth analogue of LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path:
        // the second level is a LeftJoin over Order.Owner with an unmatched row; OwnerRank is read off a missing sub-document.
        var seed = SeedLinesOrdersAndOwnersWithADanglingOwnerId();

        static List<(int? Quantity, decimal Total, int? Rank)> Run(JoinTestDbContext db) =>
            db.OrderLines
                .Join(db.Orders, l => l.OrderId, r => r.Id, (l, r) => new { l, r })
                .LeftJoin(db.Owners, e => e.r.OwnerId, o => o.Id, (e, o) => new
                {
                    LineQuantity = e.l.Quantity,
                    OrderTotal = e.r.Total,
                    OwnerRank = o.Rank
                })
                .AsEnumerable()
                .OrderBy(x => x.LineQuantity)
                .Select(x => (x.LineQuantity, x.OrderTotal, x.OwnerRank))
                .ToList();

        // Spelled out: LINQ-to-objects' LeftJoin yields a null `o` for the dangling row, so `o.Rank` would throw.
        List<(int?, decimal, int?)> expected = [(1, 10m, 7), (2, 20m, null)];

        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_then_LeftJoin_unmatched_row_reads_a_scalar_leaf_at_the_second_level_under_NativeOnly) + "_nativeOnly");
        Assert.Equal(expected, Run(nativeOnly));

        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Chained_join_then_LeftJoin_unmatched_row_reads_a_scalar_leaf_at_the_second_level_under_NativeOnly) + "_dl");
        Assert.Equal(expected, Run(driverLinq));
    }
#endif

    [Fact]
    public void Chained_join_onto_the_same_target_entity_type_disambiguates_lookup_aliases_under_NativeOnly()
    {
        // Two joins over the same navigation must get distinct aliases ("_lookup_Orders", "_lookup_Orders_1") via
        // UniquifyLookupAlias, or the second $lookup collides.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_onto_the_same_target_entity_type_disambiguates_lookup_aliases_under_NativeOnly),
            out var spyLogger);

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.Orders, e => e.o.Id, r2 => r2.OwnerId, (e, r2) => new { e.o, e.r, r2 })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .AsEnumerable()
            .Select(x => (x.r.Total, x.r2.Total))
            .OrderBy(x => x.Item1).ThenBy(x => x.Item2)
            .ToList();

        // Alice has two orders, so both joins keyed on her give 2 x 2 = 4 rows.
        var ownerAOrders = seed.Orders.Where(o => o.OwnerId == seed.Owners[0].Id).ToList();
        var expected = (from r in ownerAOrders
                         from r2 in ownerAOrders
                         select (r.Total, r2.Total))
            .OrderBy(x => x.Item1).ThenBy(x => x.Item2)
            .ToList();

        Assert.Equal(expected, results);

        // NativeOnly success is the point: driver-LINQ shares the aliasing in TranslateJoinCore, so results alone
        // wouldn't distinguish the routes.
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("_lookup_Orders\"", message);
        Assert.Contains("_lookup_Orders_1\"", message);
    }

    [Fact]
    public void Take_after_Where_over_a_two_level_collection_nav_chain_goes_native_under_NativeOnly()
    {
        // Where then Take over a 2-level chain of collection-navigation joins: PipelineOps are deferred past the
        // $lookup/$unwind blocks.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Take_after_Where_over_a_two_level_collection_nav_chain_goes_native_under_NativeOnly));

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .Take(1)
            .AsEnumerable()
            .Select(x => (x.o.Name, x.r.Total, x.l.Sku))
            .ToList();

        var allMatching = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(seed.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .Select(x => (x.o.Name, x.r.Total, x.l.Sku))
            .ToList();
        Assert.Equal(3, allMatching.Count);

        Assert.Single(results);
        Assert.Contains(results[0], allMatching);
    }

#if !EF8 && !EF9
    [Fact]
    public void Chained_join_with_an_earlier_collection_nav_pages_the_joined_result_under_NativeOnly()
    {
        // Every chain level must be 1:1-safe for paging to stay ahead of $lookup, not just the last. Level 1
        // (Owner.Orders, inner Join) is unsafe, level 2 (Order.Owner, LeftJoin) is safe; a last-only check would wrongly pass.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Chained_join_with_an_earlier_collection_nav_pages_the_joined_result_under_NativeOnly));

        var results = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .LeftJoin(db.Owners, e => e.r.OwnerId, o2 => o2.Id, (e, o2) => new { e.o, e.r, o2 })
            .Take(1)
            .AsEnumerable()
            .Select(x => (x.o.Name, x.r.Total, x.o2.Name))
            .ToList();

        var allJoined = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .GroupJoin(seed.Owners, e => e.r.OwnerId, o2 => o2.Id, (e, o2s) => new { e.o, e.r, o2s })
            .SelectMany(x => x.o2s.DefaultIfEmpty(), (x, o2) => (x.o.Name, x.r.Total, o2!.Name))
            .ToList();
        Assert.Equal(3, allJoined.Count);

        Assert.Single(results);
        Assert.Contains(results[0], allJoined);
    }
#endif

    [Fact]
    public void Take_or_Skip_after_a_confirmed_join_pages_the_joined_result_under_NativeOnly()
    {
        // Take/Skip over a join is recorded in PipelineOps, which lower before the $lookup/$unwind; over a 1:N
        // collection nav that would page owners, not joined rows. EF translates the Take before the confirming Select,
        // so the HasPaging branch defers it past the join. Alice has two Orders and Bob one: Take(2) must return two
        // joined rows (paging owners would give three) and Skip(2) one (paging owners would give none).
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Take_or_Skip_after_a_confirmed_join_pages_the_joined_result_under_NativeOnly));

        // Unordered Take/Skip has no defined row identity, so pin the count plus membership in the joined set.
        var allJoined = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .ToList();
        Assert.Equal(3, allJoined.Count);

        var taken = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .Take(2)
            .ToList();
        Assert.Equal(2, taken.Count);
        Assert.All(taken, row => Assert.Contains(row, allJoined));

        var skipped = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .Skip(2)
            .ToList();
        Assert.Single(skipped);
        Assert.All(skipped, row => Assert.Contains(row, allJoined));

        // Cross-check DriverLinq too: native and fallback can diverge (a Where on a confirmed join's Inner side ahead
        // of hoisted paging).
        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Take_or_Skip_after_a_confirmed_join_pages_the_joined_result_under_NativeOnly) + "_driverLinq");

        var takenDriverLinq = driverLinq.Owners
            .Join(driverLinq.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .Take(2)
            .ToList();
        Assert.Equal(taken.Count, takenDriverLinq.Count);
        Assert.All(takenDriverLinq, row => Assert.Contains(row, allJoined));

        var skippedDriverLinq = driverLinq.Owners
            .Join(driverLinq.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .Skip(2)
            .ToList();
        Assert.Equal(skipped.Count, skippedDriverLinq.Count);
        Assert.All(skippedDriverLinq, row => Assert.Contains(row, allJoined));
    }

    [Fact]
    public void First_after_a_confirmed_join_declines_cleanly_under_NativeOnly()
    {
        // TryBindReducer synthesizes its $limit into PipelineOps, ahead of the $lookup/$unwind. A reducer isn't hoisted
        // ahead of the pending selector, so it needs TryBindReducer's own HasConfirmedJoinLookup gate. An order-less owner
        // is seeded first: {$limit: 1} keeps it, the $unwind drops it, and First() returns nothing. Keep the whole-entity
        // `Select(x => x.r)`: a wrapped scalar projection declines earlier for an unrelated reason.
        var seed = SeedOrderlessOwnerFirst();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(First_after_a_confirmed_join_declines_cleanly_under_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => x.r)
                .First());

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => x.r)
                .FirstOrDefault());

        // Pins the clean decline instead of wrong data natively; the correctness half is
        // Reducer_after_a_bare_Inner_entity_leaf_over_a_join_returns_the_joined_row.
    }

    // Correctness half of the decline above: both modes run the driver-LINQ fallback, which must return the one joined
    // Order. The bare-leaf arm registers the join's $lookup at translation time, so the driver pushed
    // `Select(x => x.r)` down as `{ _v: "$_lookup_Orders" }` and the entity shaper (reading `_lookup_Orders`) found
    // nothing. Fixed by the fallback's strip of that Select under a reducer (MongoSelectDefinition.HasBareJoinInnerEntityLeaf).
    [Theory]
    [InlineData(MongoQueryMode.Native, "First")]
    [InlineData(MongoQueryMode.DriverLinq, "First")]
    [InlineData(MongoQueryMode.Native, "FirstOrDefault")]
    [InlineData(MongoQueryMode.DriverLinq, "FirstOrDefault")]
    [InlineData(MongoQueryMode.Native, "Single")]
    [InlineData(MongoQueryMode.DriverLinq, "Single")]
    [InlineData(MongoQueryMode.Native, "SingleOrDefault")]
    [InlineData(MongoQueryMode.DriverLinq, "SingleOrDefault")]
    public void Reducer_after_a_bare_Inner_entity_leaf_over_a_join_returns_the_joined_row(MongoQueryMode mode, string reducer)
    {
        var seed = SeedOrderlessOwnerFirst();
        using var db = CreateContext(seed, mode,
            nameof(Reducer_after_a_bare_Inner_entity_leaf_over_a_join_returns_the_joined_row) + mode + reducer);

        var inner = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.r);

        var result = reducer switch
        {
            "First" => inner.First(),
            "FirstOrDefault" => inner.FirstOrDefault(),
            "Single" => inner.Single(),
            "SingleOrDefault" => inner.SingleOrDefault(),
            _ => throw new ArgumentOutOfRangeException(nameof(reducer))
        };

        Assert.NotNull(result);
        Assert.Equal(seed.Orders[0].Id, result.Id);
        Assert.Equal(5m, result.Total);
    }

    [Fact]
    public void Where_after_a_bare_whole_entity_leaf_select_goes_native_and_filters_the_inner_side()
    {
        // Single-scope resolution is by name, so `Id` could resolve against the root (Owner) and filter the wrong
        // collection. EF hoists the Where ahead of the pending selector, and NativeSlotPopulator's Inner Where arm resolves
        // it through the join's own $lookup alias (`_lookup_Orders._id`), so it goes native and returns the one matching Order.
        var seed = SeedOwnersAndOrders();
        var targetOrderId = seed.Orders[2].Id;

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Where_after_a_bare_whole_entity_leaf_select_goes_native_and_filters_the_inner_side));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.r)
            .Where(r => r.Id == targetOrderId)
            .ToList();

        // One Order has that id; matching it against Owner._id instead would return nothing.
        Assert.Equal([targetOrderId], result.Select(r => r.Id));
        Assert.Equal(30m, Assert.Single(result).Total);
    }

    // Pins the shape behind IsSingleEligibleNativeJoinScope's `!HasUnsupportedOperator` conjunct: a join-scope Where on
    // the Inner side with a predicate the native translator can't represent declines, and without the conjunct the
    // trailing `Select(x => x.r)` would still confirm the join and move the fallback onto the flattened shape. No test
    // currently fails with the conjunct removed (kept as defence in depth); these pin the results in every mode, and the
    // NativeOnly half pins that each predicate still declines. Re-check the predicates if this starts going native.
    [Theory]
    [InlineData(MongoQueryMode.Native, "Replace")]
    [InlineData(MongoQueryMode.DriverLinq, "Replace")]
    [InlineData(MongoQueryMode.Native, "ToString")]
    [InlineData(MongoQueryMode.DriverLinq, "ToString")]
    [InlineData(MongoQueryMode.Native, "Substring")]
    [InlineData(MongoQueryMode.DriverLinq, "Substring")]
    [InlineData(MongoQueryMode.Native, "Split")]
    [InlineData(MongoQueryMode.DriverLinq, "Split")]
    public void Inner_side_Where_that_declines_natively_does_not_confirm_the_join(MongoQueryMode mode, string shape)
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Inner_side_Where_that_declines_natively_does_not_confirm_the_join) + mode + shape);

        var actual = RunInnerSideDecliningWhere(db.Owners, db.Orders, shape);
        var expected = RunInnerSideDecliningWhere(seed.Owners.AsQueryable(), seed.Orders.AsQueryable(), shape);

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(t => t), actual.OrderBy(t => t));
    }

    [Theory]
    [InlineData("ToString")]
    [InlineData("Split")]
    public void Inner_side_Where_that_declines_natively_throws_under_NativeOnly(string shape)
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Inner_side_Where_that_declines_natively_throws_under_NativeOnly) + shape);

        Assert.Throws<NativeTranslationNotSupportedException>(() => RunInnerSideDecliningWhere(db.Owners, db.Orders, shape));
    }

    private static List<decimal> RunInnerSideDecliningWhere(IQueryable<Owner> owners, IQueryable<Order> orders, string shape)
        => shape switch
        {
            "Replace" => owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Region.Replace("th", "") == "Nor").Select(x => x.r).AsEnumerable().Select(r => r.Total).ToList(),
            "ToString" => owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total.ToString() == "10").Select(x => x.r).AsEnumerable().Select(r => r.Total).ToList(),
            "Substring" => owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Region.Substring(0, 1) == "N").Select(x => x.r).AsEnumerable().Select(r => r.Total).ToList(),
            "Split" => owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Region.Split(' ')[0] == "North").Select(x => x.r).AsEnumerable().Select(r => r.Total).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

    // Left join Where combining a null check on the Inner side with an ordinary Inner predicate (EF Core's
    // GroupJoin_DefaultIfEmpty_Where). Each && conjunct is translated on its own (null check ->
    // MongoLookupNullCheckExpression) and lands after the $lookup/$unwind. Covers collection and reference navs,
    // the null check alone, and the conjuncts in the other order.
    [Theory]
    [InlineData(MongoQueryMode.Native, "CollectionAnd")]
    [InlineData(MongoQueryMode.DriverLinq, "CollectionAnd")]
    [InlineData(MongoQueryMode.NativeOnly, "CollectionAnd")]
    [InlineData(MongoQueryMode.Native, "CollectionNullCheckOnly")]
    [InlineData(MongoQueryMode.DriverLinq, "CollectionNullCheckOnly")]
    [InlineData(MongoQueryMode.NativeOnly, "CollectionNullCheckOnly")]
    [InlineData(MongoQueryMode.Native, "ReferenceAnd")]
    [InlineData(MongoQueryMode.DriverLinq, "ReferenceAnd")]
    [InlineData(MongoQueryMode.NativeOnly, "ReferenceAnd")]
    [InlineData(MongoQueryMode.Native, "ReferenceAndReversed")]
    [InlineData(MongoQueryMode.DriverLinq, "ReferenceAndReversed")]
    [InlineData(MongoQueryMode.NativeOnly, "ReferenceAndReversed")]
    public void Left_join_Where_with_an_Inner_null_check_conjunct_matches_oracle(MongoQueryMode mode, string shape)
    {
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, mode,
            nameof(Left_join_Where_with_an_Inner_null_check_conjunct_matches_oracle) + mode + shape);

        var actual = RunLeftJoinNullCheckConjunct(db.Owners, db.Orders, shape);
        // In-memory LINQ evaluates `o.Name == ... && o != null` left to right and would dereference the null Owner; under
        // EF Core's null semantics the reversed conjunction means the same as the forward one, so that is the oracle.
        var expected = RunLeftJoinNullCheckConjunct(
            seed.Owners.AsQueryable(), seed.Orders.AsQueryable(), shape == "ReferenceAndReversed" ? "ReferenceAnd" : shape);

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(t => t), actual.OrderBy(t => t));
    }

#pragma warning disable RCS1146 // Use conditional access — the explicit null check IS the shape under test.
    private static List<decimal> RunLeftJoinNullCheckConjunct(IQueryable<Owner> owners, IQueryable<Order> orders, string shape)
        => shape switch
        {
            "CollectionAnd" => (from o in owners
                                join r in orders on o.Id equals r.OwnerId into g
                                from r in g.DefaultIfEmpty()
                                where r != null && r.Total > 5m
                                select r).AsEnumerable().Select(r => r.Total).ToList(),
            "CollectionNullCheckOnly" => (from o in owners
                                          join r in orders on o.Id equals r.OwnerId into g
                                          from r in g.DefaultIfEmpty()
                                          where r != null
                                          select r).AsEnumerable().Select(r => r.Total).ToList(),
            "ReferenceAnd" => (from r in orders
                               join o in owners on r.OwnerId equals o.Id into g
                               from o in g.DefaultIfEmpty()
                               where o != null && o.Name == "Alice"
                               select r).AsEnumerable().Select(r => r.Total).ToList(),
            "ReferenceAndReversed" => (from r in orders
                                       join o in owners on r.OwnerId equals o.Id into g
                                       from o in g.DefaultIfEmpty()
                                       where o.Name == "Alice" && o != null
                                       select r).AsEnumerable().Select(r => r.Total).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
#pragma warning restore RCS1146

    // A bare whole-entity INNER leaf (`Select(x => x.r)`) over a confirmed join, in EVERY mode. The bare-leaf arm
    // registers the $lookup at translation time, so DriverLinq runs on the flattened shape; the driver pushed the Select
    // down as `{ _v: "$_lookup_Orders" }` while the entity shaper reads `_lookup_Orders`, so every row was a null
    // entity. The fallback now strips that pushed-down Select.
    [Theory]
    [InlineData(MongoQueryMode.Native, "BareSelect")]
    [InlineData(MongoQueryMode.DriverLinq, "BareSelect")]
    [InlineData(MongoQueryMode.NativeOnly, "BareSelect")]
    [InlineData(MongoQueryMode.Native, "InnerWhere")]
    [InlineData(MongoQueryMode.DriverLinq, "InnerWhere")]
    [InlineData(MongoQueryMode.Native, "OrderByAfter")]
    [InlineData(MongoQueryMode.DriverLinq, "OrderByAfter")]
    [InlineData(MongoQueryMode.Native, "SkipAfter")]
    [InlineData(MongoQueryMode.DriverLinq, "SkipAfter")]
    [InlineData(MongoQueryMode.Native, "TakeAfter")]
    [InlineData(MongoQueryMode.DriverLinq, "TakeAfter")]
    [InlineData(MongoQueryMode.Native, "WhereAfter")]
    [InlineData(MongoQueryMode.DriverLinq, "WhereAfter")]
    [InlineData(MongoQueryMode.Native, "DistinctAfter")]
    [InlineData(MongoQueryMode.NativeOnly, "DistinctAfter")]
    [InlineData(MongoQueryMode.Native, "FirstAfter")]
    [InlineData(MongoQueryMode.DriverLinq, "FirstAfter")]
    [InlineData(MongoQueryMode.NativeOnly, "InnerWhere")]
    public void Bare_Inner_entity_leaf_over_a_join_matches_oracle(MongoQueryMode mode, string shape)
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, mode, nameof(Bare_Inner_entity_leaf_over_a_join_matches_oracle) + mode + shape);

        var actual = RunBareInnerEntityLeaf(db.Owners, db.Orders, shape);
        var expected = RunBareInnerEntityLeaf(seed.Owners.AsQueryable(), seed.Orders.AsQueryable(), shape);

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(t => t), actual.OrderBy(t => t));
    }

    // `Distinct()` composed AFTER a bare Inner `Select` is the one operator EF doesn't hoist ahead of the join's pending
    // selector, so the strip of the pushed-down Select can't apply (it would dedup whole Owner+Order documents). The
    // fallback keeps Select + Distinct and re-presents the deduplicated `_v` under the join's `_lookup_<Nav>` field.
    // Every mode must match the in-memory oracle, incl. an inner row matched more than once and a left-join leaf
    // whose unmatched rows collapse to a single null. The Distinct goes native; an operator after it falls back.
    [Theory]
    [InlineData(MongoQueryMode.Native, "Distinct")]
    [InlineData(MongoQueryMode.DriverLinq, "Distinct")]
    [InlineData(MongoQueryMode.NativeOnly, "Distinct")]
    [InlineData(MongoQueryMode.Native, "DuplicateInnerDistinct")]
    [InlineData(MongoQueryMode.DriverLinq, "DuplicateInnerDistinct")]
    [InlineData(MongoQueryMode.NativeOnly, "DuplicateInnerDistinct")]
    [InlineData(MongoQueryMode.Native, "OuterDistinct")]
    [InlineData(MongoQueryMode.DriverLinq, "OuterDistinct")]
    [InlineData(MongoQueryMode.NativeOnly, "OuterDistinct")]
    [InlineData(MongoQueryMode.Native, "PagedOuterDistinct")]
    [InlineData(MongoQueryMode.DriverLinq, "PagedOuterDistinct")]
    [InlineData(MongoQueryMode.NativeOnly, "PagedOuterDistinct")]
    [InlineData(MongoQueryMode.Native, "DistinctWhere")]
    [InlineData(MongoQueryMode.DriverLinq, "DistinctWhere")]
    [InlineData(MongoQueryMode.Native, "DistinctOrderBy")]
    [InlineData(MongoQueryMode.DriverLinq, "DistinctOrderBy")]
    [InlineData(MongoQueryMode.Native, "DistinctOrderBySkip")]
    [InlineData(MongoQueryMode.DriverLinq, "DistinctOrderBySkip")]
    [InlineData(MongoQueryMode.Native, "DistinctOrderByTake")]
    [InlineData(MongoQueryMode.DriverLinq, "DistinctOrderByTake")]
    [InlineData(MongoQueryMode.Native, "DistinctFirst")]
    [InlineData(MongoQueryMode.DriverLinq, "DistinctFirst")]
    [InlineData(MongoQueryMode.Native, "DistinctCount")]
    [InlineData(MongoQueryMode.DriverLinq, "DistinctCount")]
    [InlineData(MongoQueryMode.NativeOnly, "DistinctCount")]
    [InlineData(MongoQueryMode.Native, "GroupJoinDefaultIfEmptyDistinct")]
    [InlineData(MongoQueryMode.DriverLinq, "GroupJoinDefaultIfEmptyDistinct")]
    [InlineData(MongoQueryMode.NativeOnly, "GroupJoinDefaultIfEmptyDistinct")]
    [InlineData(MongoQueryMode.Native, "GroupJoinDefaultIfEmptyDuplicateInnerDistinct")]
    [InlineData(MongoQueryMode.DriverLinq, "GroupJoinDefaultIfEmptyDuplicateInnerDistinct")]
    [InlineData(MongoQueryMode.NativeOnly, "GroupJoinDefaultIfEmptyDuplicateInnerDistinct")]
#if !EF8 && !EF9
    [InlineData(MongoQueryMode.Native, "LeftJoinDistinct")]
    [InlineData(MongoQueryMode.DriverLinq, "LeftJoinDistinct")]
    [InlineData(MongoQueryMode.NativeOnly, "LeftJoinDistinct")]
#endif
    public void Distinct_after_a_bare_Inner_entity_leaf_matches_oracle(MongoQueryMode mode, string shape)
    {
        var seed = SeedOwnersWithRepeatedAndUnmatchedOrders();
        using var db = CreateContext(seed, mode, nameof(Distinct_after_a_bare_Inner_entity_leaf_matches_oracle) + mode + shape);

        var actual = RunDistinctInnerEntityLeaf(db.Owners, db.Orders, shape);
        var expected = RunDistinctInnerEntityLeaf(seed.Owners.AsQueryable(), seed.Orders.AsQueryable(), shape);

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(t => t, StringComparer.Ordinal), actual.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Distinct_after_a_bare_Inner_entity_leaf_returns_tracked_entities(MongoQueryMode mode)
    {
        var seed = SeedOwnersWithRepeatedAndUnmatchedOrders();
        using var db = CreateContext(seed, mode, nameof(Distinct_after_a_bare_Inner_entity_leaf_returns_tracked_entities) + mode);

        var owners = db.Orders.Join(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .Select(x => x.o).Distinct().ToList();

        Assert.Equal(["Alice", "Bob"], owners.Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(owners, o => Assert.Equal(EntityState.Unchanged, db.Entry(o).State));
        Assert.All(owners, o => Assert.Same(o, db.Owners.Local.Single(l => l.Id == o.Id)));
        Assert.Equal(2, db.ChangeTracker.Entries().Count());
    }

    // KNOWN GAP, pinned: EF moves the Include's LeftJoin ABOVE the Distinct, giving a join / Distinct / join chain
    // neither path can translate. It fails loudly rather than returning wrong rows; the Outer-leaf Distinct's
    // `$unset` of the join field must never reach an Include-shaping Select (see IsIncludeShapingSelect).
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Distinct_after_an_Include_over_a_join_declines_loudly(MongoQueryMode mode)
    {
        var seed = SeedOwnersWithRepeatedAndUnmatchedOrders();
        using var db = CreateContext(seed, mode, nameof(Distinct_after_an_Include_over_a_join_declines_loudly) + mode);

        Assert.Throws<InvalidOperationException>(() => db.Orders.Include(r => r.Owner)
            .Join(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => r)
            .Distinct()
            .ToList());
    }

    private static List<string> RunDistinctInnerEntityLeaf(IQueryable<Owner> owners, IQueryable<Order> orders, string shape)
    {
        var ordersOfOwners = owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r }).Select(x => x.r);
        var ownersOfOrders = orders.Join(owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o }).Select(x => x.o);

        // Keys are read client-side, so a null entity where the oracle has a real one fails loudly.
        return shape switch
        {
            "Distinct" => OrderKeys(ordersOfOwners.Distinct()),
            "DuplicateInnerDistinct" => OwnerKeys(ownersOfOrders.Distinct()),
            "OuterDistinct" => OwnerKeys(
                owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r }).Select(x => x.o).Distinct()),
            // Paging over the joined rows must run before the Distinct: the two cheapest orders are both Alice's.
            "PagedOuterDistinct" => OwnerKeys(
                owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                    .OrderBy(x => x.r.Total).Take(2).Select(x => x.o).Distinct()),
            "DistinctWhere" => OrderKeys(ordersOfOwners.Distinct().Where(r => r.Total > 15m)),
            "DistinctOrderBy" => OrderKeys(ordersOfOwners.Distinct().OrderBy(r => r.Total)),
            "DistinctOrderBySkip" => OrderKeys(ordersOfOwners.Distinct().OrderBy(r => r.Total).Skip(1)),
            "DistinctOrderByTake" => OrderKeys(ordersOfOwners.Distinct().OrderBy(r => r.Total).Take(2)),
            "DistinctFirst" => [OrderKey(ordersOfOwners.Distinct().OrderByDescending(r => r.Total).First())],
            "DistinctCount" => [ownersOfOrders.Distinct().Count().ToString()],
            "GroupJoinDefaultIfEmptyDistinct" => OrderKeys(
                (from o in owners
                 join r in orders on o.Id equals r.OwnerId into g
                 from r in g.DefaultIfEmpty()
                 select r).Distinct()),
            "GroupJoinDefaultIfEmptyDuplicateInnerDistinct" => OwnerKeys(
                (from r in orders
                 join o in owners on r.OwnerId equals o.Id into g
                 from o in g.DefaultIfEmpty()
                 select o).Distinct()),
#if !EF8 && !EF9
            "LeftJoinDistinct" => OwnerKeys(
                orders.LeftJoin(owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o }).Select(x => x.o).Distinct()),
#endif
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        static List<string> OrderKeys(IQueryable<Order?> q) => q.AsEnumerable().Select(OrderKey).ToList();
        static List<string> OwnerKeys(IQueryable<Owner?> q) => q.AsEnumerable().Select(o => o?.Name ?? "<null>").ToList();
        static string OrderKey(Order? r) => r == null ? "<null>" : r.Id + ":" + r.Total;
    }

    // Alice owns two orders (an Order->Owner join repeats her), Bob one, Carol none (unmatched outer row for an
    // Owner->Order left join), and two orders dangle (unmatched outer rows for an Order->Owner left join, which
    // Distinct must collapse to a single null).
    private static Seed SeedOwnersWithRepeatedAndUnmatchedOrders()
    {
        var alice = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 };
        var bob = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = 8 };
        var carol = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol", Region = "West", Rank = 9 };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, Total = 10m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, Total = 20m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = bob.Id, Total = 30m, Region = "South" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), Total = 98m, Region = "East" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), Total = 99m, Region = "East" },
        };

        return new Seed([alice, bob, carol], orders, []);
    }

    private static List<decimal> RunBareInnerEntityLeaf(IQueryable<Owner> owners, IQueryable<Order> orders, string shape)
    {
        var joined = owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r });
        var inner = shape switch
        {
            "BareSelect" => joined.Select(x => x.r),
            "InnerWhere" => joined.Where(x => x.r.Total > 15m).Select(x => x.r),
            "OrderByAfter" => joined.Select(x => x.r).OrderBy(r => r.Total),
            "SkipAfter" => joined.Select(x => x.r).OrderBy(r => r.Total).Skip(1),
            "TakeAfter" => joined.Select(x => x.r).OrderBy(r => r.Total).Take(2),
            "WhereAfter" => joined.Select(x => x.r).Where(r => r.Total > 15m),
            "DistinctAfter" => joined.Select(x => x.r).Distinct(),
            "FirstAfter" => joined.Select(x => x.r).OrderBy(r => r.Total).Take(1),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        // Selecting Total client-side throws for a null entity rather than silently comparing nulls.
        return inner.AsEnumerable().Select(r => r.Total).ToList();
    }

    [Fact]
    public void Where_on_the_outer_side_then_a_wrapped_scalar_projection_goes_native()
    {
        // The Where's $match is recorded before the Select confirms; a Where that failed to translate would silently
        // block confirmation (HasUnsupportedOperator).
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Where_on_the_outer_side_then_a_wrapped_scalar_projection_goes_native));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name == "Alice")
            .Select(x => new { x.o.Name, x.r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name == "Alice")
            .Select(x => new { x.o.Name, x.r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count);
    }

#if !EF8 && !EF9
    [Fact]
    public void LeftJoin_Where_on_the_outer_side_then_a_wrapped_scalar_projection_goes_native()
    {
        // LeftJoin spelling of the above (MongoJoinScope.IsLeftOuter with a preceding Where), driven from the dependent
        // side so it resolves the reference nav Order.Owner. No dangling FK here; row preservation is pinned elsewhere.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(LeftJoin_Where_on_the_outer_side_then_a_wrapped_scalar_projection_goes_native));

        var result = db.Orders
            .LeftJoin(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .Where(x => x.r.Total > 10m)
            .Select(x => new { x.o.Name, x.r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Orders
            .LeftJoin(seed.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .Where(x => x.r.Total > 10m)
            .Select(x => new { x.o.Name, x.r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count);
    }
#endif

    [Fact]
    public void Query_filtered_join_target_still_declines_cleanly_in_NativeOnly()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Query_filtered_join_target_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders.Where(r => r.Total > 0), o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }

    // Nav-null-check ternary as ONE MEMBER of a wrapped projection over a LEFT join (EF Core's
    // Condition_on_entity_with_include shape). The ELSE branch is a non-null value on purpose: EF collapses
    // `x != null ? x.M : null` to a bare `x.M` before translation, a different, already-native shape. Over a
    // COLLECTION nav, an Owner with none carries NO joined field: the null check must fire for Bob, not read a
    // missing Total.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Wrapped_nav_null_check_ternary_over_a_collection_navigation_left_join_matches_oracle(MongoQueryMode mode)
    {
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, mode,
            nameof(Wrapped_nav_null_check_ternary_over_a_collection_navigation_left_join_matches_oracle) + mode);

        var actual = (from o in db.Owners
                      join r in db.Orders on o.Id equals r.OwnerId into g
                      from r in g.DefaultIfEmpty()
                      select new { o.Name, Total = r != null ? r.Total : -1m })
            .AsEnumerable().Select(x => (x.Name, x.Total)).OrderBy(x => x.Name).ToList();
        var expected = (from o in seed.Owners
                        join r in seed.Orders on o.Id equals r.OwnerId into g
                        from r in g.DefaultIfEmpty()
                        select (o.Name, r != null ? r.Total : -1m))
            .OrderBy(x => x.Name).ToList();

        Assert.Equal([("Alice", 10m), ("Bob", -1m)], expected);
        Assert.Equal(expected, actual);
    }

    // Reference-navigation sibling (resolves Order.Owner): the dangling-FK Order must take the ELSE branch. Also
    // mixes an Outer scalar leaf with the conditional leaf.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Wrapped_nav_null_check_ternary_over_a_reference_navigation_left_join_matches_oracle(MongoQueryMode mode)
    {
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, mode,
            nameof(Wrapped_nav_null_check_ternary_over_a_reference_navigation_left_join_matches_oracle) + mode);

        var actual = (from r in db.Orders
                      join o in db.Owners on r.OwnerId equals o.Id into g
                      from o in g.DefaultIfEmpty()
                      select new { r.Total, Name = o != null ? o.Name : "<none>" })
            .AsEnumerable().Select(x => (x.Total, x.Name)).OrderBy(x => x.Total).ToList();
        var expected = (from r in seed.Orders
                        join o in seed.Owners on r.OwnerId equals o.Id into g
                        from o in g.DefaultIfEmpty()
                        select (r.Total, o != null ? o.Name : "<none>"))
            .OrderBy(x => x.Total).ToList();

        Assert.Equal([(10m, "Alice"), (99m, "<none>")], expected);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native, false)]
    [InlineData(MongoQueryMode.DriverLinq, false)]
    [InlineData(MongoQueryMode.NativeOnly, false)]
    [InlineData(MongoQueryMode.Native, true)]
    [InlineData(MongoQueryMode.DriverLinq, true)]
    [InlineData(MongoQueryMode.NativeOnly, true)]
    public void Join_on_non_key_properties_between_navigation_related_types_joins_on_the_written_keys(
        MongoQueryMode mode, bool leftOuter)
    {
        // Keep the fixture's Owner.Orders / Order.Owner navigations: they give this test meaning. A Join on Region/Region
        // (non-key) between types with a navigation: RebindInnerShaperToOuterQuery's loose
        // `FirstOrDefault(n => n.TargetEntityType == innerEntityType)` resolves Owner.Orders, whose $lookup joins on
        // _id/OwnerId. Such a navigation is discarded and the join built as a raw-key $lookup on Region/Region.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Join_on_non_key_properties_between_navigation_related_types_joins_on_the_written_keys) + mode + leftOuter);

        List<(string Name, decimal? Total)> actual, expected;
        if (leftOuter)
        {
            actual = (from o in db.Owners
                      join r in db.Orders on o.Region equals r.Region into g
                      from r in g.DefaultIfEmpty()
                      select new { o.Name, Total = r != null ? (decimal?)r.Total : null })
                .AsEnumerable().Select(x => (x.Name, x.Total)).ToList();
            expected = (from o in seed.Owners
                        join r in seed.Orders on o.Region equals r.Region into g
                        from r in g.DefaultIfEmpty()
                        select (o.Name, r != null ? (decimal?)r.Total : null)).ToList();
        }
        else
        {
            actual = db.Owners
                .Join(db.Orders, o => o.Region, r => r.Region, (o, r) => new { o.Name, r.Total })
                .AsEnumerable().Select(x => (x.Name, (decimal?)x.Total)).ToList();
            expected = seed.Owners
                .Join(seed.Orders, o => o.Region, r => r.Region, (o, r) => (o.Name, (decimal?)r.Total)).ToList();
        }

        Assert.Equal(3, expected.Count);
        Assert.Equal(expected.OrderBy(x => x.Name).ThenBy(x => x.Total), actual.OrderBy(x => x.Name).ThenBy(x => x.Total));
    }

    [Fact]
    public void Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly()
    {
        // Owner and OrderLine have no navigation between them at all.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly));

        var results =
            db.Owners
                .Join(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { o, ol })
                .ToList();

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Genuinely_navigation_less_join_matches_in_memory_oracle_including_unmatched_rows(MongoQueryMode mode)
    {
        var owners = new[]
        {
            new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "MATCH" },
            new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "NOMATCH" },
        };
        var orderLines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = ObjectId.GenerateNewId(), Sku = "MATCH", Quantity = 1 },
        };
        var seed = new Seed(owners, [], orderLines);

        using var db = CreateContext(seed, mode,
            nameof(Genuinely_navigation_less_join_matches_in_memory_oracle_including_unmatched_rows) + mode);

        var actual = db.Owners
            .Join(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { OwnerName = o.Name, ol.Sku })
            .ToList();

        var expected = owners
            .Join(orderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { OwnerName = o.Name, ol.Sku })
            .ToList();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(
            expected.Select(e => (e.OwnerName, e.Sku)).OrderBy(x => x.OwnerName),
            actual.Select(a => (a.OwnerName, a.Sku)).OrderBy(x => x.OwnerName));
    }

#if !EF8 && !EF9
    [Fact]
    public void Genuinely_navigation_less_LeftJoin_goes_native_and_preserves_unmatched_rows_under_NativeOnly()
    {
        // A navigation-less LeftJoin (NorthwindKeylessEntitiesQueryMongoTest.Entity_mapped_to_view_on_right_side_of_join
        // shape) goes native, and an unmatched outer row survives with a null inner side.
        var owners = new[]
        {
            new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "MATCH" },
            new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "NOMATCH" },
        };
        var orderLines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = ObjectId.GenerateNewId(), Sku = "MATCH", Quantity = 1 },
        };
        var seed = new Seed(owners, [], orderLines);

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuinely_navigation_less_LeftJoin_goes_native_and_preserves_unmatched_rows_under_NativeOnly));

        var actual = db.Owners
            .LeftJoin(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { OwnerName = o.Name, ol!.Sku })
            .ToList();

        var expected = owners
            .LeftJoin(orderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { OwnerName = o.Name, ol?.Sku })
            .ToList();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(
            expected.Select(e => (e.OwnerName, Sku: (string?)e.Sku)).OrderBy(x => x.OwnerName),
            actual.Select(a => (a.OwnerName, Sku: (string?)a.Sku)).OrderBy(x => x.OwnerName));

        var matched = actual.Single(x => x.OwnerName == "Alice");
        Assert.Equal("MATCH", matched.Sku);

        var unmatched = actual.Single(x => x.OwnerName == "Bob");
        Assert.Null(unmatched.Sku);
    }
#endif

    [Fact]
    public void Where_after_join_with_a_bare_scalar_select_goes_native()
    {
        // A bare scalar inner leaf (`Select(x => x.r.Total)`) after a Where goes native via TranslateSelect's bare arm.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Where_after_join_with_a_bare_scalar_select_goes_native));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .Select(x => x.r.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name == seed.Owners[0].Name)
            .Select(x => x.r.Total)
            .ToList();

        Assert.Equal(expected.OrderBy(x => x), result.OrderBy(x => x));
    }

    [Fact]
    public void Bare_whole_inner_entity_select_after_join_goes_native()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_whole_inner_entity_select_after_join_goes_native));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.r)
            .AsEnumerable()
            .OrderBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.r)
            .OrderBy(x => x.Total)
            .ToList();

        Assert.Equal(expected.Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public void Bare_whole_outer_entity_select_after_join_goes_native()
    {
        // Whole-outer spelling: inner-join semantics still apply (order-less owners drop, two-order owners appear twice).
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_whole_outer_entity_select_after_join_goes_native));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.o)
            .AsEnumerable()
            .OrderBy(x => x.Name)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.o)
            .OrderBy(x => x.Name)
            .ToList();

        Assert.Equal(expected.Select(x => x.Id), result.Select(x => x.Id));
    }

    [Fact]
    public void Wrapped_scalar_only_join_projection_goes_native()
    {
        // AppendLookupStages emits $lookup and $unwind via the IsReference && !HasPipeline branch (same as reference Include).
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Wrapped_scalar_only_join_projection_goes_native), out var spyLogger);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })
            .OrderBy(x => x.Name).ThenBy(x => x.Total)
            .ToList();

        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"$lookup\"", message);
        Assert.Contains("\"$unwind\"", message);
    }

    [Fact]
    public void Whole_inner_entity_leaf_projection_goes_native_under_NativeOnly()
    {
        // `new { o.Name, r }`: the inner whole-entity leaf stages under the fixed alias MongoJoinScope.InnerPrefix
        // ("_lookup_Orders"), not "r", because the read side resolves it from the navigation. Outer and inner leaves use
        // different machinery ($$ROOT vs the unwound alias), so both are pinned.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_inner_entity_leaf_projection_goes_native_under_NativeOnly));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.r.Total)
            .Select(x => (x.Name, x.r.Id, x.r.OwnerId, x.r.Total))
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r })
            .OrderBy(x => x.Name).ThenBy(x => x.r.Total)
            .Select(x => (x.Name, x.r.Id, x.r.OwnerId, x.r.Total))
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Both_whole_entity_leaves_projection_goes_native_under_NativeOnly()
    {
        // `new { o, r }` as in the Applied_to_projection/GroupJoin_projection/Select_Navigations spec tests.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Both_whole_entity_leaves_projection_goes_native_under_NativeOnly));

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .AsEnumerable()
            .OrderBy(x => x.o.Name).ThenBy(x => x.r.Total)
            .Select(x => (x.o.Name, x.o.Region, x.r.Id, x.r.OwnerId, x.r.Total))
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .OrderBy(x => x.o.Name).ThenBy(x => x.r.Total)
            .Select(x => (x.o.Name, x.o.Region, x.r.Id, x.r.OwnerId, x.r.Total))
            .ToList();

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Duplicated_inner_leaf_projection_goes_native_and_reads_correctly(MongoQueryMode mode)
    {
        // `new { a = r, b = r }`: two members want the same fixed alias. Without the binder's dedup guard the native
        // pipeline fails with "Duplicate element name"; DriverLinq takes the whole-document read with two members
        // index-bound to one entry. AddToProjection dedups by expression equality.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Duplicated_inner_leaf_projection_goes_native_and_reads_correctly) + mode);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { a = r, b = r })
            .AsEnumerable()
            .OrderBy(x => x.a.Total)
            .Select(x => (x.a.Id, x.a.Total, x.b.Id, x.b.Total))
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { a = r, b = r })
            .OrderBy(x => x.a.Total)
            .Select(x => (x.a.Id, x.a.Total, x.b.Id, x.b.Total))
            .ToList();

        Assert.Equal(expected, result);
        Assert.All(result, x => Assert.Equal(x.Item1, x.Item3)); // a and b read the same row
        Assert.All(result, x => Assert.Equal(x.Item2, x.Item4));
    }

    // Whole-entity join leaves stay correct with a real query parameter under Native and NativeOnly (a
    // parameterized StartsWith is natively representable via a regex placeholder).
    [Fact]
    public void Whole_entity_leaves_behind_a_parameterized_where_reads_correct_values()
    {
        var seed = SeedOwnersAndOrders();
        var namePrefix = "A"; // a captured local, not a constant — a genuine query parameter

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(Whole_entity_leaves_behind_a_parameterized_where_reads_correct_values) + "_nativeOnly"))
        {
            var nativeOnlyResult = nativeOnly.Owners
                .Join(nativeOnly.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.o.Name.StartsWith(namePrefix))
                .Select(x => new { x.o, x.r })
                .AsEnumerable()
                .OrderBy(x => x.o.Name).ThenBy(x => x.r.Total)
                .Select(x => (x.o.Name, x.o.Region, x.r.Id, x.r.OwnerId, x.r.Total))
                .ToList();
            Assert.Equal(2, nativeOnlyResult.Count);
        }

        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Whole_entity_leaves_behind_a_parameterized_where_reads_correct_values), out var spyLogger);

        var result = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name.StartsWith(namePrefix))
            .Select(x => new { x.o, x.r })
            .AsEnumerable()
            .OrderBy(x => x.o.Name).ThenBy(x => x.r.Total)
            .Select(x => (x.o.Name, x.o.Region, x.r.Id, x.r.OwnerId, x.r.Total))
            .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name.StartsWith(namePrefix))
            .Select(x => new { x.o, x.r })
            .OrderBy(x => x.o.Name).ThenBy(x => x.r.Total)
            .Select(x => (x.o.Name, x.o.Region, x.r.Id, x.r.OwnerId, x.r.Total))
            .ToList();

        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("_lookup_Orders", mql);
        Assert.Contains("$regularExpression", mql);
    }

    // DriverLinq's whole-document read binds join projections positionally, so on a whole document only a leaf whose
    // alias equals its element name reads correctly by alias (otherwise "Document element 'Total' is missing but
    // required", or a silently null `N`).
    // MongoMixedProjectionBindingRemovingExpressionVisitor.TryBindNativeFieldLeafAsDocumentPath reads such leaves by
    // root-relative path. Asserted under DriverLinq and Native (whose late-fallback leg uses the same read).
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly(MongoQueryMode mode)
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly) + mode);

        // Outer whole-entity leaf + an INNER scalar whose path ("_lookup_Orders.Total") is not its alias ("Total").
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, x.r.Total }).OrderBy(x => x.Total)
                .Select(x => (x.o.Name, x.Total)).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, x.r.Total }).AsEnumerable().OrderBy(x => x.Total)
                .Select(x => (x.o.Name, x.Total)).ToList());

        // Inner whole-entity leaf + a RENAMED outer scalar (alias "N", element "Name").
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { N = x.o.Name, x.r }).OrderBy(x => x.r.Total)
                .Select(x => (x.N, x.r.Total)).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { N = x.o.Name, x.r }).AsEnumerable().OrderBy(x => x.r.Total)
                .Select(x => (x.N, x.r.Total)).ToList());

        // Inner whole-entity leaf + a renamed INNER scalar — both halves at once.
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.r, T = x.r.Total }).OrderBy(x => x.T)
                .Select(x => (x.r.Id, x.T)).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.r, T = x.r.Total }).AsEnumerable().OrderBy(x => x.T)
                .Select(x => (x.r.Id, x.T)).ToList());

        // `x.o.Rank.Value` beside a whole-entity leaf: TryResolveMember peels `.Value` and stages the nullable property
        // under an `int` binding, so a Convert wrap is needed to avoid a shaper-compile type mismatch. Alias "X" differs
        // from "Rank" so the path read fires.
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, X = x.o.Rank!.Value })
                .Select(x => (x.o.Name, x.X)).OrderBy(t => t.Name).ThenBy(t => t.X).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, X = x.o.Rank!.Value }).AsEnumerable()
                .Select(x => (x.o.Name, x.X)).OrderBy(t => t.Name).ThenBy(t => t.X).ToList());

        // NorthwindAsTrackingQueryMongoTest.Applied_to_body_clause_with_projection shape (never run under DriverLinq by
        // spec tests). Four mechanisms off one whole document: alias≠path outer scalar via the PK's "_id" mapping, outer
        // $$ROOT whole-entity leaf, renamed dotted inner scalar ("_lookup_Orders.OwnerId"), inner fixed-alias leaf.
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { Id = x.o.Id, x.o, rOwnerId = x.r.OwnerId, x.r })
                .OrderBy(x => x.r.Total)
                .Select(x => (x.Id, x.o.Name, x.rOwnerId, x.r.Total)).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { Id = x.o.Id, x.o, rOwnerId = x.r.OwnerId, x.r }).AsEnumerable()
                .OrderBy(x => x.r.Total)
                .Select(x => (x.Id, x.o.Name, x.rOwnerId, x.r.Total)).ToList());
    }

    // Whole-entity leaf plus an alias≠path scalar leaf with a real query parameter, under Native and NativeOnly.
    [Fact]
    public void Whole_entity_leaf_and_dotted_scalar_leaf_together_behind_a_parameterized_where()
    {
        var seed = SeedOwnersAndOrders();
        var namePrefix = "A"; // a captured local, not a constant — a genuine query parameter

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(Whole_entity_leaf_and_dotted_scalar_leaf_together_behind_a_parameterized_where) + "_nativeOnly"))
        {
            var nativeOnlyRows = nativeOnly.Owners
                .Join(nativeOnly.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.o.Name.StartsWith(namePrefix))
                .Select(x => new { x.r, T = x.r.Total })
                .ToList();
            Assert.Equal(2, nativeOnlyRows.Count);
        }

        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Whole_entity_leaf_and_dotted_scalar_leaf_together_behind_a_parameterized_where), out var spyLogger);

        // Both alias-vs-path spellings, each beside a whole-entity inner leaf:
        //   `new { r, T = r.Total }` — dotted path ("_lookup_Orders.Total") under alias "T";
        //   `new { N = o.Name, r }`  — non-dotted renamed path ("Name" under alias "N").
        var dottedRows = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name.StartsWith(namePrefix))
            .Select(x => new { x.r, T = x.r.Total })
            .AsEnumerable().OrderBy(x => x.T)
            .Select(x => (x.r.Id, x.r.Total, x.T)).ToList();

        var renamedRows = db.Owners
            .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Where(x => x.o.Name.StartsWith(namePrefix))
            .Select(x => new { N = x.o.Name, x.r })
            .AsEnumerable().OrderBy(x => x.r.Total)
            .Select(x => (x.N, x.r.Id, x.r.Total)).ToList();

        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.o.Name.StartsWith(namePrefix))
                .Select(x => new { x.r, T = x.r.Total }).OrderBy(x => x.T)
                .Select(x => (x.r.Id, x.r.Total, x.T)).ToList(),
            dottedRows);

        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.o.Name.StartsWith(namePrefix))
                .Select(x => new { N = x.o.Name, x.r }).OrderBy(x => x.r.Total)
                .Select(x => (x.N, x.r.Id, x.r.Total)).ToList(),
            renamedRows);

        Assert.Equal(2, dottedRows.Count);
        Assert.Equal(2, renamedRows.Count);

        // Both queries go fully native (the parameterized Where renders a $regularExpression).
        Assert.All(
            spyLogger.GetLogMessagesByEventId(MongoEventId.ExecutedMqlQuery),
            mql => Assert.Contains("$regularExpression", mql));
    }

#if !EF8 && !EF9
    [Fact]
    public void LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path()
    {
        // BsonBinding.GetPropertyValueAtPath's absent-intermediate-segment branch: a dependent-side LeftJoin with a
        // dangling OwnerId has no "_lookup_Owner" on that row. Nullable Rank must read null; non-nullable Region must
        // behave the same on both legs.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();

        using var dl = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path) + "_dl");

        var nullableRows = dl.Orders
            .LeftJoin(dl.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .Select(x => new { x.r, x.o.Rank })
            .AsEnumerable().OrderBy(x => x.r.Total)
            .Select(x => (x.r.Total, x.Rank)).ToList();

        // Spelled out: LINQ-to-objects would throw on x.o.Rank for the dangling row (Total 99).
        Assert.Equal([(10m, (int?)7), (99m, null)], nullableRows);

        // The non-nullable arm, on both legs, compared to each other.
        static (bool Threw, string? Error, List<(decimal, string?)> Rows) RunRegion(JoinTestDbContext db)
        {
            try
            {
                return (false, null, db.Orders
                    .LeftJoin(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
                    .Select(x => new { x.r, x.o.Region })
                    .AsEnumerable().OrderBy(x => x.r.Total)
                    .Select(x => (x.r.Total, (string?)x.Region)).ToList());
            }
            catch (InvalidOperationException e)
            {
                return (true, e.Message, []);
            }
        }

        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path) + "_native");

        var driverLinqRegion = RunRegion(dl);
        var nativeRegion = RunRegion(native);

        // The legs must agree (Native once returned null while DriverLinq threw "missing for required non-nullable
        // property"): the mode can't change the answer.
        Assert.Equal(nativeRegion.Threw, driverLinqRegion.Threw);
        Assert.Equal(nativeRegion.Error, driverLinqRegion.Error);
        Assert.Equal(nativeRegion.Rows, driverLinqRegion.Rows);
        Assert.False(nativeRegion.Threw); // guards the assertions above from passing on a mutual failure
    }
#endif

    // A computed leaf has no document path, so beside a whole-entity sibling (whole-document read) there is nothing to
    // read. The emit side declines it (Route == Fallback), correct in every mode.
    [Fact]
    public void Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly()
    {
        var seed = SeedOwnersAndOrders();

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly) + "_nativeOnly"))
        {
            var declined = nativeOnly.Owners
                .Join(nativeOnly.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, X = x.r.Total * 2 });
            Assert.Throws<NativeTranslationNotSupportedException>(() => declined.ToList());
        }

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => new { x.o, X = x.r.Total * 2 }).OrderBy(x => x.X)
            .Select(x => (x.o.Name, x.X)).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly) + mode);

            Assert.Equal(expected, db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, X = x.r.Total * 2 }).AsEnumerable().OrderBy(x => x.X)
                .Select(x => (x.o.Name, x.X)).ToList());
        }
    }

    [Fact]
    public void Whole_outer_entity_leaf_that_is_also_reference_included_goes_native()
    {
        // Include_reference_when_entity_in_projection shape: the outer side of a Join also has an unrelated
        // `.Include(o => o.Orders)` and the Select captures Owner whole beside a scalar inner leaf.
        var seed = SeedOwnersAndOrders();

        static List<(string Name, decimal Total, int OwnerOrderCount)> Run(JoinTestDbContext db) =>
            db.Owners.Include(o => o.Orders)
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total })
                .AsEnumerable()
                .OrderBy(x => x.o.Name).ThenBy(x => x.Total)
                .Select(x => (x.o.Name, x.Total, x.o.Orders.Count))
                .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total })
            .OrderBy(x => x.o.Name).ThenBy(x => x.Total)
            .Select(x => (x.o.Name, x.Total, seed.Orders.Count(order => order.OwnerId == x.o.Id)))
            .ToList();

        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_outer_entity_leaf_that_is_also_reference_included_goes_native) + "_nativeOnly");
        Assert.Equal(expected, Run(nativeOnly));

        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Whole_outer_entity_leaf_that_is_also_reference_included_goes_native) + "_driverLinq");
        Assert.Equal(expected, Run(driverLinq));
    }

#if !EF8 && !EF9
    // EF8/EF9's NavigationExpandingExpressionVisitor can't build an explicit LeftJoin with Include on the joined-in
    // side, throwing before the provider sees it.
    [Fact]
    public void Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join()
    {
        // Outer_identifier_correctly_determined_when_doing_include_on_right_side_of_left_join shape: the inner side of a
        // LeftJoin carries `.Include(r => r.OrderLines)` and both sides are captured whole. An order-less Owner checks
        // left-outer preservation.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();

        static List<(string OwnerName, decimal? Total, int LineCount)> Run(JoinTestDbContext db) =>
            db.Owners
                .LeftJoin(db.Orders.Include(r => r.OrderLines), o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .AsEnumerable()
                .OrderBy(x => x.o.Name)
                .Select(x => (x.o.Name, x.r?.Total, x.r?.OrderLines.Count ?? 0))
                .ToList();

        var expected = seed.Owners
            .LeftJoin(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .OrderBy(x => x.o.Name)
            .Select(x => (x.o.Name, x.r?.Total, x.r == null ? 0 : seed.OrderLines.Count(l => l.OrderId == x.r.Id)))
            .ToList();

        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join) + "_nativeOnly");
        Assert.Equal(expected, Run(nativeOnly));

        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Whole_inner_entity_leaf_that_is_also_collection_included_goes_native_under_a_left_join) + "_driverLinq");
        Assert.Equal(expected, Run(driverLinq));
    }
#endif

    [Theory]
    [InlineData("Join")]
    [InlineData("GroupJoinDefaultIfEmpty")]
#if !EF8 && !EF9
    [InlineData("LeftJoin")]
#endif
    public void Collection_Include_on_the_inner_side_of_a_join_reads_the_inner_entitys_own_collection(string shape)
    {
        // A collection Include on the Inner source: its $lookup must be scoped under "_lookup_Orders" (localField
        // "_lookup_Orders._id"); rooted at "_id" it silently returns empty collections. The order-less Owner checks that
        // no phantom "_lookup_Orders" sub-document is created.
        var baseSeed = SeedOwnersOrdersAndLines();
        var seed = baseSeed with
        {
            Owners = [.. baseSeed.Owners, new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol", Region = "North" }]
        };
        var isLeftOuter = shape != "Join";

        List<(string Name, decimal? Total, string Skus)> Run(JoinTestDbContext db)
        {
            var rows = shape switch
            {
                "Join" => db.Owners
                    .Join(db.Orders.Include(r => r.OrderLines), o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                    .AsEnumerable().Select(x => (x.o, (Order?)x.r)).ToList(),
                "GroupJoinDefaultIfEmpty" => (
                        from o in db.Owners
                        join r in db.Orders.Include(r => r.OrderLines) on o.Id equals r.OwnerId into g
                        from r in g.DefaultIfEmpty()
                        where o.Region == "North"
                        select new { o, r })
                    .AsEnumerable().Select(x => (x.o, (Order?)x.r)).ToList(),
#if !EF8 && !EF9
                "LeftJoin" => db.Owners
                    .LeftJoin(db.Orders.Include(r => r.OrderLines), o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                    .AsEnumerable().Select(x => (x.o, x.r)).ToList(),
#endif
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };

            return rows
                .Select(x => (x.o.Name, x.Item2?.Total,
                    string.Join(",", (x.Item2?.OrderLines ?? []).Select(l => l.Sku).OrderBy(sku => sku))))
                .OrderBy(x => x.Total).ToList();
        }

        // In-memory left join via GroupJoin/SelectMany (Enumerable.LeftJoin is .NET 10-only).
        var expected = seed.Owners
            .GroupJoin(seed.Orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o, rs })
            .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, r })
            .Where(x => isLeftOuter || x.r != null)
            .Where(x => shape != "GroupJoinDefaultIfEmpty" || x.o.Region == "North")
            .Select(x => (x.o.Name, x.r?.Total,
                string.Join(",", seed.OrderLines.Where(l => l.OrderId == x.r?.Id).Select(l => l.Sku).OrderBy(sku => sku))))
            .OrderBy(x => x.Total).ToList();
        Assert.All(expected.Where(x => x.Total != null), x => Assert.NotEqual("", x.Item3));
        Assert.Equal(isLeftOuter, expected.Any(x => x.Total == null));

        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Collection_Include_on_the_inner_side_of_a_join_reads_the_inner_entitys_own_collection) + shape + "_native");
        Assert.Equal(expected, Run(native));

        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Collection_Include_on_the_inner_side_of_a_join_reads_the_inner_entitys_own_collection) + shape + "_driverLinq");
        Assert.Equal(expected, Run(driverLinq));

#if !EF8 && !EF9
        // Goes native on EF10 (EF8/EF9's LeftJoin shim for the GroupJoin form is a separate gap).
        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Collection_Include_on_the_inner_side_of_a_join_reads_the_inner_entitys_own_collection) + shape + "_nativeOnly");
        Assert.Equal(expected, Run(nativeOnly));
#endif
    }

    [Fact]
    public void Whole_entity_leaf_that_is_also_reference_included_materializes_correctly()
    {
        // Reference Include on a whole-entity join-scope leaf: nav-expansion lowers it to a LeftJoin whose $lookup is the
        // Include's. Must go native and populate Owner on every row.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_entity_leaf_that_is_also_reference_included_materializes_correctly));

        var results = db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })
            .AsEnumerable().OrderBy(x => x.o.Total).ToList();

        var expected = seed.Orders.OrderBy(o => o.Total)
            .Select(o => (o.Total, o.OwnerId, seed.Owners.Single(w => w.Id == o.OwnerId).Name)).ToList();
        Assert.Equal(expected, results.Select(x => (x.o.Total, x.OwnerId, x.o.Owner!.Name)).ToList());
        Assert.All(results, x => Assert.Equal(x.o.OwnerId, x.o.Owner!.Id));
    }

    // Renders rows to comparable strings. Queries are AsNoTracking (bar one) because tracking fixup could populate
    // navigations from other rows, masking a missing Include $lookup.
    private static List<string> RunReferenceIncludeShape(JoinTestDbContext db, string shape)
        => shape switch
        {
            // Reference Include on the outer source of an explicit Join (a 2-level chain).
            "ExplicitJoin" => db.Orders.AsNoTracking().Include(o => o.Owner)
                .Join(db.Owners, o => o.OwnerId, w => w.Id, (o, w) => new { o, w.Name }).AsEnumerable()
                .Select(x => $"{x.o.Total}|{x.o.Owner?.Name}|{x.Name}").OrderBy(x => x).ToList(),

            // The Include target is also its own inner leaf; "_lookup_Owner" must be staged once.
            "IncludedTargetAlsoProjected" => db.Orders.AsNoTracking().Include(o => o.Owner).Select(o => new { o, W = o.Owner })
                .AsEnumerable()
                .Select(x => $"{x.o.Total}|{x.o.Owner?.Name}|{x.W?.Name}").OrderBy(x => x).ToList(),

            // A reference Include and a collection Include on the SAME whole-entity leaf.
            "MixedWithCollectionInclude" => db.Orders.AsNoTracking().Include(o => o.Owner).Include(o => o.OrderLines)
                .Select(o => new { o, o.Total }).AsEnumerable()
                .Select(x => $"{x.Total}|{x.o.Owner?.Name}|{string.Join(",", x.o.OrderLines.Select(l => l.Sku).OrderBy(s => s))}")
                .OrderBy(x => x).ToList(),

            // ThenInclude reference -> reference.
            "ReferenceThenReference" => db.OrderLines.AsNoTracking().Include(l => l.Order).ThenInclude(o => o!.Owner)
                .Select(l => new { l, l.Sku }).AsEnumerable()
                .Select(x => $"{x.Sku}|{x.l.Order?.Total}|{x.l.Order?.Owner?.Name}").OrderBy(x => x).ToList(),

            // ThenInclude reference -> collection. Tracked, since EF rejects the Owner.Orders/Order.Owner cycle
            // with no-tracking; the filter defeats fixup masking (fixup alone would give Alice one order, not two).
            "ReferenceThenCollection" => db.Orders.Where(o => o.Total != 20m).Include(o => o.Owner).ThenInclude(w => w!.Orders)
                .Select(o => new { o, o.Total }).AsEnumerable()
                .Select(x => $"{x.Total}|{x.o.Owner?.Name}|{string.Join(",", (x.o.Owner?.Orders ?? []).Select(r => r.Total).OrderBy(t => t))}")
                .OrderBy(x => x).ToList(),

            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

    // In-memory oracle computed from the seed, never from another query mode (which could share the bug).
    private static List<string> ExpectedReferenceIncludeShape(Seed seed, string shape)
    {
        Owner OwnerOf(Order o) => seed.Owners.Single(w => w.Id == o.OwnerId);
        Order OrderOf(OrderLine l) => seed.Orders.Single(o => o.Id == l.OrderId);

        return (shape switch
        {
            "ExplicitJoin" or "IncludedTargetAlsoProjected" =>
                seed.Orders.Select(o => $"{o.Total}|{OwnerOf(o).Name}|{OwnerOf(o).Name}"),
            "MixedWithCollectionInclude" => seed.Orders.Select(o =>
                $"{o.Total}|{OwnerOf(o).Name}|{string.Join(",", seed.OrderLines.Where(l => l.OrderId == o.Id).Select(l => l.Sku).OrderBy(sku => sku))}"),
            "ReferenceThenReference" => seed.OrderLines.Select(l =>
                $"{l.Sku}|{OrderOf(l).Total}|{OwnerOf(OrderOf(l)).Name}"),
            "ReferenceThenCollection" => seed.Orders.Where(o => o.Total != 20m).Select(o =>
                $"{o.Total}|{OwnerOf(o).Name}|{string.Join(",", seed.Orders.Where(other => other.OwnerId == o.OwnerId).Select(other => other.Total).OrderBy(t => t))}"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        }).OrderBy(x => x).ToList();
    }

    [Theory]
    [InlineData("ExplicitJoin")]
    [InlineData("IncludedTargetAlsoProjected")]
    [InlineData("MixedWithCollectionInclude")]
    public void Reference_included_whole_entity_leaf_goes_native_and_matches_the_oracle(string shape)
    {
        // Join-scope arm with a reference-Include-wrapped whole-entity leaf; Native and driver-LINQ are each asserted against the oracle.
        var seed = SeedOwnersOrdersAndLines();
        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Reference_included_whole_entity_leaf_goes_native_and_matches_the_oracle) + shape + "_nativeOnly");
        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Reference_included_whole_entity_leaf_goes_native_and_matches_the_oracle) + shape + "_driverLinq");

        var expected = ExpectedReferenceIncludeShape(seed, shape);
        Assert.Equal(seed.Orders.Length, expected.Count);

        Assert.Equal(expected, RunReferenceIncludeShape(nativeOnly, shape));
        Assert.Equal(expected, RunReferenceIncludeShape(driverLinq, shape));
    }

    [Theory]
    [InlineData("ReferenceThenReference")]
    [InlineData("ReferenceThenCollection")]
    public void Reference_Include_with_a_ThenInclude_on_a_join_scope_leaf_falls_back_with_correct_data(string shape)
    {
        // A ThenInclude off the reference Include's target isn't admitted (TryResolveReferenceIncludeLevel requires a
        // join-scope Inner leaf): declines under NativeOnly; Native falls back and must fully populate the chain.
        var seed = SeedOwnersOrdersAndLines();
        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Reference_Include_with_a_ThenInclude_on_a_join_scope_leaf_falls_back_with_correct_data) + shape + "_nativeOnly");
        Assert.Throws<NativeTranslationNotSupportedException>(() => RunReferenceIncludeShape(nativeOnly, shape));

        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Reference_Include_with_a_ThenInclude_on_a_join_scope_leaf_falls_back_with_correct_data) + shape + "_native");
        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Reference_Include_with_a_ThenInclude_on_a_join_scope_leaf_falls_back_with_correct_data) + shape + "_driverLinq");

        var expected = ExpectedReferenceIncludeShape(seed, shape);
        Assert.NotEmpty(expected);

        Assert.Equal(expected, RunReferenceIncludeShape(native, shape));
        Assert.Equal(expected, RunReferenceIncludeShape(driverLinq, shape));
    }

    [Fact]
    public void Reference_Include_with_no_join_in_the_query_materializes_correctly()
    {
        // `Include(o => o.Owner).Select(o => new { o, o.OwnerId })` with no explicit Join still reaches the join-scope arm.
        // Order.OwnerId is required, so the Include is an inner join and the dangling order is dropped.
        var seed = SeedLinesOrdersAndOwnersWithADanglingOwnerId();
        using var native = CreateContext(seed, MongoQueryMode.Native,
            nameof(Reference_Include_with_no_join_in_the_query_materializes_correctly) + "_native");
        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Reference_Include_with_no_join_in_the_query_materializes_correctly) + "_driverLinq");

        static List<(decimal Total, ObjectId OwnerId, string? OwnerName)> Run(JoinTestDbContext db) =>
            db.Orders.Include(o => o.Owner).Select(o => new { o, o.OwnerId })
                .AsEnumerable().Select(x => (x.o.Total, x.OwnerId, x.o.Owner?.Name)).OrderBy(x => x.Total).ToList();

        var results = Run(native);

        var matched = Assert.Single(results);
        Assert.Equal("Alice", matched.OwnerName);
        Assert.Equal(Run(driverLinq), results);
    }

    [Fact]
    public void Reference_included_Inner_leaf_of_a_join_scope_materializes_correctly()
    {
        // Reference Include on the join's Inner source: lowered to a further join onto Owners. Must go native and match driver-LINQ.
        var seed = SeedOwnersAndOrders();
        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Reference_included_Inner_leaf_of_a_join_scope_materializes_correctly) + "_nativeOnly");
        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Reference_included_Inner_leaf_of_a_join_scope_materializes_correctly) + "_driverLinq");

        static List<(string OwnerName, decimal Total, ObjectId? IncludedOwnerId, string? IncludedOwnerName)> Run(
            JoinTestDbContext db) =>
            db.Owners.Join(db.Orders.Include(r => r.Owner), o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r })
                .AsEnumerable().Select(x => (x.Name, x.r.Total, x.r.Owner?.Id, x.r.Owner?.Name))
                .OrderBy(x => x.Total).ToList();

        var results = Run(nativeOnly);

        Assert.Equal(seed.Orders.Length, results.Count);
        Assert.All(results, x => Assert.Equal(x.OwnerName, x.IncludedOwnerName));
        Assert.Equal(Run(driverLinq), results);
    }

    [Fact]
    public void Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain()
    {
        // A Levels.Count == 2 chain with the Include on the root (Owner.Orders). Including a non-root level's
        // reference nav can widen the recognized chain by one level, an unrelated hazard avoided here.
        var seed = SeedOwnersOrdersAndLines();

        static List<(string OwnerName, int OwnerOrderCount, string Sku)> Run(JoinTestDbContext db) =>
            db.Owners.Include(o => o.Orders)
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
                .AsEnumerable()
                .OrderBy(x => x.o.Name).ThenBy(x => x.LineSku)
                .Select(x => (x.o.Name, x.o.Orders.Count, x.LineSku))
                .ToList();

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(seed.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
            .OrderBy(x => x.o.Name).ThenBy(x => x.LineSku)
            .Select(x => (x.o.Name, seed.Orders.Count(order => order.OwnerId == x.o.Id), x.LineSku))
            .ToList();

        using var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain) + "_nativeOnly");
        Assert.Equal(expected, Run(nativeOnly));

        using var driverLinq = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Whole_root_entity_leaf_that_is_also_collection_included_goes_native_in_a_two_level_chain) + "_driverLinq");
        Assert.Equal(expected, Run(driverLinq));
    }

    [Fact]
    public void Chain_scalar_leaf_beside_a_whole_entity_leaf_at_a_non_adjacent_chain_level_reads_correctly()
    {
        // Chain-depth analogue of Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly. The root
        // whole-entity leaf forces a whole-document read on the fallback legs; the scalar sibling deliberately names a
        // non-adjacent scope (`l.Sku`, scope 2).
        var seed = SeedOwnersOrdersAndLines();

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(Chain_scalar_leaf_beside_a_whole_entity_leaf_at_a_non_adjacent_chain_level_reads_correctly) + "_nativeOnly"))
        {
            var results = nativeOnly.Owners
                .Join(nativeOnly.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(nativeOnly.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
                .ToList();
            Assert.NotEmpty(results);
        }

        var expected = seed.Owners
            .Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Join(seed.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
            .OrderBy(x => x.o.Name).ThenBy(x => x.LineSku)
            .Select(x => (x.o.Name, x.LineSku)).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Chain_scalar_leaf_beside_a_whole_entity_leaf_at_a_non_adjacent_chain_level_reads_correctly) + mode);

            Assert.Equal(expected, db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(db.OrderLines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, LineSku = l.Sku })
                .AsEnumerable().OrderBy(x => x.o.Name).ThenBy(x => x.LineSku)
                .Select(x => (x.o.Name, x.LineSku)).ToList());
        }
    }

#if !EF8 && !EF9
    [Fact]
    public void LeftJoin_over_a_collection_navigation_now_goes_native_under_NativeOnly()
    {
        // Principal-side LeftJoin (Owners -> Orders) over Owner.Orders: the lowerer reads
        // LookupExpression.PreserveNullAndEmptyArrays, so an order-less Owner is kept. Keep the projection all-outer and
        // translatable: a conditional leaf would decline for an unrelated reason.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(LeftJoin_over_a_collection_navigation_now_goes_native_under_NativeOnly));

        var result = db.Owners
            .LeftJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name })
            .AsEnumerable()
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToList();

        // Alice has one Order and Bob none: one row each, with Bob's navigation null.
        Assert.Equal(["Alice", "Bob"], result);
    }

    [Fact]
    public void LeftJoin_unmatched_inner_row_reads_as_null_reference_navigation()
    {
        // Dependent-side LeftJoin (Order -> Owner) over the reference nav: the read arm treats the field as not required
        // and the lowerer emits preserveNullAndEmptyArrays, so a dangling-FK Order's Owner reads as null.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(LeftJoin_unmatched_inner_row_reads_as_null_reference_navigation));

        var result = db.Orders
            .LeftJoin(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .AsEnumerable()
            .OrderBy(x => x.r.Total)
            .Select(x => (x.r.Total, OwnerName: x.o == null ? null : x.o.Name))
            .ToList();

        var expected = seed.Orders
            .LeftJoin(seed.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .OrderBy(x => x.r.Total)
            .Select(x => (x.r.Total, OwnerName: x.o == null ? null : x.o.Name))
            .ToList();

        Assert.Equal(expected, result);

        // The one dangling-FK Order (Total = 99m) has a null Owner.
        Assert.Null(result.Single(x => x.Total == 99m).OwnerName);

        // ...and the matched row still resolves its Owner correctly, so the assertion above isn't vacuous.
        Assert.Equal("Alice", result.Single(x => x.Total == 10m).OwnerName);
    }
#endif

    public static IEnumerable<object[]> JoinOracleCases()
    {
        // Each case runs the same expression tree against the DbSets and the in-memory seed. The bool says whether it
        // should succeed under NativeOnly.
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })),
            true
        ];
#if !EF8 && !EF9
        // Queryable.LeftJoin only dispatches on EF10; EF8/EF9's nav-expansion rejects it before the provider.
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.LeftJoin(orders, o => o.Id, r => r.OwnerId,
                    (o, r) => new { o.Name, Total = r == null ? (decimal?)null : r.Total })),
            true
        ];
#endif
    }

    [Theory]
    [MemberData(nameof(JoinOracleCases))]
    public void Join_result_matches_in_memory_oracle_including_unmatched_rows(
        Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>> query, bool goesNativeUnderNativeOnly)
    {
        // Seed: a matched Owner, an order-less Owner, and a dangling-FK Order. Asserts the oracle under Native and,
        // under NativeOnly, either the oracle (proving native) or a clean decline per the case's flag.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();

        // dynamic because JoinOracleCases erases the result shape to IQueryable<object>.
        var oracleResult = query(seed.Owners.AsQueryable(), seed.Orders.AsQueryable()).AsEnumerable()
            .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
            .ToList();

        using var dbNative = CreateContext(seed, MongoQueryMode.Native,
            nameof(Join_result_matches_in_memory_oracle_including_unmatched_rows) + "_native");

        // Order by a stable key before comparing.
        var nativeModeResult = query(dbNative.Owners, dbNative.Orders).AsEnumerable()
            .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
            .ToList();
        Assert.Equal(oracleResult, nativeModeResult);

        using var dbNativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Join_result_matches_in_memory_oracle_including_unmatched_rows) + "_nativeOnly");

        if (goesNativeUnderNativeOnly)
        {
            var nativeOnlyResult = query(dbNativeOnly.Owners, dbNativeOnly.Orders).AsEnumerable()
                .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
                .ToList();
            Assert.Equal(oracleResult, nativeOnlyResult);
        }
        else
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                query(dbNativeOnly.Owners, dbNativeOnly.Orders).AsEnumerable().ToList());
        }
    }

    // A parameter-free Select body (`Select(e => "Foo")`) over a single inner join must keep the $lookup/$unwind, so
    // the order-less owners are dropped (3 joined rows, not 5 owners) and paging applies to the joined rows.
    [Theory]
    [InlineData("Constant", 3)]
    [InlineData("PagedConstant", 2)]
    [InlineData("PagedConstantOuterOrder", 2)]
    [InlineData("Int", 3)]
    [InlineData("CapturedParameter", 3)]
    public void Parameter_free_projection_over_a_join_keeps_the_join_rows(string shape, int expectedCount)
    {
        var seed = SeedOwnersWithOrderlessOwnersFirst();
        Assert.Equal(5, seed.Owners.Length);

        var expected = RunParameterFreeProjectionOverJoin(seed.Owners.AsQueryable(), seed.Orders.AsQueryable(), shape);
        Assert.Equal(expectedCount, expected.Count);

        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(seed, mode,
                nameof(Parameter_free_projection_over_a_join_keeps_the_join_rows) + mode + shape);
            return RunParameterFreeProjectionOverJoin(db.Owners, db.Orders, shape);
        });

        Assert.Equal(expected, result);
    }

    private static List<string> RunParameterFreeProjectionOverJoin(
        IQueryable<Owner> owners, IQueryable<Order> orders, string shape)
    {
        var joined = from c in owners
                     join o in orders on c.Id equals o.OwnerId
                     orderby o.Id
                     select new { c.Name, o.Id };

        var captured = "Bar";

        return shape switch
        {
            "Constant" => joined.Select(e => "Foo").ToList(),
            "CapturedParameter" => joined.Select(e => captured).ToList(),
            "PagedConstant" => joined.Skip(1).Take(2).Select(e => "Foo").ToList(),
            // Outer-only sort: the sort and paging record ahead of the $lookup and must be deferred past the $unwind
            // (the Inner sort above instead routes them to PostJoinOps).
            "PagedConstantOuterOrder" => (from c in owners
                                          join o in orders on c.Id equals o.OwnerId
                                          orderby c.Name
                                          select new { c.Name, o.Id }).Skip(1).Take(2).Select(e => "Foo").ToList(),
            "Int" => joined.Select(e => 42).AsEnumerable().Select(i => i.ToString()).ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }

    // Order-less owners first (by insertion and by Name) and in the majority: a pre-$lookup Skip(1).Take(2) would page
    // owners (Alice, Bob) and unwind to 3 rows; a dropped $lookup would return one row per owner (5).
    private static Seed SeedOwnersWithOrderlessOwnersFirst()
    {
        var aaron = new Owner { Id = ObjectId.GenerateNewId(), Name = "Aaron", Region = "North" };
        var alice = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var bob = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South" };
        var carol = new Owner { Id = ObjectId.GenerateNewId(), Name = "Carol", Region = "West" };
        var dave = new Owner { Id = ObjectId.GenerateNewId(), Name = "Dave", Region = "East" };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, Total = 10m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = alice.Id, Total = 20m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = bob.Id, Total = 30m, Region = "South" },
        };

        return new Seed([aaron, alice, bob, carol, dave], orders, []);
    }

    private sealed record Seed(Owner[] Owners, Order[] Orders, OrderLine[] OrderLines = default!);

    private static Seed SeedOwnersAndOrders()
    {
        // Rank non-null: the `.Value` leaf case's oracle would throw on null. Null Rank is covered by
        // SeedOwnersAndOrdersWithUnmatchedRows.
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = 8 };
        var owners = new[] { ownerA, ownerB };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m, Region = "South" },
        };

        return new Seed(owners, orders, []);
    }

    // Matched Owner, order-less Owner (dropped by Join, kept with null Total by LeftJoin), and a dangling-FK Order
    // (dropped by both, since LeftJoin is driven from Owner).
    private static Seed SeedOwnersAndOrdersWithUnmatchedRows()
    {
        var ownerWithOrder = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 };
        var ownerWithoutOrder = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South", Rank = 8 };
        var owners = new[] { ownerWithOrder, ownerWithoutOrder };

        var orders = new[]
        {
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerWithOrder.Id, Total = 10m, Region = "North" },
            new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), Total = 99m, Region = "East" },
        };

        return new Seed(owners, orders, []);
    }

    // Order-less Owner first: an un-gated pre-$lookup {$limit: 1} would keep it and the $unwind drop it.
    private static Seed SeedOrderlessOwnerFirst()
    {
        var orderless = new Owner { Id = ObjectId.GenerateNewId(), Name = "Aaron", Region = "North" };
        var withOrder = new Owner { Id = ObjectId.GenerateNewId(), Name = "Zoe", Region = "South" };

        return new Seed(
            [orderless, withOrder],
            [new Order { Id = ObjectId.GenerateNewId(), OwnerId = withOrder.Id, Total = 5m, Region = "South" }],
            []);
    }

    // Dangling-OwnerId Order first: an un-gated pre-$lookup {$limit: 1} would keep it and the join drop it.
    private static Seed SeedDanglingOrderFirstThenMatched()
    {
        var danglingOrder = new Order
        {
            Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), Total = 99m, Region = "East"
        };
        var owner = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var matchedOrder = new Order { Id = ObjectId.GenerateNewId(), OwnerId = owner.Id, Total = 10m, Region = "North" };

        return new Seed([owner], [danglingOrder, matchedOrder], []);
    }

    private static Seed SeedOwnersOrdersAndLines()
    {
        var ownerA = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North" };
        var ownerB = new Owner { Id = ObjectId.GenerateNewId(), Name = "Bob", Region = "South" };
        var owners = new[] { ownerA, ownerB };

        var order1 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 10m, Region = "North" };
        var order2 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerA.Id, Total = 20m, Region = "North" };
        var order3 = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ownerB.Id, Total = 30m, Region = "South" };
        var orders = new[] { order1, order2, order3 };

        var lines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order1.Id, Sku = "SKU-1", Quantity = 1 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order1.Id, Sku = "SKU-2", Quantity = 2 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order2.Id, Sku = "SKU-3", Quantity = 3 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = order3.Id, Sku = "SKU-4", Quantity = 4 },
        };

        return new Seed(owners, orders, lines);
    }

    // OrderLine -> Order -> Owner with a dangling Order.OwnerId: an unmatched row at the second level over a reference nav.
    private static Seed SeedLinesOrdersAndOwnersWithADanglingOwnerId()
    {
        var owner = new Owner { Id = ObjectId.GenerateNewId(), Name = "Alice", Region = "North", Rank = 7 };

        var matchedOrder = new Order { Id = ObjectId.GenerateNewId(), OwnerId = owner.Id, Total = 10m, Region = "North" };
        var danglingOrder = new Order { Id = ObjectId.GenerateNewId(), OwnerId = ObjectId.GenerateNewId(), Total = 20m, Region = "East" };
        var orders = new[] { matchedOrder, danglingOrder };

        var lines = new[]
        {
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = matchedOrder.Id, Sku = "SKU-1", Quantity = 1 },
            new OrderLine { Id = ObjectId.GenerateNewId(), OrderId = danglingOrder.Id, Sku = "SKU-2", Quantity = 2 },
        };

        return new Seed([owner], orders, lines);
    }

    [Fact]
    public void First_over_required_reference_nav_projection_with_dangling_first_row_is_correct_in_every_mode()
    {
        // Order.OwnerId is non-nullable, so EF inner-joins: the dangling first order is DROPPED and First() must answer
        // with the matched order's owner. A $limit emitted before this $lookup would keep only the dangling order.
        var seed = SeedDanglingOrderFirstThenMatched();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(First_over_required_reference_nav_projection_with_dangling_first_row_is_correct_in_every_mode) + mode);
            Assert.Equal("Alice", db.Orders.OrderBy(o => o.Region).Select(o => o.Owner!.Name).First());
        }
    }

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";

        // Nullable on purpose: selects GetPropertyValueAtPath's "return default" absent-segment arm (non-nullable Region selects the throwing arm).
        public int? Rank { get; set; }

        public List<Order> Orders { get; set; } = [];
    }

    public class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public Owner? Owner { get; set; }
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

        // Nullable on purpose (like Owner.Rank), so the unmatched row's absent-sub-document read is unambiguously
        // null.
        public int? Quantity { get; set; }
    }

    private JoinTestDbContext CreateContext(Seed seed, MongoQueryMode mode, string name)
        => CreateContext(seed, mode, name, loggerFactory: null);

    private JoinTestDbContext CreateContext(
        Seed seed, MongoQueryMode mode, string name, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateContext(seed, mode, name, loggerFactory);
    }

    private JoinTestDbContext CreateContext(Seed seed, MongoQueryMode mode, string name, ILoggerFactory? loggerFactory)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Owners" + suffix;
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "Orders" + suffix;
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "OrderLines" + suffix;

        database.MongoDatabase.GetCollection<Owner>(ownersName).InsertMany(seed.Owners);
        if (seed.Orders.Length > 0)
            database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(seed.Orders);
        if (seed.OrderLines.Length > 0)
            database.MongoDatabase.GetCollection<OrderLine>(linesName).InsertMany(seed.OrderLines);

        return new JoinTestDbContext(database, ownersName, ordersName, linesName, mode, loggerFactory);
    }

    private sealed class JoinTestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _ordersCollection;
        private readonly string _linesCollection;

        public DbSet<Owner> Owners { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<OrderLine> OrderLines { get; set; } = null!;

        public JoinTestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string ordersCollection, string linesCollection,
            MongoQueryMode mode, ILoggerFactory? loggerFactory = null)
            : base(BuildOptions(database, mode, loggerFactory))
        {
            _ownersCollection = ownersCollection;
            _ordersCollection = ordersCollection;
            _linesCollection = linesCollection;
        }

        private static DbContextOptions BuildOptions(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<JoinTestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            if (loggerFactory != null)
            {
                optionsBuilder = optionsBuilder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            }

            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Orders).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.HasMany(o => o.OrderLines).WithOne(l => l.Order).HasForeignKey(l => l.OrderId);
            });
            modelBuilder.Entity<OrderLine>(b => b.ToCollection(_linesCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
