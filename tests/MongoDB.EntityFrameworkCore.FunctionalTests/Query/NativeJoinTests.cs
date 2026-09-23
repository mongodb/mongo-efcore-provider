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
/// EF-392 (native-join-translation plan).
/// <para>
/// <b>History, so the earlier framing here is not re-derived.</b> The first attempt at this feature concluded
/// a genuine two-sided <c>Join</c>/<c>LeftJoin</c> was PERMANENTLY out of scope (EF-439): EF Core's
/// <c>NavigationExpandingExpressionVisitor</c> normalizes such a join into the same shape as an
/// Include-generated one, so the two cannot be told apart at bind time, and a provider-side disambiguation
/// mechanism was ruled out. That premise was true but the conclusion drawn from it was too strong — the two
/// shapes do not NEED distinguishing, because for the same navigation they emit the same <c>$lookup</c> and
/// produce the same rows. The v2 plan therefore keeps the "indistinguishable" finding and drops the "always
/// declines" conclusion: a join records a <see cref="MongoDB.EntityFrameworkCore.Query.Expressions.MongoJoinScope"/>
/// (pure metadata, registering nothing), and the operator that CONSUMES it — a <c>Where</c> over the outer
/// side, a bare whole-entity <c>Select</c>, or a scalar-only wrapped <c>Select</c> — confirms it and registers
/// the <c>$lookup</c> at that point.
/// </para>
/// <para>
/// These tests pin both sides of that line: the shapes that now go native (asserted under
/// <see cref="MongoQueryMode.NativeOnly"/>, the only mode that can prove nativeness), and the shapes that
/// still decline gracefully — a chain-scalar leaf composed with trailing <c>Skip</c>/<c>Take</c>, a
/// query-filtered inner, a key-equality join with no matching navigation. A whole-entity projection leaf
/// (<c>x.Outer</c>/<c>x.Inner</c> verbatim inside a
/// wrapped <c>new {...}</c>) was originally deferred territory too, but EF-444 gave both sides a native
/// shaper — the OUTER leaf stages a <c>$$ROOT</c> reference under its own alias; the INNER leaf stages the
/// same mechanism but under a FIXED, self-referential alias (the join's own <c>$lookup</c> prefix), because
/// the read side resolves the inner entity's field name from the navigation, not the projection alias. See
/// <c>NativeJoinScopeProjectionBinder</c>'s own remarks for the full asymmetry.
/// </para>
/// </summary>
public class NativeJoinTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL()
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Recording_join_scope_does_not_change_driver_LINQ_fallback_MQL), out var spyLogger);

        // EF-392 Task 5 update (and EF-444 Task 2 re-update): the query must be one NO Select arm confirms, or
        // the invariant under test is not the one being measured. `new { o, r }` USED to be such a shape (both
        // leaves whole-entity, which NativeJoinScopeProjectionBinder declined outright pre-EF-444) but EF-444
        // gave BOTH the Outer and the Inner whole-entity leaf a native shaper, so that shape now confirms too
        // (see Both_whole_entity_leaves_projection_goes_native_under_NativeOnly). The shape that still confirms
        // NOTHING is one with a genuinely untranslatable leaf — `o.Name.ToUpper()`, a string transform outside
        // NativeJoinScopeTranslator's acceptance set — so nothing registers the $lookup and the driver-LINQ
        // shape is decided purely by the join-scope RECORDING, which is what this test is about. (The
        // `new { o.Name, r.Total }` body is confirmed and registered by that binder, at translation time and
        // therefore in every MongoQueryMode, so its driver-LINQ document shape legitimately flips to the flat
        // one — exactly as a confirmed reference Include's already does. See
        // Confirmed_join_projection_uses_the_flat_lookup_shape_under_DriverLinq_too below.)
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

        // The driver-LINQ shape for a SINGLE, UNCONFIRMED join must stay the classic "_outer"/"_inner" shape —
        // NOT flip to the flat "_lookup_<Navigation>" shape that a premature AddLookup would force. This is
        // the guard against the exact regression this task's design doc flags: recording a MongoJoinScope is
        // pure metadata and must register nothing on its own.
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.DoesNotContain("_lookup_Order", message);
    }

    [Fact]
    public void Confirmed_join_projection_uses_the_flat_lookup_shape_under_DriverLinq_too()
    {
        // The other half of the pair above. Once a Select arm CONFIRMS the join, the $lookup is registered —
        // and that happens at TRANSLATION time, before MongoQueryMode is ever read, so the flat
        // "_lookup_<Navigation>" document shape applies in every mode including explicit DriverLinq. This is
        // not a regression and not mode-specific leakage: it is exactly how a confirmed reference Include
        // already behaves, MongoQueryExpression.UsesDriverJoinFields is COMPUTED from the registered lookups
        // so the pipeline and the shaper can never disagree, and the results are unchanged either way (the
        // oracle below is the proof).
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
        // EF-392 Task 5 update: under the default Native mode this shape now goes NATIVE rather than falling
        // back (Wrapped_scalar_only_join_projection_goes_native proves that separately, under NativeOnly).
        // The test is kept, and kept under Native, precisely because the value it adds is mode-independent:
        // whichever path Native picks, the answer must equal the in-memory oracle.
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
        // EF-444 Task 1. This test used to pin `new { o, r.Total }` (a whole OUTER entity leaf mixed with a
        // scalar Inner leaf) as a clean decline — that was true through EF-392/Task 5b, which left every
        // whole-entity-leaf shape on the driver-LINQ fallback. EF-444 adds a native shaper for the OUTER leaf
        // specifically (MongoQueryableMethodTranslatingExpressionVisitor.BindResultMember folds the join's own
        // shaper into the selector body first, so the leaf arrives as the StructuralTypeShaperExpression the
        // join already built and gets rebound by index over its own EntityProjectionExpression, rather than
        // mis-registered as a scalar alias read; NativeJoinScopeProjectionBinder's Outer arm stages a $$ROOT
        // reference instead of declining). The INNER leaf ALSO now goes native (Task 2, a different,
        // self-referential alias mechanism) — see Whole_inner_entity_leaf_projection_goes_native_under_NativeOnly
        // below.
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

        // Succeeding under NativeOnly is itself the "went native" signal (Query/AGENTS.md) — a fallback shape
        // would throw NativeTranslationNotSupportedException here, not silently return wrong data.
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Depth1_whole_entity_leaf_join_with_paging_goes_native_under_NativeOnly()
    {
        // Native-post-join-paging plan. The SIMPLEST case this fix covers: a single, plain (non-left-outer)
        // Join, a bare whole-entity leaf Select (`x => x.r`, no wrapping new{}), then Skip/Take — previously
        // declined by IsSingleEligibleNativeJoinScope's HasPaging conjunct exactly like the chain-scalar case.
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
        // Owned by EF-436, not this ticket — must stay declining, not silently "fixed" by this work.
        // Raw GroupJoin with collection result shape is unsupported outright, in every mode (Native/Fallback/DriverLinq).
        // This decline is NOT NativeOnly-specific: it fails the same way in all modes because raw GroupJoin's
        // collection-result shape isn't supported by the projection binder — when rs gets substituted with a
        // single-entity shaper in TranslateJoinCore, there's a type mismatch that MongoExpressionTranslator
        // can't handle in any routing path.
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
        // Native-chained-join-scalar-projection plan (2026-09-18), Task 4. This shape (a genuine three-source
        // chain, Owners -> Orders -> OrderLines, with single-scope scalar leaves x.o.Name/l.Sku) used to
        // decline outright under the OLDER native-chained-join-scope plan's blanket "chain admits only
        // whole-entity leaves" restriction. This plan's own Task 1 test
        // (Chained_join_scalar_leaf_projection_goes_native_under_NativeOnly) is the motivating proof that it
        // should now go native instead; this method's Part 1 is updated accordingly to assert success and
        // correct data rather than a throw.
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

        // Final-review finding 4: the assertion above (and its unit-test twin,
        // NativeJoinScopeProjectionBinderTests.Binds_a_second_chained_join_reusing_the_first_joins_target_type_at_the_correct_alias)
        // is not enough on its own for the INTERMEDIATE-Select spelling below. There, the intermediate
        // `Select(x => x.o)` hits the bare-whole-entity-leaf confirm arm while Joins.Count is still 1 — so
        // AddLookup FIRES and MongoQueryExpression.UsesDriverJoinFields flips before the second join runs at
        // all. That is precisely the ordering that produced a real wrong-DATA failure elsewhere on this plan
        // (NorthwindJoinQueryMongoTest.GroupJoin_Where), so this shape needs a RESULT check, not just a route
        // check — the check below is valid regardless of whether the query actually goes native or falls back,
        // which is why it is run under MongoQueryMode.Native rather than NativeOnly.
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
        // Native-chained-join-scope plan (2026-09-07), Task 1, flipped to green by Task 6. This is now a
        // REGRESSION PIN: a second chained Join followed by a Where/OrderBy/Any over the root (outermost)
        // scope goes native under NativeOnly. Confirmation is via NativeCardinalityBinder.TryBindAggregate
        // ONLY: EF's nav-expansion elides the join's pending wrap Select entirely for this selector-less bare
        // Any() (MEASURED via LambdaExpression.Print() on the preprocessed tree — no Select node exists between
        // the last Join and Where/OrderBy/Any), so there is no trailing Select for
        // NativeJoinScopeProjectionBinder.TryBindProjection to ever see for THIS test's shape; only
        // TryBindAggregate's no-trailing-Select confirming path (see IsSingleEligibleNativeJoinScope's own
        // remarks) ever runs here. Originally added (pre-fix) as a "still declines" pin, superseded here by the
        // "now goes native" assertion below, the same way earlier tests in this file evolved as their binders
        // landed.
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
        // Native-chained-join-scope plan (2026-09-07), Task 7. The sibling test above
        // (Chained_join_Where_OrderBy_Any_goes_native_under_NativeOnly) already pins that a bare Any() over a
        // fully-eligible chain reaches NativeCardinalityBinder.TryBindAggregate's chain-confirming call site
        // (added by Task 6's fix round — see that method's own remarks). Any()/All() are the PRESENCE-ONLY
        // arm of that same method (a boolean short-circuit); Count() is the ordinary $count arm, a distinct
        // code path through the SAME confirming call site (which runs unconditionally right before the
        // method's final success return, regardless of which aggregate op reached it). Both are worth pinning
        // separately since the brief calls out Count specifically, not just Any.
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
        // Native-chained-join-scalar-projection plan (2026-09-18). Every leaf here is a scalar value rooted at
        // exactly one scope in the chain: e.o.Name is the root (scope 0), e.r.Total is join #1's Inner (scope
        // 1), l.Sku is join #2's Inner (scope 2). No Skip/Take after the Select — that is a SEPARATE,
        // still-open gap (NativeSlotPopulator's post-confirmed-join guard), not part of this plan.
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
        // The exact shape NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_
        // Properties has, minus Northwind's specific entities: a chain-scalar projection followed by Skip/Take.
        //
        // RENAMED / RE-ASSERTED (native-post-join-paging plan, Task 2, step 9 investigation, 2026-09-22). This
        // test used to assert a DECLINE, on the theory that NativeSlotPopulator's HasConfirmedJoinLookup
        // post-confirmation guard (distinct from IsSingleEligibleNativeJoinScope's pre-confirmation HasPaging
        // conjunct that this plan's Task 2 fixes) would keep a "genuinely trailing" Skip/Take like this one
        // declining even after Task 2 landed. Landing Task 2 and re-running this test (still asserting
        // Assert.Throws) surfaced "No exception was thrown" — i.e. it now goes native, identically to
        // Chained_join_scalar_leaf_projection_with_paging_goes_native_under_NativeOnly, whose ONLY LINQ
        // difference from this one is Take(2) vs Take(1).
        //
        // Investigated rather than assumed a leak: HasConfirmedJoinLookup's own remarks document that its
        // slot-operator arm was measured, across the whole functional suite (3018 tests) plus the spec suite in
        // both query modes (4613 x 2), to have ZERO hits — "DEFENCE-IN-DEPTH, NOT A LIVE GUARD". EF Core's
        // nav-expansion hoists a trailing Skip/Take ahead of a join's pending result selector unconditionally
        // (not selectively by Take count), so THIS shape's Skip/Take was ALSO always recorded pre-confirmation,
        // reaching IsSingleEligibleNativeJoinScope's HasPaging conjunct — the exact branch Task 2 changed from
        // "decline" to "defer" — never the post-confirmation guard the original comment assumed would fire.
        // There is no separate follow-up-plan gap here: this is the intended, direct effect of Task 2's fix,
        // not scope creep past it.
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

        // Fix round (2026-09-22 code review, Important finding 2): this test used to only assert the row
        // COUNT — added here to match its three re-flipped siblings
        // (Take_after_Where_over_a_two_level_collection_nav_chain_goes_native_under_NativeOnly,
        // Chained_join_with_an_earlier_collection_nav_pages_the_joined_result_under_NativeOnly,
        // Take_or_Skip_after_a_confirmed_join_pages_the_joined_result_under_NativeOnly), which all compare
        // against an in-memory LINQ oracle over the SAME two Joins and scalar projection.
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
        // Native-post-join-paging plan (2026-09-22). Identical shape to
        // Chained_join_scalar_leaf_projection_with_trailing_paging_goes_native_under_NativeOnly (a plain,
        // non-left-outer Join chain, scalar leaves, then Skip/Take, differing only in Take count) — this plan
        // is what flips both. EF Core hoists
        // the Skip/Take ahead of the chain's pending selector, so IsSingleEligibleNativeJoinScope sees
        // Select.HasPaging == true at confirmation time; since neither join here is a left-outer reference nav,
        // this plan's fix defers the whole recorded PipelineOps snapshot to run after both $lookup/$unwind pairs
        // instead of declining the join outright.
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

        // Final review, Minor finding 8: this test's sibling (with a Take count of 1 instead of 2 — otherwise
        // identical) got a differential-oracle membership check in an earlier fix round; this one didn't. Add
        // the same kind of check here rather than leaving it as a bare count assertion, so a wrong-ROW
        // regression (not just a wrong-COUNT one) would also be caught.
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
        // Native-post-join-paging plan. The crux correctness proof: an Order with a DANGLING OwnerId inserted
        // FIRST, a matched Order second. A plain (non-left-outer) Join over these drops the dangling order
        // entirely — exactly ONE joined row survives. Skip(0).Take(1) must return that ONE surviving row. If
        // paging were (incorrectly) still applied BEFORE the $lookup — the bug this plan fixes — {$limit: 1}
        // would keep the dangling order (first in insertion order), which the join then drops, returning ZERO
        // rows instead of one. Mirrors the exact technique SeedOrderlessOwnerFirst already established in this
        // file for the analogous reducer hazard (First_after_a_confirmed_join_declines_cleanly_under_NativeOnly's
        // sibling correctness test) — same idea, applied to paging instead of a reducer's $limit.
        var seed = SeedDanglingOrderFirstThenMatched(); // add this helper near SeedOrderlessOwnerFirst

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly));

        var result = db.Orders
            .Join(db.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r.Total, o.Name })
            .Skip(0).Take(1)
            .ToList();

        Assert.Single(result);
        // The dangling order's Total must NOT be the one returned — it has no matching owner and must have
        // been dropped by the join before paging ever ran.
        Assert.NotEqual(seed.Orders[0].Total, result[0].Total);
    }

    [Fact]
    public void Paging_before_a_where_reaching_a_confirmed_joins_inner_side_declines_under_NativeOnly()
    {
        // Final-review CRITICAL finding. MongoSelectDefinition.PostJoinOps (populated by
        // NativeSlotPopulator's GENERAL inner-predicate Where arm — the one whose own comment says "unlike the
        // null-check arm, no IsLeftOuter requirement" — when a Where reaches a REQUIRED, non-left-outer
        // reference navigation's Inner side) and PostLookupPagingOps (populated by
        // DeferPipelineOpsPastConfirmedJoin, for a Skip/Take recorded ahead of a join whose own $unwind isn't
        // guaranteed 1:1-safe) are NOT mutually exclusive: both can be populated for the SAME select, because
        // a required reference navigation is exactly the "not 1:1-safe" category the paging-defer branch
        // targets too.
        //
        // This query composes Skip/Take BEFORE a Where reaching Order.Owner (a required, non-left-outer
        // reference navigation — OwnerId is a non-nullable ObjectId) — the exact page-then-filter shape from
        // the final review's own illustrative example (`Skip(1).Take(2).Where(od => od.Order.CustomerID !=
        // "ALFKI")`). EF's nav-expansion hoists the Skip/Take into PipelineOps ahead of the join's pending
        // selector, same as always; the Where then flips ActiveOps to PostJoinOps and records its own $match
        // there. If the confirming Select's paging branch went on to defer the (still-pre-Where) PipelineOps
        // snapshot into PostLookupPagingOps, MongoSelectLowerer would emit PostJoinOps (the $match) BEFORE
        // PostLookupPagingOps (the $skip/$limit) — filter-then-page — inverting the page-then-filter order the
        // user actually wrote. IsSingleEligibleNativeJoinScope now declines this narrow combination outright
        // instead (see its own remarks, guarded by JoinInnerAccessConfirmed) — this test pins that
        // safety restoration. No currently-passing test exercised this exact combination before this fix (the
        // motivating Include_where_skip_take_projection family's own Where predicate is root-scope-only, e.g.
        // `Quantity == 10`, never reaching the Inner side), so declining it costs no coverage.
        //
        // No explicit .Select() here: nav-expansion synthesizes a trailing pass-through `Select(ti =>
        // ti.Outer)` for a plain `Where` over a reference-navigation dereference with no user projection, and
        // THAT is what actually confirms/registers the join (the bare-whole-entity-leaf pass-through arm) —
        // same mechanism the motivating spec test's own (Include-based) shape relies on. MEASURED: with this
        // fix's `JoinInnerAccessConfirmed` guard temporarily removed, this exact query went native and
        // returned ZERO rows instead of the correct ONE (Order Total=30, whose Owner is Bob) — the "returns
        // wrong data" hazard this fix closes.
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
        // Final review, Important finding 2. The rebaselined Include_where_skip_take_projection spec family
        // (NorthwindIncludeQueryMongoTest et al.) proves this plan's fix ALSO reaches a shape that is not a
        // Join() operator at all: a plain Where/OrderBy/Skip/Take composed before a projection that
        // dereferences a REQUIRED reference navigation (e.g. `od.Order.CustomerID`) — EF's nav-expansion builds
        // the exact same JoinScope/Joins machinery for that as it does for an explicit Join(), so it reaches
        // IsSingleEligibleNativeJoinScope's paging branch the same way. For referentially-intact data this is a
        // no-op either way (a 1:1 $unwind regardless of which side of it paging runs), but for a DANGLING
        // reference it is not: this plan's fix makes native translation drop-then-page (the $lookup/$unwind now
        // runs BEFORE the deferred $skip/$limit, dropping the dangling row first), where the pre-fix fallback
        // behavior was page-then-drop. This is a DELIBERATE, ACCEPTED semantic choice — see
        // MongoSelectDefinition.PostLookupPagingOps and the IsSingleEligibleNativeJoinScope paging branch's own
        // remarks — consistent with how a genuine Join operator's paging is now handled by this same mechanism
        // (see Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly
        // above). This test pins the CURRENT (post-fix) behavior explicitly, as a documented, intentional
        // choice, rather than leaving it unverified.
        var seed = SeedDanglingOrderFirstThenMatched();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Paging_after_a_dangling_required_reference_dereference_pages_the_joined_result_under_NativeOnly));

        // Mirrors the motivating spec shape exactly: an explicit Include() over the required reference
        // navigation, then Skip/Take, then a WRAPPED projection dereferencing it (a bare, un-wrapped leaf
        // declines for an unrelated reason — "Query projects a non-entity result" — this provider's ordinary
        // scalar-leaf projection binder only resolves an OWNED reference navigation without an Include; a
        // cross-collection one needs the Include-confirmed join scope, which requires the wrapped-leaf arm).
        var result = db.Orders
            .Include(r => r.Owner)
            .Skip(0).Take(1)
            .Select(r => new { r.Owner!.Name })
            .ToList();

        // The dangling order (inserted first) has no matching Owner, so the required reference's inner join
        // drops it BEFORE the deferred paging runs; the one row this returns is the genuinely-matched order's
        // owner, not a default/null placeholder for the dangling one.
        Assert.Equal([seed.Owners[0].Name], result.Select(x => x.Name));
    }

