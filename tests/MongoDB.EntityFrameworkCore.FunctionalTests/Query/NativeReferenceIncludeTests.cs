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
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-368 Task 5: the slice's actual capability. A single-level reference <c>Include</c>
/// (<c>Orders.Include(o => o.Buyer)</c>) now confirms the candidate reference-Include join Task 4 recorded
/// and registers a forced-unwind <see cref="Query.Expressions.LookupExpression"/>, which flips
/// <c>MongoQueryExpression.UsesDriverJoinFields</c> to <see langword="false"/> so the native lowerer, the DOM
/// shaper, and the driver-LINQ fallback all agree on the <c>_lookup_&lt;Nav&gt;</c> field. The <c>$unwind</c>
/// that follows is INNER (drops the row) for a REQUIRED navigation and LEFT-OUTER (keeps the row, nav null)
/// for an OPTIONAL one — <c>Buyer</c>/<c>BuyerId</c> below is required, <c>Carrier</c>/<c>CarrierId</c> is
/// optional. See <c>Task_5_report.md</c> for the full write-up.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferenceIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void User_join_is_not_admitted_by_the_candidate_join_signal()
    {
        // A user join with NO Include: nothing ever confirms, so the candidate signal alone must not
        // make this native. NativeOnly forbids the fallback, so a decline surfaces as a throw.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(User_join_is_not_admitted_by_the_candidate_join_signal));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.Join(db.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o).ToList());
    }

    [Fact]
    public void Required_reference_Include_goes_native_with_an_inner_unwind()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Required_reference_Include_goes_native_with_an_inner_unwind), out var spyLogger);

        var results = db.Orders.Include(o => o.Buyer).ToList();

        // O3's buyer is dangling and the navigation is REQUIRED, so the inner $unwind drops it: 4 orders
        // seeded, 1 with a dangling BuyerId, 3 remain.
        Assert.Equal(3, results.Count);
        Assert.All(results, o => Assert.NotNull(o.Buyer));

        AssertMql(spyLogger,
            "{ \"$lookup\" : { \"from\" : \"" + db.BuyersCollectionName +
            "\", \"localField\" : \"BuyerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Buyer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Buyer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    [Fact]
    public void Reference_Include_whose_target_owns_an_embedded_type_still_goes_native()
    {
        // EF-368 fix round 1 (I3). The reviewer measured that TryConfirmReferenceInclude's original
        // NavigationExpression-is-IncludeExpression guard was over-broad: EF auto-includes an owned
        // (embedded) navigation on the reference-Include's TARGET the exact same way it nests a real
        // ThenInclude, so the blanket decline silently narrowed the whole feature to targets with no owned
        // types at all. Buyer.Address (OwnsOne, seeded in CreateContext below) is exactly that shape.
        // Every OTHER test in this file already exercises this narrowed guard incidentally (Buyer is the
        // shared fixture entity), but this test makes the coverage intent explicit and asserts the owned
        // data itself materializes correctly through the confirmed native path.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Reference_Include_whose_target_owns_an_embedded_type_still_goes_native), out var spyLogger);

        var results = db.Orders.Include(o => o.Buyer).ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, o => Assert.NotNull(o.Buyer));
        Assert.All(results, o => Assert.Equal("Springfield", o.Buyer.Address.City));

        AssertMql(spyLogger,
            "{ \"$lookup\" : { \"from\" : \"" + db.BuyersCollectionName +
            "\", \"localField\" : \"BuyerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Buyer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Buyer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    [Fact]
    public void A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines()
    {
        // EF-368 fix round 2 (review finding B), CORRECTED in fix round 3 after two reviewers reached
        // opposite conclusions about whether this shape reaches HasNonEmbeddedThenInclude at all, and the
        // question was settled by direct instrumentation rather than by picking a side.
        //
        // Round 1's reviewer reported this shape (Address owned, Region a real cross-collection nav
        // ThenIncluded underneath it) was silently ADMITTED by round 1's guard, with Region left
        // unpopulated. Round 2's reviewer reported the opposite: that TryConfirmReferenceInclude — and so
        // HasNonEmbeddedThenInclude — is NEVER CALLED for this shape at all, in either version, because
        // adding a real nav (Region) makes EF's nav-expansion inject an ADDITIONAL join, which restructures
        // the OUTER (Buyer) IncludeExpression's own EntityExpression into a DOUBLE hop (ti.Outer.Outer) —
        // so IsSingleLevelReferenceIncludeSelector's PRE-EXISTING single-hop conjunct rejects the whole
        // shape before TryConfirmReferenceInclude is ever reached.
        //
        // ROUND 3 VERDICT, by direct instrumentation of IsSingleLevelReferenceIncludeSelector /
        // TryConfirmReferenceInclude / HasNonEmbeddedThenInclude and running this exact test: round 2 was
        // RIGHT. For this shape the log shows IsSingleLevelReferenceIncludeSelector logging
        // Navigation=Buyer, EntityExpression=o.Outer.Outer, and returning false — neither
        // TryConfirmReferenceInclude nor HasNonEmbeddedThenInclude is ever entered. So this test does NOT
        // discriminate the round-2 recursion fix (a round-1-only build passes it too, for the SAME reason,
        // since the single-hop conjunct — untouched by either round — is what declines it). It is kept
        // anyway as a plain decline tripwire for this shape (a real nav ThenIncluded under an embedded one
        // must keep failing loudly, not silently drop data), not as coverage for
        // HasNonEmbeddedThenInclude's own recursion, which remains defence-in-depth with no known-reachable
        // discriminating test — see that method's own corrected doc comment.
        //
        // Measured (round 2 review, still accurate): without EITHER round's fix, both Native and DriverLinq
        // agreed — neither threw, both returned the row with Region silently null. That agreement was NOT
        // evidence of correctness; it is pre-existing behavior this slice must not newly admit. This test
        // asserts the decline, not the silent-drop data shape.
        //
        // Exception TYPE deliberately not pinned: on EF10 this reaches IsSingleLevelReferenceIncludeSelector
        // returning false, which falls through to the ordinary post-terminal projection-binder path and
        // declines there; on EF8/EF9 the identical LINQ shape fails upstream, inside EF Core's own
        // translation visitor, with a plain InvalidOperationException ("could not be translated") before
        // this provider's Include machinery is even reached — measured, not assumed. Both are a clean
        // decline (no row returned, no silent data loss); per this repo's versioning rubric the exception
        // type of an unsupported shape is not part of the contract, so this asserts ThrowsAny rather than a
        // specific type.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines));

        Assert.ThrowsAny<Exception>(
            () => db.Orders.Include(o => o.Buyer).ThenInclude(b => b.Address).ThenInclude(a => a.Region).ToList());
    }

    /// <summary>
    /// EF-368 Task 6: every decline in <c>TryConfirmReferenceInclude</c> must be a decline, not a silent
    /// pass (design §5.3). Each row here asserts BOTH halves: <see cref="MongoQueryMode.NativeOnly"/>
    /// throws (proving the shape actually declines rather than silently going native), and
    /// <see cref="MongoQueryMode.Native"/> returns the SAME rows — by count AND by value, not merely by
    /// count — as <see cref="MongoQueryMode.DriverLinq"/> (proving the fallback is correct).
    /// </summary>
    // Theory data is just the description string, not the query-builder Func itself: a Func closing over
    // the PRIVATE nested ReferenceIncludeDbContext type can't appear in a public [Theory] method's
    // signature (CS0050/CS0051, inconsistent accessibility) — GetDeclinedShapeBuilder below resolves the
    // description back to its builder from inside the (private-type-using, but not publicly-signatured)
    // test body instead.
    public static TheoryData<string> DeclinedShapeDescriptions => new()
    {
        "sibling reference Includes",
        "same-target sibling Includes",
        "ThenInclude / transitive",
        "after a terminal",
        "reference + collection",
        // THE LOAD-BEARING ROW. A user-authored join with a downstream Include produces a trailing
        // IncludeExpression whose EntityExpression is ti.Outer.Outer - a DOUBLE hop, confirmed by direct
        // instrumentation (see the GetDeclinedShapeBuilder comment on this row below and task-6-report.md's
        // fix-round-1 section for the mutation evidence, INCLUDING a correction to what was originally
        // claimed here about which guard is load-bearing for this exact reachable shape).
        "user join with downstream Include",
        // "composite FK/PK" is NOT in this list — see Composite_FK_and_PK_still_declines below. Measured:
        // the driver's own LINQ v3 provider cannot translate ANY Join/Include over a composite key at all
        // (ExpressionNotSupportedException, "cannot be translated to a dotted field name"), so there is no
        // working DriverLinq oracle for this shape to compare Native against — a different disposition
        // from every other row here, which is why it gets its own test instead of a row in this theory.
    };

    private static Func<ReferenceIncludeDbContext, IQueryable> GetDeclinedShapeBuilder(string description)
        => description switch
        {
            "sibling reference Includes" => db => db.Lines.Include(l => l.Order).Include(l => l.Product),
            // NO trailing .Select here — fix round 1 review (2026-08-04) measured that a trailing scalar
            // Select on this shape and on "user join with downstream Include" below produced a DEAD test:
            // EF Core's own nav-expansion drops the pending Include entirely once a trailing scalar Select
            // doesn't reference it, so both rows threw the GENERIC bare-scalar-projection decline ("Query
            // projects a non-entity result") under NativeOnly — identical whether or not the Include was
            // even present — rather than the shape's own decline. The whole-entity form below is what
            // actually reaches the recognizer/guard machinery.
            //
            // MUTATION EVIDENCE (fix round 1, full trace in task-6-report.md): this row's real EF-compiled
            // tree is a NESTED IncludeExpression — selector.Body is IncludeExpression(Navigation: Editor,
            // EntityExpression: IncludeExpression(Navigation: Author, ...)) — NOT a ti.Outer.Outer
            // double-hop MemberExpression chain (that shape belongs to the "user join with downstream
            // Include" row below; sibling Includes produce a DIFFERENT tree shape). Direct instrumentation
            // confirms IsSingleLevelReferenceIncludeSelector's `include.EntityExpression is MemberExpression`
            // pattern match fails outright on this nested-IncludeExpression EntityExpression (Result=False,
            // logged before TryConfirmReferenceInclude is ever reached) — this structural type check, not
            // the hop-depth `outerAccess.Expression == selector.Parameters[0]` conjunct, is what actually
            // declines THIS row, and it does so ALONE: with the candidate/confirmed counter (Task 4)
            // separately neutralized (HasUnconfirmedCandidateJoin mutated to return false unconditionally),
            // the row still declined correctly, because the recognizer's structural mismatch means
            // TryConfirmReferenceInclude — and so MarkReferenceIncludeConfirmed — is never called for the
            // nested (Author) Include at all. Only loosening the recognizer to ALSO accept a nested
            // IncludeExpression as EntityExpression, WHILE ALSO neutralizing the counter, admitted the
            // shape — and it then produced SILENT WRONG DATA (Author materialized null while Editor
            // materialized correctly), because the inner (Author) Include is structurally discarded the
            // moment only the outer (Editor) Include is confirmed. The ForceUnwind-pending-lookup guard
            // this task's brief named (GetPendingLookups().Any(l => l.ForceUnwind), the line commented
            // "second reference Include, incl. same-target") is NOT what protects this row — verified by
            // restoring it while the recognizer and counter mutations stayed in place: the shape still
            // wrongly admitted, because TryConfirmReferenceInclude is called only ONCE (for the outer
            // Editor Include) and GetPendingLookups() is still empty at that point, so the guard never
            // fires. So this row is doubly protected by two DIFFERENT mechanisms than the brief assumed —
            // the recognizer's structural EntityExpression-type check (sufficient alone) and the
            // candidate/confirmed counter (sufficient alone, once the recognizer is defeated) — and NOT by
            // the ForceUnwind guard at all for this exact tree shape.
            "same-target sibling Includes" => db => db.Docs.Include(d => d.Author).Include(d => d.Editor),
            "ThenInclude / transitive" => db => db.Lines.Include(l => l.Order).ThenInclude(o => o.Buyer),
            "after a terminal" => db => db.Orders.Distinct().Include(o => o.Buyer),
            "reference + collection" => db => db.Orders.Include(o => o.Buyer).Include(o => o.Lines),
            // NO trailing .Select here either — see the comment on "same-target sibling Includes" above;
            // this is the SAME dead-test defect, on the load-bearing row itself.
            //
            // MUTATION EVIDENCE (fix round 1, full trace in task-6-report.md): direct instrumentation
            // confirms this row's EntityExpression genuinely IS ti.Outer.Outer (a double hop), and that
            // loosening IsSingleLevelReferenceIncludeSelector's hop-depth conjunct alone (removing
            // `outerAccess.Expression == selector.Parameters[0]`) does NOT admit the shape — NativeOnly
            // still threw "Query is not natively representable" unchanged. That is because this exact
            // query independently trips the candidate/confirmed counter (Task 4): the translated tree
            // contains TWO Join nodes (the user's own explicit Join, plus the nav-expansion's own synthesized
            // join for Include(Buyer)) — both against the SAME target type, Buyer — so
            // MarkSawCandidateReferenceIncludeJoin fires twice while at most one MarkReferenceIncludeConfirmed
            // can fire, leaving HasUnconfirmedCandidateJoin true regardless of the recognizer. Defeating
            // BOTH the hop-depth conjunct AND the counter together (mutation-verified) admits the shape as
            // native — but for THIS query, where the join and the Include target the SAME entity (Buyer)
            // and Buyer's FK (BuyerId) lives on the true document root regardless of accessor depth, the
            // wrongly-admitted native pipeline still happened to return the correct 3 rows with correctly
            // resolved Buyer navigations. A SEPARATE probe confirmed the danger the recognizer's own doc
            // comment describes IS real for a shape where the user's join targets a DIFFERENT entity than
            // the Include (e.g. joining Carriers then Include(Buyer)) — but that shape is independently
            // declined by InnerCollections.Count != 1 (Carrier and Buyer are different entity types, so
            // they do NOT collapse into one InnerCollections entry), even with both other guards defeated.
            // So for the BRIEF'S LITERAL query the hop-depth conjunct's own unique contribution could not be
            // isolated from the candidate/confirmed counter — the two are genuinely redundant for this
            // reachable shape, a correction to this row's original claim that the conjunct is uniquely
            // "load-bearing, not defence-in-depth" here. The row is kept: it still proves EF's real
            // ti.Outer.Outer tree declines cleanly (the brief's core requirement), and it is not a pure
            // duplicate of Two_joins_onto_the_same_target_stay_declined above (a structurally different
            // tree — Include-then-Join vs. this row's Join-then-Include) even though both are ultimately
            // caught by the same candidate/confirmed counter.
            "user join with downstream Include" =>
                db => db.Orders.Join(db.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o).Include(o => o.Buyer),
            _ => throw new ArgumentOutOfRangeException(nameof(description), description, "Unknown declined shape.")
        };

    [Theory]
    [MemberData(nameof(DeclinedShapeDescriptions))]
    public void Declined_shapes_throw_under_NativeOnly_and_match_DriverLinq_under_Native(string description)
    {
        var build = GetDeclinedShapeBuilder(description);
        var testName = nameof(Declined_shapes_throw_under_NativeOnly_and_match_DriverLinq_under_Native) + "_"
            + description.Replace(" ", "_").Replace("/", "_");

        using (var nativeOnly = CreateContext(MongoQueryMode.NativeOnly, testName + "_NativeOnly"))
        {
            var ex = Assert.Throws<NativeTranslationNotSupportedException>(
                () => build(nativeOnly).Cast<object>().ToList());

            // Fix round 1 (2026-08-04 review): every row in this theory is a WHOLE-ENTITY Include shape
            // that IsSingleLevelReferenceIncludeSelector/TryConfirmReferenceInclude either recognizes-and-
            // declines or never recognizes at all — both routes call MarkNotNativelyRepresentable(), which
            // resolves Route to Fallback and throws THIS message
            // (MongoShapedQueryCompilingExpressionVisitor's generic Route==Fallback guard) — "Query is not
            // natively representable...". A DIFFERENT, unrelated decline exists for an out-of-scope
            // PROJECTED query ("Query projects a non-entity result...") — reached only when a trailing
            // Select populates Route.Projection. Pinning THIS substring is what proves NativeOnly threw
            // because the recognizer/guard declined the Include shape itself, not because of an incidental
            // trailing projection (the earlier, dead version of two of these rows carried a trailing
            // .Select and threw the WRONG one of these two messages without anyone noticing — see the
            // GetDeclinedShapeBuilder comments on "same-target sibling Includes" and "user join with
            // downstream Include").
            Assert.Contains("Query is not natively representable", ex.Message);
        }

        if (HasNoDriverLinqParityOracle(description))
        {
            // Fix round 1: for these two rows, a WHOLE-ENTITY Native == DriverLinq comparison hits a
            // SEPARATE, pre-existing driver-LINQ bug materializing a chained-join whole-entity result
            // (InvalidOperationException, "Document element is missing for required non-nullable property
            // 'Id'" — identical symptom to the one Two_joins_onto_the_same_target_stay_declined documents
            // and works around by projecting to a scalar) — reproduced with NO Include and NO EF-368 code
            // involved at all. Round 1 tried the scalar-projection workaround here too and found it made
            // the NativeOnly half test the WRONG decline (see the GetDeclinedShapeBuilder comments above) —
            // so for these two rows only the NativeOnly-throws-for-the-right-reason half above is asserted;
            // the Native == DriverLinq parity half is genuinely untestable for this shape and is documented
            // rather than faked with a workaround that defeats the row's own purpose.
            return;
        }

        using var native = CreateContext(MongoQueryMode.Native, testName + "_Native");
        using var driverLinq = CreateContext(MongoQueryMode.DriverLinq, testName + "_DriverLinq");

        var nativeRows = build(native).Cast<object>().ToList();
        var driverRows = build(driverLinq).Cast<object>().ToList();

        // Each mode's CreateContext seeds its OWN collection with freshly-generated ObjectIds, so rows
        // can only be compared by VALUE (a stable, seed-independent scalar per entity), never by identity
        // or raw entity equality. Canonicalize extracts exactly that, and both sides are sorted before
        // comparing so ordering differences between the two independently-executed queries don't matter.
        var nativeCanonical = nativeRows.Select(Canonicalize).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var driverCanonical = driverRows.Select(Canonicalize).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(driverRows.Count, nativeRows.Count);
        Assert.Equal(driverCanonical, nativeCanonical);
        Assert.NotEmpty(nativeCanonical); // guard against a vacuous pass from two empty result sets
    }

    // See the comment on the HasNoDriverLinqParityOracle branch above for why these two, specifically,
    // cannot run the Native == DriverLinq parity half.
    private static bool HasNoDriverLinqParityOracle(string description)
        => description is "same-target sibling Includes" or "user join with downstream Include";

    // Extracts a stable, seed-independent scalar identity for a row so two independently-seeded
    // collections (one per MongoQueryMode, per CreateContext's own doc comment) can be compared by VALUE
    // rather than by ObjectId, which differs across contexts even for "the same" seeded row.
    private static string Canonicalize(object entity) => entity switch
    {
        Order o => $"Order:{o.Total}",
        Line l => $"Line:{l.Quantity}",
        Doc d => $"Doc:{d.Title}",
        CompositeLine cl => $"CompositeLine:{cl.Quantity}",
        decimal total => $"Total:{total}",
        string title => $"Title:{title}",
        _ => throw new NotSupportedException($"No canonical form registered for {entity.GetType()}.")
    };

    /// <summary>
    /// EF-368 Task 6: the composite-FK/composite-PK decline (a composite principal key always implies a
    /// matching composite FK on the dependent side, so one guard check covers both). Unlike every row in
    /// <see cref="DeclinedShapeDescriptions"/>, this shape has NO working driver-LINQ oracle to compare
    /// against — <c>db.CompositeLines.Include(l => l.Order)</c> throws
    /// <c>MongoDB.Driver.Linq.ExpressionNotSupportedException</c> ("cannot be translated to a dotted field
    /// name") under explicit <see cref="MongoQueryMode.DriverLinq"/> too, i.e. the driver's own LINQ v3
    /// provider cannot translate a composite-key <c>Join</c>/<c>Include</c> at all. So the correct,
    /// provable claim for this shape is narrower than "Native matches DriverLinq": it is "every mode
    /// throws, none silently drops the FK correlation or returns wrong data".
    /// </summary>
    [Fact]
    public void Composite_FK_and_PK_still_declines_and_has_no_driver_linq_oracle()
    {
        using (var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
                   nameof(Composite_FK_and_PK_still_declines_and_has_no_driver_linq_oracle) + "_NativeOnly"))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.CompositeLines.Include(l => l.Order).ToList());
        }

        using var native = CreateContext(MongoQueryMode.Native,
            nameof(Composite_FK_and_PK_still_declines_and_has_no_driver_linq_oracle) + "_Native");
        Assert.ThrowsAny<Exception>(() => native.CompositeLines.Include(l => l.Order).ToList());

        using var driverLinq = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Composite_FK_and_PK_still_declines_and_has_no_driver_linq_oracle) + "_DriverLinq");
        Assert.ThrowsAny<Exception>(() => driverLinq.CompositeLines.Include(l => l.Order).ToList());
    }

    /// <summary>
    /// EF-368 Task 5 fix, predating this task's brief: a <c>HasQueryFilter</c> on the reference-Include's
    /// TARGET must decline. Without this guard the query returned 830 rows where 80 was correct (measured
    /// against <c>NorthwindQueryFiltersQueryMongoTest.Included_many_to_one_query</c>) — a plain
    /// <c>$lookup</c> has no way to apply the inner-side predicate, so admitting it is silent wrong data,
    /// not merely a missed optimization. Needs its OWN model (a query filter on Buyer would change every
    /// other test in this file), so it is a standalone test rather than a
    /// <see cref="DeclinedShapeDescriptions"/> row.
    /// </summary>
    [Fact]
    public void Query_filter_on_the_included_target_still_declines()
    {
        using (var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
                   nameof(Query_filter_on_the_included_target_still_declines) + "_NativeOnly", buyerQueryFilter: true))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Orders.Include(o => o.Buyer).ToList());
        }

        // THE ROW-COUNT PROOF THIS TEST USED TO CARRY IS NO LONGER AVAILABLE, and that is a driver change
        // rather than a provider one. It used to decline native and then execute on driver-LINQ, so it could
        // assert the FILTERED row count and catch the guard's removal as "830 rows where 80 is correct".
        // Driver 3.11 rejects a join whose inner is a filtered sub-query outright (EF-X022), so the shape now
        // hard-fails in BOTH fallback-capable modes and there is no row set left to count.
        //
        // What still holds, and is what matters: the filter is never SILENTLY IGNORED. Every route either
        // declines or throws - none returns unfiltered rows.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(mode,
                nameof(Query_filter_on_the_included_target_still_declines) + "_" + mode, buyerQueryFilter: true);

            Assert.Throws<ExpressionNotSupportedException>(() => db.Orders.Include(o => o.Buyer).ToList());
        }

        // Non-vacuity control: the same Include over the same seed WITHOUT the query filter still runs
        // natively and returns rows. So the failure above is specific to the filter, not a blanket
        // inability to translate this Include - which is what a reader would otherwise have to assume.
        using var unfiltered = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Query_filter_on_the_included_target_still_declines) + "_Unfiltered", buyerQueryFilter: false);
        Assert.NotEmpty(unfiltered.Orders.Include(o => o.Buyer).ToList());
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    //  EF-368 final fix wave, Finding 1 — the two query-filter routes the metadata guard MISSED
    // ════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The guard this replaced read `navigation.TargetEntityType.GetQueryFilter() != null`, which sees only
    // the target's OWN ANONYMOUS filter. Two reachable routes slipped past it, and each returned SILENTLY
    // WRONG rows in EVERY query mode (Native, DriverLinq and NativeOnly alike — the ForceUnwind lookup is
    // registered at translation time, so StripJoinForLookup strips the filter's own Where along with the
    // Join on the fallback path too; UseQueryMode(DriverLinq) is NOT an escape hatch):
    //
    //   (a) A filter INHERITED from the root of a TPH hierarchy, where the Include target is a DERIVED type.
    //       GetQueryFilter() on the derived type returns null on all three majors.
    //   (b) An EF10 NAMED query filter (HasQueryFilter("soft", …)), which lives in GetDeclaredQueryFilters()
    //       while GetQueryFilter() returns null.
    //
    // Both are now closed STRUCTURALLY, by MongoSelectDefinition.IsBareCollectionScan applied to the join's
    // INNER select in TranslateJoinCore: however the filter is spelled in metadata, EF applies it as a Where
    // on the join's inner sequence, so an inner that is not a bare collection scan declines.
    //
    // Each test below asserts BOTH halves of the disposition: NativeOnly DECLINES (proving the shape is not
    // admitted), and Native returns the SAME rows as DriverLinq (proving the fallback is what runs and that
    // it is right). The row COUNT assertion is what makes them discriminating — with the guard reverted both
    // return 2 rows where 1 is correct.

    [Fact]
    public void Query_filter_inherited_from_a_TPH_root_on_the_included_target_declines()
    {
        var (tickets, parties) = FilteredTargetCollections(
            nameof(Query_filter_inherited_from_a_TPH_root_on_the_included_target_declines));
        SeedTphFilterModel(tickets, parties);

        // The row-count mutation proof this test used to carry ("2 where 1 is correct") is no longer
        // available: driver 3.11 rejects a join over a filtered inner sub-query outright (EF-X022), so the
        // shape hard-fails in both fallback-capable modes instead of executing. See the sibling
        // Query_filter_on_the_included_target_still_declines for the full note. What is still pinned is that
        // a TPH-ROOT-INHERITED filter is never silently dropped - the route the old metadata guard missed.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = new TphFilterDbContext(database, tickets, parties, mode);
            Assert.Throws<ExpressionNotSupportedException>(() => db.Tickets.Include(t => t.Owner).ToList());
        }

        using var nativeOnly = new TphFilterDbContext(database, tickets, parties, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Tickets.Include(t => t.Owner).ToList());
    }

