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
/// Native reference <c>Include</c>: a confirmed reference-Include join registers a forced-unwind
/// <see cref="Query.Expressions.LookupExpression"/> so the native lowerer, DOM shaper and driver-LINQ fallback all
/// agree on the <c>_lookup_&lt;Nav&gt;</c> field. The <c>$unwind</c> is inner (drops the row) for a required
/// navigation (<c>Buyer</c>) and left-outer (nav null) for an optional one (<c>Carrier</c>).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferenceIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void User_join_is_not_admitted_by_the_candidate_join_signal()
    {
        // A user join with no Include: the candidate-join signal alone must not make this native. The
        // selector must be a shape no confirming arm claims — the bare `(o, b) => o` is confirmed (see the next
        // test), but `new { o, b }` has whole-entity leaves, which NativeJoinScopeProjectionBinder declines,
        // so HasUnconfirmedCandidateJoin still forces Fallback.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(User_join_is_not_admitted_by_the_candidate_join_signal));

        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Orders.Join(db.Buyers, o => o.BuyerId, b => b.Id, (o, b) => new { o, b }).ToList());
    }

    [Fact]
    public void User_join_projecting_the_whole_outer_entity_goes_native_with_correct_results()
    {
        // EF normalizes `(o, b) => o` to a TransparentIdentifier join plus `Select(x => x.Outer)`, which the
        // bare-whole-entity arm confirms. The inner $unwind reproduces inner-join semantics: O3's dangling
        // BuyerId drops it, leaving 3 of 4 orders.
        using var nativeOnlyDb = CreateContext(MongoQueryMode.NativeOnly,
            nameof(User_join_projecting_the_whole_outer_entity_goes_native_with_correct_results) + "_NativeOnly");
        var nativeResults = nativeOnlyDb.Orders
            .Join(nativeOnlyDb.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o)
            .ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(User_join_projecting_the_whole_outer_entity_goes_native_with_correct_results) + "_DriverLinq");
        var driverResults = driverDb.Orders
            .Join(driverDb.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o)
            .ToList();

        // Each context seeds its own collection with fresh ObjectIds, so compare by count and Total, not identity.
        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.Equal(3, nativeResults.Count);
        Assert.Equal(
            driverResults.Select(o => o.Total).OrderBy(t => t),
            nativeResults.Select(o => o.Total).OrderBy(t => t));
    }

    [Fact]
    public void Required_reference_Include_goes_native_with_an_inner_unwind()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Required_reference_Include_goes_native_with_an_inner_unwind), out var spyLogger);

        var results = db.Orders.Include(o => o.Buyer).ToList();

        // O3's buyer is dangling and the navigation is required, so the inner $unwind drops it: 3 of 4 remain.
        Assert.Equal(3, results.Count);
        Assert.All(results, o => Assert.NotNull(o.Buyer));

        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.BuyersCollectionName +
            "\", \"localField\" : \"BuyerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Buyer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Buyer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    [Fact]
    public void Reference_Include_whose_target_owns_an_embedded_type_still_goes_native()
    {
        // EF auto-includes an owned navigation on the Include target (Buyer.Address) the same way it nests a
        // real ThenInclude; a guard that declined any nested IncludeExpression would exclude every target with
        // owned types. Also asserts the owned data materializes through the native path.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Reference_Include_whose_target_owns_an_embedded_type_still_goes_native), out var spyLogger);

        var results = db.Orders.Include(o => o.Buyer).ToList();

        Assert.Equal(3, results.Count);
        Assert.All(results, o => Assert.NotNull(o.Buyer));
        Assert.All(results, o => Assert.Equal("Springfield", o.Buyer.Address.City));

        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.BuyersCollectionName +
            "\", \"localField\" : \"BuyerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Buyer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Buyer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    [Fact]
    public void A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines()
    {
        // A real nav (Region) ThenIncluded under an owned one (Address) must decline loudly, not silently drop
        // Region. The extra join EF injects for Region turns the Buyer IncludeExpression's EntityExpression into
        // a double hop (ti.Outer.Outer), so IsSingleLevelReferenceIncludeSelector rejects it before
        // HasNonEmbeddedThenInclude is reached — this is a decline tripwire, not coverage of that method.
        //
        // Exception type not pinned: EF10 declines in the projection binder; EF8/EF9 fail earlier inside EF
        // Core's translator with InvalidOperationException. Unsupported-shape exception types aren't contract.
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(A_real_ThenInclude_nested_underneath_an_embedded_hop_still_declines));

        Assert.ThrowsAny<Exception>(
            () => db.Orders.Include(o => o.Buyer).ThenInclude(b => b.Address).ThenInclude(a => a.Region).ToList());
    }

    [Fact]
    public void Sibling_reference_Includes_go_native_with_correct_data()
    {
        // Sibling Includes with different target types. NativeOnly proves native; Native == DriverLinq proves
        // the two independently-scoped $lookups return the right rows.
        using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Sibling_reference_Includes_go_native_with_correct_data) + "_NativeOnly");
        var nativeOnlyResults = nativeOnly.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

        // 4 lines: 2 clean, 1 dangling OrderId, 1 dangling ProductId. Both navs are required, so 2 survive.
        Assert.Equal(2, nativeOnlyResults.Count);
        Assert.All(nativeOnlyResults, l => Assert.NotNull(l.Order));
        Assert.All(nativeOnlyResults, l => Assert.NotNull(l.Product));

        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Sibling_reference_Includes_go_native_with_correct_data) + "_Native");
        var nativeResults = nativeDb.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Sibling_reference_Includes_go_native_with_correct_data) + "_DriverLinq");
        var driverResults = driverDb.Lines.Include(l => l.Order).Include(l => l.Product).ToList();

        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.All(nativeResults, l => Assert.NotNull(l.Order));
        Assert.All(nativeResults, l => Assert.NotNull(l.Product));
    }

    [Fact]
    public void Same_target_sibling_reference_Includes_go_native_with_correct_data()
    {
        // Same target type (Buyer) for Author and Editor: InnerCollections is keyed by entity type and would
        // collapse the two joins, so correctness must rest on Joins (one entry per join) and the
        // navigation-keyed $lookup alias.
        using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Same_target_sibling_reference_Includes_go_native_with_correct_data) + "_NativeOnly");
        var nativeOnlyResults = nativeOnly.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

        // 3 docs: 1 clean, 1 dangling AuthorId, 1 dangling EditorId. 1 survives, and its Author and Editor must
        // differ (not one lookup field reused for both).
        var only = Assert.Single(nativeOnlyResults);
        Assert.NotNull(only.Author);
        Assert.NotNull(only.Editor);
        Assert.NotEqual(only.Author.Id, only.Editor.Id);

        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Same_target_sibling_reference_Includes_go_native_with_correct_data) + "_Native");
        var nativeResults = nativeDb.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Same_target_sibling_reference_Includes_go_native_with_correct_data) + "_DriverLinq");
        var driverResults = driverDb.Docs.Include(d => d.Author).Include(d => d.Editor).ToList();

        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.All(nativeResults, d => Assert.NotEqual(d.Author.Id, d.Editor.Id));
    }

    [Fact]
    public void Reference_and_collection_Include_combo_goes_native_with_correct_data()
    {
        // Buyer (reference, forced-unwind $lookup) and Lines (collection, flat $lookup) on the same query.
        using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Reference_and_collection_Include_combo_goes_native_with_correct_data) + "_NativeOnly");
        var nativeOnlyResults = nativeOnly.Orders.Include(o => o.Buyer).Include(o => o.Lines).ToList();

        // O3's dangling buyer drops it (3 of 4 survive); O4 has no Lines, covering the empty-collection case
        // alongside the reference drop.
        Assert.Equal(3, nativeOnlyResults.Count);
        Assert.All(nativeOnlyResults, o => Assert.NotNull(o.Buyer));
        var order1 = Assert.Single(nativeOnlyResults, o => o.Total == 5);
        Assert.Equal(2, order1.Lines.Count);
        var order4 = Assert.Single(nativeOnlyResults, o => o.Total == 35);
        Assert.Empty(order4.Lines);

        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Reference_and_collection_Include_combo_goes_native_with_correct_data) + "_Native");
        var nativeResults = nativeDb.Orders.Include(o => o.Buyer).Include(o => o.Lines).ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Reference_and_collection_Include_combo_goes_native_with_correct_data) + "_DriverLinq");
        var driverResults = driverDb.Orders.Include(o => o.Buyer).Include(o => o.Lines).ToList();

        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.All(nativeResults, o => Assert.NotNull(o.Buyer));
        Assert.Equal(
            driverResults.OrderBy(o => o.Total).Select(o => o.Lines.Count),
            nativeResults.OrderBy(o => o.Total).Select(o => o.Lines.Count));
    }

    [Fact]
    public void Reference_ThenInclude_chain_goes_native_with_correct_data()
    {
        // A 2-hop reference ThenInclude chain.
        using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Reference_ThenInclude_chain_goes_native_with_correct_data) + "_NativeOnly");
        var nativeOnlyResults = nativeOnly.Lines.Include(l => l.Order).ThenInclude(o => o.Buyer).ToList();

        // The dangling-Order line is dropped; every surviving line's Order has a valid Buyer.
        Assert.All(nativeOnlyResults, l => Assert.NotNull(l.Order));
        Assert.All(nativeOnlyResults, l => Assert.NotNull(l.Order.Buyer));

        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Reference_ThenInclude_chain_goes_native_with_correct_data) + "_Native");
        var nativeResults = nativeDb.Lines.Include(l => l.Order).ThenInclude(o => o.Buyer).ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Reference_ThenInclude_chain_goes_native_with_correct_data) + "_DriverLinq");
        var driverResults = driverDb.Lines.Include(l => l.Order).ThenInclude(o => o.Buyer).ToList();

        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.All(nativeResults, l => Assert.NotNull(l.Order));
        Assert.All(nativeResults, l => Assert.NotNull(l.Order.Buyer));
    }

    [Fact]
    public void Deep_ThenInclude_through_embedded_hop_returns_correct_data_via_fallback()
    {
        // The shape above declines natively and falls back under Native mode. Pins that the fallback populates
        // Region: the join-stripping bridge (MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs) must scope
        // the second $lookup's localField under the first alias ("_lookup_Buyer.Address.RegionId"). On EF8/EF9
        // this relies on the LeftJoin shim being admitted (see IsEf8Ef9LeftJoinShim).
        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Deep_ThenInclude_through_embedded_hop_returns_correct_data_via_fallback) + "_Native");
        var nativeResults = nativeDb.Orders.Include(o => o.Buyer).ThenInclude(b => b.Address).ThenInclude(a => a.Region)
            .ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Deep_ThenInclude_through_embedded_hop_returns_correct_data_via_fallback) + "_DriverLinq");
        var driverResults = driverDb.Orders.Include(o => o.Buyer).ThenInclude(b => b.Address).ThenInclude(a => a.Region)
            .ToList();

        // O3's buyer is dangling (required FK), so 3 of the 4 seeded orders survive the Buyer unwind.
        Assert.Equal(3, nativeResults.Count);
        Assert.Equal(3, driverResults.Count);
        Assert.All(nativeResults, o => Assert.Equal("Midwest", o.Buyer.Address.Region?.Name));
        Assert.All(driverResults, o => Assert.Equal("Midwest", o.Buyer.Address.Region?.Name));
    }

    /// <summary>
    /// Shapes <c>TryConfirmReferenceInclude</c> must decline rather than silently admit. Each row asserts that
    /// <see cref="MongoQueryMode.NativeOnly"/> throws and that <see cref="MongoQueryMode.Native"/> returns the same
    /// rows (by value) as <see cref="MongoQueryMode.DriverLinq"/>.
    /// </summary>
    // Rows are description strings because a Func over the private ReferenceIncludeDbContext can't appear in a
    // public [Theory] signature (CS0050/CS0051); GetDeclinedShapeBuilder maps them back.
    public static TheoryData<string> DeclinedShapeDescriptions => new()
    {
        // A user join with a downstream Include: the trailing IncludeExpression's EntityExpression is a double
        // hop (ti.Outer.Outer). See GetDeclinedShapeBuilder for what declines it.
        "user join with downstream Include",
        // Composite FK/PK is a separate test: driver LINQ can't translate it either, so there's no oracle.
    };

    private static Func<ReferenceIncludeDbContext, IQueryable> GetDeclinedShapeBuilder(string description)
        => description switch
        {
            // No trailing Select: EF drops an unreferenced Include under a scalar Select, so the row would hit
            // the generic non-entity-projection decline instead of the Include guard.
            //
            // The double hop matches what TryGetReferenceIncludeChain admits for an N=2 sibling chain; what
            // declines this row is TryConfirmReferenceIncludeChain's Joins.Count != chain.Count check (two Joins
            // — the user's and nav-expansion's — but one IncludeExpression). The candidate/confirmed counter
            // (two candidates, one confirmation) backs it up independently.
            "user join with downstream Include" =>
                db => db.Orders.Join(db.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o).Include(o => o.Buyer),
            _ => throw new ArgumentOutOfRangeException(nameof(description), description, "Unknown declined shape.")
        };

    [Fact]
    public void Distinct_then_Include_goes_native_with_correct_data()
    {
        // A whole-entity Distinct() before a reference Include is an ordinary PipelineOp (MongoDistinctOp),
        // lowered to a $group/$replaceRoot dedup ahead of the $lookup/$unwind; the Include confirmation only
        // inspects the inner side, so it is unaffected.
        using var nativeOnly = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Distinct_then_Include_goes_native_with_correct_data) + "_NativeOnly");
        var nativeOnlyResults = nativeOnly.Orders.Distinct().Include(o => o.Buyer).ToList();

        // O3 (dangling BuyerId) is dropped: 3 of 4 survive. Ids are unique, so Distinct() is a no-op here —
        // this pins the composition, not duplicate collapsing.
        Assert.Equal(3, nativeOnlyResults.Count);
        Assert.All(nativeOnlyResults, o => Assert.NotNull(o.Buyer));

        using var nativeDb = CreateContext(MongoQueryMode.Native,
            nameof(Distinct_then_Include_goes_native_with_correct_data) + "_Native");
        var nativeResults = nativeDb.Orders.Distinct().Include(o => o.Buyer).ToList();

        using var driverDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Distinct_then_Include_goes_native_with_correct_data) + "_DriverLinq");
        var driverResults = driverDb.Orders.Distinct().Include(o => o.Buyer).ToList();

        var nativeCanonical = nativeResults.Select(o => (o.Total, o.Buyer?.Name)).OrderBy(x => x.Total).ToList();
        var driverCanonical = driverResults.Select(o => (o.Total, o.Buyer?.Name)).OrderBy(x => x.Total).ToList();
        Assert.Equal(driverCanonical, nativeCanonical);
        Assert.NotEmpty(nativeCanonical);
    }

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

            // Pinning this message (the Route == Fallback guard) proves the Include guard declined, not the
            // unrelated "Query projects a non-entity result" decline a trailing projection would trigger.
            Assert.Contains("Query is not natively representable", ex.Message);
        }

        if (HasNoDriverLinqParityOracle(description))
        {
            // Driver LINQ can't materialize a chained-join whole-entity result ("Document element is missing
            // for required non-nullable property 'Id'"), and projecting to a scalar would make the NativeOnly
            // half hit the wrong decline, so parity is untestable for these rows.
            return;
        }

        using var native = CreateContext(MongoQueryMode.Native, testName + "_Native");
        using var driverLinq = CreateContext(MongoQueryMode.DriverLinq, testName + "_DriverLinq");

        var nativeRows = build(native).Cast<object>().ToList();
        var driverRows = build(driverLinq).Cast<object>().ToList();

        // Each context seeds fresh ObjectIds, so compare sorted canonical values rather than identities.
        var nativeCanonical = nativeRows.Select(Canonicalize).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var driverCanonical = driverRows.Select(Canonicalize).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(driverRows.Count, nativeRows.Count);
        Assert.Equal(driverCanonical, nativeCanonical);
        Assert.NotEmpty(nativeCanonical); // guard against a vacuous pass from two empty result sets
    }

    // See the HasNoDriverLinqParityOracle branch above.
    private static bool HasNoDriverLinqParityOracle(string description)
        => description is "same-target sibling Includes" or "user join with downstream Include";

    // A seed-independent value for a row; ObjectIds differ across independently-seeded contexts.
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
    /// Composite FK/PK Include declines. Driver LINQ can't translate a composite-key Join/Include either
    /// ("cannot be translated to a dotted field name"), so the claim is that every mode throws rather than
    /// returning rows with a dropped FK correlation.
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
    /// A <c>HasQueryFilter</c> on the Include target must decline: a plain <c>$lookup</c> can't apply the inner
    /// predicate, so admitting it returns unfiltered rows (see
    /// <c>NorthwindQueryFiltersQueryMongoTest.Included_many_to_one_query</c>).
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

        // Driver 3.11 rejects a join over a filtered inner sub-query, so the fallback modes throw rather than
        // return a countable row set. What's pinned: no route returns unfiltered rows.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(mode,
                nameof(Query_filter_on_the_included_target_still_declines) + "_" + mode, buyerQueryFilter: true);

            Assert.Throws<ExpressionNotSupportedException>(() => db.Orders.Include(o => o.Buyer).ToList());
        }

        // Non-vacuity control: without the filter the same Include runs natively, so the failure is filter-specific.
        using var unfiltered = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Query_filter_on_the_included_target_still_declines) + "_Unfiltered", buyerQueryFilter: false);
        Assert.NotEmpty(unfiltered.Orders.Include(o => o.Buyer).ToList());
    }

    // Query-filter routes a metadata check (`TargetEntityType.GetQueryFilter() != null`) misses:
    //   (a) a filter inherited from a TPH root when the Include target is derived (GetQueryFilter() is null);
    //   (b) an EF10 named filter, which lives in GetDeclaredQueryFilters().
    // Admitting either returns unfiltered rows in every mode — the ForceUnwind lookup is registered at
    // translation time, so StripJoinForLookup drops the filter's Where on the fallback path too. Both are
    // closed structurally by MongoSelectDefinition.IsBareCollectionScan on the join's inner select in
    // TranslateJoinCore: EF applies any filter as a Where on the inner, so a filtered inner declines.

    [Fact]
    public void Query_filter_inherited_from_a_TPH_root_on_the_included_target_declines()
    {
        var (tickets, parties) = FilteredTargetCollections(
            nameof(Query_filter_inherited_from_a_TPH_root_on_the_included_target_declines));
        SeedTphFilterModel(tickets, parties);

        // Driver 3.11 throws on the fallback modes (see Query_filter_on_the_included_target_still_declines);
        // pins that a TPH-root-inherited filter is never silently dropped.
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

        // As the TPH test above, for an EF10 named filter (GetDeclaredQueryFilters(), not GetQueryFilter()).
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

    // Seeded through EF so the provider writes the TPH discriminator. Query filters don't apply to SaveChanges,
    // so the soft-deleted party is stored too.
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

    // TPH root declares the filter; VipParty (the Include target) inherits it but GetQueryFilter() returns null.
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
            // EF forbids declaring a filter on a derived type.
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
    // EF10 named filter: GetQueryFilter() returns null; GetDeclaredQueryFilters() holds it.
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
        // $match before $lookup: filter/sort/paging push ahead of the join.
        spyLogger.AssertExecutedMqlContains("{ \"$match\" : { \"Total\" : { \"$gt\" : { \"$numberDecimal\" : \"10\" } } } }, " +
            "{ \"$lookup\" : { \"from\" : \"" + db.BuyersCollectionName +
            "\", \"localField\" : \"BuyerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Buyer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Buyer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    /// <summary>
    /// Reference Include on a unidirectional model, so the root is streaming-eligible and the one-pass streaming
    /// materializer's <c>LookupReferencePlan</c> runs (elsewhere Buyer's inverse <c>Orders</c> forces the DOM shaper).
    /// <para>
    /// Streaming requires both <c>StreamingEligibility.IsEligible(root)</c> and
    /// <c>AllPendingLookupsAreStreamable</c>. Row correctness can't tell the shapers apart and the
    /// <c>LookupExpression</c> isn't observable here, so the second gate is asserted by proxy: no eager-loaded
    /// navigation on the target (metadata), and a plain non-piped, unprefixed <c>$lookup</c>/<c>$unwind</c> (MQL),
    /// which is what <c>IsStreamableReference</c> computes. See
    /// <see cref="Reference_Include_whose_target_has_an_eager_loaded_navigation_still_returns_correct_rows_via_the_DOM_shaper"/>
    /// for the companion where gate 1 passes but gate 2 fails.
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

        // Gate 2, eager-load fact: the looked-up entity must have no eager-loaded navigation.
        var targetEntityType = db.Model.FindEntityType(typeof(UniCustomer))!;
        Assert.False(
            targetEntityType.GetNavigations().Any(n => n.IsEagerLoaded),
            "UniCustomer must carry no eager-loaded navigation, or AllPendingLookupsAreStreamable's " +
            "second (unchecked-by-IsEligible-alone) condition would route this query to the DOM shaper " +
            "instead of streaming, regardless of gate 1.");

        var orders = db.UniOrders.Include(o => o.UniCustomer).ToList();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.NotNull(o.UniCustomer));

        // Gate 2, IsStreamableReference fact (IsReference && !HasPipeline && local field not _lookup_-prefixed).
        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.CustomersCollectionName +
            "\", \"localField\" : \"UniCustomerId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_UniCustomer\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_UniCustomer\", \"preserveNullAndEmptyArrays\" : false } }");
    }

    /// <summary>
    /// Companion to the streaming test above: the target owns an <c>Address</c> (always eager-loaded), so gate 2
    /// fails while <see cref="StreamingEligibility.IsEligible"/> for the root still passes. The DOM shaper runs
    /// and rows are still correct — which is why the streaming test must assert gate 2 directly.
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

        // Gate 2's eager-load fact fails here: an owned Address is always eager-loaded.
        var targetEntityType = db.Model.FindEntityType(typeof(UniCustomerWithAddress))!;
        Assert.True(
            targetEntityType.GetNavigations().Any(n => n.IsEagerLoaded),
            "UniCustomerWithAddress's owned Address must be eager-loaded, or this model no longer " +
            "exercises AllPendingLookupsAreStreamable's second, IsEligible-blind condition.");

        // Native via the DOM shaper, not streaming; rows are still correct.
        var orders = db.UniOrdersWithEagerTarget.Include(o => o.UniCustomerWithAddress).ToList();

        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.NotNull(o.UniCustomerWithAddress));
        Assert.All(orders, o => Assert.Equal("Springfield", o.UniCustomerWithAddress.Address.City));
    }

    [Fact]
    public void Optional_reference_Include_goes_native_with_a_left_outer_unwind()
    {
        using var db = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Optional_reference_Include_goes_native_with_a_left_outer_unwind), out var spyLogger);

        var results = db.Orders.Include(o => o.Carrier).ToList();

        // Left-outer: null-FK and dangling-FK rows both survive with a null navigation; all 4 remain.
        Assert.Equal(4, results.Count);
        Assert.Contains(results, o => o.Carrier == null);

        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.CarriersCollectionName +
            "\", \"localField\" : \"CarrierId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Carrier\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Carrier\", \"preserveNullAndEmptyArrays\" : true } }");
    }

    [Fact]
    public void Plain_bool_null_check_sort_key_over_optional_join_inner_matches_oracle()
    {
        // `o.Carrier.Name != null` over a left-outer inner must sort missing/dangling Carriers first (false <
        // true). A bare aggregation `{$ne: ["$_lookup_Carrier.Name", null]}` answers true for a missing operand
        // (unlike query-dialect `{field: null}`), so every row would look non-null and the sort would silently
        // degrade to the ThenBy key (see NativeSlotPopulator's innerSortScope arm).
        using var nativeOnlyDb = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Plain_bool_null_check_sort_key_over_optional_join_inner_matches_oracle) + "_NativeOnly");
        var nativeResults = nativeOnlyDb.Orders.OrderBy(o => o.Carrier.Name != null).ThenBy(o => o.Total).ToList();

        using var oracleDb = CreateContext(MongoQueryMode.DriverLinq,
            nameof(Plain_bool_null_check_sort_key_over_optional_join_inner_matches_oracle) + "_Oracle");
        var oracle = oracleDb.Orders.Include(o => o.Carrier).AsEnumerable()
            .OrderBy(o => o.Carrier?.Name != null).ThenBy(o => o.Total).Select(o => o.Total).ToList();

        Assert.Equal(oracle, nativeResults.Select(o => o.Total).ToList());
    }

    [Fact]
    public void Two_joins_onto_the_same_target_stay_declined()
    {
        // Include(Buyer).Join(db.Buyers, ...) registers two candidate joins onto Buyer; InnerCollections (keyed
        // by entity type) collapses them to one entry, so only the candidate/confirmed counter catches this:
        // HasUnconfirmedCandidateJoin stays true and Route computes Fallback.
        using var nativeOnlyDb = CreateContext(MongoQueryMode.NativeOnly,
            nameof(Two_joins_onto_the_same_target_stay_declined) + "_NativeOnly");
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            nativeOnlyDb.Orders.Include(o => o.Buyer).Join(nativeOnlyDb.Buyers, o => o.BuyerId, b => b.Id, (o, b) => o)
                .ToList());

        // Projects o.Id rather than the whole entity: driver LINQ mis-renders two chained Joins (second $lookup
        // localField "_outer._outer.BuyerId", "Document element is missing for required non-nullable property
        // 'Id'"), independent of Include.
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

        // Separately-seeded contexts: compare by row count only.
        Assert.Equal(driverResults.Count, nativeResults.Count);
        Assert.Equal(3, nativeResults.Count); // 4 orders seeded, 1 dangling buyer, inner Join drops it.
    }

    [Fact]
    public void Optional_reference_Include_with_a_reducer_and_a_navigation_null_predicate_falls_back_correctly()
    {
        // Include(Carrier).First(o => o.Carrier == null) declines natively (whole-navigation comparison); this
        // pins the fallback. EF pushes the predicate below the Include's flattening Select, so First's own
        // source item type is already Order. ReattachComposedOperator must gate its "generic argument still
        // mentions the TransparentIdentifier" check on IsTransparentIdentifier(oldSourceItemType); otherwise it
        // refuses the strip, the driver renders its own _outer/_inner LeftJoin, and the shaper (committed to
        // _lookup_Carrier) throws. GuardAgainstUnstrippableForceUnwindJoin turns any such future mismatch into
        // a clean InvalidOperationException.
        using var db = CreateContext(MongoQueryMode.Native,
            nameof(Optional_reference_Include_with_a_reducer_and_a_navigation_null_predicate_falls_back_correctly),
            out var spyLogger);

        var result = db.Orders.Include(o => o.Carrier).First(o => o.Carrier == null);

        Assert.Null(result.Carrier);
        spyLogger.AssertExecutedMqlContains("{ \"$lookup\" : { \"from\" : \"" + db.CarriersCollectionName +
            "\", \"localField\" : \"CarrierId\", \"foreignField\" : \"_id\", \"as\" : \"_lookup_Carrier\" } }, " +
            "{ \"$unwind\" : { \"path\" : \"$_lookup_Carrier\", \"preserveNullAndEmptyArrays\" : true } }, " +
            "{ \"$match\" : { \"_lookup_Carrier\" : null } }, " +
            "{ \"$limit\" : 1 }");
    }

    [Fact]
    public void Native_and_DriverLinq_agree_on_reference_Include_with_a_reducer_and_a_navigation_null_predicate()
    {
        // Same shape as above; Native must agree with DriverLinq (compared by shape — separately seeded).
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

        var product1Id = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<Product>(productsName).InsertMany(
        [
            new() { Id = product1Id, Name = "Widget" },
        ]);

        var danglingOrderId = ObjectId.GenerateNewId(); // never inserted: dangling FK, Line -> Order side.
        var danglingProductId = ObjectId.GenerateNewId(); // never inserted: dangling FK, Line -> Product side.

        database.MongoDatabase.GetCollection<Line>(linesName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), OrderId = order1Id, ProductId = product1Id, Quantity = 2 },
            new() { Id = ObjectId.GenerateNewId(), OrderId = order2Id, ProductId = product1Id, Quantity = 3 },
            // One dangling FK per side, on different rows, so each lookup's inner unwind is tested independently.
            new() { Id = ObjectId.GenerateNewId(), OrderId = danglingOrderId, ProductId = product1Id, Quantity = 1 },
            new() { Id = ObjectId.GenerateNewId(), OrderId = order1Id, ProductId = danglingProductId, Quantity = 1 },
        ]);

        database.MongoDatabase.GetCollection<Doc>(docsName).InsertMany(
        [
            new() { Id = ObjectId.GenerateNewId(), AuthorId = buyer1Id, EditorId = buyer2Id, Title = "Doc1" },
            // Dangling AuthorId and EditorId on separate rows, so _lookup_Author/_lookup_Editor are shown to be
            // independently scoped.
            new() { Id = ObjectId.GenerateNewId(), AuthorId = ObjectId.GenerateNewId(), EditorId = buyer2Id, Title = "Doc2" },
            new() { Id = ObjectId.GenerateNewId(), AuthorId = buyer1Id, EditorId = ObjectId.GenerateNewId(), Title = "Doc3" },
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

    private class Order
    {
        public ObjectId Id { get; set; }
        public ObjectId BuyerId { get; set; }
        public ObjectId? CarrierId { get; set; }
        public decimal Total { get; set; }
        public Buyer Buyer { get; set; } = null!;
        public Carrier? Carrier { get; set; }

        public List<Line> Lines { get; set; } = [];
    }

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

    // Author and Editor both target Buyer, which InnerCollections (keyed by entity type) would collapse.
    private class Doc
    {
        public ObjectId Id { get; set; }
        public ObjectId AuthorId { get; set; }
        public ObjectId EditorId { get; set; }
        public string Title { get; set; } = "";
        public Buyer Author { get; set; } = null!;
        public Buyer Editor { get; set; } = null!;
    }

    // A composite principal key implies a composite FK, so this exercises both single-property key guards.
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

    // Owned navigation on the Include target, auto-included by EF. Must not trip the ThenInclude decline — it
    // lives inside the document the $lookup already reads.
    private class Address
    {
        public string City { get; set; } = "";
        public ObjectId? RegionId { get; set; }
        public Region? Region { get; set; }
    }

    // A cross-collection navigation under an owned one (Buyer -> Address -> Region); reaches past the looked-up
    // Buyer document, so the native Include must decline it.
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

    // Unidirectional: no inverse collection on UniCustomer, so UniOrder is streaming-eligible.
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
                // No inverse collection on UniCustomer.
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

    // Unidirectional root whose target owns an (always eager-loaded) Address: IsEligible passes for the root,
    // AllPendingLookupsAreStreamable fails for the target.
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
                // No inverse collection anywhere in this model.
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
                // A plain $lookup can't carry this predicate, so an Include of Buyer must decline.
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