#if !EF8 && !EF9
    [Fact]
    public void Chained_join_then_LeftJoin_unmatched_row_reads_a_scalar_leaf_at_the_second_level_under_NativeOnly()
    {
        // Final-review Important-1: the depth-1 analogue of this exact hazard
        // (LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path, below) has its own
        // dedicated test; this is the chain-depth (2-level) equivalent for the shape THIS plan newly admits to
        // native. A 2-level chain — OrderLine -> Order -> Owner — whose SECOND level is a LeftJoin over the
        // REFERENCE navigation Order.Owner (the same safe left-outer shape the depth-1 test below pins), with an
        // unmatched row (danglingOrder's OwnerId matches no seeded Owner), and a scalar leaf at every level:
        // LineQuantity is the root (scope 0), OrderTotal is join #1's Inner (scope 1), OwnerRank is join #2's —
        // the LeftJoin's — Inner (scope 2). For the dangling row, the emitted $project reads OwnerRank off a
        // MISSING sub-document rather than a merely-null field.
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

        // Spelled out rather than taken from an in-memory oracle: LINQ-to-objects' own LeftJoin hands back a
        // null `o` for the unmatched danglingOrder row, and `o.Rank` (the exact spelling the query under test
        // uses) would NullReferenceException over that — precisely the case under test.
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
        // Final-review fix (M9b). The final whole-branch review verified the "two joins onto the same target
        // entity type within one chain" alias-disambiguation behavior (UniquifyLookupAlias producing
        // "_lookup_Orders" then "_lookup_Orders_1") by hand but found it untested — this pins it. Both joins
        // here are Owner.Orders (the SAME navigation, resolved twice): the second join's outer key selector
        // (`e.o.Id`) reaches back to the ROOT scope through a pure Outer-hop chain (isDirectFromRoot), not
        // through the first join's Inner side, so both joins resolve against Owner.GetNavigations() and find
        // the identical Owner.Orders collection navigation — the exact shape that requires
        // UniquifyLookupAlias's counter suffix rather than a bare navigation-name alias, or the second join's
        // $lookup would collide with the first's.
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

        // Owner[0] ("Alice") has exactly two orders in SeedOwnersOrdersAndLines (order1: 10, order2: 20), so
        // the cross-product over both joins keyed on the SAME owner is 2 x 2 = 4 combinations.
        var ownerAOrders = seed.Orders.Where(o => o.OwnerId == seed.Owners[0].Id).ToList();
        var expected = (from r in ownerAOrders
                         from r2 in ownerAOrders
                         select (r.Total, r2.Total))
            .OrderBy(x => x.Item1).ThenBy(x => x.Item2)
            .ToList();

        Assert.Equal(expected, results);

        // Succeeding under NativeOnly is itself the "went native" signal — this is the actual point of the
        // test, since a driver-LINQ fallback would ALSO happen to produce disambiguated aliases (the aliasing
        // mechanism lives in TranslateJoinCore, shared by both legs) and so wouldn't distinguish the two.
        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("_lookup_Orders\"", message);
        Assert.Contains("_lookup_Orders_1\"", message);
    }

    [Fact]
    public void Take_after_Where_over_a_two_level_collection_nav_chain_goes_native_under_NativeOnly()
    {
        // Native-chained-join-scope plan (2026-09-07), Task 7 — ORIGINALLY pinned this shape (a Where then a
        // Take over a 2-level Owners->Orders->OrderLines chain) as a DECLINE, and its investigation correctly
        // identified the mechanism: the PRE-confirmation HasPaging conjunct in IsSingleEligibleNativeJoinScope
        // (not NativeSlotPopulator's HasConfirmedJoinLookup post-confirmation guard, which this and every other
        // chained-join + Take/Skip construction tried there measured ZERO hits for).
        //
        // RE-FLIPPED (native-post-join-paging plan, Task 2, step 10 broader regression sweep, 2026-09-22): Task
        // 2 changes exactly that conjunct from "decline" to "defer the whole recorded PipelineOps snapshot past
        // the $lookup/$unwind block(s)" whenever a join isn't already in the narrow 1:1-safe carve-out — both
        // levels here (Owner.Orders, Order.OrderLines) are ordinary Joins over COLLECTION navigations, so
        // neither is 1:1-safe, and this shape now defers instead of declining. This is the intended, direct
        // effect of Task 2's fix (see Task 3's own "leaf-shape-agnostic" framing in that plan) — not scope
        // creep past it. Re-verified as a CORRECTNESS win, not just "doesn't throw": the differential oracle
        // below confirms the returned row is a genuine member of the Where-filtered, fully-joined set.
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
        // Fix round 1, Finding 1 (task-6-report.md review). IsSingleEligibleNativeJoinScope's
        // paging/reducing carve-out used to inspect ONLY mongoQueryExpression.Joins[^1] (the LAST join in
        // the chain) for the "$unwind preserves the outer row count 1:1" property. That was unsound for a
        // chain of depth > 1: checking only the last level proves nothing about EARLIER levels. Level 1 here
        // (Owners.Join(Orders, ...)) resolves Owner.Orders — a 1:N COLLECTION navigation via an ordinary
        // (non-left-outer) Join — UNSAFE on its own. Level 2 (.LeftJoin(Owners again, ...)) resolves
        // Order.Owner — a REFERENCE navigation taken as a LeftJoin — SAFE on its own. A last-only check sees
        // ONLY level 2's safety; the fix requires EVERY level to be individually 1:1-safe before paging may
        // stay ahead of $lookup.
        //
        // RE-FLIPPED (native-post-join-paging plan, Task 2, step 10 broader regression sweep, 2026-09-22):
        // this test used to assert a DECLINE — correct at the time, since IsSingleEligibleNativeJoinScope had
        // no alternative to declining once it found a not-fully-1:1-safe chain. Task 2 replaces that decline
        // with a DEFER (the whole recorded PipelineOps snapshot moves past the $lookup/$unwind block(s)), so
        // this exact shape — level 1 individually unsafe — now goes native and pages the correctly-joined
        // result instead of failing. Re-verified as a CORRECTNESS win via a differential oracle (LINQ-to-
        // objects' own GroupJoin + DefaultIfEmpty standing in for LeftJoin), not just "doesn't throw".
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
        // FINAL-REVIEW CRITICAL 1. A Take/Skip composed onto a join was recorded into PipelineOps, which
        // MongoSelectLowerer emits BEFORE the $lookup + $unwind. Over a COLLECTION navigation that $unwind is
        // 1:N, so an UN-GATED native pipeline would page the un-joined OWNER rows, not the joined result rows
        // LINQ pages.
        //
        // The guard that used to catch it was the HasPaging conjunct in IsSingleEligibleNativeJoinScope, i.e.
        // the join was never CONFIRMED once a $skip/$limit was already recorded — not the post-confirmation
        // HasConfirmedJoinLookup signal (MEASURED: EF Core's nav-expansion defers a join's result selector as a
        // PENDING SELECTOR applied LAST, so the Take is translated BEFORE the confirming Select, and a
        // post-confirmation gate alone would never see it).
        //
        // RE-FLIPPED (native-post-join-paging plan, Task 2, step 10 broader regression sweep, 2026-09-22): this
        // test used to assert a DECLINE at that conjunct. Task 2 replaces the decline with a DEFER — the whole
        // recorded PipelineOps snapshot (the Take's $limit / Skip's $skip) moves past the $lookup/$unwind block
        // — so this exact shape now goes native and pages the CORRECTLY-joined result, rather than failing.
        //
        // The seed is what makes this a wrong-DATA-turned-correctness pin: Alice has two Orders and Bob one, so
        // the join has three rows over two owner documents. `Take(2)` must return exactly TWO joined rows — the
        // OLD un-gated (pre-EF-392) native pipeline emitted {$limit: 2}, {$lookup}, {$unwind} and got THREE
        // (limiting to two OWNERS, then expanding them); `Skip(2)` must drop two joined rows and return one,
        // where the old bug skipped two owner documents and left nothing at all. Task 2's deferred-ops fix
        // moves the $limit/$skip to AFTER the $lookup/$unwind, so it now pages the joined rows correctly.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Take_or_Skip_after_a_confirmed_join_pages_the_joined_result_under_NativeOnly));

        // Differential oracle. Take/Skip over an unordered source has no defined row IDENTITY in LINQ, so what
        // is pinned is the row COUNT (which is fully defined — and is exactly the quantity the OLD un-gated
        // native pipeline got wrong: 3 instead of 2, and 0 instead of 1) plus membership of every returned row
        // in the full joined set.
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

        // Final review, Important finding 4. This test used to also cross-check the DriverLinq fallback leg
        // before the deferred-paging fix landed; that leg was dropped along the way. Restoring it matters MORE
        // now, not less — the critical fix above establishes there ARE shapes where native and fallback
        // genuinely diverge (a Where reaching a confirmed join's Inner side ahead of hoisted paging), so having
        // at least one paging-over-a-confirmed-join test that cross-checks both modes agree is valuable
        // regression coverage, not redundant belt-and-suspenders.
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
        // FINAL-REVIEW CRITICAL 1, reducer half. NativeCardinalityBinder.TryBindReducer synthesizes its
        // $limit into PipelineOps, i.e. ahead of the $lookup/$unwind — so an un-gated First() reduced the
        // OUTER rows and only then joined.
        //
        // This is the ONE shape measured to reach the post-confirmation MongoSelectDefinition
        // .HasConfirmedJoinLookup signal (instrumented across the functional + both spec runs: two hits, both
        // from this test). Unlike Take/Skip, a reducer is NOT hoisted ahead of the join's pending result
        // selector, so it arrives after the confirming Select — which is exactly why the forward-ordering
        // conjuncts in IsSingleEligibleNativeJoinScope cannot cover it and TryBindReducer needs its own gate.
        //
        // The seed puts an owner with NO orders FIRST in the collection, which is what turns this from a
        // "maybe the wrong row" hazard into a hard, observable failure: {$limit: 1} keeps that owner, the
        // $unwind (preserveNullAndEmptyArrays: false, per the collection-nav arm) then drops it, and First()
        // returns nothing at all where LINQ answers with the OTHER owner's joined row.
        //
        // FUTURE EDITORS — the WHOLE-ENTITY bare-leaf spelling (`Select(x => x.r)`) is load-bearing and must
        // not be "simplified" to the scalar `new { o.Name, r.Total }` projection the Take/Skip test above uses.
        // MEASURED (2026-08-27): a reducer over a wrapped scalar projection declines earlier and for an
        // unrelated reason ("Query projects a non-entity result…"), so that spelling stays green with this
        // test's guard deleted — i.e. it would pin nothing. The whole-entity spelling reaches the bare-leaf
        // confirm arm with a reducer's $limit already recorded, which is the case actually under test.
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

        // NO Native-mode correctness half here, and that omission is deliberate + measured, not an oversight.
        // `Join(...).Select(x => x.r).FirstOrDefault()` returns NULL — where LINQ's answer is the one joined
        // Order — on the DRIVER-LINQ path as well, in explicit DriverLinq mode, and with this guard both
        // enabled AND disabled (so also at the pre-fix-wave branch tip 706154b, and, since the guard prevents
        // the $lookup from ever being registered for this shape, on a pipeline identical to the pre-EF-392
        // one). That is a PRE-EXISTING fallback defect in the reducer-after-a-whole-entity-join-projection
        // shape, entirely independent of this fix wave, so there is no correct result for this test to assert
        // under Native. What this test pins is the thing this fix wave OWNS: the shape declines cleanly instead
        // of silently going native (before the guard, NativeOnly returned wrong data — null, or a
        // "Sequence contains more than one element" throw depending on the seed — rather than throwing
        // NativeTranslationNotSupportedException). Verified by mutation (2026-08-27): disabling
        // TryBindReducer's HasConfirmedJoinLookup gate makes both assertions above fail with "No exception was
        // thrown", and the query then returns null / throws "Sequence contains more than one element" natively
        // depending on the seed.
    }

    [Fact]
    public void Where_after_a_bare_whole_entity_leaf_select_declines_cleanly_under_NativeOnly()
    {
        // FINAL-REVIEW CRITICAL 2, and the mechanism below is NOT the one the review hypothesised — this
        // comment records what was actually measured, because the difference decides which guard may be
        // deleted later.
        //
        // The hypothesis was: `Select(x => x.r)` is confirmed by the bare whole-entity-leaf arm and the shaper
        // then yields ORDER entities, while MongoQueryExpression.CollectionExpression.EntityType is still OWNER
        // (nothing updates it) — and that is what NativeSlotPopulator builds its single-scope
        // MongoExpressionTranslator from. Since MongoExpressionTranslator.Members resolves a member off a
        // ParameterExpression by NAME only, "Id" would resolve against OWNER and emit a $match on the ROOT
        // document's _id: OWNERS filtered by an ORDER id, before the $lookup.
        //
        // MEASURED (2026-08-27, via the real preprocessor + QMTEV): that ordering never occurs. EF Core's
        // nav-expansion HOISTS the trailing Where ahead of the join's pending result selector, so this query
        // preprocesses to `Where(ti => ti.Inner.Id == k).Select(ti => ti.Inner)` — the predicate is still
        // expressed against the transparent identifier's INNER side, which NativeSlotPopulator's Where arm
        // declines outright (NativeJoinScopeTranslator.ReferencesInnerScope: a $match lowers before the
        // $lookup). That decline marks the query non-native, which in turn blocks confirmation through
        // IsSingleEligibleNativeJoinScope's HasUnsupportedOperator conjunct. Both guards are pre-existing; the
        // post-confirmation HasConfirmedJoinLookup signal added by this fix wave is NOT what saves this shape
        // (it is defence-in-depth for the reversed ordering — see MongoSelectDefinition.HasConfirmedJoinLookup).
        //
        // The test is kept because the SHAPE is what matters: `Id`/`_id` is a near-universal property name, and
        // this pins that the join instance of the "single-scope resolution is by name only" hazard declines and
        // returns correct rows rather than silently filtering the wrong collection. The general, join-
        // independent form of that hazard is pre-existing and deliberately out of scope here.
        var seed = SeedOwnersAndOrders();
        var targetOrderId = seed.Orders[2].Id;

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Where_after_a_bare_whole_entity_leaf_select_declines_cleanly_under_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => x.r)
                .Where(r => r.Id == targetOrderId)
                .ToList());

        using var dbNative = CreateContext(seed, MongoQueryMode.Native,
            nameof(Where_after_a_bare_whole_entity_leaf_select_declines_cleanly_under_NativeOnly) + "_fallback");

        var result = dbNative.Owners
            .Join(dbNative.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .Select(x => x.r)
            .Where(r => r.Id == targetOrderId)
            .ToList();

        // Exactly one Order carries that id, and it belongs to a matched Owner — so the correct answer is that
        // one Order. Matching the same id against OWNER._id (the mis-resolution above) matches no owner at all
        // and returns nothing.
        Assert.Equal([targetOrderId], result.Select(r => r.Id));
        Assert.Equal(30m, Assert.Single(result).Total);
    }

    [Fact]
    public void Where_on_the_outer_side_then_a_wrapped_scalar_projection_goes_native()
    {
        // FINAL-REVIEW IMPORTANT 3: the full composed pipeline through the wrapped-scalar-projection arm —
        // Task 4's Where-over-the-OUTER-side capability feeding Task 5's wrapped scalar-only Select. Every
        // other positive test for that binder projects straight off the join with no Where in between, so the
        // two capabilities had never been exercised together (the Where records a $match conjunct BEFORE the
        // Select confirms and registers the $lookup, and the shared gate's !HasUnsupportedOperator conjunct
        // means a Where that failed to translate would silently prevent confirmation).
        //
        // NativeOnly + an in-memory differential oracle: succeeding here is the "went native" proof, and the
        // oracle is what makes it a correctness check rather than a "doesn't throw" check.
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
        // The LeftJoin spelling of the composition above (final-review finding 3's second half): MongoJoinScope
        // .IsLeftOuter is live for this shape and was untested in combination with a preceding Where.
        //
        // Driven from the DEPENDENT side (Orders → Owners) deliberately: that resolves Order.Owner, a REFERENCE
        // navigation, which is the arm that threads requiredness through properly. The principal-side spelling
        // resolves a COLLECTION navigation, which also goes native (see
        // LeftJoin_over_a_collection_navigation_now_goes_native_under_NativeOnly) but was historically declined
        // and is kept as a separate pin rather than merged into this test.
        //
        // The seed has no dangling FK, so every Order has an Owner and `x.o.Name` is never a null dereference;
        // left-outer ROW PRESERVATION is pinned by that other test, not this one — what this pins is the
        // Where-then-wrapped-Select composition staying native for the left-outer scope.
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

    [Fact]
    public void Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly()
    {
        // FUTURE EDITORS — the fixture's Owner.Orders / Order.Owner navigations are what make this test mean
        // something; do NOT "simplify" them away. (An earlier version of this comment claimed there was "no
        // corresponding model navigation between the two entity types", which was simply inaccurate for this
        // fixture and would have invited exactly that simplification.)
        //
        // What is actually pinned: a Join on Region/Region, i.e. NON-key properties, between two types that DO
        // have a navigation. RebindInnerShaperToOuterQuery's navigation resolution ends in a loose
        // `FirstOrDefault(n => n.TargetEntityType == innerEntityType)` fallback, so it happily resolves
        // Owner.Orders here and builds a $lookup joining on _id/OwnerId — a completely different join condition
        // from the one written. That is harmless while the lookup is never emitted, but the moment a Select arm
        // confirms the join it would emit natively and return silently wrong rows. TranslateJoinCore's
        // JoinLookupImplementsKeySelectors conjunct is what declines it, by requiring the emitted $lookup's
        // localField/foreignField to be exactly the element names of the join's own key properties. Remove the
        // navigations from the fixture and this test still passes — for the wrong reason (no navigation, so no
        // JoinScope at all), silently defanging that pin.
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Owners
                .Join(db.Orders, o => o.Region, r => r.Region, (o, r) => new { o.Name, r.Total })
                .AsEnumerable()
                .ToList());
    }

    [Fact]
    public void Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly()
    {
        // Unlike Navigation_less_key_equality_join_still_declines_cleanly_in_NativeOnly above (which pins a
        // WRONGLY-resolved navigation between two types that DO have one), Owner and OrderLine have NO
        // navigation connecting them at all — this is the genuinely-navigation-less case.
        var seed = SeedOwnersOrdersAndLines();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Genuinely_navigation_less_key_equality_join_goes_native_under_NativeOnly));

        var results =
            db.Owners
                .Join(db.OrderLines, o => o.Region, ol => ol.Sku, (o, ol) => new { o, ol })
                .ToList();

        Assert.Empty(results); // No seeded Region/Sku values match — this only proves the query TRANSLATES
                                // and EXECUTES natively (no NativeTranslationNotSupportedException), not a
                                // row-count claim. Row-shape correctness is Task 2's job.
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
        // Final-review fix: this is the plan's own headline shape — a navigation-less LEFT-OUTER join (the
        // original motivating test, NorthwindKeylessEntitiesQueryMongoTest.Entity_mapped_to_view_on_right_side_of_join,
        // is itself a navigation-less LEFT-OUTER join) — but every other new test in this file uses an inner
        // Join, so this pins the left-outer shape directly: a navigation-less LeftJoin goes native under
        // NativeOnly (proving nativeness — succeeding under NativeOnly IS the "went native" signal, see
        // Query/AGENTS.md), AND an unmatched outer row survives with a null inner side rather than being
        // silently dropped (the actual left-outer-preservation hazard a wrong-data bug would break).
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

        // (a) executed without throwing NativeTranslationNotSupportedException — proves it went native.
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(
            expected.Select(e => (e.OwnerName, Sku: (string?)e.Sku)).OrderBy(x => x.OwnerName),
            actual.Select(a => (a.OwnerName, Sku: (string?)a.Sku)).OrderBy(x => x.OwnerName));

        // (b) the matched owner's row has a non-null inner side with the correct joined value.
        var matched = actual.Single(x => x.OwnerName == "Alice");
        Assert.Equal("MATCH", matched.Sku);

        // (c) the unmatched owner still APPEARS in the results with a null inner side.
        var unmatched = actual.Single(x => x.OwnerName == "Bob");
        Assert.Null(unmatched.Sku);
    }