#if !EF8 && !EF9
    [Fact]
    public void Named_query_filter_on_the_included_target_declines()
    {
        var (cards, members) = FilteredTargetCollections(nameof(Named_query_filter_on_the_included_target_declines));
        SeedNamedFilterModel(cards, members);

        // Row-count proof no longer available under driver 3.11, same as the TPH test above (EF-X022). What
        // is still pinned is that an EF10 NAMED filter - the second route the old metadata guard missed,
        // since it lives in GetDeclaredQueryFilters() rather than GetQueryFilter() - is never silently
        // dropped.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = new NamedFilterDbContext(database, cards, members, mode);
            Assert.Throws<ExpressionNotSupportedException>(() => db.Cards.Include(c => c.Member).ToList());
        }

        using var nativeOnly = new NamedFilterDbContext(database, cards, members, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Cards.Include(c => c.Member).ToList());
    }
#endif

    private static (string Root, string Target) FilteredTargetCollections(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "F" + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "T" + suffix);
    }

    // Seeded through EF (not the raw driver) so the TPH discriminator is written by the provider's own
    // convention rather than hand-guessed here. Query filters do not apply to SaveChanges, so both parties
    // — including the soft-deleted one — really are stored.
    private void SeedTphFilterModel(string ticketsCollection, string partiesCollection)
    {
        using var seed = new TphFilterDbContext(database, ticketsCollection, partiesCollection, MongoQueryMode.DriverLinq);
        var live = new VipParty { Id = ObjectId.GenerateNewId(), Name = "Live", IsDeleted = false };
        var deleted = new VipParty { Id = ObjectId.GenerateNewId(), Name = "Gone", IsDeleted = true };
        seed.Parties.AddRange(live, deleted);
        seed.Tickets.AddRange(
            new Ticket { Id = ObjectId.GenerateNewId(), OwnerId = live.Id, Code = "live" },
            new Ticket { Id = ObjectId.GenerateNewId(), OwnerId = deleted.Id, Code = "dead" });
        seed.SaveChanges();
    }