#endif

    [Fact]
    public void Where_after_join_with_a_bare_scalar_select_goes_native()
    {
        // Formerly "Where_after_join_still_declines_under_NativeOnly_pending_the_Select_side_binder" — its own
        // comment anticipated this exact revisit: "Once Task 5 lands the Select-side binder, THIS test should
        // be revisited: either extended to assert NativeOnly success ... or left as the 'still declines for
        // shapes Task 5 doesn't cover' pin". EF-322 Task 3b is that binder for a BARE (unwrapped) scalar leaf
        // specifically — `Select(x => x.r.Total)` is neither a whole-entity leaf nor a `new {...}` wrapper, and
        // is exactly the shape Task 3b's new TranslateSelect arm now handles — so this shape goes native under
        // NativeOnly instead of declining.
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
        // The whole-OUTER spelling of the same shape. Inner-join semantics still apply — an Owner with no
        // Order drops out, and an Owner with two Orders appears twice — so this is not the same query as a
        // bare `db.Owners.ToList()`; the oracle below encodes that.
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
        // EF-392 Task 6: this is the flattened-scalar-join smoke test the Task 6 brief describes
        // (Flattened_scalar_join_goes_native_with_correct_results_and_mql) — it already existed from Task 5,
        // so rather than duplicate it, it's extended here with the MQL assertion Task 6 needs: proof that
        // AppendLookupStages emits both $lookup and $unwind for this confirmed join, with no lowerer change
        // required (outcome A from the brief — the pre-existing IsReference && !HasPipeline branch, built via
        // the same LookupExpression(navigation, forceUnwind: true) construction the reference-Include path
        // uses, already handles it).
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

        // Succeeding under NativeOnly is itself the "went native" signal (Query/AGENTS.md).
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"$lookup\"", message);
        Assert.Contains("\"$unwind\"", message);
    }

    [Fact]
    public void Whole_inner_entity_leaf_projection_goes_native_under_NativeOnly()
    {
        // EF-444 Task 2. This test used to pin `new { o.Name, r }` (a scalar OUTER leaf mixed with a whole
        // INNER entity leaf) as a clean decline — true through Task 1, which only gave the OUTER leaf a native
        // shaper. Task 2 adds the Inner arm: it stages under a FIXED, self-referential alias
        // (MongoJoinScope.InnerPrefix, e.g. "_lookup_Orders") rather than the member's own alias ("r"), because
        // the read side (MongoProjectionBindingRemovingExpressionVisitor's cross-collection arm) resolves the
        // inner entity's field name from the NAVIGATION, not from the projection alias.
        //
        // The INNER-side spelling, deliberately — Whole_outer_entity_leaf_projection_goes_native_under_NativeOnly
        // above covers the OUTER-side one (`new { o, r.Total }`). The check
        // (IsTransparentIdentifierOuterOrInnerAccess) is position-agnostic, but the two leaves are shaped by
        // different machinery (the outer entity reads off the root document via $$ROOT, the inner one out of
        // the $lookup's own unwound alias), so both positions are worth pinning rather than asserting the same
        // shape twice.
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

        // Succeeding under NativeOnly is itself the "went native" signal (Query/AGENTS.md) — a fallback shape
        // would throw NativeTranslationNotSupportedException here, not silently return wrong data.
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Both_whole_entity_leaves_projection_goes_native_under_NativeOnly()
    {
        // EF-444 Task 2. `new { o, r }` — no scalars at all, both sides whole-entity. Mirrors the exact shape
        // SpecificationTests' Applied_to_projection/GroupJoin_projection/Select_Navigations exercise (those
        // are re-baselined, not re-asserted, by Task 5 — this test is the differential-correctness proof that
        // the DATA those baselines will move to is actually correct).
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
        // EF-444 Task 2: the real hazard the Task 0 spike found. `new { a = r, b = r }` — two members both
        // wanting the SAME fixed self-referential alias (MongoJoinScope.InnerPrefix). Without the dedup guard
        // in NativeJoinScopeProjectionBinder, this crashes at pipeline-build time with
        // InvalidOperationException: Duplicate element name, under Native/NativeOnly (verified live by mutation
        // while implementing that task — disabling the guard reproduces exactly this exception). An explicit
        // DriverLinq builds no native pipeline at all, so the crash cannot occur there; that leg instead takes
        // the STRIPPED whole-document read with ONE staged field and TWO members index-bound to the same
        // projection entry, which is why this is a [Theory] over all three modes rather than a NativeOnly-only
        // [Fact] (final-review finding: the DriverLinq/late-fallback leg for this shape was untested).
        //
        // Both members must materialize correctly in every mode: the bind side's own AddToProjection dedups by
        // expression equality to the same index regardless of how many times the emit side stages the field.
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

    // EF-444 Task 4 — originally pinned the LATE-FALLBACK LEG (a query routed native at translate time,
    // Route == Projection, whose TryBuildNativeFactory then declined MID-COMPILE because the Where's
    // parameterized StartsWith had no native regex form and RenderRegex threw). That trigger no longer
    // declines: a parameterized StartsWith/EndsWith/Contains term is natively representable (it defers its
    // escape/anchor to a regex placeholder sentinel resolved at Build time — see
    // MongoQueryLanguageRenderer.RenderRegex/PlaceholderTable.CreateRegexPlaceholder), so this shape now goes
    // fully native under BOTH Native and NativeOnly rather than falling to the mixed/whole-document shaper.
    // The mixed-shaper correctness this test used to exercise via that trigger remains covered directly under
    // explicit MongoQueryMode.DriverLinq by Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly
    // below. This test now just proves the join's whole-entity leaves stay correct with a genuine (non-baked)
    // query parameter, under both Native and NativeOnly.
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

        // VALUES, not counts.
        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count);

        var mql = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("_lookup_Orders", mql);
        Assert.Contains("$regularExpression", mql);
    }

    // EF-444 Task 4 — the sibling half of the problem above, found while measuring it, and NOT specific to the
    // late-fallback leg: an explicit MongoQueryMode.DriverLinq takes the SAME whole-document read, and Tasks 1
    // and 2 had broken it there too. MEASURED before the fix, on the branch tip:
    //   new { o, r.Total }      → InvalidOperationException: Document element 'Total' is missing but required
    //   new { N = o.Name, r }   → N came back SILENTLY null
    //   new { r, T = r.Total }  → InvalidOperationException: Document element 'T' is missing but required
    // and each was correct in every mode before EF-444 gave the whole-entity leaf a native shaper (verified by
    // mutation: disabling the Inner arm restores all three). The cause is one rule, not three bugs — a join
    // projection binds its members POSITIONALLY (index-bound ProjectionBindingExpressions), which the mixed
    // shaper used to read by projection ALIAS; on a whole document only a leaf whose alias happens to equal its
    // own element name reads correctly. MongoMixedProjectionBindingRemovingExpressionVisitor
    // .TryBindNativeFieldLeafAsDocumentPath now reads such a leaf by its root-relative PATH instead.
    //
    // Asserted in BOTH modes against the in-memory oracle: DriverLinq is the user-facing escape hatch and Native
    // is the default, and the same read serves the late-fallback leg of the latter.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly(MongoQueryMode mode)
    {
        var seed = SeedOwnersAndOrders();
        using var db = CreateContext(seed, mode,
            nameof(Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly) + mode);

        // Outer whole-entity leaf + an INNER scalar, whose path ("_lookup_Orders.Total") is not its alias
        // ("Total") and is not even a top-level name.
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, x.r.Total }).OrderBy(x => x.Total)
                .Select(x => (x.o.Name, x.Total)).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, x.r.Total }).AsEnumerable().OrderBy(x => x.Total)
                .Select(x => (x.o.Name, x.Total)).ToList());

        // Inner whole-entity leaf + a RENAMED outer scalar, whose alias ("N") is not its element name ("Name").
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

        // FINAL-REVIEW FINDING 1 — a `Nullable<T>.Value` leaf beside a whole-entity leaf.
        //
        // `x.o.Rank` is `int?`; MongoExpressionTranslator.TryResolveMember peels the `.Value` (EF-402) and
        // stages the NULLABLE property under a NON-nullable (`int`) binding type. On the whole-document leg
        // that lands in TryBindNativeFieldLeafAsDocumentPath, whose BsonBinding.CreateGetPropertyValueAtPath
        // call types its generic argument `property.IsNullable ? mappedType.MakeNullable() : mappedType` —
        // i.e. it hands back an `int?` read where the binding wants `int`. Without the Convert wrap (which the
        // NATIVE leg's equivalent read has always had, and which this method was missing) that is a hard
        // shaper-compile type mismatch. VERIFIED BY MUTATION: reverting the wrap makes this block fail with
        // "Expression of type 'System.Nullable`1[System.Int32]' cannot be used for ... 'System.Int32'".
        //
        // The alias ("X") deliberately differs from the element name ("Rank") so the path read actually fires;
        // the whole-entity `x.o` sibling is what forces the whole-document leg under DriverLinq at all.
        Assert.Equal(
            seed.Owners.Join(seed.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, X = x.o.Rank!.Value })
                .Select(x => (x.o.Name, x.X)).OrderBy(t => t.Name).ThenBy(t => t.X).ToList(),
            db.Owners.Join(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => new { x.o, X = x.o.Rank!.Value }).AsEnumerable()
                .Select(x => (x.o.Name, x.X)).OrderBy(t => t.Name).ThenBy(t => t.X).ToList());

        // FINAL-REVIEW FINDING 2 — the FLAGSHIP spec shape, which nothing else asserts under explicit
        // DriverLinq. NorthwindAsTrackingQueryMongoTest.Applied_to_body_clause_with_projection's
        // `new { CustomerID = c.CustomerID, c, ocid = o.CustomerID, o }` combines FOUR of EF-444's mechanisms
        // in one projection, and the spec-test infrastructure only ever runs default Native and (via
        // MONGODB_EF_NATIVE_ONLY=1) NativeOnly — never explicit DriverLinq. Structurally reproduced here:
        //   Id        = x.o.Id      — an alias≠path OUTER scalar reading through the PK's own "_id" mapping;
        //   x.o                     — the OUTER $$ROOT whole-entity leaf;
        //   rOwnerId  = x.r.OwnerId — a renamed DOTTED INNER scalar ("_lookup_Orders.OwnerId");
        //   x.r                     — the INNER fixed-alias whole-entity leaf.
        // All four must resolve simultaneously off one whole, un-projected document.
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

    // EF-444 Task 4 — originally THE SEAM between this task's two mechanisms (a parameterized-regex
    // late-compile decline forcing the whole-entity leaf + alias!=path scalar leaf combination through the
    // mixed/whole-document shaper). That decline trigger no longer applies — a parameterized StartsWith term
    // is now natively representable (see MongoQueryLanguageRenderer.RenderRegex) — so this shape goes fully
    // native under both Native and NativeOnly instead. The mixed-shaper "Total" → "_lookup_Orders.Total"
    // path-read this test used to force is still covered directly under explicit MongoQueryMode.DriverLinq by
    // Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly above (its
    // `new { x.r, T = x.r.Total }` case is the identical shape). This test now just proves the combination
    // stays correct with a genuine query parameter, under both Native and NativeOnly.
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

        // BOTH seam spellings, because the alias-vs-path disagreement they create is different:
        //   `new { r, T = r.Total }` — a DOTTED path ("_lookup_Orders.Total") under alias "T", so the swapped-in
        //                             visitor must walk into the joined sub-document, not just rename;
        //   `new { N = o.Name, r }`  — a non-dotted renamed path ("Name" under alias "N").
        // Each pairs its scalar with a whole-entity INNER leaf, which is what makes the swap fire in the first
        // place.
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

        // Both queries now go fully native (the parameterized Where renders a $regularExpression rather than
        // declining), rather than landing on the mixed/whole-document fallback leg.
        Assert.All(
            spyLogger.GetLogMessagesByEventId(MongoEventId.ExecutedMqlQuery),
            mql => Assert.Contains("$regularExpression", mql));
    }