#if !EF8 && !EF9
    private void SeedNamedFilterModel(string cardsCollection, string membersCollection)
    {
        using var seed = new NamedFilterDbContext(database, cardsCollection, membersCollection, MongoQueryMode.DriverLinq);
        var live = new Member { Id = ObjectId.GenerateNewId(), Name = "Live", IsDeleted = false };
        var deleted = new Member { Id = ObjectId.GenerateNewId(), Name = "Gone", IsDeleted = true };
        seed.Members.AddRange(live, deleted);
        seed.Cards.AddRange(
            new Card { Id = ObjectId.GenerateNewId(), MemberId = live.Id, Code = "live" },
            new Card { Id = ObjectId.GenerateNewId(), MemberId = deleted.Id, Code = "dead" });
        seed.SaveChanges();
    }
#endif

    // TPH root that DECLARES the query filter; VipParty (the Include target) inherits it, and
    // VipParty.GetQueryFilter() returns null — the gap Finding 1 closed.
    private class Party
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsDeleted { get; set; }
    }

    private class VipParty : Party;

    private class Ticket
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public string Code { get; set; } = "";
        public VipParty Owner { get; set; } = null!;
    }

    private class TphFilterDbContext : DbContext
    {
        private readonly string _ticketsCollection;
        private readonly string _partiesCollection;

        public DbSet<Ticket> Tickets { get; set; } = null!;
        public DbSet<Party> Parties { get; set; } = null!;

        public TphFilterDbContext(
            TemporaryDatabaseFixture database, string ticketsCollection, string partiesCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<TphFilterDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ticketsCollection = ticketsCollection;
            _partiesCollection = partiesCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Party>().ToCollection(_partiesCollection);
            // The filter is declared on the TPH ROOT only — EF forbids declaring one on a derived type.
            modelBuilder.Entity<Party>().HasQueryFilter(p => !p.IsDeleted);
            modelBuilder.Entity<VipParty>();

            modelBuilder.Entity<Ticket>(b =>
            {
                b.ToCollection(_ticketsCollection);
                b.HasOne(t => t.Owner).WithMany().HasForeignKey(t => t.OwnerId).IsRequired();
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

#if !EF8 && !EF9
    // EF10 NAMED query filter: GetQueryFilter() returns null for this shape while GetDeclaredQueryFilters()
    // holds it — the second gap Finding 1 closed.
    private class Member
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsDeleted { get; set; }
    }

    private class Card
    {
        public ObjectId Id { get; set; }
        public ObjectId MemberId { get; set; }
        public string Code { get; set; } = "";
        public Member Member { get; set; } = null!;
    }

    private class NamedFilterDbContext : DbContext
    {
        private readonly string _cardsCollection;
        private readonly string _membersCollection;

        public DbSet<Card> Cards { get; set; } = null!;
        public DbSet<Member> Members { get; set; } = null!;

        public NamedFilterDbContext(
            TemporaryDatabaseFixture database, string cardsCollection, string membersCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<NamedFilterDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _cardsCollection = cardsCollection;
            _membersCollection = membersCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Member>(b =>
            {
                b.ToCollection(_membersCollection);
                b.HasQueryFilter("soft", m => !m.IsDeleted);
            });

            modelBuilder.Entity<Card>(b =>
            {
                b.ToCollection(_cardsCollection);
                b.HasOne(c => c.Member).WithMany().HasForeignKey(c => c.MemberId).IsRequired();
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
#endif

    [Fact]
    public void Composed_Where_stays_ahead_of_the_lookup()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Composed_Where_stays_ahead_of_the_lookup), out var spyLogger);

        var results = db.Orders.Where(o => o.Total > 10).Include(o => o.Buyer).ToList();

        Assert.NotEmpty(results);
        // $match BEFORE $lookup: filter/sort/paging push ahead of the join (design §6).
        AssertMql(spyLogger,
            "{ \"$match\" : { \"Total\" : { \"$gt\" : { \"$numberDecimal\" : \"10\" } } } }, " +
            "{ \"$lookup\" : { \"from\" : \"" + db.BuyersCollectionName +
            "\", \"localField\" : \"BuyerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Buyer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Buyer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    /// <summary>
    /// EF-368 Task 7: reference Include on a UNIDIRECTIONAL model: <see cref="StreamingEligibility.IsEligible"/>
    /// admits the root (no inverse collection on the target), so this exercises the one-pass STREAMING
    /// materializer's <c>LookupReferencePlan</c> rather than the DOM shaper every other test in this file
    /// exercises (Buyer carries an inverse <c>Orders</c> collection, which makes Order streaming-ineligible).
    /// Both materializers read <c>_lookup_&lt;Nav&gt;</c>, so a test that only covers the bidirectional case
    /// would leave streaming untested while looking covered.
    /// <para>
    /// Fix round 1 (reviewer finding): <c>MongoShapedQueryCompilingExpressionVisitor.CompileShapedQuery</c>
    /// gates streaming on TWO independent conditions —
    /// <c>StreamingEligibility.IsEligible(rootEntityType) &amp;&amp; AllPendingLookupsAreStreamable(mongoQueryExpression)</c>
    /// — and asserting only the first (as this test originally did) verifies a necessary precondition, not
    /// the actual routing decision. <c>AllPendingLookupsAreStreamable</c> itself checks, per pending lookup,
    /// <c>lookup.IsStreamableReference &amp;&amp; !lookup.Navigation.TargetEntityType.GetNavigations().Any(n => n.IsEagerLoaded)</c>
    /// — but the <c>LookupExpression</c> instance registered for THIS compiled query is internal
    /// compile-time state with no functional-test seam to read it back directly. So both constituent facts
    /// are asserted by the closest available proxy instead of inferred from row correctness (which both
    /// shapers satisfy identically): the eager-load fact directly via public <c>IEntityType</c> metadata
    /// (the same kind of static check <c>IsEligible</c> itself is), and the <c>IsStreamableReference</c> fact
    /// structurally, via the actual emitted MQL (the same <c>AssertMql</c> idiom this whole file already
    /// uses to pin lookup shape) — <c>IsStreamableReference</c> is exactly "a reference nav, no filtered-
    /// Include pipeline, not a transitive <c>_lookup_</c> local field", which is precisely what a plain,
    /// unprefixed, non-piped <c>$lookup</c>/<c>$unwind</c> pair in the executed pipeline proves.
    /// </para>
    /// <para>
    /// See <see cref="Reference_Include_whose_target_has_an_eager_loaded_navigation_still_returns_correct_rows_via_the_DOM_shaper"/>
    /// for the mutation-proof companion: a model where gate 1 (<c>IsEligible</c>) is STILL true but gate 2's
    /// eager-load fact is now true, demonstrating that gate 1 alone cannot tell the two shapes apart even
    /// though only one of them can stream. See design §6.3.
    /// </para>
    /// </summary>
    [Fact]
    public void Reference_Include_on_a_unidirectional_model_uses_the_streaming_materializer()
    {
        using var db = UnidirectionalContext(out var spyLogger);

        // Gate 1: StreamingEligibility.IsEligible(root).
        var rootEntityType = db.Model.FindEntityType(typeof(UniOrder))!;
        Assert.True(
            StreamingEligibility.IsEligible(rootEntityType),
            "UniOrder must be streaming-eligible (a unidirectional model - UniCustomer carries no inverse " +
            "collection navigation back to UniOrder) or this test does not exercise the streaming " +
            "materializer's LookupReferencePlan at all.");

        // Gate 2, eager-load fact: AllPendingLookupsAreStreamable additionally requires the looked-up
        // entity to carry NO eager-loaded navigation of its own.
        var targetEntityType = db.Model.FindEntityType(typeof(UniCustomer))!;
        Assert.False(
            targetEntityType.GetNavigations().Any(n => n.IsEagerLoaded),
            "UniCustomer must carry no eager-loaded navigation, or AllPendingLookupsAreStreamable's " +
            "second (unchecked-by-IsEligible-alone) condition would route this query to the DOM shaper " +
            "instead of streaming, regardless of gate 1.");

        var orders = db.UniOrders.Include(o => o.UniCustomer).ToList();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.NotNull(o.UniCustomer));

        // Gate 2, IsStreamableReference fact: structurally, a plain unprefixed non-piped $lookup/$unwind
        // pair is exactly what IsStreamableReference computes (IsReference && !HasPipeline &&
        // !LocalField.StartsWith(_lookup_ prefix)) - the same AssertMql idiom this file already uses
        // elsewhere to pin lookup shape.
        AssertMql(spyLogger,
            "{ \"$lookup\" : { \"from\" : \"" + db.CustomersCollectionName +
            "\", \"localField\" : \"UniCustomerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_UniCustomer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_UniCustomer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    /// <summary>
    /// EF-368 Task 7 fix round 1 (mutation-proof companion, reviewer finding). A SECOND unidirectional model
    /// variant where the looked-up entity (<c>UniCustomerWithAddress</c>) owns an embedded sub-document
    /// (<c>Address</c>, via <c>OwnsOne</c> - the same idiom <c>Buyer.Address</c> uses elsewhere in this file).
    /// An owned reference navigation is always eager-loaded by EF Core convention, so
    /// <c>targetEntityType.GetNavigations().Any(n => n.IsEagerLoaded)</c> is TRUE here - <c>gate 2</c>'s
    /// eager-load fact fails - even though <see cref="StreamingEligibility.IsEligible"/> for the ROOT is
    /// still TRUE (an owned reference is itself streaming-eligible; <c>IsEligible</c>'s recursive walk has
    /// no eager-load check at all). This is the exact divergence the fix round 1 review named: gate 1 alone
    /// cannot distinguish this shape from the genuinely-streaming one above, and the query still returns
    /// CORRECT ROWS via the (silently substituted) DOM shaper either way - so row correctness proves nothing
    /// about which materializer actually ran, which is why the streaming test above must assert gate 2
    /// directly rather than infer routing from output.
    /// <para>
    /// MUTATION PROOF (fix round 1): temporarily asserting <c>Assert.False(... IsEagerLoaded)</c> here (the
    /// same assertion the passing companion test above makes, applied to THIS model) fails with "Assert.False()
    /// Failure" - proving the eager-load assertion actually discriminates the two shapes rather than passing
    /// vacuously on both. See task-7-report.md's fix-round-1 section for the captured failure output.
    /// </para>
    /// </summary>
    [Fact]
    public void Reference_Include_whose_target_has_an_eager_loaded_navigation_still_returns_correct_rows_via_the_DOM_shaper()
    {
        using var db = UnidirectionalContextWithEagerLoadedTarget();

        // Gate 1 (IsEligible) is satisfied - an owned reference is itself streaming-eligible.
        var rootEntityType = db.Model.FindEntityType(typeof(UniOrderWithEagerTarget))!;
        Assert.True(
            StreamingEligibility.IsEligible(rootEntityType),
            "This model must stay IsEligible == true, or it no longer demonstrates that gate 1 alone is " +
            "insufficient to decide routing.");

        // Gate 2's eager-load fact FAILS here - this is the point of this test. An owned Address on the
        // looked-up entity is always eager-loaded by convention.
        var targetEntityType = db.Model.FindEntityType(typeof(UniCustomerWithAddress))!;
        Assert.True(
            targetEntityType.GetNavigations().Any(n => n.IsEagerLoaded),
            "UniCustomerWithAddress's owned Address must be eager-loaded, or this model no longer " +
            "exercises AllPendingLookupsAreStreamable's second, IsEligible-blind condition.");

        // Despite gate 2 failing (so the query is native but NOT streaming - the DOM shaper materializes
        // it instead), the query still returns fully correct rows: this is exactly why row correctness
        // cannot be used to infer which materializer ran.
        var orders = db.UniOrdersWithEagerTarget.Include(o => o.UniCustomerWithAddress).ToList();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.NotNull(o.UniCustomerWithAddress));
        Assert.All(orders, o => Assert.Equal("Springfield", o.UniCustomerWithAddress.Address.City));
    }

#if !EF8 && !EF9
    [Fact]
    public void Optional_reference_Include_goes_native_with_a_left_outer_unwind()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Optional_reference_Include_goes_native_with_a_left_outer_unwind), out var spyLogger);

        var results = db.Orders.Include(o => o.Carrier).ToList();

        // Left-outer: rows with no FK and rows with a DANGLING FK both survive, navigation null. All 4
        // seeded orders survive.
        Assert.Equal(4, results.Count);
        Assert.Contains(results, o => o.Carrier == null);

        AssertMql(spyLogger,
            "{ \"$lookup\" : { \"from\" : \"" + db.CarriersCollectionName +
            "\", \"localField\" : \"CarrierId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Carrier\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Carrier\", \"preserveNullAndEmptyArrays\" : true } }");
    }
#endif

    [Fact]
    public void Two_joins_onto_the_same_target_stay_declined()
    {
        // EF-368 Task 4 review finding: Include(Buyer).Join(db.Buyers, ...) registers TWO candidate joins
        // (the nav-expansion's own Buyer join, plus the user's explicit one) that BOTH target the Buyer
        // entity type — InnerCollections is keyed by entity type, so the dictionary collapses to ONE entry
        // and Count stays 1, defeating that guard. Only the candidate/confirmed COUNTER (Task 4) catches
        // this: one join confirms, the second bumps the candidate count past it, so
        // HasUnconfirmedCandidateJoin stays true and Route computes Fallback.
        using var nativeOnlyDb = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Two_joins_onto_the_same_target_stay_declined) + "_NativeOnly");
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Orders.Include(o => o.Buyer).Join(nativeOnlyDb.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o)
                .ToList());

        // The result selector projects a scalar (o.Id) rather than the brief's literal "(o, b) => o" whole-
        // entity form. Investigated: the whole-entity form hits a SEPARATE, PRE-EXISTING bug in the driver-LINQ
        // fallback's rewrite of two chained Queryable.Join calls (reproduced with NO Include and NO EF-368
        // code involved at all — plain "db.Orders.Join(db.Buyers,...).Join(db.Buyers,...)" under explicit
        // DriverLinq throws the identical "Document element is missing for required non-nullable property
        // 'Id'" from a malformed second $lookup localField, "_outer._outer.BuyerId"). That bug pre-dates this
        // task and is out of its scope; the scalar projection below still exercises the EXACT mechanism this
        // test is for (the candidate/confirmed counter declining a same-target double join) without tripping
        // over the unrelated chained-join materialization defect.
        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Two_joins_onto_the_same_target_stay_declined) + "_Native");
        var nativeResults = nativeDb.Orders.Include(o => o.Buyer)
            .Join(nativeDb.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o.Id)
            .ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Two_joins_onto_the_same_target_stay_declined) + "_DriverLinq");
        var driverResults = driverDb.Orders.Include(o => o.Buyer)
            .Join(driverDb.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o.Id)
            .ToList();

        // Each mode's CreateContext seeds its OWN collection with freshly-generated ObjectIds, so the two
        // result sets can only be compared by shape (row count), not by identity.
        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.Equal(3, nativeResults.Count); // 4 orders seeded, 1 dangling buyer, inner Join drops it.
    }

#if !EF8 && !EF9
    [Fact]
    public void Optional_reference_Include_with_a_reducer_and_a_navigation_null_predicate_falls_back_correctly()
    {
        // EF-368 Task 5 fix round 1 (C1 — CRITICAL). An optional reference Include combined with a
        // reducer whose predicate tests the navigation for null: Include(o => o.Carrier).First(o =>
        // o.Carrier == null). This shape's OWN predicate (a comparison against the whole navigation, not
        // one of its members) is not natively representable, so the query correctly declines to
        // Fallback — NativeOnly still throws NativeTranslationNotSupportedException for it, unchanged and
        // by design. What was actually broken, and what this test pins, is the FALLBACK path.
        //
        // EF folds First(predicate) into the single 2-arg Queryable.First(source, predicate) call rather
        // than a separate Where+First, and the predicate itself is pushed BELOW the Include's own
        // synthesized flattening Select, onto the join's TransparentIdentifier (ti => ti.Inner == null).
        //
        // The actual bug: MongoEFToLinqTranslatingExpressionVisitor.ReattachComposedOperator's guard
        // ("does a generic argument still mention the eliminated TransparentIdentifier type") compared
        // against oldSourceItemType unconditionally. For an operator sitting ABOVE the synthesized
        // flattening Select — First's OWN immediate source in the captured chain is that Select, not the
        // join — oldSourceItemType is already the flattened root type (Order), not the
        // TransparentIdentifier, so oldSourceItemType == newSourceItemType (both Order) even though no
        // generic argument ever mentioned the TransparentIdentifier at all. The guard's "still contains
        // oldSourceItemType" check fired as a false positive on that coincidence, refused the strip, and
        // let the join survive — so the driver rendered its OWN native LeftJoin (_outer/_inner) shape,
        // which the shaper (already committed to the flat _lookup_Carrier layout the moment the
        // ForceUnwind lookup was registered, via MongoQueryExpression.UsesDriverJoinFields) cannot read:
        // a shaper-time InvalidOperationException ("Document element is missing for required
        // non-nullable property") instead of a correct fallback answer.
        //
        // Fixed by gating that guard on IsTransparentIdentifier(oldSourceItemType) — see
        // ReattachComposedOperator's own comment for the corrected reasoning. A defence-in-depth guard,
        // GuardAgainstUnstrippableForceUnwindJoin, also now converts ANY future unstrippable-join-with-
        // pending-ForceUnwind-lookup mismatch into a clean InvalidOperationException in every mode,
        // rather than a silent shape mismatch, in case another such gap surfaces later.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Optional_reference_Include_with_a_reducer_and_a_navigation_null_predicate_falls_back_correctly),
            out var spyLogger);

        var result = db.Orders.Include(o => o.Carrier).First(o => o.Carrier == null);

        Assert.Null(result.Carrier);
        AssertMql(spyLogger,
            "{ \"$lookup\" : { \"from\" : \"" + db.CarriersCollectionName +
            "\", \"localField\" : \"CarrierId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Carrier\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Carrier\", \"preserveNullAndEmptyArrays\" : true } }, " +
            "{ \"$match\" : { \"_lookup_Carrier\" : null } }, " +
            "{ \"$limit\" : 1 }");
    }

    [Fact]
    public void Reducer_with_navigation_null_predicate_still_declines_cleanly_under_NativeOnly()
    {
        // The shape's own predicate (comparing the whole navigation, not a member of it) is out of scope
        // for the native $match translator — this must keep declining cleanly under NativeOnly, unchanged
        // by the C1 fix, which is a fallback-path-only fix.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Reducer_with_navigation_null_predicate_still_declines_cleanly_under_NativeOnly));

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Orders.Include(o => o.Carrier).First(o => o.Carrier == null));
    }

    [Fact]
    public void Native_and_DriverLinq_agree_on_reference_Include_with_a_reducer_and_a_navigation_null_predicate()
    {
        // Same shape as the NativeOnly test above, but asserting Native agrees with DriverLinq — each mode
        // seeds its OWN collection (see CreateContext), so rows are compared by SHAPE (a matching row
        // exists, and its Carrier materializes null) rather than by identity/ordinal, matching the idiom
        // Two_joins_onto_the_same_target_stay_declined already uses above.
        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Native_and_DriverLinq_agree_on_reference_Include_with_a_reducer_and_a_navigation_null_predicate)
            + "_Native");
        var nativeResult = nativeDb.Orders.Include(o => o.Carrier).First(o => o.Carrier == null);

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Native_and_DriverLinq_agree_on_reference_Include_with_a_reducer_and_a_navigation_null_predicate)
            + "_DriverLinq");
        var driverResult = driverDb.Orders.Include(o => o.Carrier).First(o => o.Carrier == null);

        Assert.Null(nativeResult.Carrier);
        Assert.Null(driverResult.Carrier);
    }