#if !EF8 && !EF9
    [Fact]
    public void LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path()
    {
        // EF-444 Task 4 — BsonBinding.GetPropertyValueAtPath's ABSENT-INTERMEDIATE-SEGMENT branch, both arms,
        // MEASURED rather than reasoned about (the same standard Task 3 held itself to for the unmatched-row
        // case it pinned). A LeftJoin driven from the dependent side over SeedOwnersAndOrdersWithUnmatchedRows
        // has one Order whose OwnerId matches no Owner, so "_lookup_Owner" is simply ABSENT from that row's
        // document — which is exactly the intermediate segment the path "_lookup_Owner.Rank" walks through.
        //
        // Under DriverLinq the shape takes the whole-document leg (whole-entity leaf ⇒ Select stripped ⇒ mixed
        // removing visitor ⇒ TryBindNativeFieldLeafAsDocumentPath), so this is the read under test.
        //
        // NULLABLE arm (Rank, int?): the absent segment must read as null, NOT throw — this is what keeps an
        // unmatched left-outer row an ordinary null-valued row.
        // NON-NULLABLE arm (Region, string): asserted to behave the SAME as the equivalent read on the NATIVE
        // leg, so the two legs cannot silently disagree about a dangling row. Whichever disposition the native
        // path has, this test pins that they match rather than hard-coding one.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();

        using var dl = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path) + "_dl");

        var nullableRows = dl.Orders
            .LeftJoin(dl.Owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
            .Select(x => new { x.r, x.o.Rank })
            .AsEnumerable().OrderBy(x => x.r.Total)
            .Select(x => (x.r.Total, x.Rank)).ToList();

        // Spelled out rather than taken from an in-memory oracle: LINQ-to-objects would NullReferenceException
        // on x.o.Rank for the dangling row, which is precisely the case under test. The seed has an Order with
        // Total 10 whose Owner (Alice, Rank 7) exists, and an Order with Total 99 whose OwnerId matches nothing.
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

        // The legs must agree. They did NOT when GetPropertyValueAtPath's absent-segment arm dispatched on
        // property.IsNullable: Native returned (99, null) for the dangling row while DriverLinq threw
        // "Document element '_lookup_Owner.Region' is missing for required non-nullable property 'Region'".
        // That divergence is the reason this test compares the legs to each other rather than asserting a
        // hard-coded disposition — the invariant that matters is "the mode you pick cannot change the answer".
        Assert.Equal(nativeRegion.Threw, driverLinqRegion.Threw);
        Assert.Equal(nativeRegion.Error, driverLinqRegion.Error);
        Assert.Equal(nativeRegion.Rows, driverLinqRegion.Rows);
        Assert.False(nativeRegion.Threw); // guards the assertions above from passing on a mutual failure
    }
#endif

    // EF-444 Task 4 — the one shape the whole-document read canNOT rescue, so the emit side declines it instead.
    // A COMPUTED leaf has no document path at all (it only ever exists as a value the $project would have
    // materialised), so once a whole-entity sibling forces the fallback legs onto whole documents there is
    // nothing to read: MEASURED as `Document element 'X' is missing but required` under DriverLinq before this
    // decline was added. Declining returns the shape to its pre-EF-444 routing (Route == Fallback → the mixed
    // shaper over EF's own ProjectionMapping), which is correct in every mode — the two oracle checks below are
    // that proof, and are the reason this is a narrowing of native coverage rather than a loss of function.
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
    public void Chain_scalar_leaf_beside_a_whole_entity_leaf_at_a_non_adjacent_chain_level_reads_correctly()
    {
        // Final-review Important-3, the chain-depth analogue of
        // Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly above. The unit-test twin
        // (NativeJoinScopeProjectionBinderTests.Binds_a_chain_scalar_leaf_mixed_with_a_whole_entity_leaf) proves
        // this shape STAGES correctly (NativeRoute.Projection) at the unit level only; this proves both the
        // NATIVE leg (now the default route for this shape) and the explicit DriverLinq opt-out fallback leg
        // read correct DATA. A whole-entity leaf (`e.o`, the root, scope 0) forces BOTH fallback legs onto a
        // whole-document read, so the chain-scalar sibling here deliberately names a NON-root, NON-adjacent
        // level (`l.Sku`, join #2's Inner, scope 2 — the middle scope 1, `r`, is skipped entirely) rather than
        // the root's own immediate join partner, which is the case that needed an end-to-end check.
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
        // Was LeftJoin_over_a_collection_navigation_still_declines_under_NativeOnly (EF-TBD: native
        // left-join-over-collection-Include support). Driving the join from the PRINCIPAL side
        // (Owners → Orders) resolves Owner.Orders, a COLLECTION navigation. IsNativelyEligible's
        // left-outer/collection conjunct used to decline this outright, because MongoSelectLowerer's
        // collection-ForceUnwind arm hard-coded preserveNullAndEmptyArrays: false — correct for an inner
        // Join, silently wrong for a LeftJoin (an Owner with no Order would be DROPPED instead of kept with
        // nulls). The lowerer now reads LookupExpression.PreserveNullAndEmptyArrays (threaded from
        // JoinInfo.IsLeftOuter at registration) instead, so the conjunct was removed and this shape is
        // admitted with correct left-outer semantics.
        //
        // FUTURE EDITORS — the projection body here is load-bearing and must stay ALL-OUTER and fully
        // translatable. An earlier version of this test used `Total = r == null ? (decimal?)null : r.Total`,
        // whose ConditionalExpression MongoExpressionTranslator has no support for at all — so
        // TryBindProjection declined on the LEAF, for a reason entirely unrelated to left-outer-ness. `new
        // { o.Name }` translates cleanly and so isolates the left-outer/collection behavior on its own.
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(LeftJoin_over_a_collection_navigation_now_goes_native_under_NativeOnly));

        // Succeeding under NativeOnly is itself the "went native" signal (Query/AGENTS.md) — a fallback shape
        // would throw NativeTranslationNotSupportedException, not silently return wrong or partial data.
        var result = db.Owners
            .LeftJoin(db.Orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name })
            .AsEnumerable()
            .Select(x => x.Name)
            .OrderBy(x => x)
            .ToList();

        // Spelled out rather than computed from an in-memory LINQ oracle: SeedOwnersAndOrdersWithUnmatchedRows
        // gives Alice exactly one Order and Bob none, so a correct LeftJoin is exactly one row each — the
        // Order-less owner (Bob) must survive with a null/absent navigation, not be silently dropped.
        Assert.Equal(["Alice", "Bob"], result);
    }

    [Fact]
    public void LeftJoin_unmatched_inner_row_reads_as_null_reference_navigation()
    {
        // EF-444 Task 3 (Task 0 spike, "Step 7"). Driven from the DEPENDENT side — Order.LeftJoin(Owner, ...) —
        // so the join resolves Order.Owner, a REFERENCE navigation. That is deliberately the mirror of
        // LeftJoin_over_a_collection_navigation_now_goes_native_under_NativeOnly above, which drives from the
        // PRINCIPAL side (Owner.LeftJoin(Order, ...)) and resolves the COLLECTION navigation Owner.Orders —
        // both spellings now go native with correct left-outer semantics, kept as separate pins.
        //
        // No new production code exists for this case — see the code comment at
        // MongoProjectionBindingRemovingExpressionVisitor's cross-collection arm (fieldRequired = false) for
        // the mechanism. In short: the cross-collection read arm already treats the joined field as NOT
        // required, and MongoSelectLowerer already emits preserveNullAndEmptyArrays: true for a left-outer
        // REFERENCE navigation — the same property the sibling test above pins for a left-outer COLLECTION
        // navigation. Together, a dangling-FK Order's Owner leaf reads as a plain null reference
        // — EF Core's own null-reference-navigation convention — with no exception and no partial entity. This
        // test pins that behavior permanently so a future editor doesn't add unnecessary null-handling code
        // believing it's missing.
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

        // Succeeding under NativeOnly is itself the "went native" signal (Query/AGENTS.md) — a fallback shape
        // would throw NativeTranslationNotSupportedException, not silently return wrong or partial data.
        Assert.Equal(expected, result);

        // Spelled out, not just via the oracle equality above: SeedOwnersAndOrdersWithUnmatchedRows has exactly
        // one dangling-FK Order (Total = 99m, OwnerId matching no seeded Owner), so this is the concrete
        // unmatched-row assertion the test exists to make.
        Assert.Null(result.Single(x => x.Total == 99m).OwnerName);

        // ...and the matched row still resolves its Owner correctly, so the assertion above isn't vacuous.
        Assert.Equal("Alice", result.Single(x => x.Total == 10m).OwnerName);
    }