#endif

    private ReferenceIncludeDbContext CreateContext(
        MongoQueryMode mode, string name, ILoggerFactory? loggerFactory = null, bool buyerQueryFilter = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "O" + suffix;
        var buyersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "B" + suffix;
        var carriersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "C" + suffix;

        var regionsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "R" + suffix;
        var productsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "P" + suffix;
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "L" + suffix;
        var docsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "D" + suffix;
        var compositeOrdersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "XO" + suffix;
        var compositeLinesName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "XL" + suffix;

        var buyer1Id = ObjectId.GenerateNewId();
        var buyer2Id = ObjectId.GenerateNewId();
        var danglingBuyerId = ObjectId.GenerateNewId(); // never inserted: dangling FK.

        var regionId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Region>(regionsName).InsertMany(
        [
            new() { Id = regionId, Name = "Midwest" },
        ]);

        database.MongoDatabase.GetCollection<Buyer>(buyersName).InsertMany(
        [
            new() { Id = buyer1Id, Name = "Alice", Address = new() { City = "Springfield", RegionId = regionId } },
            new() { Id = buyer2Id, Name = "Bob", Address = new() { City = "Springfield", RegionId = regionId } },
        ]);

        var carrier1Id = ObjectId.GenerateNewId();
        var danglingCarrierId = ObjectId.GenerateNewId(); // never inserted: dangling FK.

        database.MongoDatabase.GetCollection<Carrier>(carriersName).InsertMany(
        [
            new() { Id = carrier1Id, Name = "FastShip" },
        ]);

        var order1Id = ObjectId.GenerateNewId();
        var order2Id = ObjectId.GenerateNewId();
        var order3Id = ObjectId.GenerateNewId();
        var order4Id = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<Order>(ordersName).InsertMany(
        [
            new() { Id = order1Id, BuyerId = buyer1Id, CarrierId = carrier1Id, Total = 5 },
            new() { Id = order2Id, BuyerId = buyer2Id, CarrierId = null, Total = 15 },
            new() { Id = order3Id, BuyerId = danglingBuyerId, CarrierId = danglingCarrierId, Total = 25 },
            new() { Id = order4Id, BuyerId = buyer1Id, CarrierId = null, Total = 35 },
        ]);

        // EF-368 Task 6: model additions for the DeclinedShapes tripwires (sibling/same-target sibling
        // Includes, reference + collection Include, and composite FK/PK).
        var product1Id = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Product>(productsName).InsertMany(
        [
            new() { Id = product1Id, Name = "Widget" },
        ]);

        database.MongoDatabase.GetCollection<Line>(linesName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), OrderId = order1Id, ProductId = product1Id, Quantity = 2 },
            new() { Id = ObjectId.GenerateNewId(), OrderId = order2Id, ProductId = product1Id, Quantity = 3 },
        ]);

        database.MongoDatabase.GetCollection<Doc>(docsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), AuthorId = buyer1Id, EditorId = buyer2Id, Title = "Doc1" },
        ]);

        database.MongoDatabase.GetCollection<CompositeOrder>(compositeOrdersName).InsertMany(
        [
            new() { Key1 = 1, Key2 = 1, Name = "CO1" },
        ]);

        database.MongoDatabase.GetCollection<CompositeLine>(compositeLinesName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), OrderKey1 = 1, OrderKey2 = 1, Quantity = 5 },
        ]);

        return new ReferenceIncludeDbContext(
            database, ordersName, buyersName, carriersName, regionsName, productsName, linesName, docsName,
            compositeOrdersName, compositeLinesName, mode, loggerFactory, buyerQueryFilter);
    }

    private ReferenceIncludeDbContext CreateContext(MongoQueryMode mode, string name, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return CreateContext(mode, name, loggerFactory);
    }

    // Full-message equality would also have to match the "Executed MQL query\n<namespace>.aggregate([...])"
    // wrapper — Assert.Contains against the captured pipeline fragment pins the pipeline shape without
    // coupling to that wrapper (idiom copied from NativeOwnedCollectionCountTests.cs).
    private static void AssertMql(SpyLoggerProvider spyLogger, string expected)
        => Assert.Contains(expected, spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery));

    private class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId BuyerId { get; set; }
        public ObjectId? CarrierId { get; set; }
        public decimal Total { get; set; }
        public Buyer Buyer { get; set; } = null!;
        public Carrier? Carrier { get; set; }

        // EF-368 Task 6: for the "reference + collection" DeclinedShapes row
        // (Orders.Include(o => o.Buyer).Include(o => o.Lines)).
        public List<Line> Lines { get; set; } = [];
    }

    // EF-368 Task 6: target of the "sibling reference Includes" row (Lines.Include(l => l.Order)
    // .Include(l => l.Product)) and the "ThenInclude / transitive" row.
    private class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Line
    {
        public ObjectId Id { get; set; }
        public ObjectId OrderId { get; set; }
        public ObjectId ProductId { get; set; }
        public int Quantity { get; set; }
        public Order Order { get; set; } = null!;
        public Product Product { get; set; } = null!;
    }

    // EF-368 Task 6: the "same-target sibling Includes" row (Docs.Include(d => d.Author)
    // .Include(d => d.Editor)) — Author and Editor both target Buyer, which is what the
    // InnerCollections.Count guard (keyed by entity type, not by navigation) is proving against.
    private class Doc
    {
        public ObjectId Id { get; set; }
        public ObjectId AuthorId { get; set; }
        public ObjectId EditorId { get; set; }
        public string Title { get; set; } = "";
        public Buyer Author { get; set; } = null!;
        public Buyer Editor { get; set; } = null!;
    }

    // EF-368 Task 6: the "composite FK/PK" row (CompositeLines.Include(l => l.Order)) — a composite
    // principal key always implies a matching composite FK, so one model shape exercises both guards
    // (navigation.ForeignKey.Properties.Count != 1 and .PrincipalKey.Properties.Count != 1) at once.
    private class CompositeOrder
    {
        public int Key1 { get; set; }
        public int Key2 { get; set; }
        public string Name { get; set; } = "";
    }

    private class CompositeLine
    {
        public ObjectId Id { get; set; }
        public int OrderKey1 { get; set; }
        public int OrderKey2 { get; set; }
        public int Quantity { get; set; }
        public CompositeOrder Order { get; set; } = null!;
    }

    private class Buyer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Address Address { get; set; } = new();
    }

    // EF-368 fix round 1 (I3): an OWNED (embedded) navigation on the reference-Include's TARGET, auto-included
    // by EF Core convention. Buyer.Address must NOT trip the ThenInclude decline in TryConfirmReferenceInclude —
    // it lives inside the same document the $lookup already reads.
    private class Address
    {
        public string City { get; set; } = "";
        public ObjectId? RegionId { get; set; }
        public Region? Region { get; set; }
    }

    // EF-368 fix round 2 (review finding B): a REAL cross-collection navigation nested UNDERNEATH an owned
    // (embedded) one — Buyer -> Address (owned) -> Region (real, non-embedded). HasNonEmbeddedThenInclude
    // must decline this: Region reaches past the looked-up Buyer document and this single-level slice has
    // no lookup for it.
    private class Region
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class Carrier
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    // EF-368 Task 7: a genuinely UNIDIRECTIONAL model - UniCustomer carries NO inverse collection navigation
    // back to UniOrder (contrast Buyer/Order above, where Buyer.Orders makes Order streaming-ineligible per
    // StreamingEligibility.IsEligible). This is what admits the root into the one-pass streaming
    // materializer instead of the DOM shaper.
    private class UniCustomer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class UniOrder
    {
        public ObjectId Id { get; set; }
        public ObjectId UniCustomerId { get; set; }
        public UniCustomer UniCustomer { get; set; } = null!;
        public decimal Total { get; set; }
    }

    private class UnidirectionalDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _customersCollection;

        public DbSet<UniOrder> UniOrders { get; set; } = null!;
        public DbSet<UniCustomer> UniCustomers { get; set; } = null!;

        public string CustomersCollectionName => _customersCollection;

        public UnidirectionalDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string customersCollection,
            ILoggerFactory? loggerFactory = null)
            : base(Configure(database, loggerFactory))
        {
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
        }

        private static DbContextOptions<UnidirectionalDbContext> Configure(
            TemporaryDatabaseFixture database, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<UnidirectionalDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(MongoQueryMode.NativeOnly))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

            if (loggerFactory != null)
            {
                builder = builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            }

            return builder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UniCustomer>().ToCollection(_customersCollection);
            modelBuilder.Entity<UniOrder>(b =>
            {
                b.ToCollection(_ordersCollection);
                // .WithMany() with no navigation expression: NO inverse collection on UniCustomer.
                b.HasOne(x => x.UniCustomer)
                    .WithMany()
                    .HasForeignKey(x => x.UniCustomerId)
                    .IsRequired();
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private UnidirectionalDbContext UnidirectionalContext(ILoggerFactory? loggerFactory = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Reference_Include_on_a_unidirectional_model_uses_the_streaming_materializer)) + "O" + suffix;
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Reference_Include_on_a_unidirectional_model_uses_the_streaming_materializer)) + "C" + suffix;

        var customer1Id = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<UniCustomer>(customersName).InsertMany(
        [
            new() { Id = customer1Id, Name = "Alice" }
        ]);

        database.MongoDatabase.GetCollection<UniOrder>(ordersName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), UniCustomerId = customer1Id, Total = 10 },
            new() { Id = ObjectId.GenerateNewId(), UniCustomerId = customer1Id, Total = 20 }
        ]);

        return new UnidirectionalDbContext(database, ordersName, customersName, loggerFactory);
    }

    private UnidirectionalDbContext UnidirectionalContext(out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return UnidirectionalContext(loggerFactory);
    }

    // EF-368 Task 7 fix round 1: the mutation-proof companion model. UniAddress/UniCustomerWithAddress mirror
    // Buyer/Address above (an OWNED sub-document, always eager-loaded by EF convention) but on a genuinely
    // unidirectional root (UniOrderWithEagerTarget - no inverse collection anywhere), so
    // StreamingEligibility.IsEligible stays TRUE for the root while AllPendingLookupsAreStreamable's
    // eager-load condition goes FALSE for the target. See the test that uses this model for the full
    // reasoning.
    private class UniAddress
    {
        public string City { get; set; } = "";
    }

    private class UniCustomerWithAddress
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public UniAddress Address { get; set; } = new();
    }

    private class UniOrderWithEagerTarget
    {
        public ObjectId Id { get; set; }
        public ObjectId UniCustomerWithAddressId { get; set; }
        public UniCustomerWithAddress UniCustomerWithAddress { get; set; } = null!;
        public decimal Total { get; set; }
    }

    private class UnidirectionalEagerTargetDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _customersCollection;

        public DbSet<UniOrderWithEagerTarget> UniOrdersWithEagerTarget { get; set; } = null!;
        public DbSet<UniCustomerWithAddress> UniCustomersWithAddress { get; set; } = null!;

        public UnidirectionalEagerTargetDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string customersCollection)
            : base(new DbContextOptionsBuilder<UnidirectionalEagerTargetDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(MongoQueryMode.NativeOnly))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UniCustomerWithAddress>(b =>
            {
                b.ToCollection(_customersCollection);
                b.OwnsOne(x => x.Address);
            });
            modelBuilder.Entity<UniOrderWithEagerTarget>(b =>
            {
                b.ToCollection(_ordersCollection);
                // .WithMany() with no navigation expression: NO inverse collection anywhere in this model.
                b.HasOne(x => x.UniCustomerWithAddress)
                    .WithMany()
                    .HasForeignKey(x => x.UniCustomerWithAddressId)
                    .IsRequired();
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private UnidirectionalEagerTargetDbContext UnidirectionalContextWithEagerLoadedTarget()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = nameof(Reference_Include_whose_target_has_an_eager_loaded_navigation_still_returns_correct_rows_via_the_DOM_shaper);
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "O" + suffix;
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "C" + suffix;

        var customer1Id = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<UniCustomerWithAddress>(customersName).InsertMany(
        [
            new() { Id = customer1Id, Name = "Alice", Address = new() { City = "Springfield" } }
        ]);

        database.MongoDatabase.GetCollection<UniOrderWithEagerTarget>(ordersName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), UniCustomerWithAddressId = customer1Id, Total = 10 },
            new() { Id = ObjectId.GenerateNewId(), UniCustomerWithAddressId = customer1Id, Total = 20 }
        ]);

        return new UnidirectionalEagerTargetDbContext(database, ordersName, customersName);
    }

    private class ReferenceIncludeDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _buyersCollection;
        private readonly string _carriersCollection;
        private readonly string _regionsCollection;
        private readonly string _productsCollection;
        private readonly string _linesCollection;
        private readonly string _docsCollection;
        private readonly string _compositeOrdersCollection;
        private readonly string _compositeLinesCollection;
        private readonly bool _buyerQueryFilter;

        public ReferenceIncludeDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string buyersCollection,
            string carriersCollection, string regionsCollection, string productsCollection, string linesCollection,
            string docsCollection, string compositeOrdersCollection, string compositeLinesCollection,
            MongoQueryMode mode, ILoggerFactory? loggerFactory, bool buyerQueryFilter = false)
            : base(Configure(database, mode, loggerFactory))
        {
            _ordersCollection = ordersCollection;
            _buyersCollection = buyersCollection;
            _carriersCollection = carriersCollection;
            _regionsCollection = regionsCollection;
            _productsCollection = productsCollection;
            _linesCollection = linesCollection;
            _docsCollection = docsCollection;
            _compositeOrdersCollection = compositeOrdersCollection;
            _compositeLinesCollection = compositeLinesCollection;
            _buyerQueryFilter = buyerQueryFilter;
        }

        public string BuyersCollectionName => _buyersCollection;
        public string CarriersCollectionName => _carriersCollection;

        private static DbContextOptions<ReferenceIncludeDbContext> Configure(
            TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var builder = new DbContextOptionsBuilder<ReferenceIncludeDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

            if (loggerFactory != null)
            {
                builder = builder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            }

            return builder.Options;
        }

        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<Buyer> Buyers { get; set; } = null!;
        public DbSet<Carrier> Carriers { get; set; } = null!;
        public DbSet<Region> Regions { get; set; } = null!;
        public DbSet<Product> Products { get; set; } = null!;
        public DbSet<Line> Lines { get; set; } = null!;
        public DbSet<Doc> Docs { get; set; } = null!;
        public DbSet<CompositeOrder> CompositeOrders { get; set; } = null!;
        public DbSet<CompositeLine> CompositeLines { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Order>().ToCollection(_ordersCollection);
            modelBuilder.Entity<Order>()
                .HasMany(o => o.Lines)
                .WithOne(l => l.Order)
                .HasForeignKey(l => l.OrderId);

            var buyerBuilder = modelBuilder.Entity<Buyer>();
            buyerBuilder.ToCollection(_buyersCollection);
            buyerBuilder.OwnsOne(b => b.Address);
            if (_buyerQueryFilter)
            {
                // EF-368 Task 6 (predates the brief's DeclinedShapes list): a HasQueryFilter on the
                // reference-Include's TARGET must decline — a plain $lookup cannot carry this predicate.
                buyerBuilder.HasQueryFilter(b => b.Name == "Alice");
            }

            modelBuilder.Entity<Carrier>().ToCollection(_carriersCollection);
            modelBuilder.Entity<Region>().ToCollection(_regionsCollection);

            modelBuilder.Entity<Product>().ToCollection(_productsCollection);
            modelBuilder.Entity<Line>().ToCollection(_linesCollection);
            modelBuilder.Entity<Line>().HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId);

            modelBuilder.Entity<Doc>().ToCollection(_docsCollection);
            modelBuilder.Entity<Doc>().HasOne(d => d.Author).WithMany().HasForeignKey(d => d.AuthorId);
            modelBuilder.Entity<Doc>().HasOne(d => d.Editor).WithMany().HasForeignKey(d => d.EditorId);

            modelBuilder.Entity<CompositeOrder>().ToCollection(_compositeOrdersCollection).HasKey(o => new { o.Key1, o.Key2 });
            modelBuilder.Entity<CompositeLine>().ToCollection(_compositeLinesCollection);
            modelBuilder.Entity<CompositeLine>()
                .HasOne(l => l.Order)
                .WithMany()
                .HasForeignKey(l => new { l.OrderKey1, l.OrderKey2 });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