#endif

    public static IEnumerable<object[]> JoinOracleCases()
    {
        // Each case is a Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<...>> so the SAME expression
        // tree runs against both the native-backed DbSet and the in-memory seed collection, following the
        // NativeOwnedCollectionAllTests.cs oracle pattern exactly. The trailing bool is `goesNativeUnderNativeOnly`:
        // whether THIS case is expected to succeed under MongoQueryMode.NativeOnly (proving it actually goes
        // native, per Task 5/6) or is expected to still decline there (falling back correctly under the
        // default Native mode only). See the theory body below for how each half is asserted.
        yield return
        [
            (Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<object>>)((owners, orders) =>
                owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total })),
            true
        ];
#if !EF8 && !EF9
        // Queryable.LeftJoin itself only dispatches on EF10 (EF-X020, per RequiredNavigationUnwindTests.cs) —
        // on EF8/EF9, EF Core's OWN NavigationExpandingExpressionVisitor throws "could not be translated"
        // for ANY LeftJoin call, before the expression ever reaches this (or any) provider. That is an EF
        // Core-version limitation, not a Mongo provider gap, so this case is compiled out rather than run
        // and expected to fail on EF8/EF9.
        //
        // This case's projection leaf is `Total = r == null ? (decimal?)null : r.Total` over Owner.Orders, a
        // COLLECTION navigation reached via LeftJoin — now natively eligible (EF-TBD: native
        // left-join-over-collection-Include support widened IsNativelyEligible to admit a left-outer
        // collection-navigation join, since MongoSelectLowerer now threads
        // LookupExpression.PreserveNullAndEmptyArrays through instead of hard-coding false).
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
        // Seed must include: an Owner with a matching Order, an Owner with NO Order (left-join/unmatched
        // case), and an Order whose OwnerId matches nothing (dangling FK, dropped by inner Join).
        //
        // EF-392 Task 6 update: the stale premise here — "Do NOT use MongoQueryMode.NativeOnly, it would
        // always throw for this shape, by design" — no longer holds for any case in JoinOracleCases (EF-TBD
        // widened native eligibility to left-outer collection-navigation joins too, so both cases now go
        // native). So this test now asserts the oracle match under the default Native mode UNCONDITIONALLY
        // (proving whichever path Native picks - fallback or native - is correct), and ADDITIONALLY, under
        // NativeOnly, either the oracle match again (when goesNativeUnderNativeOnly, proving native itself,
        // since NativeOnly can only succeed by translating natively - there is no silent fallback in that
        // mode) or the expected clean decline (when not — kept as a `bool` per-case rather than assumed, so a
        // future case that genuinely can't go native still has somewhere to say so).
        var seed = SeedOwnersAndOrdersWithUnmatchedRows();

        // dynamic is used below (rather than a typed lambda) because this method's result shape is erased to
        // IQueryable<object> by JoinOracleCases' shared delegate signature.
        var oracleResult = query(seed.Owners.AsQueryable(), seed.Orders.AsQueryable()).AsEnumerable()
            .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
            .ToList();

        using var dbNative = CreateContext(seed, MongoQueryMode.Native,
            nameof(Join_result_matches_in_memory_oracle_including_unmatched_rows) + "_native");

        // Ordered by a stable key before comparing — an unordered List<object> equality check would only
        // pass by coincidence of seed/pipeline ordering. Mirrors the OrderBy(x => x.Name).ThenBy(x => x.Total)
        // pattern used on both sides three methods up (Genuine_two_sided_join_returns_correct_results_via_fallback).
        var nativeModeResult = query(dbNative.Owners, dbNative.Orders).AsEnumerable()
            .OrderBy(x => ((dynamic)x).Name).ThenBy(x => ((dynamic)x).Total)
            .ToList();
        Assert.Equal(oracleResult, nativeModeResult);

        using var dbNativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Join_result_matches_in_memory_oracle_including_unmatched_rows) + "_nativeOnly");

        if (goesNativeUnderNativeOnly)
        {
            // NativeOnly never falls back silently — it either translates natively or throws. So a passing
            // result here is itself the proof this case reaches and completes native translation, not some
            // other decline path (see Task 6 brief's rigor requirement re: guard/test bugs earlier in this
            // ticket family).
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

    private sealed record Seed(Owner[] Owners, Order[] Orders, OrderLine[] OrderLines = default!);

    private static Seed SeedOwnersAndOrders()
    {
        // Rank is populated (non-null) here deliberately: the `Nullable<T>.Value` leaf case in
        // Whole_entity_leaf_beside_a_renamed_or_dotted_scalar_leaf_reads_correctly projects `x.o.Rank.Value`,
        // and its in-memory oracle would NullReferenceException on a null Rank. The NULL-Rank/absent-segment
        // behavior is covered separately by SeedOwnersAndOrdersWithUnmatchedRows.
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

    // Covers, in one fixture, the three row shapes a Join/LeftJoin differential must exercise: an Owner
    // with a matching Order (both cases include it), an Owner with NO Order (unmatched — dropped by inner
    // Join, kept with a null Total by LeftJoin), and an Order whose OwnerId matches no seeded Owner (a
    // dangling FK — dropped by inner Join and, since LeftJoin here is driven from the Owner side, also
    // absent from the LeftJoin result).
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

    // An Owner with NO Orders inserted FIRST, followed by an Owner with exactly one — the ordering is
    // load-bearing for First_after_a_confirmed_join_declines_cleanly_under_NativeOnly: it makes an un-gated
    // reducer's pre-$lookup {$limit: 1} keep the order-less owner, which the 1:N $unwind then drops entirely,
    // turning a wrong-row hazard into an observable empty result.
    private static Seed SeedOrderlessOwnerFirst()
    {
        var orderless = new Owner { Id = ObjectId.GenerateNewId(), Name = "Aaron", Region = "North" };
        var withOrder = new Owner { Id = ObjectId.GenerateNewId(), Name = "Zoe", Region = "South" };

        return new Seed(
            [orderless, withOrder],
            [new Order { Id = ObjectId.GenerateNewId(), OwnerId = withOrder.Id, Total = 5m, Region = "South" }],
            []);
    }

    // An Order with a DANGLING (unmatched) OwnerId inserted FIRST, followed by a genuinely matched Order/Owner
    // pair — the ordering is load-bearing for
    // Paging_after_a_confirmed_join_applies_to_the_joined_row_count_not_the_outer_one_under_NativeOnly: it makes
    // an un-gated pre-$lookup {$limit: 1} keep the dangling order (first in insertion order), which the join
    // then drops entirely, turning a wrong-row hazard into an observable empty result. Mirrors
    // SeedOrderlessOwnerFirst's own technique, applied to a paging hazard instead of a reducer one.
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

    // A three-source chain (OrderLine -> Order -> Owner) with a DANGLING Order.OwnerId — needed by
    // Chained_join_then_LeftJoin_unmatched_row_reads_a_scalar_leaf_at_the_second_level_under_NativeOnly, which
    // requires an unmatched row at the SECOND chain level driven over a REFERENCE navigation (Order.Owner, the
    // same safe left-outer shape LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path
    // pins at depth 1) — SeedOwnersAndOrdersWithUnmatchedRows only has two joinable collections, and
    // SeedOwnersOrdersAndLines has no dangling FK at all. Driving the equivalent chain over Owner.Orders (a
    // COLLECTION navigation) instead would exercise the UNRELATED single-join shape
    // LeftJoin_over_a_collection_navigation_now_goes_native_under_NativeOnly already pins, rather than the
    // chained shape this plan admits — deliberately avoided here.
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

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public string Region { get; set; } = "";

        // NULLABLE deliberately, and only used by
        // LeftJoin_unmatched_row_reads_a_dotted_scalar_leaf_through_the_whole_document_path: it is the only
        // property on either test entity whose IProperty.IsNullable is true, which is what selects the
        // "return default" arm of BsonBinding.GetPropertyValueAtPath's absent-intermediate-segment branch.
        // Region (non-nullable) selects the throwing arm of the same branch; the two are asserted together so
        // the branch is covered in both directions.
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

        // NULLABLE deliberately, same technique as Owner.Rank above: only used by
        // Chained_join_then_LeftJoin_unmatched_row_reads_a_scalar_leaf_at_the_second_level_under_NativeOnly, so
        // the absent-sub-document read for the unmatched row is asserted as null unambiguously, rather than
        // depending on Sku's own (unrelated) nullability disposition.
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
