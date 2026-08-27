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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-347 (slice 3) native inner-<c>Select</c> owned-collection <c>SelectMany</c> —
/// <c>q.SelectMany(o =&gt; o.Items.Select(i =&gt; new {o.X, i.Y}))</c> — translates to a native
/// <c>$unwind</c> + <c>$project</c> pipeline (inner-join flatten semantics: an owner with an empty/absent
/// owned collection contributes no rows). Slice 4 (below) extends this to the explicit-result-selector /
/// query-syntax form — <c>SelectMany(o =&gt; o.Items, (o, i) =&gt; new {...})</c> / <c>from o in q from i in
/// o.Items select new {...}</c> — which normalizes to the SAME bare-nav + trailing-<c>Select</c> tree and goes
/// native the same way, so it is no longer a hard-fail-in-every-mode shape. What remains out of scope and
/// still hard-fails translation exactly as before (no regression) — <see cref="NativeSelectManyBinder.TryBind"/>
/// returning <see langword="false"/> means EF's own translation-failure path is reached directly, with no
/// <c>MarkNotNativelyRepresentable</c>-style graceful degradation attempt at all, so these throw in every
/// <see cref="MongoQueryMode"/>, not just <see cref="MongoQueryMode.NativeOnly"/> — is: a computed/non-member
/// projection leaf, a SelectMany over a reference (non-owned) navigation, and a nested SelectMany. A bare-nav
/// SelectMany whose trailing selector projects the WHOLE inner entity rather than a member-access projection
/// (<c>SelectMany(o =&gt; o.Items, (o, i) =&gt; i)</c> / <c>from o in q from i in o.Items select i</c> / the
/// bare 1-arg <c>SelectMany(o =&gt; o.Items)</c>, which normalizes to the identical tree) is a DIFFERENT,
/// narrower case (see the <c>Bare_owned_whole_inner_element_*</c> / <c>Bare_SelectMany_*</c> tests below).
/// <see cref="NativeSelectManyBinder.TryBindBareNavUnwind"/> accepts the bare nav structurally (it cannot see
/// the trailing selector yet), so <c>UnwindSource</c> is set; the trailing <c>ti =&gt; ti.Inner</c> selector
/// then bypasses both the pending-SelectMany projection branch (it is not an anonymous/DTO construction) and
/// the post-terminal
/// <see cref="MongoQueryableMethodTranslatingExpressionVisitor.IsTransparentIdentifierMemberAccessSelector"/>
/// guard (it IS that shape). **EF-347 Task 3: for an OWNED collection, this shape now goes NATIVE** — a
/// dedicated guard in <c>TranslateSelect</c> ("<c>UnwindSource</c> set, <c>Projection</c> still empty, AND the
/// selector is the whole-inner-entity <c>IsWholeInnerEntitySelector</c> shape, AND the unwind's
/// <c>Kind</c> is <c>Owned</c>") sets <c>UnwindSource.WholeElement</c>, driving the lowerer to emit
/// <c>$unwind(includeArrayIndex)</c> + <c>$replaceRoot($mergeObjects)</c> so the owned element becomes the root
/// document, carrying its owner key + array ordinal along under sentinel field names; the standard DOM shaper,
/// rooted at the owned element type by <c>MongoShapedQueryCompilingExpressionVisitor</c>'s dedicated
/// <c>WholeElement</c> branch, then materializes it, reading those sentinel fields back for the owned key (see
/// <c>MongoProjectionBindingRemovingExpressionVisitor.CreateGetValueExpression</c>'s re-rooted branch). This
/// SUPERSEDES the prior clean <see cref="NotSupportedException"/> decline for the OWNED case specifically — the
/// pre-Task-3 "graceful" <c>MarkNotNativelyRepresentable()</c> fallback did not actually work for this shape
/// (the driver-LINQ path could not materialize a bare owned-collection element with no owner context either, so
/// it used to crash with an internal <see cref="System.Collections.Generic.KeyNotFoundException"/> instead of
/// declining cleanly), so the clean decline that replaced it was itself later superseded by a genuine native
/// translation once the re-rooting mechanism (<c>$replaceRoot</c> + sentinel key-carrying) was built. A tracking
/// (non-<c>AsNoTracking</c>) query for this owned whole-inner shape still throws — but now it is EF Core's OWN
/// "can't track an owned entity without its owner" <see cref="InvalidOperationException"/>, firing at shaper-
/// materializer injection time, not this provider's translation-time guard (see
/// <c>Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode</c>). The whole-OUTER form
/// (<c>select o</c> / <c>(o, i) =&gt; o</c>) is NOT this case (<c>IsWholeInnerEntitySelector</c> requires
/// <c>Member.Name == "Inner"</c>) — it remains declined with the original clean
/// <see cref="NotSupportedException"/>, thrown at TRANSLATION time (before <see cref="MongoQueryMode"/> is even
/// consulted) so the SAME exception fires in all three modes, regardless of tracking (see
/// <c>Whole_outer_entity_form_still_declines_cleanly_in_every_mode</c> and, for the REFERENCE form,
/// <c>Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode</c>). A whole-inner result from a
/// REFERENCE (non-owned) navigation was ALSO originally excluded here (the Kind == Owned check above), but this
/// is superseded by EF-347 Task 4/5 below — see
/// <c>Reference_form_bare_entity_result_goes_native_all_three_spellings</c>, which now goes NATIVE for the
/// non-eager-loaded case, and <c>Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode</c>,
/// which still declines cleanly for a reference element that itself eager-loads a further navigation (via a
/// genuine cross-collection AutoInclude, which does NOT reach <c>IsWholeElementRepresentable</c>'s reference
/// eager-nav guard at all — see that test's own comment; <c>Reference_form_bare_entity_with_owned_embedded_navigation_declines_cleanly_via_representability_guard</c>
/// is the test that DOES prove the guard is reachable, via an eager-loaded OWNED sub-navigation instead). By
/// contrast, the computed-leaf case (e.g.
/// <c>SelectMany(o =&gt; o.Items, (o, i) =&gt; new { X = i.Price * 2 })</c>) is NOT this shape either (its
/// selector body is a <c>NewExpression</c>, not a bare member access) and still falls back gracefully via
/// <c>MarkNotNativelyRepresentable()</c> — its driver-LINQ fallback genuinely works. Composing a FURTHER
/// operator after an already-
/// native SelectMany is a separate, narrower story (see the
/// <c>*_after_SelectMany_hard_fails_in_every_mode</c> / <c>Count_after_SelectMany_falls_back_gracefully...</c>
/// tests below): most such operators still hard-fail in every mode too (their own guards reach
/// <see cref="Expressions.MongoSelectDefinition.HasTerminalOperator"/>, but the driver-LINQ fallback the guard
/// would normally allow cannot rebuild a shaper that re-reads the SelectMany's already-by-index-resolved
/// projection) — except a cardinality-only operator like <c>Count</c>, which needs no shaper at all and so
/// falls back gracefully (and correctly) under <see cref="MongoQueryMode.Native"/>/<see cref="MongoQueryMode.DriverLinq"/>,
/// throwing only under <see cref="MongoQueryMode.NativeOnly"/> like any other graceful-fallback shape.
/// EF-347 slice 5 (see the <c>Reference_*</c> tests below) makes the SAME explicit-result-selector / query-
/// syntax projected form ALSO go native over a cross-collection REFERENCE (non-owned) navigation — via
/// <see cref="NativeSelectManyBinder.TryBindReferenceNavUnwind"/>, a <c>ForceUnwind</c> <c>$lookup</c> +
/// inner-join <c>$unwind</c> (<c>preserveNullAndEmptyArrays: false</c>) — so "a SelectMany over a reference
/// (non-owned) navigation" above is no longer categorically a hard-fail: it is the INNER-<c>Select</c> form
/// specifically (<see cref="NativeSelectManyBinder.TryBind"/> rejecting it structurally) that still hard-fails
/// for a reference nav, not the explicit/query-syntax form. UNLIKE the owned forms, the reference form has NO
/// driver-LINQ baseline at all (the driver's own LINQ v3 provider rejects any cross-collection SelectMany
/// outright), so its "graceful" <c>MarkNotNativelyRepresentable()</c> fallback paths (e.g. its computed-leaf
/// case) still throw under Native/DriverLinq too — see <c>Reference_form_computed_leaf_hard_fails_in_every_mode</c>
/// and <c>Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode</c>.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSelectManyTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";

        // EF-347 root-scope computed leaf: a numeric member on the OUTER/root entity, so a computed leaf like
        // `o.Rank * 2m` inside a SelectMany trailing projection exercises the scope-0 (no-prefix, "$Rank")
        // re-rooting path end-to-end, not just Item.Price's scope-1 ("$Items.Price") path.
        public decimal Rank { get; set; }
        public List<Item> Items { get; set; } = [];
    }

    private class Item
    {
        // Deliberately shares its "Name" member name with Owner, to prove the two-scope binder never
        // conflates the outer (unprefixed "$Name") and inner ("$Items.Name") field refs.
        public string Name { get; set; } = "";
        public decimal Price { get; set; }

        // Regression coverage (MongoFieldPrefixRewriter throwing instead of declining on
        // MongoConditionalExpression/MongoDatePartExpression cross-scope leaves): a DateTime field so an
        // inner-scope date-part extraction (i.Occurred.Year) and a conditional (i.Flag ? ... : ...) can appear
        // as a computed result-selector leaf that requires the scope-1 ("$Items.Occurred") prefix.
        public DateTime Occurred { get; set; }
        public bool Flag { get; set; }
    }

    private static Owner[] SeedOwners() =>
    [
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Alice", Rank = 3m,
            Items =
            [
                new Item { Name = "Widget", Price = 9.99m, Occurred = new DateTime(2020, 3, 15), Flag = true },
                new Item { Name = "Gadget", Price = 19.99m, Occurred = new DateTime(2021, 7, 4), Flag = false },
            ],
        },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Rank = 5m, Items = [] }, // empty owned collection
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Carol", Rank = 7m,
            Items = [new Item { Name = "Thing", Price = 5m, Occurred = new DateTime(2022, 12, 25), Flag = true }],
        },
        // EF-347 correlated-beyond-outer: discriminating owner so a correlated predicate like
        // i.Name == o.Name has both a satisfying row (Match/Match) and a non-satisfying one (Match/NoMatch) —
        // none of Alice/Bob/Carol's items ever equal their own owner's Name, so without this owner every
        // correlated-equality test below would pass vacuously (empty == empty).
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Match", Rank = 11m,
            Items =
            [
                new Item { Name = "Match", Price = 3m }, // i.Name == o.Name → included
                new Item { Name = "NoMatch", Price = 4m }, // i.Name != o.Name → excluded
            ],
        },
    ];

    private SingleEntityDbContext<Owner> CreateContext(Owner[] seed, MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Owner>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed);

        return SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<Owner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private SingleEntityDbContext<Owner> CreateContextWithLogging(
        Owner[] seed, MongoQueryMode mode, string name, out SpyLoggerProvider spyLogger)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Owner>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed);

        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: mb => mb.Entity<Owner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    [Fact]
    public void Inner_select_form_goes_native_with_correct_results_and_mql()
    {
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Inner_select_form_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = db.Entities
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Price\" : \"$Items.Price\"", message);
    }

    [Fact]
    public void Inner_select_form_produces_correct_results_under_explicit_DriverLinq_mode()
    {
        // The success tests above all run under NativeOnly (the only way to positively prove "this executed
        // via the native path"). This test locks the OTHER side of the invariant: the same supported shape
        // must ALSO produce identical, correct results when forced through the driver-LINQ path
        // (MongoQueryMode.DriverLinq) rather than falling through Native's transitive default coverage.
        // Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly already proves DriverLinq can
        // execute a captured `.SelectMany(...).Count()` chain, so this supported inner-Select chain is
        // expected to succeed here too, not throw.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Inner_select_form_produces_correct_results_under_explicit_DriverLinq_mode));

        var result = db.Entities
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        Assert.Equal(expected, result);
    }

    private sealed class ItemDto
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    [Fact]
    public void Inner_select_MemberInit_dto_form_goes_native_with_correct_results_and_mql()
    {
        // Covers the MemberInitExpression arm of NativeSelectManyBinder.TryReadProjection and
        // BuildSelectManyResultShaper — the tests above only exercise the anonymous-type (`new { ... }`) arm
        // of those same two methods. Same shape as Inner_select_form_goes_native_with_correct_results_and_mql,
        // just projecting into a named DTO with a MemberInitExpression body instead.
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Inner_select_MemberInit_dto_form_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = db.Entities
            .SelectMany(o => o.Items.Select(i => new ItemDto { Name = o.Name, Price = i.Price }))
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items.Select(i => new ItemDto { Name = o.Name, Price = i.Price }))
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal. ItemDto has no Equals override, so
        // compare member-wise (mirrors NativeGateRoutingTests' MemberInit-DTO assertion idiom) rather than
        // relying on Assert.Equal's reference-equality default for a plain class.
        Assert.Equal(expected.Count, result.Count);
        for (var idx = 0; idx < expected.Count; idx++)
        {
            Assert.Equal(expected[idx].Name, result[idx].Name);
            Assert.Equal(expected[idx].Price, result[idx].Price);
        }

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Price\" : \"$Items.Price\"", message);
    }

    [Fact]
    public void Empty_or_absent_owned_collection_contributes_no_rows()
    {
        var seed = SeedOwners(); // Bob has an empty Items list
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Empty_or_absent_owned_collection_contributes_no_rows));

        var result = db.Entities.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price })).ToList();

        Assert.Equal(5, result.Count); // 2 from Alice + 1 from Carol + 2 from Match; Bob (empty) contributes 0
        Assert.DoesNotContain(result, r => r.Name == "Bob");
    }

    [Fact]
    public void Shared_outer_inner_member_name_resolves_to_distinct_values()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Shared_outer_inner_member_name_resolves_to_distinct_values));

        var result = db.Entities
            .SelectMany(o => o.Items.Select(i => new { OuterName = o.Name, InnerName = i.Name }))
            .AsEnumerable()
            .OrderBy(r => r.OuterName).ThenBy(r => r.InnerName)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items.Select(i => new { OuterName = o.Name, InnerName = i.Name }))
            .OrderBy(r => r.OuterName).ThenBy(r => r.InnerName)
            .ToList();

        Assert.Equal(expected, result);
        // Sanity: outer/inner never conflated (an owner's own name never leaks into InnerName and vice versa) —
        // EXCEPT the "Match" owner's "Match" item, which the EF-347 correlated-beyond-outer seed (see
        // SeedOwners) DELIBERATELY names identically to its owner so the correlated-equality tests elsewhere
        // have a genuinely matching row. That is a real, seeded data coincidence, not a scoping bug, so it is
        // explicitly excluded here rather than silently weakening this sanity check for every other row.
        Assert.All(result.Where(r => !(r.OuterName == "Match" && r.InnerName == "Match")),
            r => Assert.NotEqual(r.OuterName, r.InnerName));
        Assert.Contains(result, r => r.OuterName == "Match" && r.InnerName == "Match");
    }

    [Fact]
    public void Explicit_result_selector_form_goes_native_with_correct_results_and_mql()
    {
        // EF-347 slice 4: the explicit-result-selector form — SelectMany(collectionSelector, resultSelector)
        // — normalizes (via EF's nav-expansion) to the SAME bare-nav + TransparentIdentifier tree as the
        // query-syntax form below; both are exercised for completeness.
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = db.Entities
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Price\" : \"$Items.Price\"", message);
    }

    [Fact]
    public void Query_syntax_form_goes_native_with_correct_results_and_mql()
    {
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Query_syntax_form_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = (from o in db.Entities
                       from i in o.Items
                       select new { o.Name, i.Price })
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = (from o in seed
                         from i in o.Items
                         select new { o.Name, i.Price })
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Price\" : \"$Items.Price\"", message);
    }

    [Fact]
    public void Explicit_result_selector_form_produces_correct_results_under_explicit_DriverLinq_mode()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Explicit_result_selector_form_produces_correct_results_under_explicit_DriverLinq_mode));

        var result = db.Entities
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Explicit_result_selector_form_shared_outer_inner_member_name_resolves_to_distinct_values()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_shared_outer_inner_member_name_resolves_to_distinct_values));

        var result = db.Entities
            .SelectMany(o => o.Items, (o, i) => new { OuterName = o.Name, InnerName = i.Name })
            .AsEnumerable()
            .OrderBy(r => r.OuterName).ThenBy(r => r.InnerName)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { OuterName = o.Name, InnerName = i.Name })
            .OrderBy(r => r.OuterName).ThenBy(r => r.InnerName)
            .ToList();

        Assert.Equal(expected, result);
        // See Shared_outer_inner_member_name_resolves_to_distinct_values's comment re: the "Match"/"Match" row —
        // a deliberate seeded coincidence for the correlated-equality tests, not a scoping bug.
        Assert.All(result.Where(r => !(r.OuterName == "Match" && r.InnerName == "Match")),
            r => Assert.NotEqual(r.OuterName, r.InnerName));
        Assert.Contains(result, r => r.OuterName == "Match" && r.InnerName == "Match");
    }

    [Fact]
    public void Explicit_result_selector_form_empty_or_absent_owned_collection_contributes_no_rows()
    {
        var seed = SeedOwners(); // Bob has an empty Items list
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_empty_or_absent_owned_collection_contributes_no_rows));

        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price }).ToList();

        Assert.Equal(5, result.Count); // 2 from Alice + 1 from Carol + 2 from Match; Bob (empty) contributes 0
        Assert.DoesNotContain(result, r => r.Name == "Bob");
    }

    [Fact]
    public void Explicit_result_selector_MemberInit_dto_form_goes_native_with_correct_results_and_mql()
    {
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_MemberInit_dto_form_goes_native_with_correct_results_and_mql), out var spyLogger);

        var result = db.Entities
            .SelectMany(o => o.Items, (o, i) => new ItemDto { Name = o.Name, Price = i.Price })
            .AsEnumerable()
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new ItemDto { Name = o.Name, Price = i.Price })
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        Assert.Equal(expected.Count, result.Count);
        for (var idx = 0; idx < expected.Count; idx++)
        {
            Assert.Equal(expected[idx].Name, result[idx].Name);
            Assert.Equal(expected[idx].Price, result[idx].Price);
        }

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Price\" : \"$Items.Price\"", message);
    }

    [Fact]
    public void Explicit_result_selector_form_computed_arithmetic_leaf_goes_native()
    {
        // EF-347 SelectMany computed-leaf: a single-scope (inner-only) arithmetic leaf in the trailing
        // projection now binds natively via TryBindTransparentIdentifierProjection's arithmetic branch
        // (reusing TryTranslateValue). Owned inner-only projection HAS a driver oracle, so assert parity
        // across Native/DriverLinq AND that NativeOnly succeeds (the "went native" signal).
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2m })
            .OrderBy(r => r.X).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_computed_arithmetic_leaf_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2m })
                .AsEnumerable().OrderBy(r => r.X).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Root_scope_computed_leaf_in_selectmany_goes_native()
    {
        // EF-347 external-review gap: the prior computed-leaf coverage above only exercised an INNER-scope
        // (scope k>0, unwind-path-prefixed "$Items.Price") leaf end-to-end. A pure OUTER/root-scope (scope 0,
        // NO-prefix "$Rank") computed leaf was previously only covered at the IR level. Owner.Rank makes this
        // shape materializable end-to-end: NativeOnly succeeding is the "went native" signal, and the expected
        // set proves the VALUES are right too (one row per unwound item, each carrying its owner's Rank * 2).
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { Doubled = o.Rank * 2m })
            .OrderBy(r => r.Doubled).ToList();

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Root_scope_computed_leaf_in_selectmany_goes_native));
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { Doubled = o.Rank * 2m })
            .AsEnumerable().OrderBy(r => r.Doubled).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Root_scope_computed_leaf_in_selectmany_emits_unprefixed_field_in_mql()
    {
        // Same shape as Root_scope_computed_leaf_in_selectmany_goes_native, but additionally proves the
        // no-prefix claim directly against the emitted MQL: the $project's $multiply operates over "$Rank",
        // never "$Items.Rank" (Item has no Rank member at all, so a prefixed reference would be a bug, not
        // just a stylistic difference).
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Root_scope_computed_leaf_in_selectmany_emits_unprefixed_field_in_mql), out var spyLogger);

        _ = db.Entities.SelectMany(o => o.Items, (o, i) => new { Doubled = o.Rank * 2m }).ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$multiply", message);
        Assert.Contains("$Rank", message);
        Assert.DoesNotContain("Items.Rank", message);
    }

    [Fact]
    public void Mixed_operator_computed_leaves_in_selectmany_go_native()
    {
        // EF-347 external-review gap: all prior SelectMany computed-leaf coverage used multiply only. This
        // exercises +, -, %, and decimal / (NOT integer division, so Guard A's truncation check doesn't apply)
        // over an inner-only (i.Price) leaf, all four in one projection.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items,
                (o, i) => new { Sum = i.Price + 1m, Diff = i.Price - 1m, Mod = i.Price % 2m, Half = i.Price / 2m })
            .OrderBy(r => r.Sum).ToList();

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Mixed_operator_computed_leaves_in_selectmany_go_native));
        var result = db.Entities.SelectMany(o => o.Items,
                (o, i) => new { Sum = i.Price + 1m, Diff = i.Price - 1m, Mod = i.Price % 2m, Half = i.Price / 2m })
            .AsEnumerable().OrderBy(r => r.Sum).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Explicit_result_selector_form_computed_date_part_leaf_goes_native()
    {
        // Regression test for the MongoFieldPrefixRewriter gap: a date-part extraction (i.Occurred.Year) over
        // the INNER (scope-1) unwound element requires MongoFieldPrefixRewriter.Rewrite to prefix a
        // MongoDatePartExpression's operand with the unwind path ("$Items.Occurred"). Before the fix, this
        // reached the switch's default arm and threw NativeTranslationNotSupportedException in every
        // MongoQueryMode instead of falling back gracefully — asserted here under both Native and NativeOnly
        // to prove it now succeeds outright (native), not merely falls back.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { X = i.Occurred.Year + 1 })
            .OrderBy(r => r.X).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_computed_date_part_leaf_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { X = i.Occurred.Year + 1 })
                .AsEnumerable().OrderBy(r => r.X).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Explicit_result_selector_form_computed_conditional_leaf_goes_native()
    {
        // Regression test for the MongoFieldPrefixRewriter gap: a ternary (i.Flag ? ... : ...) over the INNER
        // (scope-1) unwound element requires MongoFieldPrefixRewriter.Rewrite to prefix a
        // MongoConditionalExpression's Test/IfTrue/IfFalse operands with the unwind path. Before the fix, this
        // hit the switch's default arm and threw NativeTranslationNotSupportedException in every
        // MongoQueryMode instead of falling back gracefully. NativeSelectManyBinder.IsArithmeticComputedLeaf
        // only admits a leaf whose TOP node is arithmetic (a bare top-level ternary declines there, unrelated
        // to this bug), so the conditional is nested inside a top-level `+` to reach the same computed-leaf
        // re-rooting path Explicit_result_selector_form_computed_date_part_leaf_goes_native exercises.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { X = (i.Flag ? 1m : 2m) + 0m })
            .OrderBy(r => r.X).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_computed_conditional_leaf_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { X = (i.Flag ? 1m : 2m) + 0m })
                .AsEnumerable().OrderBy(r => r.X).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Inner_select_form_filtered_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Inner_select_form_filtered_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m).Select(i => new { o.Name, i.Price }))
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Price > 6m).Select(i => new { o.Name, i.Price }))
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
        Assert.DoesNotContain(result, x => x.Price <= 6m); // "Thing" (5) excluded
    }

    [Fact]
    public void Explicit_result_selector_form_filtered_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Explicit_result_selector_form_filtered_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Filtered_owned_selectmany_emits_match_after_unwind_before_project()
    {
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Filtered_owned_selectmany_emits_match_after_unwind_before_project), out var spyLogger);

        _ = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$unwind", message);
        Assert.Contains("$match", message);
        Assert.Contains("Items.Price", message); // filter is scope-prefixed with the owned unwind path
    }

    [Fact]
    public void Bare_owned_whole_inner_element_filtered_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_filtered_goes_native));

        var result = db.Entities.AsNoTracking()
            .SelectMany(o => o.Items.Where(i => i.Price > 6m))
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        var expected = seed.SelectMany(o => o.Items.Where(i => i.Price > 6m))
            .Select(i => i.Name).OrderBy(n => n).ToList();

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Thing", result); // Price 5 excluded
    }

    [Fact]
    public void Filtered_owned_stacked_where_ands_together_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Filtered_owned_stacked_where_ands_together_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Price > 6m).Where(i => i.Name != "Gadget"), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Price > 6m && i.Name != "Gadget"), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Filtered_owned_excluding_all_children_contributes_no_rows()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Filtered_owned_excluding_all_children_contributes_no_rows));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == "nonexistent"), (o, i) => new { o.Name, i.Price })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Filtered_owned_composes_with_parametrized_outer_predicate()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly, nameof(Filtered_owned_composes_with_parametrized_outer_predicate));

        var ownerName = "Alice";
        var result = db.Entities
            .Where(o => o.Name == ownerName)
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Price).ToList();

        var expected = seed.Where(o => o.Name == ownerName)
            .SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_inner_select_form_goes_native()
    {
        // o.Items.Where(i => i.Name == o.Name) — correlated beyond the owner/element pair. Now native: the
        // ReferencesParameter guard routes the conjunct to a two-scope translator that renders it as $expr in
        // the post-$unwind $match (see Owned_correlated_beyond_outer_emits_expr_match_after_unwind below). No
        // driver-LINQ oracle exists for ANY correlated owned SelectMany shape (spike-confirmed), so this is
        // proven NativeOnly + an in-memory oracle, not Native/DriverLinq parity. Only the "Match" owner's
        // "Match" item (see SeedOwners) satisfies the predicate — its "NoMatch" sibling, and every item of
        // Alice/Bob/Carol, does not — so the expected set is neither empty nor "everything".
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_inner_select_form_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Select(i => new { o.Name, i.Price }))
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Select(i => new { o.Name, i.Price }))
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result); // non-vacuous
    }

    [Fact]
    public void Owned_correlated_beyond_outer_explicit_form_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_explicit_form_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_bare_whole_element_goes_native()
    {
        // Bare-whole-element result requires AsNoTracking() (owned element without owner) — same tracking
        // contract as Bare_owned_whole_inner_element_goes_native_all_three_spellings above.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_bare_whole_element_goes_native));

        var result = db.Entities.AsNoTracking()
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name))
            .AsEnumerable().Select(i => i.Price).OrderBy(p => p).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name))
            .Select(i => i.Price).OrderBy(p => p).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_stacked_where_goes_native()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_stacked_where_goes_native));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Where(i => i.Price > 0m), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name).Where(i => i.Price > 0m), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_excluding_all_children_contributes_no_rows()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_excluding_all_children_contributes_no_rows));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name && i.Price < 0m), (o, i) => new { o.Name, i.Price })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_composes_with_parametrized_outer_predicate()
    {
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_composes_with_parametrized_outer_predicate));

        var cutoff = "Bob";
        var result = db.Entities.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_emits_expr_match_after_unwind()
    {
        // MQL assertion: the correlated conjunct renders as $expr in the post-$unwind $match, comparing the
        // unwind-path-prefixed inner field ("$Items.Name") to the root-relative outer field ("$Name") — no
        // $lookup alias exists for owned data (unlike the reference form's "_lookup_Refs.Tag"), so the inner
        // field is prefixed with the unwind path itself.
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_emits_expr_match_after_unwind), out var spyLogger);

        _ = db.Entities
            .SelectMany(o => o.Items.Where(i => i.Name == o.Name), (o, i) => new { o.Name, i.Price })
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$unwind", message);
        Assert.Contains("$match", message);
        Assert.Contains("$expr", message);
        Assert.Contains("Items.Name", message); // inner field, unwind-path-prefixed
        Assert.Contains("$Name", message); // outer field, root-relative
    }

    [Fact]
    public void Filtered_owned_computed_operator_hard_fails_in_every_mode()
    {
        // A filter using an operator the native translator does not support (string.ToUpper) declines the
        // ordinary way (inner translator returns false). Same no-oracle hard-fail in every mode.
        var seed = SeedOwners();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Filtered_owned_computed_operator_hard_fails_in_every_mode) + mode);
            Assert.ThrowsAny<Exception>(() =>
                db.Entities.SelectMany(o => o.Items.Where(i => i.Name.ToUpper() == "WIDGET"), (o, i) => new { o.Name, i.Price }).ToList());
        }
    }

    [Fact]
    public void Filtered_owned_computed_arithmetic_leaf_goes_native()
    {
        // A FILTERED owned SelectMany whose trailing projection has a single-scope (inner-only) arithmetic
        // leaf (i.Price * 2m) now goes native (the $match from the inner Where is emitted before the
        // computed $project). Inner-only projection has a driver oracle → parity + NativeOnly succeeds.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { X = i.Price * 2m })
            .OrderBy(r => r.X).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Filtered_owned_computed_arithmetic_leaf_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items.Where(i => i.Price > 6m), (o, i) => new { X = i.Price * 2m })
                .AsEnumerable().OrderBy(r => r.X).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Cross_scope_computed_leaf_in_selectmany_goes_native()
    {
        // EF-422 part A. A computed leaf mixing the OUTER/root scope (o.Rank) and the INNER/unwound-element
        // scope (i.Price) used to decline: TryTranslateSingleScopeComputedLeaf re-roots the WHOLE arithmetic
        // subtree onto one scope's synthetic parameter, so a two-scope subtree tripped its CrossScope flag.
        // The cross-scope fallback translates each operand against its own scope instead.
        //
        // The seed makes the VALUES discriminating, not just the row count: every owner has a distinct Rank
        // (3/5/7/11) and every item a distinct Price, so a leaf that mis-scoped either operand (reading
        // "$Items.Rank" — Item has no Rank at all — or "$Price" at the root) would produce nulls/zeros rather
        // than the products asserted here. An owned inner+outer projection HAS a driver-LINQ oracle, so all
        // three modes must agree AND NativeOnly must succeed (the "went native" signal).
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { Total = o.Rank * i.Price })
            .OrderBy(r => r.Total).ToList();
        Assert.Equal(5, expected.Count);
        Assert.All(expected, r => Assert.True(r.Total > 0m));

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode, nameof(Cross_scope_computed_leaf_in_selectmany_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { Total = o.Rank * i.Price })
                .AsEnumerable().OrderBy(r => r.Total).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Cross_scope_computed_leaf_emits_both_scopes_unprefixed_and_prefixed_in_mql()
    {
        // The MQL companion to the test above: the ONE $multiply must reference the outer operand at the
        // document root ("$Rank") and the inner operand under the unwind path ("$Items.Price"). Asserting both
        // sides is what makes this discriminating — a binder that resolved both operands against a single
        // scope would still emit a $multiply of two fields.
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(Cross_scope_computed_leaf_emits_both_scopes_unprefixed_and_prefixed_in_mql), out var spyLogger);

        _ = db.Entities.SelectMany(o => o.Items, (o, i) => new { Total = o.Rank * i.Price }).ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$multiply", message);
        Assert.Contains("\"$Rank\"", message);
        Assert.Contains("\"$Items.Price\"", message);
        Assert.DoesNotContain("$Items.Rank", message);
    }

    [Fact]
    public void Cross_scope_computed_leaf_with_a_constant_operand_goes_native()
    {
        // The cross-scope subtree is now an OPERAND of the top-level node ((o.Rank * i.Price) + 1), so the
        // per-operand translation has to recurse; the constant `1` exercises the scope-free operand arm.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { Total = o.Rank * i.Price + 1m })
            .OrderBy(r => r.Total).ToList();

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Cross_scope_computed_leaf_with_a_constant_operand_goes_native));
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { Total = o.Rank * i.Price + 1m })
            .AsEnumerable().OrderBy(r => r.Total).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Cross_scope_string_concatenation_leaf_declines_and_answers_correctly()
    {
        // The cross-scope binder recombines the two translated operands itself, so it must reproduce
        // TranslateOperand's own numeric-type guard: `o.Name + i.Name` is ExpressionType.Add, and both
        // operands translate perfectly well as string fields, but "$add" over strings is a hard server error.
        // Without the guard this shape would go native and blow up (or answer wrongly) instead of falling back.
        // Owner "Match" makes the concatenation values distinguishable.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { Combined = o.Name + i.Name })
            .OrderBy(r => r.Combined).ToList();

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(Cross_scope_string_concatenation_leaf_declines_and_answers_correctly) + "NativeOnly"))
        {
            Assert.ThrowsAny<Exception>(() =>
                nativeOnly.Entities.SelectMany(o => o.Items, (o, i) => new { Combined = o.Name + i.Name }).ToList());
        }

        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(Cross_scope_string_concatenation_leaf_declines_and_answers_correctly));
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { Combined = o.Name + i.Name })
            .AsEnumerable().OrderBy(r => r.Combined).ToList();
        Assert.Equal(expected, result);
    }

    [Fact]
    public void One_arg_selectmany_bare_computed_leaf_goes_native()
    {
        // EF-422 part B. The ONE-arg `SelectMany(o => o.Items).Select(i => i.Price * 2)` form folds (in EF's
        // nav-expansion) into a trailing Select over the transparent identifier whose body is a BARE
        // arithmetic expression — no `new {}` wrapper, so no member name, so the projection binder used to
        // decline it outright. It now binds under the reserved `_v` alias. Parity across all three modes plus
        // NativeOnly succeeding is the "went native, with the same values" signal.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items).Select(i => i.Price * 2m).OrderBy(x => x).ToList();
        Assert.Equal(5, expected.Count);

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode, nameof(One_arg_selectmany_bare_computed_leaf_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items).Select(i => i.Price * 2m)
                .AsEnumerable().OrderBy(x => x).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void One_arg_selectmany_bare_cross_scope_computed_leaf_goes_native()
    {
        // Parts A and B compose: a bare (unwrapped) body that is ALSO cross-scope. Spelled with the two-arg
        // result selector, since the one-arg form cannot reach the outer element at all.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => o.Rank * i.Price).OrderBy(x => x).ToList();

        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(One_arg_selectmany_bare_cross_scope_computed_leaf_goes_native));
        var result = db.Entities.SelectMany(o => o.Items, (o, i) => o.Rank * i.Price)
            .AsEnumerable().OrderBy(x => x).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void One_arg_selectmany_bare_computed_leaf_emits_the_synthetic_alias_in_mql()
    {
        var seed = SeedOwners();
        using var db = CreateContextWithLogging(seed, MongoQueryMode.NativeOnly,
            nameof(One_arg_selectmany_bare_computed_leaf_emits_the_synthetic_alias_in_mql), out var spyLogger);

        _ = db.Entities.SelectMany(o => o.Items).Select(i => i.Price * 2m).ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"_v\"", message);
        Assert.Contains("$multiply", message);
        Assert.Contains("\"$Items.Price\"", message);
    }

    [Fact]
    public void One_arg_selectmany_bare_MEMBER_leaf_still_declines_and_answers_correctly()
    {
        // Boundary: only an ARITHMETIC bare body is admitted. A bare member access is a path-addressable leaf
        // whose alias would have to be its own document path for a late fallback to read it correctly
        // (NativeProjectionBinder's tier 1) — a different contract from the `_v` tier — so it keeps declining
        // and falling back, which still answers correctly.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items).Select(i => i.Name).OrderBy(x => x).ToList();

        using (var nativeOnly = CreateContext(seed, MongoQueryMode.NativeOnly,
                   nameof(One_arg_selectmany_bare_MEMBER_leaf_still_declines_and_answers_correctly) + "NativeOnly"))
        {
            Assert.ThrowsAny<Exception>(() =>
                nativeOnly.Entities.SelectMany(o => o.Items).Select(i => i.Name).ToList());
        }

        using var db = CreateContext(seed, MongoQueryMode.Native,
            nameof(One_arg_selectmany_bare_MEMBER_leaf_still_declines_and_answers_correctly));
        var result = db.Entities.SelectMany(o => o.Items).Select(i => i.Name)
            .AsEnumerable().OrderBy(x => x).ToList();
        Assert.Equal(expected, result);
    }

    private class NestedSubFilterOwner
    {
        public ObjectId Id { get; set; }
        public List<NestedSubFilterItem> Items { get; set; } = [];
    }

    private class NestedSubFilterItem
    {
        public decimal Price { get; set; }
        public NestedSubFilterSub Sub { get; set; } = new();
    }

    private class NestedSubFilterSub
    {
        public string City { get; set; } = "";
    }

    private SingleEntityDbContext<NestedSubFilterOwner> CreateNestedSubFilterContext(
        NestedSubFilterOwner[] seed, MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<NestedSubFilterOwner>(collectionName);
        if (seed.Length > 0)
            collection.InsertMany(seed);

        return SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<NestedSubFilterOwner>()
                .OwnsMany(o => o.Items, ib => ib.OwnsOne(i => i.Sub)),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    [Fact]
    public void Filtered_owned_nested_subproperty_predicate_now_goes_native_via_EF424()
    {
        // FLIPPED BY EF-424 (this test USED TO ASSERT A HARD FAIL, under the name
        // "..._hard_fails_in_every_mode_not_double_prefixed" — the paragraph that stood here explained why).
        //
        // CRITICAL regression fix (EF-322 whole-branch review, pre-EF-424): TryBuildOwnedInnerFilter builds a
        // SINGLE-scope MongoExpressionTranslator on the OWNED inner element type (NestedSubFilterItem — NOT a
        // document root) and then prefixes whatever it translates with the unwind path ("Items") via
        // MongoFieldPrefixRewriter.Rewrite. When the inner filter itself reaches through a FURTHER owned
        // single-reference sub-property (i.Sub.City), it used to hit MongoExpressionTranslator.
        // TryResolveOwnedFieldPath's GetDocumentPath()-based construction — a path ALREADY rooted from the
        // TRUE document root, so it ALREADY contained the "Items" containing-element name — which
        // TryBuildOwnedInnerFilter's blanket MongoFieldPrefixRewriter.Rewrite(..., "Items") then prefixed a
        // SECOND time, producing a double-prefixed, non-existent field path ("Items.Items.Sub.City") that
        // silently matched nothing under Native. The interim fix (a blanket "decline whenever _entityType is
        // not IsDocumentRoot()" guard) closed the silent-wrong-data hole by hard-failing this shape in every
        // mode instead — safe, but it also gave up a genuinely representable shape.
        //
        // EF-424 replaced that guard with a SCOPE-RELATIVE construction (joining each hop's own containing
        // element name, mirroring the sibling TryResolveOwnedCollectionPath), so TryResolveOwnedFieldPath now
        // resolves i.Sub.City to "Sub.City" — relative to the element scope, with NO document-root prefix
        // baked in — which composes with TryBuildOwnedInnerFilter's own single "Items" prefix EXACTLY ONCE,
        // emitting "Items.Sub.City". Confirmed empirically (not assumed) by running this exact test body: it
        // now returns the correct two rows, identically, in every mode — the double-prefixing risk this test
        // was written to catch never resurfaces, because the fix makes the path relative to the SAME scope the
        // caller's own prefix already accounts for, rather than merely skipping the guard.
        var seed = new[]
        {
            new NestedSubFilterOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items =
                [
                    new NestedSubFilterItem { Price = 9.99m, Sub = new NestedSubFilterSub { City = "NYC" } },
                    new NestedSubFilterItem { Price = 19.99m, Sub = new NestedSubFilterSub { City = "LA" } },
                ],
            },
            new NestedSubFilterOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items = [new NestedSubFilterItem { Price = 5m, Sub = new NestedSubFilterSub { City = "NYC" } }],
            },
        };

        var expected = seed.SelectMany(o => o.Items.Where(i => i.Sub.City == "NYC"), (o, i) => new { i.Price })
            .Select(r => r.Price).OrderBy(p => p).ToList();
        Assert.Equal([5m, 9.99m], expected); // sanity: the fixture has the discriminating rows this test needs.

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestedSubFilterContext(seed, mode,
                nameof(Filtered_owned_nested_subproperty_predicate_now_goes_native_via_EF424) + mode);

            var result = db.Entities
                .SelectMany(o => o.Items.Where(i => i.Sub.City == "NYC"), (o, i) => new { i.Price })
                .AsEnumerable().Select(r => r.Price).OrderBy(p => p).ToList();

            Assert.Equal(expected, result);
        }
    }

    private class RefOwner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";

        // EF-347 correlated-beyond-FK: paired with RefItem.Score for a numeric-comparison correlated
        // predicate (r.Score >= o.Threshold) that exercises $expr $gte, not just field-to-field equality.
        public int Threshold { get; set; }
        public List<RefItem> Refs { get; set; } = [];
    }

    private class RefItem
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";

        // Deliberately shares its "Name" member name with RefOwner (mirrors Item's "Name" vs. Owner's "Name"
        // in the owned-collection fixture above), to prove the two-scope binder never conflates the outer
        // ("$Name") and inner ("$_lookup_Refs.Name") field refs for the REFERENCE form either.
        public string Name { get; set; } = "";

        // See RefOwner.Threshold.
        public int Score { get; set; }

        // Nullable, and deliberately null on most seeded rows, so `r.Note != null` is a DISCRIMINATING inner
        // filter (EF-355: a `!= null` user filter must never be absorbed into the FK correlation's null-guard
        // handling and silently dropped — a dropped filter returns every child instead of only these).
        public string? Note { get; set; }

        public ObjectId? OwnerId { get; set; }
        public RefOwner? Owner { get; set; }
    }

    private sealed class RefOwnerItemDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _refsCollection;

        public DbSet<RefOwner> Owners { get; set; } = null!;
        public DbSet<RefItem> Refs { get; set; } = null!;

        public RefOwnerItemDbContext(TemporaryDatabaseFixture database, string ownersCollection, string refsCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode, null))
        {
            _ownersCollection = ownersCollection;
            _refsCollection = refsCollection;
        }

        public RefOwnerItemDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string refsCollection, MongoQueryMode mode,
            ILoggerFactory loggerFactory)
            : base(BuildOptions(database, mode, loggerFactory))
        {
            _ownersCollection = ownersCollection;
            _refsCollection = refsCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<RefOwnerItemDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            if (loggerFactory != null)
                optionsBuilder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RefOwner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Refs).WithOne(r => r.Owner).HasForeignKey(r => r.OwnerId);
            });
            modelBuilder.Entity<RefItem>(b => b.ToCollection(_refsCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// Seeds four principals — Alice (2 children), Bob (0 children — proves inner-join semantics), Carol (1
    /// child), and Dave (2 children) — mirroring <see cref="SeedOwners"/>'s owned-collection shape, but as a
    /// REFERENCE (cross-collection) relationship: <see cref="RefItem.OwnerId"/> is a real FK, not an embedded
    /// array.
    ///
    /// EF-347 correlated-beyond-FK (Task 3): Dave and his two children exist ONLY to make the
    /// correlated-beyond-FK predicates (r.Tag == o.Name, r.Name == o.Name, r.Score >= o.Threshold)
    /// DISCRIMINATING — i.e. each has both a satisfying row and a non-satisfying row, so a correlated test
    /// comparing empty==empty never passes vacuously:
    ///  - Dave.Refs[0] (Tag="Dave", Name="DaveItem1"): Tag == Dave's own Name ("Dave") → the ONE row satisfying
    ///    r.Tag == o.Name anywhere in the seed. Its own Name ("DaveItem1") does NOT equal "Dave", so it does
    ///    NOT also satisfy the shadow r.Name == o.Name predicate — the two correlated predicates are kept
    ///    genuinely distinct by this row.
    ///  - Dave.Refs[1] (Tag="DaveItem2Tag", Name="Dave"): Name == Dave's own Name ("Dave") → the ONE row
    ///    satisfying the shadow r.Name == o.Name predicate. Its Tag ("DaveItem2Tag") differs from its own Name
    ///    ("Dave"), so the shadow test genuinely reads RefItem.Name, not RefItem.Tag.
    /// Threshold/Score (below) are set on every owner/item so r.Score >= o.Threshold has both included and
    /// excluded rows too: Alice.Threshold=10 (Widget:Score=5 excluded, Gadget:Score=20 included); Carol.
    /// Threshold=8 (Thing:Score=8 included — exact boundary, proves >= not >); Dave.Threshold=100 (Refs[0]:
    /// Score=50 excluded, Refs[1]:Score=150 included).
    /// </summary>
    private static (RefOwner[] Owners, RefItem[] Items) SeedRefData()
    {
        var alice = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Alice", Threshold = 10 };
        var bob = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Bob" }; // no children
        var carol = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Carol", Threshold = 8 };
        var dave = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Dave", Threshold = 100 };

        var items = new[]
        {
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Widget", Name = "WidgetName", OwnerId = alice.Id, Score = 5, Note = "alice-note" },
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Gadget", Name = "GadgetName", OwnerId = alice.Id, Score = 20 },
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Thing", Name = "ThingName", OwnerId = carol.Id, Score = 8, Note = "carol-note" },
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Dave", Name = "DaveItem1", OwnerId = dave.Id, Score = 50 },
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "DaveItem2Tag", Name = "Dave", OwnerId = dave.Id, Score = 150 },
        };

        return ([alice, bob, carol, dave], items);
    }

    private (string Owners, string Refs) NewRefCollectionNames(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Owners") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Refs") + suffix);
    }

    private void SeedRefContext(string ownersCollection, string refsCollection, RefOwner[] owners, RefItem[] items)
    {
        using var seedDb = new RefOwnerItemDbContext(database, ownersCollection, refsCollection, MongoQueryMode.Native);
        seedDb.Owners.AddRange(owners);
        seedDb.Refs.AddRange(items);
        seedDb.SaveChanges();
    }

    private RefOwnerItemDbContext CreateRefContext(MongoQueryMode mode, string name, out RefOwner[] owners, out RefItem[] items)
    {
        var (ownersCollection, refsCollection) = NewRefCollectionNames(name);
        (owners, items) = SeedRefData();
        SeedRefContext(ownersCollection, refsCollection, owners, items);
        return new RefOwnerItemDbContext(database, ownersCollection, refsCollection, mode);
    }

    private RefOwnerItemDbContext CreateRefContextWithLogging(
        MongoQueryMode mode, string name, out RefOwner[] owners, out RefItem[] items, out SpyLoggerProvider spyLogger)
    {
        var (ownersCollection, refsCollection) = NewRefCollectionNames(name);
        (owners, items) = SeedRefData();
        SeedRefContext(ownersCollection, refsCollection, owners, items);

        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return new RefOwnerItemDbContext(database, ownersCollection, refsCollection, mode, loggerFactory);
    }

    // ── Nested (2-level) cross-collection reference SelectMany fixture (EF-347 nested-reference) ──────────
    // NestOwner --(Mids, FK OwnerId)--> NestMid --(Leaves, FK MidId)--> NestLeaf. All cross-collection
    // (ToCollection) references, mirroring RefOwnerItemDbContext's pattern one level deeper.

    private class NestOwner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<NestMid> Mids { get; set; } = [];
    }

    private class NestMid
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";
        public ObjectId? OwnerId { get; set; }
        public NestOwner? Owner { get; set; }
        public List<NestLeaf> Leaves { get; set; } = [];
    }

    private class NestLeaf
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ObjectId? MidId { get; set; }
        public NestMid? Mid { get; set; }
        public List<NestGrandLeaf> GrandLeaves { get; set; } = [];

        // EF-347 SelectMany computed-leaf: a numeric field at the deepest nested-reference scope, to prove the
        // arithmetic-computed-leaf binder's InnerScopePath prefixing at scope k=2 (see
        // Nested_reference_single_scope_computed_leaf_goes_native). Other NestLeaf seed instances are unaffected
        // at the default 0.
        public int Height { get; set; }
    }

    private class NestGrandLeaf
    {
        public ObjectId Id { get; set; }
        public string Detail { get; set; } = "";
        public ObjectId? LeafId { get; set; }
        public NestLeaf? Leaf { get; set; }
    }

    private sealed class NestDbContext : DbContext
    {
        private readonly string _ownersCollection;
        private readonly string _midsCollection;
        private readonly string _leavesCollection;
        private readonly string _grandLeavesCollection;

        public DbSet<NestOwner> Owners { get; set; } = null!;
        public DbSet<NestMid> Mids { get; set; } = null!;
        public DbSet<NestLeaf> Leaves { get; set; } = null!;
        public DbSet<NestGrandLeaf> GrandLeaves { get; set; } = null!;

        public NestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string midsCollection, string leavesCollection,
            string grandLeavesCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode, null))
        {
            _ownersCollection = ownersCollection;
            _midsCollection = midsCollection;
            _leavesCollection = leavesCollection;
            _grandLeavesCollection = grandLeavesCollection;
        }

        public NestDbContext(
            TemporaryDatabaseFixture database, string ownersCollection, string midsCollection, string leavesCollection,
            string grandLeavesCollection, MongoQueryMode mode, ILoggerFactory loggerFactory)
            : base(BuildOptions(database, mode, loggerFactory))
        {
            _ownersCollection = ownersCollection;
            _midsCollection = midsCollection;
            _leavesCollection = leavesCollection;
            _grandLeavesCollection = grandLeavesCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode, ILoggerFactory? loggerFactory)
        {
            var optionsBuilder = new DbContextOptionsBuilder<NestDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            if (loggerFactory != null)
                optionsBuilder.UseLoggerFactory(loggerFactory).EnableSensitiveDataLogging();
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NestOwner>(b =>
            {
                b.ToCollection(_ownersCollection);
                b.HasMany(o => o.Mids).WithOne(m => m.Owner).HasForeignKey(m => m.OwnerId);
            });
            modelBuilder.Entity<NestMid>(b =>
            {
                b.ToCollection(_midsCollection);
                b.HasMany(m => m.Leaves).WithOne(l => l.Mid).HasForeignKey(l => l.MidId);
            });
            modelBuilder.Entity<NestLeaf>(b =>
            {
                b.ToCollection(_leavesCollection);
                b.HasMany(l => l.GrandLeaves).WithOne(g => g.Leaf).HasForeignKey(g => g.LeafId);
            });
            modelBuilder.Entity<NestGrandLeaf>(b => b.ToCollection(_grandLeavesCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    /// <summary>
    /// Seeds a discriminating 3-level dataset: OwnerA (2 Mids, each with Leaves — a genuine multi-row join),
    /// OwnerB (0 Mids — proves an owner with no children contributes no rows), OwnerC (1 Mid with 0 Leaves —
    /// proves a MID with no leaves contributes no rows even though its owner has a mid). Expected joined
    /// (Owner,Mid,Leaf) triples: (OwnerA,A1,Red), (OwnerA,A1,Blue), (OwnerA,A2,Green) — exactly 3 rows.
    /// </summary>
    private static (NestOwner[] Owners, NestMid[] Mids, NestLeaf[] Leaves) SeedNestData()
    {
        var ownerA = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "OwnerA" };
        var ownerB = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "OwnerB" }; // no mids
        var ownerC = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "OwnerC" };

        var midA1 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "A1", OwnerId = ownerA.Id };
        var midA2 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "A2", OwnerId = ownerA.Id };
        var midC1 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "C1", OwnerId = ownerC.Id }; // no leaves

        var leafA1a = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Red", MidId = midA1.Id, Height = 1 };
        var leafA1b = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Blue", MidId = midA1.Id, Height = 2 };
        var leafA2a = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Green", MidId = midA2.Id, Height = 3 };

        return (
            [ownerA, ownerB, ownerC],
            [midA1, midA2, midC1],
            [leafA1a, leafA1b, leafA2a]);
    }

    private (string Owners, string Mids, string Leaves, string GrandLeaves) NewNestCollectionNames(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Owners") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Mids") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Leaves") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(name + "GrandLeaves") + suffix);
    }

    private void SeedNestContext(
        string ownersCollection, string midsCollection, string leavesCollection, string grandLeavesCollection,
        NestOwner[] owners, NestMid[] mids, NestLeaf[] leaves)
    {
        using var seedDb = new NestDbContext(
            database, ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, MongoQueryMode.Native);
        seedDb.Owners.AddRange(owners);
        seedDb.Mids.AddRange(mids);
        seedDb.Leaves.AddRange(leaves);
        // No NestGrandLeaf rows: the 3-level decline test below fails at translation time, before any data is
        // read, so the collection can stay empty.
        seedDb.SaveChanges();
    }

    private NestDbContext CreateNestContext(
        MongoQueryMode mode, string name, out NestOwner[] owners, out NestMid[] mids, out NestLeaf[] leaves,
        (NestOwner[] Owners, NestMid[] Mids, NestLeaf[] Leaves)? seed = null)
    {
        var (ownersCollection, midsCollection, leavesCollection, grandLeavesCollection) = NewNestCollectionNames(name);
        (owners, mids, leaves) = seed ?? SeedNestData();
        SeedNestContext(ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, owners, mids, leaves);
        return new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, mode);
    }

    private NestDbContext CreateNestContextWithLogging(
        MongoQueryMode mode, string name, out NestOwner[] owners, out NestMid[] mids, out NestLeaf[] leaves,
        out SpyLoggerProvider spyLogger)
    {
        var (ownersCollection, midsCollection, leavesCollection, grandLeavesCollection) = NewNestCollectionNames(name);
        (owners, mids, leaves) = SeedNestData();
        SeedNestContext(ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, owners, mids, leaves);

        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;
        return new NestDbContext(database, ownersCollection, midsCollection, leavesCollection, grandLeavesCollection, mode, loggerFactory);
    }

    [Fact]
    public void Nested_reference_selectmany_projected_goes_native()
    {
        // No driver-LINQ oracle (cross-collection SelectMany), so proven via Native + NativeOnly succeeding
        // plus an expected in-memory-computed result set — no DriverLinq iteration, no parity assertion.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_projected_goes_native) + mode, out var owners, out var mids, out var leaves);

            var result = (
                from o in db.Owners
                from m in o.Mids
                from l in m.Leaves
                select new { o.Name, m.Tag, l.Label })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            var expected = (
                from o in owners
                from m in mids.Where(m => m.OwnerId == o.Id)
                from l in leaves.Where(l => l.MidId == m.Id)
                select new { o.Name, m.Tag, l.Label })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            Assert.Equal(expected, result);
            Assert.Equal(3, result.Count); // OwnerA/A1/Red, OwnerA/A1/Blue, OwnerA/A2/Green
            Assert.DoesNotContain(result, x => x.Name == "OwnerB" || x.Name == "OwnerC");
        }
    }

    [Fact]
    public void Nested_reference_selectmany_bare_entity_goes_native()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_bare_entity_goes_native) + mode, out _, out _, out var leaves);

            var result = (from o in db.Owners from m in o.Mids from l in m.Leaves select l)
                .AsEnumerable().OrderBy(x => x.Label).ToList();

            var expected = leaves.OrderBy(x => x.Label).Select(x => x.Label).ToList();

            Assert.Equal(3, result.Count);
            Assert.Equal(expected, result.Select(x => x.Label).ToList());
            Assert.Equal(leaves.OrderBy(x => x.Label).Select(x => x.Id).ToList(), result.Select(x => x.Id).ToList());
        }
    }

    [Fact]
    public void Nested_reference_selectmany_emits_two_lookups_and_unwinds()
    {
        using var db = CreateNestContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Nested_reference_selectmany_emits_two_lookups_and_unwinds), out _, out _, out _, out var spyLogger);

        _ = (from o in db.Owners from m in o.Mids from l in m.Leaves select new { o.Name, m.Tag, l.Label })
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(message, "\\$lookup").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(message, "\\$unwind").Count);
        Assert.Contains("_lookup_Mids", message);
        Assert.Contains("_lookup_Leaves", message);
        Assert.Contains("_lookup_Mids._id", message); // level-2 localField, scoped under level 1's alias
        Assert.Contains("$project", message);
    }

    [Fact]
    public void Nested_reference_selectmany_composes_with_parametrized_outer_predicate()
    {
        // Bespoke seed (NOT the shared SeedNestData): TWO owners each contribute joined rows, so filtering
        // on the outer predicate is genuinely discriminating — if the outer Where were dropped or
        // mis-composed, the result would include Owner2's row too.
        var owner1 = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "Owner1" };
        var owner2 = new NestOwner { Id = ObjectId.GenerateNewId(), Name = "Owner2" };
        var mid1 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "Mid1", OwnerId = owner1.Id };
        var mid2 = new NestMid { Id = ObjectId.GenerateNewId(), Tag = "Mid2", OwnerId = owner2.Id };
        var leaf1 = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Leaf1", MidId = mid1.Id };
        var leaf2 = new NestLeaf { Id = ObjectId.GenerateNewId(), Label = "Leaf2", MidId = mid2.Id };
        var seed = (Owners: new[] { owner1, owner2 }, Mids: new[] { mid1, mid2 }, Leaves: new[] { leaf1, leaf2 });

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_composes_with_parametrized_outer_predicate) + mode,
                out _, out _, out _, seed);
            var excludedName = owner2.Name;

            var result = (
                from o in db.Owners
                where o.Name != excludedName
                from m in o.Mids
                from l in m.Leaves
                select new { o.Name, m.Tag, l.Label })
                .AsEnumerable().OrderBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            var expected = (
                from o in seed.Owners
                where o.Name != excludedName
                from m in seed.Mids.Where(m => m.OwnerId == o.Id)
                from l in seed.Leaves.Where(l => l.MidId == m.Id)
                select new { o.Name, m.Tag, l.Label })
                .OrderBy(x => x.Tag).ThenBy(x => x.Label).ToList();

            // Non-vacuous, strict subset: Owner1's row is present, Owner2's row (which the unfiltered query
            // would include) is excluded.
            Assert.Equal(expected, result);
            Assert.NotEmpty(result);
            Assert.Single(result);
            Assert.DoesNotContain(result, x => x.Name == excludedName);
            Assert.Contains(result, x => x.Name == owner1.Name && x.Tag == mid1.Tag && x.Label == leaf1.Label);
        }
    }

    [Fact]
    public void Nested_reference_selectmany_whole_outer_owner_result_still_declines_cleanly_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_whole_outer_owner_result_still_declines_cleanly_in_every_mode) + mode,
                out _, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                (from o in db.Owners from m in o.Mids from l in m.Leaves select o).ToList());
        }
    }

    [Fact]
    public void Nested_reference_selectmany_whole_outer_mid_result_still_declines_cleanly_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_whole_outer_mid_result_still_declines_cleanly_in_every_mode) + mode,
                out _, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                (from o in db.Owners from m in o.Mids from l in m.Leaves select m).ToList());
        }
    }

    [Fact]
    public void Nested_reference_selectmany_third_level_still_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_selectmany_third_level_still_hard_fails_in_every_mode) + mode,
                out _, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                (from o in db.Owners
                 from m in o.Mids
                 from l in m.Leaves
                 from g in l.GrandLeaves
                 select new { o.Name, m.Tag, l.Label, g.Detail }).ToList());
        }
    }

    private class EagerParent
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<EagerChild> EagerChildren { get; set; } = [];
    }

    private class EagerChild
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";
        public ObjectId? ParentId { get; set; }
        public EagerParent? Parent { get; set; }
        public ObjectId? DetailId { get; set; }

        // The eager-loaded navigation: EF auto-includes this on every query that returns an EagerChild. NOTE:
        // this is a genuine CROSS-COLLECTION AutoInclude, which (per the empirical finding on
        // Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode below)
        // does NOT actually reach IsWholeElementRepresentable's narrowed reference-nav guard — it declines via
        // a different mechanism (a doubly-nested TransparentIdentifier). See
        // Reference_form_bare_entity_with_owned_embedded_navigation_declines_cleanly_via_representability_guard
        // for the shape that DOES reach the guard (an eager-loaded OWNED sub-navigation).
        public EagerDetail? Detail { get; set; }
    }

    private class EagerDetail
    {
        public ObjectId Id { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Mirrors <see cref="RefOwnerItemDbContext"/>'s structure/helpers, with a third collection for the
    /// eager-loaded <see cref="EagerChild.Detail"/> navigation.
    /// </summary>
    private sealed class EagerRefDbContext : DbContext
    {
        private readonly string _parentsCollection;
        private readonly string _childrenCollection;
        private readonly string _detailsCollection;

        public DbSet<EagerParent> Parents { get; set; } = null!;
        public DbSet<EagerChild> Children { get; set; } = null!;
        public DbSet<EagerDetail> Details { get; set; } = null!;

        public EagerRefDbContext(
            TemporaryDatabaseFixture database, string parentsCollection, string childrenCollection,
            string detailsCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode))
        {
            _parentsCollection = parentsCollection;
            _childrenCollection = childrenCollection;
            _detailsCollection = detailsCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var optionsBuilder = new DbContextOptionsBuilder<EagerRefDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EagerParent>(b =>
            {
                b.ToCollection(_parentsCollection);
                b.HasMany(p => p.EagerChildren).WithOne(c => c.Parent).HasForeignKey(c => c.ParentId);
            });
            modelBuilder.Entity<EagerChild>(b =>
            {
                b.ToCollection(_childrenCollection);
                b.HasOne(c => c.Detail).WithMany().HasForeignKey(c => c.DetailId);
                b.Navigation(c => c.Detail).AutoInclude();
            });
            modelBuilder.Entity<EagerDetail>(b => b.ToCollection(_detailsCollection));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private EagerRefDbContext CreateEagerRefContext(MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parentsCollection = TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Parents") + suffix;
        var childrenCollection = TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Children") + suffix;
        var detailsCollection = TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Details") + suffix;

        using (var seedDb = new EagerRefDbContext(
                   database, parentsCollection, childrenCollection, detailsCollection, MongoQueryMode.Native))
        {
            var detail = new EagerDetail { Id = ObjectId.GenerateNewId(), Note = "Detail" };
            var parent = new EagerParent { Id = ObjectId.GenerateNewId(), Name = "Parent" };
            var child = new EagerChild
            {
                Id = ObjectId.GenerateNewId(), Tag = "Child", ParentId = parent.Id, DetailId = detail.Id,
            };
            seedDb.Details.Add(detail);
            seedDb.Parents.Add(parent);
            seedDb.Children.Add(child);
            seedDb.SaveChanges();
        }

        return new EagerRefDbContext(database, parentsCollection, childrenCollection, detailsCollection, mode);
    }

    private class OwnedSubNavParent
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<OwnedSubNavChild> Children { get; set; } = [];
    }

    private class OwnedSubNavChild
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";
        public ObjectId? ParentId { get; set; }
        public OwnedSubNavParent? Parent { get; set; }

        // The eager-loaded OWNED (embedded) sub-navigation: every owned nav is eager-loaded by EF Core
        // convention, and — unlike EagerChild.Detail above (a genuine cross-collection nav) — this is
        // structurally a SINGLE-hop IncludeExpression(ti.Inner, ownedNav), which
        // TryGetWholeEntityMemberAccess's unwrap-through-Include loop reduces back to the bare `ti.Inner`
        // member access. See
        // Reference_form_bare_entity_with_owned_embedded_navigation_declines_cleanly_via_representability_guard.
        public OwnedSubNavEmbedded Embedded { get; set; } = new();
    }

    private class OwnedSubNavEmbedded
    {
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Mirrors <see cref="RefOwnerItemDbContext"/>'s structure/helpers — a reference (cross-collection)
    /// parent/child relationship — but the CHILD entity additionally owns an embedded sub-navigation
    /// (<see cref="OwnedSubNavChild.Embedded"/>), so a bare-entity <c>SelectMany</c> over the reference
    /// collection projects a whole child entity that itself eager-loads an owned member.
    /// </summary>
    private sealed class OwnedSubNavDbContext : DbContext
    {
        private readonly string _parentsCollection;
        private readonly string _childrenCollection;

        public DbSet<OwnedSubNavParent> Parents { get; set; } = null!;
        public DbSet<OwnedSubNavChild> Children { get; set; } = null!;

        public OwnedSubNavDbContext(
            TemporaryDatabaseFixture database, string parentsCollection, string childrenCollection, MongoQueryMode mode)
            : base(BuildOptions(database, mode))
        {
            _parentsCollection = parentsCollection;
            _childrenCollection = childrenCollection;
        }

        private static DbContextOptions BuildOptions(TemporaryDatabaseFixture database, MongoQueryMode mode)
        {
            var optionsBuilder = new DbContextOptionsBuilder<OwnedSubNavDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(mode);
            return optionsBuilder.Options;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OwnedSubNavParent>(b =>
            {
                b.ToCollection(_parentsCollection);
                b.HasMany(p => p.Children).WithOne(c => c.Parent).HasForeignKey(c => c.ParentId);
            });
            modelBuilder.Entity<OwnedSubNavChild>(b =>
            {
                b.ToCollection(_childrenCollection);
                b.OwnsOne(c => c.Embedded);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private OwnedSubNavDbContext CreateOwnedSubNavContext(MongoQueryMode mode, string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parentsCollection = TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Parents") + suffix;
        var childrenCollection = TemporaryDatabaseFixtureBase.CreateCollectionName(name + "Children") + suffix;

        using (var seedDb = new OwnedSubNavDbContext(database, parentsCollection, childrenCollection, MongoQueryMode.Native))
        {
            var parent = new OwnedSubNavParent { Id = ObjectId.GenerateNewId(), Name = "Parent" };
            var child = new OwnedSubNavChild
            {
                Id = ObjectId.GenerateNewId(), Tag = "Child", ParentId = parent.Id,
                Embedded = new OwnedSubNavEmbedded { Note = "EmbeddedNote" },
            };
            seedDb.Parents.Add(parent);
            seedDb.Children.Add(child);
            seedDb.SaveChanges();
        }

        return new OwnedSubNavDbContext(database, parentsCollection, childrenCollection, mode);
    }

    // EF-347 slice 5 PROBE (Task 4, Step 2): the spike that informed the design only ran EF10. This is the ONE
    // core success test run first on EF10 and then, unmodified, on EF8/EF9 to determine whether cross-collection
    // reference SelectMany translates identically across all three EF versions BEFORE the full suite below was
    // written. Recorded outcome: all three EF versions produced identical (correct, native-under-NativeOnly)
    // results for this shape — see NativeSelectManyBinder's/AGENTS.md's as-built notes and
    // .superpowers/sdd/task4-report.md for the full record — so the suite below carries NO EF8/EF9 guard.
    [Fact]
    public void Reference_form_goes_native_with_correct_results()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_goes_native_with_correct_results), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);
    }

    private sealed class RefItemDto
    {
        public string Name { get; set; } = "";
        public string Tag { get; set; } = "";
    }

    [Fact]
    public void Reference_explicit_result_selector_form_goes_native_with_correct_results_and_mql()
    {
        using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Reference_explicit_result_selector_form_goes_native_with_correct_results_and_mql),
            out var owners, out var items, out var spyLogger);

        var result = db.Owners
            .SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"from\" : \"", message);
        Assert.Contains("\"localField\" : \"_id\"", message);
        Assert.Contains("\"foreignField\" : \"OwnerId\"", message);
        Assert.Contains("\"as\" : \"_lookup_Refs\"", message);
        Assert.Contains("\"path\" : \"$_lookup_Refs\"", message);
        Assert.Contains("\"preserveNullAndEmptyArrays\" : false", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Tag\" : \"$_lookup_Refs.Tag\"", message);
    }

    [Fact]
    public void Reference_query_syntax_form_goes_native_with_correct_results_and_mql()
    {
        using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Reference_query_syntax_form_goes_native_with_correct_results_and_mql),
            out var owners, out var items, out var spyLogger);

        var result = (from o in db.Owners
                       from r in o.Refs
                       select new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = (from o in owners
                         from r in items.Where(r => r.OwnerId == o.Id)
                         select new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"localField\" : \"_id\"", message);
        Assert.Contains("\"foreignField\" : \"OwnerId\"", message);
        Assert.Contains("\"as\" : \"_lookup_Refs\"", message);
        Assert.Contains("\"path\" : \"$_lookup_Refs\"", message);
        Assert.Contains("\"preserveNullAndEmptyArrays\" : false", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Tag\" : \"$_lookup_Refs.Tag\"", message);
    }

    [Fact]
    public void Reference_form_MemberInit_dto_form_goes_native_with_correct_results()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_MemberInit_dto_form_goes_native_with_correct_results), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs, (o, r) => new RefItemDto { Name = o.Name, Tag = r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new RefItemDto { Name = o.Name, Tag = r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        Assert.Equal(expected.Count, result.Count);
        for (var idx = 0; idx < expected.Count; idx++)
        {
            Assert.Equal(expected[idx].Name, result[idx].Name);
            Assert.Equal(expected[idx].Tag, result[idx].Tag);
        }
    }

    [Fact]
    public void Reference_form_principal_with_zero_children_contributes_no_rows()
    {
        // Bob has no RefItems. This proves the FORCE-UNWIND $lookup+$unwind is an INNER join (preserve:false)
        // — the key semantic difference from Include's reference $lookup+$unwind (a LEFT join, preserve:true):
        // a principal with no matching children must contribute NO rows at all, not a row with null inner
        // values.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_principal_with_zero_children_contributes_no_rows), out _, out _);

        var result = db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).ToList();

        Assert.Equal(5, result.Count); // 2 from Alice + 1 from Carol + 2 from Dave; Bob (0 children) contributes 0
        Assert.DoesNotContain(result, x => x.Name == "Bob");
    }

    [Fact]
    public void Reference_form_shared_outer_inner_member_name_resolves_to_distinct_values()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_shared_outer_inner_member_name_resolves_to_distinct_values), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs, (o, r) => new { OuterName = o.Name, InnerName = r.Name })
            .AsEnumerable()
            .OrderBy(x => x.OuterName).ThenBy(x => x.InnerName)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { OuterName = o.Name, InnerName = r.Name })
            .OrderBy(x => x.OuterName).ThenBy(x => x.InnerName)
            .ToList();

        Assert.Equal(expected, result);
        // Sanity: outer/inner never conflated (an owner's own name never leaks into InnerName and vice versa) —
        // EXCEPT Dave's second child, which the EF-347 correlated-beyond-FK seed (see SeedRefData) DELIBERATELY
        // gives Name == "Dave" (its owner's own Name) so the correlated shadow-member test elsewhere
        // (Reference_form_correlated_shadowed_member_name_resolves_by_scope) has a genuinely matching row. That
        // is a real, seeded data coincidence, not a scoping bug, so it is explicitly excluded here rather than
        // silently weakening this sanity check for every other row.
        Assert.All(result.Where(r => !(r.OuterName == "Dave" && r.InnerName == "Dave")),
            r => Assert.NotEqual(r.OuterName, r.InnerName));
        Assert.Contains(result, r => r.OuterName == "Dave" && r.InnerName == "Dave");
    }

    [Fact]
    public void Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode()
    {
        // UNLIKE the owned-collection forms (where the same supported shape ALSO succeeds under explicit
        // DriverLinq), the reference form has NO driver-LINQ baseline at all — spike/design-confirmed
        // (.superpowers/sdd/refselectmany-spike.md; the design doc's Background §2 — "no driver-LINQ
        // baseline") and reconfirmed empirically here: the driver's own LINQ v3 provider rejects ANY
        // cross-collection SelectMany between two separate DbSets/collections ("Unsupported cross-DbSet
        // query...", InvalidOperationException), regardless of whether the projection shape is one this
        // provider's native translator supports. So forcing MongoQueryMode.DriverLinq (bypassing the native
        // path this slice adds) still throws, exactly as it did before this slice existed — this slice only
        // ever ADDS a native path; it does not create a driver-LINQ one where none existed.
        using var db = CreateRefContext(MongoQueryMode.DriverLinq,
            nameof(Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode), out _, out _);

        Assert.Throws<InvalidOperationException>(() =>
            db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).ToList());
    }

    [Fact]
    public void Reference_form_bare_entity_result_goes_native_all_three_spellings()
    {
        // EF-347 Task 4: a bare-nav SelectMany whose trailing selector projects the WHOLE inner entity now goes
        // NATIVE for a REFERENCE-kind unwind too (not just Owned, as of Task 3) — TranslateSelect's
        // whole-inner-entity gate admits Kind is Owned or Reference, and IsWholeElementRepresentable is
        // kind-aware: for Reference it rejects only an EAGER-LOADED navigation. RefItem.Owner is a plain lazy
        // inverse back-reference (never auto-included), so it materializes fine as null and does not block this
        // shape. All three equivalent user spellings normalize to the identical tree and all go native.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_result_goes_native_all_three_spellings), out _, out var items);

        var expectedTags = items.Select(i => i.Tag).OrderBy(t => t).ToList(); // Alice(2)+Carol(1)+Dave(2)=5; Bob(0) none

        // 1-arg
        var oneArg = db.Owners.SelectMany(o => o.Refs).AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();
        // query syntax
        var querySyntax = (from o in db.Owners from r in o.Refs select r)
            .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();
        // explicit result selector
        var explicitRs = db.Owners.SelectMany(o => o.Refs, (o, r) => r)
            .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expectedTags, oneArg);
        Assert.Equal(expectedTags, querySyntax);
        Assert.Equal(expectedTags, explicitRs);
    }

    [Fact]
    public void Reference_form_bare_entity_owner_with_zero_children_contributes_no_rows()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_owner_with_zero_children_contributes_no_rows), out _, out var items);

        var result = db.Owners.SelectMany(o => o.Refs).AsEnumerable().Select(r => r.Id).OrderBy(x => x).ToList();
        var expected = items.Select(i => i.Id).OrderBy(x => x).ToList(); // Bob contributes nothing (inner join)

        Assert.Equal(expected, result);
        Assert.Equal(5, result.Count); // 2 from Alice + 1 from Carol + 2 from Dave; Bob (0 children) contributes 0
    }

    [Fact]
    public void Reference_form_bare_entity_reads_root_relative_not_owner_scoped()
    {
        // RefItem.Name deliberately shares its member name with RefOwner.Name. A bare-entity result is the
        // RefItem, so r.Name must be the ITEM's Name ("WidgetName"/…), read from the re-rooted document — NOT
        // the owner's Name leaking through.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_reads_root_relative_not_owner_scoped), out _, out var items);

        var names = db.Owners.SelectMany(o => o.Refs).AsEnumerable().Select(r => r.Name).OrderBy(n => n).ToList();
        var expected = items.Select(i => i.Name).OrderBy(n => n).ToList();

        Assert.Equal(expected, names);
        Assert.DoesNotContain("Alice", names); // no owner-Name leak
    }

    [Fact]
    public void Reference_form_bare_entity_emits_lookup_unwind_plain_replaceRoot()
    {
        using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_emits_lookup_unwind_plain_replaceRoot),
            out _, out _, out var spyLogger);

        _ = db.Owners.SelectMany(o => o.Refs).ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$lookup", message);
        Assert.Contains("$unwind", message);
        Assert.Contains("$replaceRoot", message);
        Assert.Contains("\"newRoot\" : \"$_lookup_Refs\"", message);
        Assert.DoesNotContain("$mergeObjects", message); // plain replaceRoot, not the owned sentinel form
    }

    [Fact]
    public void Reference_form_filtered_inner_projected_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_projected_goes_native), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal; the "Widget" row is excluded.
        Assert.Equal(expected, result);
        Assert.DoesNotContain(result, x => x.Tag == "Widget");
    }

    [Fact]
    public void Reference_form_filtered_inner_bare_entity_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_bare_entity_goes_native), out _, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"))
            .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();

        var expected = items.Where(r => r.Tag != "Widget").Select(r => r.Tag).OrderBy(t => t).ToList();

        Assert.Equal(expected, result);
        Assert.DoesNotContain("Widget", result);
    }

    [Fact]
    public void Reference_form_filtered_inner_not_null_predicate_goes_native_and_excludes_null_rows()
    {
        // EF-355. A `!= null` inner filter is the one user-predicate shape that is structurally identical to
        // the FK correlation's own null-guard, and so the one most at risk of being silently absorbed into the
        // correlation match (leaving Filter == null → EVERY child row returned). Only 2 of the 5 seeded
        // children have a non-null Note, so a dropped filter shows up as extra rows, not just as different MQL.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_not_null_predicate_goes_native_and_excludes_null_rows),
            out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Note != null), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Note != null), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count); // the three null-Note children are excluded
    }

    [Fact]
    public void Reference_form_filtered_inner_stacked_where_ands_together_goes_native()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_stacked_where_ands_together_goes_native), out var owners, out var items);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget").Where(r => r.Tag != "Gadget"), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Tag != "Widget" && r.Tag != "Gadget"), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_filtered_inner_excluding_all_children_contributes_no_rows()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_excluding_all_children_contributes_no_rows), out _, out _);

        // No RefItem has Tag "nonexistent" → every principal contributes zero rows.
        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == "nonexistent"), (o, r) => new { o.Name, r.Tag })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Reference_form_filtered_inner_emits_match_after_unwind_before_project()
    {
        using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_emits_match_after_unwind_before_project),
            out _, out _, out var spyLogger);

        _ = db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag }).ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$lookup", message);
        Assert.Contains("$unwind", message);
        Assert.Contains("$match", message);
        Assert.Contains("_lookup_Refs.Tag", message); // filter is scope-prefixed
    }

    [Fact]
    public void Reference_form_filtered_inner_composes_with_parametrized_outer_predicate()
    {
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_filtered_inner_composes_with_parametrized_outer_predicate), out var owners, out var items);

        var ownerName = "Alice";
        var result = db.Owners
            .Where(o => o.Name == ownerName)
            .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Tag).ToList();

        var expected = owners.Where(o => o.Name == ownerName)
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id && r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Tag).ToList();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Reference_form_correlated_beyond_fk_inner_goes_native()
    {
        // r.Tag == o.Name references the outer entity beyond the FK. Now native: a $expr field-to-field
        // comparison in the post-$unwind $match. No driver-LINQ oracle for cross-collection SelectMany at all
        // (Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode — this is
        // true of EVERY reference-form SelectMany shape, not just correlated ones), so this is proven under
        // Native + NativeOnly only, NOT DriverLinq (which still throws, exactly as it always did for this form).
        // Only Dave.Refs[0] (Tag="Dave", OwnerId=Dave whose Name is "Dave") satisfies this — see SeedRefData's
        // discriminating-seed comment — so the expected set is neither empty nor "everything".
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_beyond_fk_inner_goes_native) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
            Assert.Equal(expected, result);
            Assert.NotEmpty(result); // Dave/Dave matches
            Assert.DoesNotContain(result, x => x.Name == "Alice" || x.Name == "Carol"); // theirs never match
        }
    }

    [Fact]
    public void Reference_form_correlated_shadowed_member_name_resolves_by_scope()
    {
        // r.Name == o.Name — RefItem.Name deliberately shadows RefOwner.Name. Native routing by parameter
        // identity must compare the inner element's Name to the outer owner's Name, not the item to itself.
        // Only Dave.Refs[1] (Name="Dave") satisfies this.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_shadowed_member_name_resolves_by_scope) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Name == o.Name), (o, r) => new { OuterName = o.Name, InnerName = r.Name })
                .AsEnumerable().OrderBy(x => x.OuterName).ThenBy(x => x.InnerName).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Name == o.Name), (o, r) => new { OuterName = o.Name, InnerName = r.Name })
                .OrderBy(x => x.OuterName).ThenBy(x => x.InnerName).ToList();
            Assert.Equal(expected, result);
            Assert.NotEmpty(result);
            Assert.All(result, r => Assert.Equal("Dave", r.OuterName));
        }
    }

    [Fact]
    public void Reference_form_correlated_mixed_conjunct_goes_native()
    {
        // Inner-only conjunct ANDed with a correlated one in one .Where. Dave.Refs[0] (Tag="Dave") satisfies
        // both conjuncts; every other row fails at least one.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_mixed_conjunct_goes_native) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget" && r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Tag != "Widget" && r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
            Assert.Equal(expected, result);
            Assert.NotEmpty(result);
        }
    }

    [Fact]
    public void Reference_form_correlated_numeric_comparison_goes_native()
    {
        // r.Score >= o.Threshold — proves comparison-operator breadth end-to-end via $expr $gte. Included:
        // Alice/Gadget (20>=10), Carol/Thing (8>=8, exact boundary), Dave/Refs[1] (150>=100). Excluded:
        // Alice/Widget (5<10), Dave/Refs[0] (50<100).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_correlated_numeric_comparison_goes_native) + mode, out var owners, out _);

            var result = db.Owners
                .SelectMany(o => o.Refs.Where(r => r.Score >= o.Threshold), (o, r) => new { o.Name, r.Tag })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

            var expected = owners
                .SelectMany(o => o.Refs.Where(r => r.Score >= o.Threshold), (o, r) => new { o.Name, r.Tag })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
            Assert.Equal(expected, result);
            Assert.Contains(result, x => x.Tag == "Gadget");
            Assert.Contains(result, x => x.Tag == "Thing");
            Assert.Contains(result, x => x.Tag == "DaveItem2Tag");
            Assert.DoesNotContain(result, x => x.Tag == "Widget");
            Assert.DoesNotContain(result, x => x.Tag == "Dave");
        }
    }

    [Fact]
    public void Reference_form_correlated_bare_entity_result_goes_native()
    {
        // Bare-entity trailing selector composes with the correlated $match (which runs before $replaceRoot).
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_bare_entity_result_goes_native), out var owners, out _);

        var result = (from o in db.Owners from r in o.Refs.Where(r => r.Tag == o.Name) select r)
            .AsEnumerable().Select(r => r.Tag).OrderBy(t => t).ToList();

        var expected = owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => r)
            .Select(r => r.Tag).OrderBy(t => t).ToList();
        Assert.Equal(expected, result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void Reference_form_correlated_stacked_where_goes_native()
    {
        // Stacked .Where(...).Where(...): the first conjunct (r.Tag == o.Name) is correlated-beyond-FK and only
        // Dave.Refs[0] satisfies it; the second (r.Name == o.Name) further narrows using the SAME correlation
        // but a DIFFERENT member, and Dave.Refs[0]'s own Name ("DaveItem1") does NOT equal "Dave" — so stacking
        // the two conjuncts together excludes even Dave.Refs[0], yielding an empty (but correctly-computed,
        // non-vacuous-by-construction) result. This proves both Wheres genuinely AND together rather than one
        // being silently dropped (which would instead have produced Dave.Refs[0] as a false-positive row).
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_stacked_where_goes_native), out var owners, out _);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name).Where(r => r.Name == o.Name), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        var expected = owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name).Where(r => r.Name == o.Name), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
        Assert.Equal(expected, result);
        Assert.Empty(result); // no single row satisfies BOTH r.Tag == o.Name AND r.Name == o.Name
    }

    [Fact]
    public void Reference_form_correlated_excluding_all_children_contributes_no_rows()
    {
        // A correlated predicate ANDed with an inner-only conjunct that matches nothing (r.Score < 0 — Score is
        // never negative in the seed) yields no rows (inner-join semantics preserved), even though the
        // correlated conjunct alone (r.Tag == o.Name) DOES match Dave.Refs[0] — proving the second conjunct
        // genuinely narrows the result rather than being ignored. (A string-concatenation correlated predicate,
        // e.g. r.Tag == o.Name + "literal", is a MethodCallExpression (String.Concat), which the translator's
        // computed-long-tail exclusion structurally declines — that shape hard-fails translation rather than
        // going native, so it is not used here.)
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_excluding_all_children_contributes_no_rows), out _, out _);

        var result = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name).Where(r => r.Score < 0), (o, r) => new { o.Name, r.Tag })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Reference_form_correlated_composes_with_parametrized_outer_predicate()
    {
        // An outer Where parameter still substitutes correctly alongside the correlated inner $match. Excluding
        // Dave (the cutoff) removes the only owner whose children satisfy r.Tag == o.Name, so the result is
        // empty — still a meaningful, non-vacuous assertion, since it proves the outer filter and the
        // correlated inner filter compose (rather than one being silently ignored, which would instead surface
        // Dave/Dave's row here).
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_correlated_composes_with_parametrized_outer_predicate), out var owners, out _);

        var cutoff = "Dave";
        var result = db.Owners.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();

        var expected = owners.Where(o => o.Name != cutoff)
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Name).ThenBy(x => x.Tag).ToList();
        Assert.Equal(expected, result);
        Assert.Empty(result); // confirms the outer filter really did exclude Dave's would-be matching row

        // Sanity: WITHOUT excluding Dave, the same correlated filter does produce a row — proves the empty
        // result above is because of the outer predicate, not because the correlated filter is broken.
        var withoutCutoff = owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .ToList();
        Assert.NotEmpty(withoutCutoff);
    }

    [Fact]
    public void Reference_form_correlated_emits_expr_match_after_unwind()
    {
        // MQL assertion: the correlated conjunct renders as $expr in the post-$unwind $match, comparing the
        // scope-prefixed inner field to the root-relative outer field.
        using var db = CreateRefContextWithLogging(MongoQueryMode.Native,
            nameof(Reference_form_correlated_emits_expr_match_after_unwind), out _, out _, out var spyLogger);

        _ = db.Owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .ToList();

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("$lookup", message);
        Assert.Contains("$unwind", message);
        Assert.Contains("$match", message);
        Assert.Contains("$expr", message);
        Assert.Contains("_lookup_Refs.Tag", message); // inner field, scope-prefixed
        Assert.Contains("$Name", message); // outer field, root-relative
    }

    [Fact]
    public void Reference_form_computed_filter_operator_hard_fails_in_every_mode()
    {
        // A filter using an operator the native translator does not support (string.ToUpper) declines: the
        // inner-scope translator rejects it → bind returns false → hard-fails in every mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_computed_filter_operator_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag.ToUpper() == "WIDGET"), (o, r) => new { o.Name, r.Tag }).ToList());
        }
    }

    [Fact]
    public void Filtered_reference_collection_count_still_falls_back()
    {
        // NOT a SelectMany — a projected filtered Count (c.Refs.Count(pred)). NativeCorrelationMatcher /
        // NativeProjectionBinder's Count binder requires a bare, no-predicate Count over the FK-equality Where
        // and rejects the extra conjunct exactly as before this slice — that part of the "still falls back"
        // contract is unchanged. VERIFIED (not assumed, per the brief's flag): empirically, the shared
        // fallback path this then defers to (MongoProjectionBindingExpressionVisitor.
        // TryBindProjectedCollectionNavigationCount) ALSO requires a bare, no-predicate Count and rejects the
        // same shape — a PRE-EXISTING gap independent of this slice (see the identical, already-committed
        // precedent QueryModeGateIncludeTests.Projected_collection_Count_with_predicate_still_falls_back),
        // not a regression it introduces. So the query fails identically in every mode with
        // InvalidOperationException rather than silently landing on a wrong-shape native $size — the point
        // this test preserves is that the native SelectMany filter mechanism added in this slice does NOT
        // leak into the Count binder and does NOT change its (pre-existing) behavior either way.
        using var db = CreateRefContext(MongoQueryMode.Native,
            nameof(Filtered_reference_collection_count_still_falls_back), out _, out _);

        Assert.Throws<InvalidOperationException>(() =>
            db.Owners
                .Select(o => new { o.Name, N = o.Refs.Count(r => r.Tag != "Widget") })
                .AsEnumerable().OrderBy(x => x.Name).ToList());
    }

    [Fact]
    public void Reference_form_computed_leaf_hard_fails_in_every_mode()
    {
        // DIFFERENT from the owned form's Explicit_result_selector_form_computed_leaf_falls_back_gracefully_
        // except_under_NativeOnly, for the SAME reason as
        // Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode above:
        // TryBindReferenceNavUnwind still succeeds on STRUCTURE alone (the collectionSelector is a valid
        // FK-correlated reference nav) before it can know the trailing projection is unsupported, so
        // UnwindSource is set unconditionally; the SEPARATE trailing Select's
        // TryBindTransparentIdentifierProjection then rejects the computed leaf (r.Tag + "!" is not a bare
        // ti.Outer.<m>/ti.Inner.<m> access) and calls the ordinary MarkNotNativelyRepresentable() guard — the
        // SAME "graceful fallback" mechanism the owned form's sibling test relies on. But here the graceful
        // fallback does not actually work (empirically confirmed, not assumed): the reference form has no
        // driver-LINQ baseline at all (see the DriverLinq test above), so the fallback attempt itself throws
        // the SAME "Unsupported cross-DbSet query" InvalidOperationException under BOTH Native and DriverLinq;
        // NativeOnly (which forbids the fallback attempt entirely) throws its own distinct
        // NativeTranslationNotSupportedException before ever reaching that fallback code, hence ThrowsAny there.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_computed_leaf_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { X = r.Tag + "!" }).ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_computed_leaf_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { X = r.Tag + "!" }).ToList());
    }

    [Fact]
    public void Reference_form_computed_arithmetic_leaf_goes_native()
    {
        // Reference SelectMany has NO driver oracle, so prove native via NativeOnly + expected in-memory set,
        // and assert the computed $project MQL. r.Score * 2 is single inner scope → $_lookup_Refs.Score.
        using var db = CreateRefContextWithLogging(MongoQueryMode.NativeOnly,
            nameof(Reference_form_computed_arithmetic_leaf_goes_native), out var owners, out var items, out var spyLogger);

        var result = db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, Doubled = r.Score * 2 })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Doubled).ToList();

        var expected = owners.SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { o.Name, Doubled = r.Score * 2 })
            .OrderBy(x => x.Name).ThenBy(x => x.Doubled).ToList();

        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("$multiply", message);
        Assert.Contains("$_lookup_Refs.Score", message);
    }

    [Fact]
    public void Two_field_single_scope_computed_leaf_goes_native()
    {
        // Two field-refs in one leaf, both inner scope → both get the Items. prefix. Owned inner-only has an oracle.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { Sq = i.Price * i.Price })
            .OrderBy(r => r.Sq).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode, nameof(Two_field_single_scope_computed_leaf_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { Sq = i.Price * i.Price })
                .AsEnumerable().OrderBy(r => r.Sq).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Mixed_member_and_computed_leaf_projection_goes_native()
    {
        // One bare member (o.Name) + one arithmetic leaf (i.Price * 2m) in the same projection — both aliases correct.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { o.Name, Doubled = i.Price * 2m })
            .OrderBy(r => r.Name).ThenBy(r => r.Doubled).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode, nameof(Mixed_member_and_computed_leaf_projection_goes_native) + mode);
            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, Doubled = i.Price * 2m })
                .AsEnumerable().OrderBy(r => r.Name).ThenBy(r => r.Doubled).ToList();
            Assert.Equal(expected, result);
        }
    }

    [Fact]
    public void Nested_reference_single_scope_computed_leaf_goes_native()
    {
        // A single-scope arithmetic leaf at the DEEPEST scope (k=2, the leaf) of a two-level nested reference
        // SelectMany — proves the deeper InnerScopePath prefixing (_lookup_Leaves.Height). No driver oracle
        // (cross-collection) → prove via Native + NativeOnly + expected in-memory set.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateNestContext(mode,
                nameof(Nested_reference_single_scope_computed_leaf_goes_native) + mode, out var owners, out var mids, out var leaves);

            var result = (from o in db.Owners from m in o.Mids from l in m.Leaves
                          select new { o.Name, m.Tag, Doubled = l.Height * 2 })
                .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Doubled).ToList();

            var expected = (from o in owners
                            from m in mids.Where(m => m.OwnerId == o.Id)
                            from l in leaves.Where(l => l.MidId == m.Id)
                            select new { o.Name, m.Tag, Doubled = l.Height * 2 })
                .OrderBy(x => x.Name).ThenBy(x => x.Tag).ThenBy(x => x.Doubled).ToList();

            Assert.Equal(expected, result);
            Assert.Equal(3, result.Count);
        }
    }

    // EF-422 RE-BASELINE. Was Cross_scope_computed_leaf_declines_and_hard_fails_in_every_mode, which pinned
    // the single-scope-only boundary: o.Threshold * r.Score spans OUTER + INNER, the single-scope binder
    // declined, and the reference form's lack of a driver oracle turned that decline into a hard fail in every
    // mode. EF-422 adds the cross-scope binder, and it is scope-kind-agnostic — it prefixes an inner operand
    // with the unwind source's own InnerScopePath, which for a REFERENCE source is the $lookup alias
    // ("_lookup_Refs") exactly as it is the embedded array path ("Items") for an owned one. So this shape now
    // goes native here too, and the corresponding hard-fail assertion is gone rather than weakened.
    [Fact]
    public void Cross_scope_computed_leaf_goes_native_for_the_reference_form_too_EF422()
    {
        // Threshold x Score: Alice(10) x {5, 20}, Carol(8) x {8}, Dave(100) x {50, 150} — five distinct,
        // non-zero products, so a mis-scoped operand (reading "$Score" at the document root, or
        // "$_lookup_Refs.Threshold", neither of which exists) would surface as zeros/nulls, not as the values
        // asserted. DriverLinq is excluded, as elsewhere in this file: reference-collection SelectMany has no
        // driver-LINQ oracle at all.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Cross_scope_computed_leaf_goes_native_for_the_reference_form_too_EF422) + mode,
                out var owners, out var items);

            var result = db.Owners.SelectMany(o => o.Refs, (o, r) => new { Combined = o.Threshold * r.Score })
                .AsEnumerable().Select(x => x.Combined).OrderBy(x => x).ToList();

            var expected = owners
                .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => o.Threshold * r.Score)
                .OrderBy(x => x).ToList();

            Assert.Equal([50, 64, 200, 5000, 15000], expected); // sanity: the fixture products are discriminating.
            Assert.Equal(expected, result);
        }
    }

    // EF-434 RE-BASELINE. Was Integer_division_computed_leaf_declines_and_hard_fails_in_every_mode, which
    // pinned TryTranslateValue's blanket integer-division decline (then "Guard A"). That guard is gone: an
    // integral division now translates to MongoBinaryOperator.IntegerDivide and renders as $trunc-of-$divide,
    // so this leaf goes native like any other arithmetic one.
    //
    // Scores are 5, 20, 8, 50, 150 → C# halves 2, 10, 4, 25, 75. Widget's 5/2 is the discriminator: raw
    // $divide would give 2.5 (and fail to deserialize into the int member at all). DriverLinq is excluded,
    // as elsewhere in this file, because reference-collection SelectMany has no driver-LINQ oracle.
    [Fact]
    public void Integer_division_computed_leaf_goes_native_and_truncates_EF434()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Integer_division_computed_leaf_goes_native_and_truncates_EF434) + mode, out _, out _);

            var halves = db.Owners.SelectMany(o => o.Refs, (o, r) => new { Half = r.Score / 2 })
                .AsEnumerable().Select(x => x.Half).OrderBy(x => x).ToList();

            Assert.Equal([2, 4, 10, 25, 75], halves);
        }
    }

    [Fact]
    public void Reference_form_followed_by_Where_hard_fails_in_every_mode()
    {
        // EF-347 slice 5 composition seam. UNLIKE the owned-form sibling
        // (Explicit_result_selector_form_followed_by_Where_falls_back_gracefully_except_under_NativeOnly), this
        // hard-fails in EVERY mode, not just NativeOnly — empirically confirmed (spiked against the running
        // server, not assumed) rather than copied from the owned-form analogy. The mechanism up to the point of
        // fallback is identical to the owned form: NativeSlotPopulator's post-terminal guard
        // (HasTerminalOperator, true because UnwindSource is set) calls MarkNotNativelyRepresentable() for this
        // Where, which is the SAME graceful-fallback call the owned form's Where relies on succeeding. But the
        // reference form's captured chain still starts with a cross-collection SelectMany, and — per
        // Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode above — the
        // driver's own LINQ v3 provider rejects ANY cross-collection SelectMany outright, independent of what
        // follows it. So the "graceful" fallback attempt itself throws the identical
        // "Unsupported cross-DbSet query..." InvalidOperationException under BOTH Native and DriverLinq (the
        // fallback is attempted and fails, rather than never being attempted); NativeOnly forbids the fallback
        // attempt entirely and throws its own distinct NativeTranslationNotSupportedException first.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_Where_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Where(x => x.Tag != "Widget").ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_followed_by_Where_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Where(x => x.Tag != "Widget").ToList());
    }

    [Fact]
    public void Reference_form_followed_by_OrderBy_hard_fails_in_every_mode()
    {
        // Same outcome and mechanism as Reference_form_followed_by_Where_hard_fails_in_every_mode above — see
        // its comment. UNLIKE the owned form's
        // Explicit_result_selector_form_followed_by_OrderBy_falls_back_gracefully_except_under_NativeOnly,
        // there is no driver-LINQ baseline to fall back to for a reference-nav SelectMany, so this hard-fails
        // in every mode too (empirically confirmed).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_OrderBy_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).OrderBy(x => x.Tag).ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_followed_by_OrderBy_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).OrderBy(x => x.Tag).ToList());
    }

    [Fact]
    public void Reference_form_followed_by_Skip_hard_fails_in_every_mode()
    {
        // Same outcome and mechanism as Reference_form_followed_by_Where_hard_fails_in_every_mode above. UNLIKE
        // the owned form's Explicit_result_selector_form_followed_by_Skip_falls_back_gracefully_except_under_
        // NativeOnly, no driver-LINQ baseline exists for the reference form, so this hard-fails in every mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_Skip_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Skip(1).ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_followed_by_Skip_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Skip(1).ToList());
    }

    [Fact]
    public void Reference_form_followed_by_Take_hard_fails_in_every_mode()
    {
        // Same outcome and mechanism as Reference_form_followed_by_Where_hard_fails_in_every_mode above. UNLIKE
        // the owned form's Explicit_result_selector_form_followed_by_Take_falls_back_gracefully_except_under_
        // NativeOnly, no driver-LINQ baseline exists for the reference form, so this hard-fails in every mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_Take_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Take(1).ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_followed_by_Take_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Take(1).ToList());
    }

    [Fact]
    public void Reference_form_followed_by_Count_hard_fails_in_every_mode()
    {
        // THE headline divergence from the owned form (per the brief's "even Count" prediction, empirically
        // confirmed rather than assumed): the owned form's
        // Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly demonstrates a cardinality-only
        // operator falling back gracefully because Count needs no shaper at all — the driver-LINQ fallback
        // rebuild for a shaper-less Count genuinely succeeds. That reasoning does NOT carry over to the
        // reference form: the fallback still has to translate the CAPTURED CHAIN, which begins with a
        // cross-collection SelectMany the driver's own LINQ v3 provider rejects unconditionally (per
        // Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode above),
        // regardless of what operator follows it or whether that operator needs a shaper. So Count is NOT a
        // graceful-fallback exception here the way it is for the owned form — it hard-fails in every mode, same
        // as Where/OrderBy/Skip/Take above.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_Count_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Count());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_followed_by_Count_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).Count());
    }

    [Fact]
    public void Reference_form_followed_by_GroupBy_hard_fails_in_every_mode()
    {
        // Same OUTCOME as the owned form's GroupBy_after_SelectMany_hard_fails_in_every_mode /
        // Explicit_result_selector_form_followed_by_GroupBy_hard_fails_in_every_mode (GroupBy hard-fails for
        // BOTH forms, unlike Where/OrderBy/Skip/Take/Count which differ between forms) — but empirically via a
        // DIFFERENT underlying message under Native/DriverLinq: "Calling 'ShapedQueryExpression.VisitChildren'
        // is not allowed" (the same shaper-rebuild failure the owned form hits — TranslateGroupBy's own
        // Translate override bypasses the ordinary catch-all guards and its fallback cannot re-read the prior
        // SelectMany projection's already-resolved-by-index members), NOT the "Unsupported cross-DbSet query"
        // message the other operators above hit. Both failure modes are pre-empted before ever reaching the
        // driver for real, so the "no driver-LINQ baseline" fact and the "GroupBy fallback shaper cannot
        // rebuild" fact are both independently sufficient to hard-fail this shape; NativeOnly throws its own
        // distinct NativeTranslationNotSupportedException, hence ThrowsAny across all three modes (mirroring the
        // owned-form sibling tests' idiom for this same operator).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_GroupBy_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).GroupBy(x => x.Name).ToList());
        }
    }

    [Fact]
    public void Reference_form_followed_by_another_SelectMany_hard_fails_in_every_mode()
    {
        // Same outcome and reason as the owned form's Another_SelectMany_after_SelectMany_hard_fails_in_every_
        // mode / Explicit_result_selector_form_followed_by_another_SelectMany_hard_fails_in_every_mode: the
        // second SelectMany's collection-selector body (`new[] { x.Tag }`) is not a nested Queryable.Select(...)
        // call, a bare owned/reference nav, nor a correlated Where-over-EntityQueryRoot, so EVERY
        // NativeSelectManyBinder entry point rejects it structurally regardless of what the first SelectMany
        // was over — TranslateSelectMany returns null with NO MarkNotNativelyRepresentable() attempt at all, so
        // EF's own translation-failure path is reached directly, at TRANSLATION time, before MongoQueryMode is
        // even consulted. Empirically (spiked against the running server) the identical "could not be
        // translated" InvalidOperationException fires in ALL THREE modes alike, including DriverLinq — this is
        // the translation-time failure itself, not the reference form's "no driver-LINQ baseline for cross-
        // collection SelectMany" fact (which never even gets a chance to matter here).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_another_SelectMany_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).SelectMany(x => new[] { x.Tag }).ToList());
        }
    }

    [Fact]
    public void Reference_form_followed_by_Distinct_hard_fails_in_every_mode()
    {
        // EF-347 Slice 5 fix (silent-null Distinct-after-SelectMany, whole-branch review): BEFORE the fix,
        // NativeGroupByBinder.TryBindDistinctFromProjection did not guard on UnwindSource != null, so a
        // Distinct after this (already-terminal) projected reference SelectMany "succeeded" — it converted the
        // SelectMany's Projection into a degenerate $group and set IsDistinct, while UnwindSource stayed set.
        // MongoSelectLowerer.Lower checks UnwindSource BEFORE Grouping, so it returned early with
        // [$lookup, $unwind, $project(flatten)] and NEVER emitted the $group — the flatten $project then read
        // "_id.Name", which doesn't exist without the $group, producing count=3 names=[null,null,null] under
        // BOTH Native and NativeOnly (silently — NativeOnly didn't even throw). Empirically confirmed (this was
        // the exact reproduction used to diagnose the bug) — see PROBE runs in the fix commit's history.
        // AFTER the fix (UnwindSource != null added to the guard), TryBindDistinctFromProjection declines, so
        // this collapses into the SAME "operator after reference SelectMany" family as Where/OrderBy/Skip/Take/
        // Count/GroupBy above: no driver-LINQ baseline exists for a cross-collection SelectMany
        // (Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode), so the
        // graceful MarkNotNativelyRepresentable() fallback the guard would normally permit still throws the same
        // "Unsupported cross-DbSet query..." InvalidOperationException under BOTH Native and DriverLinq, and
        // NativeOnly forbids the fallback attempt entirely and throws its own distinct
        // NativeTranslationNotSupportedException first — hard-failing in every mode, never returning wrong data.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_followed_by_Distinct_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name }).Distinct().ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_followed_by_Distinct_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name }).Distinct().ToList());
    }

    [Fact]
    public void Reference_form_parametrized_outer_predicate_composes_correctly_with_native_SelectMany()
    {
        // Reference-form sibling of Explicit_result_selector_form_parametrized_outer_predicate_composes_
        // correctly_with_native_SelectMany: a Where BEFORE the native reference SelectMany lowers into the
        // normal pre-$lookup $match slot exactly like any other native query, proving a parametrized predicate
        // (a captured local, not a constant) substitutes correctly through the shared PlaceholderTable when
        // composed with the $lookup + inner-join-$unwind + $project pipeline this slice adds.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_parametrized_outer_predicate_composes_correctly_with_native_SelectMany), out var owners, out var items);
        var captured = "Alice";

        var result = db.Owners
            .Where(o => o.Name == captured)
            .SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag })
            .AsEnumerable()
            .OrderBy(x => x.Tag)
            .ToList();

        var expected = owners
            .Where(o => o.Name == captured)
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { o.Name, r.Tag })
            .OrderBy(x => x.Tag)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_Where_falls_back_gracefully_except_under_NativeOnly()
    {
        // Non-terminal composition: a Where applied AFTER the (natively-representable) explicit-result-
        // selector SelectMany. Empirically this does NOT reproduce the inner-Select form's
        // Operator_after_SelectMany_is_unsupported_and_never_returns_silent_wrong_data hard-fail-in-every-mode
        // outcome (verified against expected in-memory results, not just "no exception") — and the difference
        // is structural, not a regression of that invariant. The inner-Select form's captured method chain
        // (used to rebuild the driver-LINQ fallback from MongoQueryExpression.CapturedExpression) contains an
        // EF-synthesized `.Select(ti => ti.Inner)` unwrap over an internal TransparentIdentifier CLR type that
        // the driver's own LINQ bridge cannot map, so its fallback can never succeed. The explicit-result-
        // selector form has no such extra unwrap call in its captured chain — nav-expansion produces exactly
        // `SelectMany(bareNav, trivialResultSelector).Select(ti => new{...}).Where(...)`, a chain the driver's
        // own LINQ v3 provider already knows how to translate on its own — so once the post-terminal guard
        // marks the query non-native (NativeSlotPopulator's Where-after-terminal guard, same as any other
        // post-SelectMany/GroupBy/Distinct operator), the graceful driver-LINQ fallback genuinely succeeds with
        // correct results under Native/DriverLinq; only NativeOnly (which forbids the fallback) throws.
        var seed = SeedOwners();
        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .Where(r => r.Price > 10)
            .OrderBy(r => r.Name).ThenBy(r => r.Price)
            .ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_followed_by_Where_falls_back_gracefully_except_under_NativeOnly) + mode);

            var result = db.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .Where(r => r.Price > 10)
                .AsEnumerable()
                .OrderBy(r => r.Name).ThenBy(r => r.Price)
                .ToList();
            Assert.Equal(expected, result);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_followed_by_Where_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .Where(r => r.Price > 10)
                .ToList());
    }

    [Fact]
    public void Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode()
    {
        // EF-347 Task 3: a bare `db.Entities.SelectMany(o => o.Items)` normalizes (via EF's nav-expansion) to
        // the bare-nav + trivial TransparentIdentifier(Outer,Inner) tree that TranslateSelect's
        // IsWholeInnerEntitySelector guard now recognizes as the OWNED whole-inner-element shape — it sets
        // UnwindSource.WholeElement and routes NATIVE (see
        // Bare_owned_whole_inner_element_goes_native_all_three_spellings, which proves the AsNoTracking/native
        // success side of this same shape). But THIS query is a TRACKING query (no .AsNoTracking()), and owned
        // entities cannot be tracked without their owner: EF Core's OWN runtime guard —
        // "A tracking query is attempting to project an owned entity without a corresponding owner in its
        // result, but owned entities cannot be tracked without their owner. Either include the entity that owns
        // this one, or make the query non-tracking using 'AsNoTracking'." — fires at shaper-materializer
        // injection time (InjectStructuralTypeMaterializers/InjectEntityMaterializers), which runs BEFORE the
        // native-vs-driver-LINQ split (TryBuildNativeFactory) has any bearing on the result, so all three modes
        // throw the identical InvalidOperationException. This SUPERSEDES the pre-Task-3 behavior, where this
        // same tracking query instead threw a provider-level NotSupportedException (the shape was unconditionally
        // declined at translation time, so EF's own tracking guard never got a chance to run) — the tracking
        // contract is intentionally EF Core's own, not something this provider enforces or should enforce.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode,
                nameof(Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() => db.Entities.SelectMany(o => o.Items).ToList());
        }
    }

    [Fact]
    public void Computed_leaf_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode, nameof(Computed_leaf_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities.SelectMany(o => o.Items.Select(i => new { X = i.Price * 2 })).ToList());
        }
    }

    [Fact]
    public void Parametrized_outer_predicate_composes_correctly_with_native_SelectMany()
    {
        // A Where BEFORE the native SelectMany lowers into the normal pre-$unwind $match slot exactly like
        // any other native query — this proves a parametrized predicate (a captured local, not a constant)
        // still substitutes correctly through the shared PlaceholderTable when composed with the $unwind +
        // $project pipeline.
        var seed = SeedOwners();
        var p = "Alice";
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Parametrized_outer_predicate_composes_correctly_with_native_SelectMany));

        var result = db.Entities
            .Where(o => o.Name == p)
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .AsEnumerable()
            .OrderBy(r => r.Price)
            .ToList();

        var expected = seed
            .Where(o => o.Name == p)
            .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
            .OrderBy(r => r.Price)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Absent_owned_collection_field_contributes_no_rows()
    {
        // "Absent" (the element missing from the document entirely) is a DIFFERENT case from "empty" (an
        // empty array, covered by Empty_or_absent_owned_collection_contributes_no_rows above) — EF's own
        // insert path always serializes Owner.Items as at least `[]`, so absent is unreachable through
        // SingleEntityDbContext/collection.InsertMany(Owner[]). Insert raw BsonDocuments (bypassing the
        // mapped Owner serializer) to get a document with no "Items" field at all, and confirm the plain
        // `$unwind: "$Items"` (see MongoUnwindFieldStage — no preserveNullAndEmptyArrays) drops it exactly
        // like an empty array, rather than erroring or treating a missing path as a single null element.
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Absent_owned_collection_field_contributes_no_rows)) + Guid.NewGuid().ToString("N")[..8];

        var rawCollection = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        rawCollection.InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Dave" } }, // no "Items" field
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Erin" },
                {
                    "Items", new BsonArray
                    {
                        new BsonDocument { { "Name", "Thingy" }, { "Price", new BsonDecimal128(3m) } }
                    }
                }
            },
        ]);

        var collection = database.MongoDatabase.GetCollection<Owner>(collectionName);
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<Owner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var result = db.Entities.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price })).ToList();

        Assert.Single(result);
        Assert.Equal("Erin", result[0].Name);
        Assert.DoesNotContain(result, r => r.Name == "Dave");
    }

    [Fact]
    public void OrderBy_after_SelectMany_hard_fails_in_every_mode()
    {
        // Composition-seam regression (mirrors NativeSetOpsTests' one-test-per-shape pattern): the native
        // SelectMany's Select is terminal (MongoSelectDefinition.HasTerminalOperator includes
        // UnwindSource != null — see EF-347 slice 3 Task 1), so NativeSlotPopulator.PopulateNativeSlots'
        // post-terminal guard marks the query non-native for this OrderBy, exactly like the established
        // post-GroupBy/post-Distinct/post-Union guards. No graceful fallback exists for SelectMany at all
        // (see the type doc comment), so every mode throws.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode,
                nameof(OrderBy_after_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .OrderBy(r => r.Price)
                    .ToList());
        }
    }

    [Fact]
    public void Skip_after_SelectMany_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode, nameof(Skip_after_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .Skip(1)
                    .ToList());
        }
    }

    [Fact]
    public void Take_after_SelectMany_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode, nameof(Take_after_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .Take(1)
                    .ToList());
        }
    }

    [Fact]
    public void Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly()
    {
        // Empirically DIFFERENT from Where/OrderBy/Skip/Take/GroupBy above: NativeCardinalityBinder
        // .TryBindAggregate's HasTerminalOperator guard calls MarkNotNativelyRepresentable() (Route =
        // Fallback) rather than a hard throw, and — unlike the Select/GroupBy cases, whose fallback shaper
        // cannot be rebuilt because it needs to re-read a prior by-index-resolved projection — Count needs no
        // shaper at all, so the driver-LINQ fallback (MongoEFToLinqTranslatingExpressionVisitor rewriting the
        // full captured `.SelectMany(...).Count()` chain) succeeds for real under Native/DriverLinq, returning
        // the CORRECT count (this is a legitimate graceful fallback, not a silent-wrong-data bug — confirmed
        // by asserting the exact expected count, not just "no exception"). Only NativeOnly forbids the
        // fallback and throws (a distinct exception type from the other hard-fail cases here —
        // NativeTranslationNotSupportedException, not InvalidOperationException — hence ThrowsAny below).
        var seed = SeedOwners();
        var expectedCount = seed.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price })).Count();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode, nameof(Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly) + mode);

            var count = db.Entities.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price })).Count();
            Assert.Equal(expectedCount, count);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price })).Count());
    }

    [Fact]
    public void GroupBy_after_SelectMany_hard_fails_in_every_mode()
    {
        // TranslateGroupBy has its own Translate override (bypassing the NativeSlotPopulator/
        // NativeCardinalityBinder catch-all guards), so it reads Select.HasTerminalOperator directly
        // (captured as hadTerminalGrouping BEFORE it unconditionally sets IsGroupBy = true) and marks the
        // query non-native rather than rebinding over the SelectMany's already-populated Grouping-adjacent
        // state. MarkNotNativelyRepresentable() would normally mean a graceful driver-LINQ fallback under
        // Native/DriverLinq (as Count above demonstrates it CAN work for some operators) — but here, exactly
        // like the Where case above, the fallback shaper cannot rebuild a driver-LINQ expression that re-reads
        // the prior native SelectMany projection's already-resolved-by-index members
        // ("Calling 'ShapedQueryExpression.VisitChildren' is not allowed"), so this still throws in every
        // mode (no silent wrong data, same invariant) — just via a DIFFERENT exception type under NativeOnly
        // (NativeTranslationNotSupportedException, since the fallback is forbidden before it even gets a
        // chance to hit the shaper-rebuild failure) than under Native/DriverLinq (InvalidOperationException),
        // hence ThrowsAny rather than a single exception type across all three modes.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode, nameof(GroupBy_after_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.ThrowsAny<Exception>(() =>
                db.Entities
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .GroupBy(r => r.Name)
                    .ToList());
        }
    }

    [Fact]
    public void Another_SelectMany_after_SelectMany_hard_fails_in_every_mode()
    {
        // The second SelectMany's collectionSelector body (`new[] { r.Name }`) is not a nested
        // Queryable.Select(...) call at all, so NativeSelectManyBinder.TryBind's very first structural check
        // rejects it regardless of the first SelectMany's terminal state — confirming the composition-seam
        // stays a hard fail (there is no graceful SelectMany fallback in any mode).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode,
                nameof(Another_SelectMany_after_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities
                    .SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                    .SelectMany(r => new[] { r.Name })
                    .ToList());
        }
    }

    [Fact]
    public void Operator_after_SelectMany_is_unsupported_and_never_returns_silent_wrong_data()
    {
        // A Where applied AFTER our native SelectMany reaches TranslateSelect's non-grouped projection
        // branch (the shaper is our anonymous-type projection, no longer the placeholder terminal shape),
        // bypassing the slot-operator route entirely — the post-terminal guard there
        // (mongoQueryExpression.Select.HasTerminalOperator, true because Task 1 extended it to include
        // UnwindSource != null) marks the query non-native (MarkNotNativelyRepresentable), mirroring the
        // established GroupBy/Distinct "Select after a terminal" guard exactly (see
        // NativeGroupByTests.Select_after_GroupBy_is_unsupported_and_never_returns_silent_null_data /
        // NativeDistinctTests.Select_after_Distinct_is_unsupported_and_never_returns_silent_null_data).
        // In practice this provider cannot build a shaper that re-reads a prior native SelectMany
        // projection's already-resolved-by-index members through the driver-LINQ fallback path
        // (MongoProjectionBindingExpressionVisitor's ProjectionBindingExpression pass-through is
        // deliberately scoped to Route == Projection, so it does NOT cover this now-Fallback-routed case —
        // see the comment there) — so the shape is UNSUPPORTED and throws during translation in EVERY mode,
        // Native, DriverLinq, and NativeOnly alike. This locks in the same invariant as the GroupBy/Distinct
        // siblings: Native never diverges from DriverLinq by silently returning wrong/stale data.
        var seed = SeedOwners();

        using var nativeDb = CreateContext(seed, MongoQueryMode.Native,
            nameof(Operator_after_SelectMany_is_unsupported_and_never_returns_silent_wrong_data) + "N");
        using var driverDb = CreateContext(seed, MongoQueryMode.DriverLinq,
            nameof(Operator_after_SelectMany_is_unsupported_and_never_returns_silent_wrong_data) + "D");

        Exception? Run(SingleEntityDbContext<Owner> db) => Record.Exception(() =>
            db.Entities.SelectMany(o => o.Items.Select(i => new { o.Name, i.Price }))
                .Where(r => r.Price > 10)
                .ToList());

        Assert.NotNull(Run(nativeDb));   // Native throws — NOT a silent wrong-data success
        Assert.NotNull(Run(driverDb));   // DriverLinq throws the same way — no Native-vs-DriverLinq divergence
    }

    [Fact]
    public void Bare_owned_whole_inner_element_goes_native_all_three_spellings()
    {
        // EF-347 Task 3: the bare whole-inner-element owned SelectMany — `from o in q from i in o.Items
        // select i` and its two equivalent spellings — now goes NATIVE (superseding the prior clean-decline
        // behavior the OLD Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking test locked in).
        // TranslateSelect's IsWholeInnerEntitySelector guard sets UnwindSource.WholeElement, the lowerer emits
        // $unwind(includeArrayIndex) + $replaceRoot($mergeObjects) so the owned element becomes the root
        // document (carrying its owner key + array ordinal along under sentinel field names), and
        // MongoShapedQueryCompilingExpressionVisitor's WholeElement branch roots the standard DOM shaper at the
        // owned Item type. Must be .AsNoTracking() — see Bare_SelectMany_tracking_query_throws_
        // InvalidOperationException_in_every_mode for the tracking contract. The trailing `.Select(i => i.Name)`
        // is applied AFTER materialization (.AsEnumerable() first), NOT inside the query — a trailing scalar
        // Select inside the query is POST-TERMINAL composition and throws under NativeOnly (spike-confirmed;
        // see .superpowers/sdd/EF-347-bare-owned-selectmany-spike.md), so asserting over the query result
        // itself (not a further in-query projection) is what actually exercises the native $unwind→$replaceRoot
        // path.
        var seed = SeedOwners();

        using var oneArg = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_goes_native_all_three_spellings) + "OneArg");
        var r1 = oneArg.Entities.AsNoTracking().SelectMany(o => o.Items)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        using var query = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_goes_native_all_three_spellings) + "Query");
        var r2 = (from o in query.Entities.AsNoTracking() from i in o.Items select i)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        using var explicitSel = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_goes_native_all_three_spellings) + "Explicit");
        var r3 = explicitSel.Entities.AsNoTracking().SelectMany(o => o.Items, (o, i) => i)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        var expected = seed.SelectMany(o => o.Items).Select(i => i.Name).OrderBy(n => n).ToList();
        Assert.Equal(expected, r1);
        Assert.Equal(expected, r2);
        Assert.Equal(expected, r3);
    }

    [Fact]
    public void Bare_owned_whole_inner_element_owner_with_zero_items_contributes_no_rows()
    {
        // SeedOwners's Bob has an empty Items list — inner-flatten semantics mean he contributes zero rows,
        // exactly like the projected forms' Empty_or_absent_owned_collection_contributes_no_rows.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_owner_with_zero_items_contributes_no_rows));

        var result = db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList();

        Assert.Equal(5, result.Count); // 2 from Alice + 1 from Carol + 2 from Match; Bob (empty) contributes 0
        Assert.Equal(["Gadget", "Match", "NoMatch", "Thing", "Widget"], result.Select(i => i.Name).OrderBy(n => n).ToList());
    }

    [Fact]
    public void Bare_owned_whole_inner_element_reads_root_relative_not_owner_scoped()
    {
        // Item deliberately shares its "Name" member name with Owner (see the Item class doc comment). If the
        // re-rooted shaper somehow read owner-scoped fields instead of the re-rooted element's own fields, the
        // returned elements' Name would be the OWNER's name (e.g. "Alice"), not the ITEM's own name (e.g.
        // "Widget"/"Gadget"). Asserting the returned Name set matches the items' own names (never the owners')
        // proves the shaper reads root-relative, off the re-rooted element document — not owner-scoped.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_reads_root_relative_not_owner_scoped));

        var names = db.Entities.AsNoTracking().SelectMany(o => o.Items)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        Assert.Equal(["Gadget", "Match", "NoMatch", "Thing", "Widget"], names);
        // "Match" legitimately appears here as the "Match" owner's OWN item Name (see SeedOwners) — a
        // deliberate seeded coincidence for the correlated-equality tests elsewhere, not an owner-Name leak, so
        // it is not added to this forbidden list.
        Assert.DoesNotContain(names, n => n is "Alice" or "Bob" or "Carol");
    }

    [Fact]
    public void Whole_outer_entity_form_still_declines_cleanly_in_every_mode()
    {
        // The whole-OUTER form (`select o`, query syntax) is a DIFFERENT shape from the now-native whole-INNER
        // form above — IsWholeInnerEntitySelector requires Member.Name == "Inner", so `ti => ti.Outer` still
        // falls to the IsTransparentIdentifierMemberAccessSelector else-if and keeps throwing the same clean
        // NotSupportedException as before this Task, in every mode, regardless of tracking (thrown at
        // TRANSLATION time, before MongoQueryMode/tracking behavior are even consulted). There is no re-rooted
        // shaper for a bare OUTER entity (re-rooting via $replaceRoot only ever points at the INNER/owned
        // element), so this shape remains genuinely unsupported, not just untested.
        //
        // NOTE (EF-347 Task 3 claim, RETRACTED by EF-354): this comment used to say the EXPLICIT
        // method-call spelling `SelectMany(o => o.Items, (o, i) => o)` did NOT reach this guard at all —
        // supposedly because EF's nav-expansion optimizes away the intermediate TransparentIdentifier
        // wrap/unwrap for a trivial pass-through of the OUTER parameter — and therefore crashed with a
        // confusing runtime InvalidOperationException. EF-354 MEASURED that on EF8, EF9 and EF10 (both
        // tracking modes, all three query modes) and it is not so: the wrap is emitted for that spelling
        // too, it reaches this same guard, and it declines cleanly. That spelling is now pinned by
        // Whole_outer_entity_explicit_method_call_form_declines_cleanly_in_every_mode below.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            var seed = SeedOwners();

            using var querySyntaxDb = CreateContext(seed, mode,
                nameof(Whole_outer_entity_form_still_declines_cleanly_in_every_mode) + mode + "QuerySyntax");
            Assert.Throws<NotSupportedException>(() =>
                (from o in querySyntaxDb.Entities.AsNoTracking() from i in o.Items select o).ToList());
        }
    }

    [Fact]
    public void Whole_outer_entity_explicit_method_call_form_declines_cleanly_in_every_mode()
    {
        // EF-354 regression pin. The EF-347 Task-3 note on the sibling test above claimed the EXPLICIT
        // method-call spelling `SelectMany(o => o.Items, (o, i) => o)` bypassed the whole-outer guard
        // entirely — the theory being that EF's nav-expansion optimizes away the intermediate
        // TransparentIdentifier wrap/unwrap for a trivial pass-through of the OUTER parameter, leaving
        // BuildBareNavWrappedShaper's resultSelector.Body as the bare parameter `o` and the shaper as the
        // OUTER (owner) entity's own shaper with UnwindSource still set, which then crashed at
        // materialization with a confusing InvalidOperationException ("Document element is missing for
        // required non-nullable property 'Id'").
        //
        // MEASURED (EF-354, all three of EF8/EF9/EF10, tracking and no-tracking, all three query modes):
        // that does NOT happen. EF's nav-expansion DOES emit the TransparentIdentifier wrap plus the
        // trailing `ti => ti.Outer` Select for this spelling too, so it reaches exactly the same
        // TryGetWholeEntityMemberAccess whole-outer guard in TranslateSelect as the query-syntax spelling
        // and already declines with the same clean NotSupportedException. (The REFERENCE-navigation
        // counterpart of this spelling was likewise already covered and green — see
        // Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode.) No provider change was
        // needed; this test exists so the clean decline stays pinned for this spelling too.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            var seed = SeedOwners();

            using var db = CreateContext(seed, mode,
                nameof(Whole_outer_entity_explicit_method_call_form_declines_cleanly_in_every_mode) + mode);

            var ex = Assert.Throws<NotSupportedException>(() =>
                db.Entities.AsNoTracking().SelectMany(o => o.Items, (o, i) => o).ToList());
            Assert.Contains("Projecting a whole entity other than an owned or reference collection element", ex.Message);

            // Tracking too — the guard fires at translation time, before tracking behavior is consulted.
            Assert.Throws<NotSupportedException>(() => db.Entities.SelectMany(o => o.Items, (o, i) => o).ToList());
        }
    }

    private class SentinelOwner
    {
        public ObjectId Id { get; set; }
        public List<SentinelItem> Items { get; set; } = [];
    }

    private class SentinelItem
    {
        public string Name { get; set; } = "";

        // A real stored element literally named "__ord" — MongoReplaceRootStage.OrdinalField, which used to be
        // merged in as a TOP-LEVEL key by the $replaceRoot's $mergeObjects and would therefore SILENTLY
        // OVERWRITE this property's real stored value. The sentinels now live nested under the single reserved
        // MongoReplaceRootStage.ShadowField wrapper (EF-428), so a top-level "__ord" element can no longer
        // collide with anything the merge adds and this shape goes native with its real data intact.
        [MongoDB.Bson.Serialization.Attributes.BsonElement("__ord")]
        public int RealOrd { get; set; }
    }

    private class ShadowFieldSentinelOwner
    {
        public ObjectId Id { get; set; }
        public List<ShadowFieldSentinelItem> Items { get; set; } = [];
    }

    private class ShadowFieldSentinelItem
    {
        public string Name { get; set; } = "";

        // The ONE name that can still collide after EF-428: the sentinel wrapper field itself. $mergeObjects
        // adds it after the unwound element, so a real element of this name would still be silently
        // overwritten — hence the remaining (now single-name) guard in IsWholeElementRepresentable.
        [MongoDB.Bson.Serialization.Attributes.BsonElement(MongoReplaceRootStage.ShadowField)]
        public int RealShadow { get; set; }
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_sentinel_collision_now_goes_native()
    {
        // EF-428 item 1. A real stored property whose configured element name collides with a synthesized
        // sentinel ("__ord"/"__ownerKey") previously declined cleanly, because $mergeObjects merged those two
        // names directly into the re-rooted element's own top-level namespace and would silently overwrite a
        // same-named real field. The sentinels now live under ONE reserved wrapper field
        // (MongoReplaceRootStage.ShadowField), so an ordinary top-level property can never collide with them
        // and this shape goes native — with the colliding property's REAL stored value preserved, which is the
        // regression this test guards.
        var seed = new[]
        {
            new SentinelOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items =
                [
                    new SentinelItem { Name = "Widget", RealOrd = 42 },
                    new SentinelItem { Name = "Sprocket", RealOrd = 43 }
                ],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_sentinel_collision_now_goes_native)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<SentinelOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<SentinelOwner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Ordered client-side: an operator composed AFTER a native SelectMany hard-fails in every mode.
        var elements = db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList()
            .OrderBy(i => i.Name).ToList();

        Assert.Equal(2, elements.Count);
        Assert.Equal(["Sprocket", "Widget"], elements.Select(e => e.Name));
        // The real stored "__ord" values, NOT the synthesized 0/1 array ordinals that used to overwrite them.
        Assert.Equal([43, 42], elements.Select(e => e.RealOrd));
    }

    [Fact]
    public void Bare_owned_whole_inner_element_colliding_with_the_shadow_wrapper_still_declines_cleanly()
    {
        // The narrowed EF-428 guard still has exactly one name to protect: MongoReplaceRootStage.ShadowField
        // itself, the wrapper the $mergeObjects adds. A real element of that name would still be silently
        // overwritten, so IsWholeElementRepresentable declines this shape at TRANSLATION time — the same clean
        // NotSupportedException every other unrepresentable whole-element shape gets.
        var seed = new[]
        {
            new ShadowFieldSentinelOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items = [new ShadowFieldSentinelItem { Name = "Widget", RealShadow = 42 }],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_colliding_with_the_shadow_wrapper_still_declines_cleanly))
            + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ShadowFieldSentinelOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<ShadowFieldSentinelOwner>().OwnsMany(o => o.Items),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var ex = Assert.Throws<NotSupportedException>(() =>
            db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList());
        Assert.IsNotType<KeyNotFoundException>(ex);
        Assert.Contains("Projecting a whole entity other than an owned or reference collection element", ex.Message);
    }

    private class NestedMemberOwner
    {
        public ObjectId Id { get; set; }
        public List<NestedMemberItem> Items { get; set; } = [];
    }

    private class NestedMemberItem
    {
        public string Name { get; set; } = "";
        public ItemDetail Detail { get; set; } = new();
    }

    private class ItemDetail
    {
        public string Note { get; set; } = "";
        public int Weight { get; set; }
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_nested_owned_reference_member_now_goes_native()
    {
        // EF-353 / EF-428 item 2. The re-rooted owned element itself owns a nested single-reference Detail.
        // This previously declined via IsWholeElementRepresentable's blanket GetNavigations().Any() guard,
        // because BuildBareNavWrappedShaper's element shaper bound through the query's ROOT ProjectionMember,
        // which resolves to the OUTER (owner) entity's own EntityProjectionExpression — so EF's auto-generated
        // IncludeExpression for Detail tried (and failed) to bind against the WRONG entity projection
        // ("Unable to bind 'navigation' 'Detail' to an entity projection of 'NestedMemberOwner'").
        // The fix is NOT in BuildBareNavWrappedShaper (which is unchanged): that method runs from
        // TranslateSelectMany, before it is known whether the trailing selector projects the whole element at
        // all. It is MongoQueryExpression.ReRootProjectionAt, called from TranslateSelect at the point
        // UnwindSource.WholeElement is set — it re-points that ROOT ProjectionMember at a fresh
        // EntityProjectionExpression/RootReferenceExpression pair for the ELEMENT's own entity type (the same
        // construction MongoQueryExpression's constructor uses for the query root). The shaper then binds the
        // nested member relative to the re-rooted document — where, after $replaceRoot, "Detail" is a direct
        // top-level field.
        var seed = new[]
        {
            new NestedMemberOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items =
                [
                    new NestedMemberItem { Name = "Widget", Detail = new ItemDetail { Note = "Fragile", Weight = 7 } },
                    new NestedMemberItem { Name = "Sprocket", Detail = new ItemDetail { Note = "Heavy", Weight = 11 } }
                ],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_nested_owned_reference_member_now_goes_native)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<NestedMemberOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<NestedMemberOwner>()
                .OwnsMany(o => o.Items, ib => ib.OwnsOne(i => i.Detail)),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Ordered client-side: an operator composed AFTER a native SelectMany hard-fails in every mode.
        var items = db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList()
            .OrderBy(i => i.Name).ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(["Sprocket", "Widget"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.NotNull(i.Detail));
        // The nested owned member's own scalar leaves must carry the REAL seeded values, read from a direct
        // dotted path relative to the re-rooted element document ("Detail.Note"/"Detail.Weight").
        Assert.Equal(["Heavy", "Fragile"], items.Select(i => i.Detail.Note));
        Assert.Equal([11, 7], items.Select(i => i.Detail.Weight));
    }

    private class ComplexSentinelOwner
    {
        public ObjectId Id { get; set; }
        public List<ComplexSentinelItem> Items { get; set; } = [];
    }

    private class ComplexSentinelItem
    {
        public string Name { get; set; } = "";
        public ComplexSentinelSub Sub { get; set; } = new();
    }

    // [ComplexType] forces EF Core to treat every property of this CLR type as a COMPLEX property
    // (Microsoft.EntityFrameworkCore.Metadata.Conventions.ComplexTypeAttributeConvention), regardless of which
    // builder configured the containing type — load-bearing here because OwnedNavigationBuilder/
    // OwnedNavigationBuilder<,> have NO .ComplexProperty(...) overload at all (only EntityTypeBuilder<T> does),
    // so this is the only way to get a complex property onto an OWNED collection element's entity type.
    [System.ComponentModel.DataAnnotations.Schema.ComplexType]
    private class ComplexSentinelSub
    {
        public string Note { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_complex_property_sentinel_collision_declines_cleanly()
    {
        // EF-347 final-review Minor 1 (fix wave 2): the sentinel-collision guard must also scan COMPLEX
        // properties — innerEntityType.GetProperties() never sees a complex property's own top-level document
        // slot, since IReadOnlyComplexProperty is a distinct metadata type from IReadOnlyProperty. A complex
        // property still occupies exactly one top-level field in the unwound element document (the properties
        // nested INSIDE its ComplexType are sub-fields, e.g. "Sub.Note", and can never collide with a top-level
        // sentinel) — so a complex property named/renamed to the reserved wrapper field would slip past a
        // properties-only guard and be SILENTLY OVERWRITTEN by the $mergeObjects merge exactly like the
        // scalar-property case. There is no dedicated Mongo builder API to rename a complex property's own
        // document slot (unlike PropertyBuilder.HasElementName for scalar properties), so this test sets the
        // Mongo:ElementName annotation directly on the auto-discovered complex property's metadata (the same
        // annotation GetComplexPropertyElementName reads) to construct the exact colliding shape.
        // Since EF-428 the only name that can still collide is MongoReplaceRootStage.ShadowField (the sentinels
        // themselves are now nested one level under it), so that is the name used here.
        // IsWholeElementRepresentable declines this shape at TRANSLATION time, falling through to the SAME
        // clean NotSupportedException every other unrepresentable whole-element shape gets — confirmed to
        // require no live server connection (a purely translation-time decline).
        var seed = new[]
        {
            new ComplexSentinelOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items = [new ComplexSentinelItem { Name = "Widget", Sub = new ComplexSentinelSub { Note = "Fragile" } }],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_complex_property_sentinel_collision_declines_cleanly)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ComplexSentinelOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<ComplexSentinelOwner>().OwnsMany(o => o.Items);

                // No OwnedNavigationBuilder.ComplexProperty(...) overload exists, so reach the auto-discovered
                // ([ComplexType]-driven) complex property via the model directly and set its element-name
                // annotation to collide with the sentinel wrapper field.
                var itemEntityType = mb.Model.FindEntityType(typeof(ComplexSentinelItem))!;
                var complexProperty = itemEntityType.FindComplexProperty(nameof(ComplexSentinelItem.Sub))!;
                complexProperty.SetAnnotation(MongoAnnotationNames.ElementName, MongoReplaceRootStage.ShadowField);
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var ex = Assert.Throws<NotSupportedException>(() =>
            db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList());
        Assert.IsNotType<KeyNotFoundException>(ex);
    }

    private class ConvertedKeyOwner
    {
        public ObjectId Id { get; set; }
        public List<ConvertedKeyItem> Items { get; set; } = [];
    }

    private class ConvertedKeyItem
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_value_converted_owner_key_declines_cleanly()
    {
        // EF-347 final-review Minor 2 (fix wave 2): the owner's Id (ObjectId) is configured with a value
        // converter (ObjectId <-> string). EF Core automatically propagates that SAME converter instance onto
        // the owned element's shadow owner-FK key property (confirmed empirically via model introspection
        // during this Task: the OwnerId shadow property on ConvertedKeyItem reports the identical converter) —
        // but the $replaceRoot __ownerKey sentinel is populated straight from the owner document's raw "$_id"
        // (see MongoPipelineFactory's $replaceRoot rendering), bypassing that converter entirely. A property
        // whose materialization expects the CONVERTED (string) representation but is fed the raw, unconverted
        // ObjectId would diverge/crash at materialization. IsWholeElementRepresentable now declines this shape
        // via NativeGroupByBinder.HasDefaultKeySerialization — reused UNCHANGED from the identical GroupBy-key /
        // OfType-discriminator guard, not a parallel/duplicate predicate — applied to the owned element's
        // owned-key properties (IsOwnedTypeKey(): the owner-FK and the array-ordinal shadow properties).
        // Falls through to the SAME clean NotSupportedException every other unrepresentable whole-element shape
        // gets; confirmed to require no live server connection (a purely translation-time decline).
        var seed = new[]
        {
            new ConvertedKeyOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items = [new ConvertedKeyItem { Name = "Widget" }],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_value_converted_owner_key_declines_cleanly)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ConvertedKeyOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<ConvertedKeyOwner>(b =>
                {
                    b.Property(o => o.Id).HasConversion(id => id.ToString(), s => ObjectId.Parse(s));
                    b.OwnsMany(o => o.Items);
                });
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var ex = Assert.Throws<NotSupportedException>(() =>
            db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList());
        Assert.IsNotType<KeyNotFoundException>(ex);
    }

    private class ExplicitKeyOwner
    {
        public ObjectId Id { get; set; }
        public List<ExplicitKeyItem> Items { get; set; } = [];
    }

    private class ExplicitKeyItem
    {
        public Guid Key { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_explicit_owned_key()
    {
        // EF-347 spike residual risk #2 (USER DECISION: test + defer if broken): the fixture used everywhere
        // else in this file relies on the DEFAULT synthesized owned key (shadow owner-FK + shadow array
        // ordinal). An OwnsMany configured with an EXPLICIT user-defined key property has a different key
        // shape the edit-8 sentinel-read branch was not spiked against. This test exercises that shape
        // directly: if it materializes correctly under NativeOnly + AsNoTracking, this assertion passes; if the
        // provider cannot yet support it, the test (and the production guard it exercises) must instead assert
        // a CLEAN decline (fallback or a documented exception), never silent wrong data — see the report for
        // which outcome was observed.
        var key1 = Guid.NewGuid();
        var key2 = Guid.NewGuid();
        var seed = new[]
        {
            new ExplicitKeyOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items = [new ExplicitKeyItem { Key = key1, Name = "Widget" }, new ExplicitKeyItem { Key = key2, Name = "Gadget" }],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_explicit_owned_key)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ExplicitKeyOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<ExplicitKeyOwner>()
                .OwnsMany(o => o.Items, ib => ib.HasKey(i => i.Key)),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var names = db.Entities.AsNoTracking().SelectMany(o => o.Items)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        Assert.Equal(["Gadget", "Widget"], names);
    }

    private class SharedElementOwner
    {
        public ObjectId Id { get; set; }
        public List<SharedElement> Primary { get; set; } = [];
        public List<SharedElement> Secondary { get; set; } = [];
    }

    // Owned by TWO navigations (Primary/Secondary) on the SAME owner, with no per-nav CLR type or unique
    // name given to either — the pattern SharedClrTypeProjectionTests.MultiSameTypeOwner also uses to force a
    // SHARED-TYPE owned entity type (Model.FindEntityType(typeof(SharedElement)) resolves to null because the
    // model cannot pick a single unambiguous entity type for that CLR type).
    private class SharedElement
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_over_shared_clr_type_goes_native()
    {
        // EF-347 Task 3 review finding M1 (fix wave 1): MongoShapedQueryCompilingExpressionVisitor.
        // VisitShapedQuery computed `projectedEntityType` via QueryCompilationContext.Model.FindEntityType on
        // the result CLR type and, when that returned null, returned VisitProjectedQuery(...) BEFORE ever
        // reaching the WholeElement branch immediately below it in the method — so a bare whole-inner-element
        // SelectMany over a SHARED-TYPE owned collection (this fixture: SharedElement is owned by both
        // Primary and Secondary on the same owner) bypassed the correct re-rooted shaper entirely, landing on
        // an untested, undetermined path. The fix moves the WholeElement check to run FIRST: it roots the
        // shaper at wholeElementUnwind.InnerEntityType — the owner-scoped IEntityType the binder captured
        // from navigation.TargetEntityType — which is correct regardless of whether
        // FindEntityType(elementClrType) can resolve a single entity type for that CLR type. This test proves
        // both owned collections (Primary and Secondary, same shared CLR type) materialize correctly and
        // independently under NativeOnly + AsNoTracking.
        var seed = new[]
        {
            new SharedElementOwner
            {
                Id = ObjectId.GenerateNewId(),
                Primary = [new SharedElement { Name = "P1" }, new SharedElement { Name = "P2" }],
                Secondary = [new SharedElement { Name = "S1" }],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_over_shared_clr_type_goes_native)) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<SharedElementOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<SharedElementOwner>(b =>
            {
                b.OwnsMany(o => o.Primary);
                b.OwnsMany(o => o.Secondary);
            }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Sanity check (per the task brief): confirm the fixture actually produces a shared-type entity type —
        // otherwise this test would not exercise M1 at all.
        Assert.Null(db.Model.FindEntityType(typeof(SharedElement)));

        var primaryNames = db.Entities.AsNoTracking().SelectMany(o => o.Primary)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();
        Assert.Equal(["P1", "P2"], primaryNames);

        var secondaryNames = db.Entities.AsNoTracking().SelectMany(o => o.Secondary)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();
        Assert.Equal(["S1"], secondaryNames);
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_OrderBy_falls_back_gracefully_except_under_NativeOnly()
    {
        // Composition-seam coverage (EF-347 slice 4, Task 3): empirically (spiked against the running server,
        // not guessed) an OrderBy composed directly after the explicit-result-selector SelectMany behaves the
        // SAME as the Where case above (Explicit_result_selector_form_followed_by_Where_falls_back_gracefully_
        // except_under_NativeOnly) — NOT like the inner-Select form's OrderBy_after_SelectMany_hard_fails_in_
        // every_mode. The post-terminal guard still marks the query non-native (HasTerminalOperator), but the
        // captured chain here has no EF-synthesized ti.Inner unwrap over an internal TransparentIdentifier CLR
        // type (see the type doc comment), so the driver's own LINQ v3 provider genuinely translates
        // SelectMany(bareNav, trivialResultSelector).Select(ti => new{...}).OrderBy(...) and returns the
        // CORRECT ordered result under Native/DriverLinq; only NativeOnly (forbidding the fallback) throws.
        var seed = SeedOwners();
        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .OrderBy(r => r.Price)
            .ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_followed_by_OrderBy_falls_back_gracefully_except_under_NativeOnly) + mode);

            var result = db.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .OrderBy(r => r.Price)
                .ToList();
            Assert.Equal(expected, result);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_followed_by_OrderBy_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .OrderBy(r => r.Price)
                .ToList());
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_Skip_falls_back_gracefully_except_under_NativeOnly()
    {
        // Same graceful-fallback family as OrderBy/Where above. Skip has no preceding $sort so its result
        // depends on natural document order; empirically (spiked against the running server) natural order
        // matches insertion order for this small, single-writer seeded collection with no index in play, so
        // comparing directly against the in-memory expected (computed over the SAME seed array order) is safe
        // and deterministic here — the SAME assumption the pre-existing Skip_after_SelectMany_hard_fails_in_
        // every_mode test (inner-Select form) relies on implicitly by not needing a result comparison at all.
        var seed = SeedOwners();
        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .Skip(1)
            .ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_followed_by_Skip_falls_back_gracefully_except_under_NativeOnly) + mode);

            var result = db.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .Skip(1)
                .ToList();
            Assert.Equal(expected, result);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_followed_by_Skip_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .Skip(1)
                .ToList());
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_Take_falls_back_gracefully_except_under_NativeOnly()
    {
        // Same graceful-fallback family as Skip above (see its comment re: natural-order determinism).
        var seed = SeedOwners();
        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .Take(1)
            .ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_followed_by_Take_falls_back_gracefully_except_under_NativeOnly) + mode);

            var result = db.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .Take(1)
                .ToList();
            Assert.Equal(expected, result);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_followed_by_Take_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                .Take(1)
                .ToList());
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_Count_falls_back_gracefully_except_under_NativeOnly()
    {
        // Mirrors Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly (inner-Select form) —
        // Count needs no shaper at all, so it falls back gracefully for BOTH forms alike, unlike Where/OrderBy/
        // Skip/Take/GroupBy/another-SelectMany above and below (which differ between the two forms).
        var seed = SeedOwners();
        var expectedCount = seed.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price }).Count();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_followed_by_Count_falls_back_gracefully_except_under_NativeOnly) + mode);

            var count = db.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price }).Count();
            Assert.Equal(expectedCount, count);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_followed_by_Count_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price }).Count());
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_Distinct_falls_back_gracefully_except_under_NativeOnly()
    {
        // EF-347 Slice 5 fix (silent-null Distinct-after-SelectMany, whole-branch review). This is the OWNED
        // sibling of Reference_form_followed_by_Distinct_hard_fails_in_every_mode above — same root cause
        // (predates this slice; the owned form was already reachable at base d2cacc5 = slice 4), same one-line
        // fix (NativeGroupByBinder.TryBindDistinctFromProjection now also declines when UnwindSource != null).
        // BEFORE the fix, this query "succeeded" under BOTH Native and NativeOnly with count=3
        // names=[null,null,null] — empirically confirmed by direct reproduction (the UnwindSource branch in
        // MongoSelectLowerer.Lower returns early with a flatten $project reading "_id.Name", which was never
        // populated because the $group the flatten depends on was never emitted). Only DriverLinq mode
        // returned the correct count=3 names=[Alice,Carol,Match] (Bob's empty Items collection contributes no
        // rows; Alice's two items collapse to one distinct Name; Carol and Match each contribute one) — that
        // DriverLinq run is the baseline this test locks in.
        // AFTER the fix, the Distinct declines to bind natively (same as the bare-scalar/whole-entity Distinct
        // cases in NativeDistinctTests), so this collapses into the SAME "operator after (owned) SelectMany"
        // graceful-fallback family as Where/OrderBy/Skip/Take/Count above: the captured chain
        // (SelectMany(bareNav, trivialResultSelector).Select(ti => new{...}).Distinct()) is one the driver's own
        // LINQ v3 provider already translates correctly, so Native and DriverLinq both return the CORRECT
        // distinct rows — asserted here explicitly (not just "no exception") so the silent-null regression can
        // never reappear — and only NativeOnly (which forbids the fallback) throws.
        var seed = SeedOwners();
        var expected = seed
            .SelectMany(o => o.Items, (o, i) => new { o.Name })
            .Distinct()
            .OrderBy(r => r.Name)
            .ToList();
        Assert.Equal(new[] { "Alice", "Carol", "Match" }, expected.Select(r => r.Name).ToArray()); // Bob (0 items) contributes no rows

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_followed_by_Distinct_falls_back_gracefully_except_under_NativeOnly) + mode);

            var result = db.Entities
                .SelectMany(o => o.Items, (o, i) => new { o.Name })
                .Distinct()
                .AsEnumerable()
                .OrderBy(r => r.Name)
                .ToList();
            Assert.Equal(expected, result);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_followed_by_Distinct_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name }).Distinct().ToList());
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_GroupBy_hard_fails_in_every_mode()
    {
        // UNLIKE Where/OrderBy/Skip/Take/Count above, GroupBy composed after the explicit-result-selector
        // SelectMany does NOT gracefully fall back — empirically (spiked against the running server) it throws
        // in EVERY mode, Native and DriverLinq included: InvalidOperationException("Calling
        // 'ShapedQueryExpression.VisitChildren' is not allowed...") under Native/DriverLinq, and
        // NativeTranslationNotSupportedException under NativeOnly. Same outcome as the inner-Select form's
        // GroupBy_after_SelectMany_hard_fails_in_every_mode — GroupBy has its own TranslateGroupBy override
        // (bypassing the ordinary slot/cardinality catch-all guards) whose own fallback shaper cannot rebuild a
        // driver-LINQ expression that re-reads the prior native SelectMany projection's already-resolved-by-
        // index members, regardless of which SelectMany form produced that projection.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode,
                nameof(Explicit_result_selector_form_followed_by_GroupBy_hard_fails_in_every_mode) + mode);

            Assert.ThrowsAny<Exception>(() =>
                db.Entities
                    .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                    .GroupBy(r => r.Name)
                    .ToList());
        }
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_another_SelectMany_hard_fails_in_every_mode()
    {
        // UNLIKE Where/OrderBy/Skip/Take/Count above: a second SelectMany composed after the explicit-result-
        // selector SelectMany hard-fails in every mode — empirically (spiked against the running server) an
        // InvalidOperationException("could not be translated") in ALL THREE modes alike, including DriverLinq.
        // The second SelectMany's collection-selector body (`new[] { r.Name }`) is not a nested
        // Queryable.Select(...) call, so NativeSelectManyBinder.TryBind's structural check rejects it exactly
        // like the inner-Select form's sibling test (Another_SelectMany_after_SelectMany_hard_fails_in_every_
        // mode) — but here the failure surfaces even under explicit DriverLinq (rather than only NativeOnly),
        // because the driver's own LINQ v3 provider independently cannot translate this exact array-literal
        // shape either, regardless of which path produced the row set it is applied to.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode,
                nameof(Explicit_result_selector_form_followed_by_another_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities
                    .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
                    .SelectMany(r => new[] { r.Name })
                    .ToList());
        }
    }

    [Fact]
    public void Explicit_result_selector_form_parametrized_outer_predicate_composes_correctly_with_native_SelectMany()
    {
        // Explicit-result-selector-form sibling of Parametrized_outer_predicate_composes_correctly_with_
        // native_SelectMany (inner-Select form) above: a Where BEFORE the native SelectMany lowers into the
        // normal pre-$unwind $match slot exactly like any other native query, and this proves a parametrized
        // predicate (a captured local, not a constant) substitutes correctly through the shared PlaceholderTable
        // when composed with the bare-nav-unwind + trailing-Select $unwind + $project pipeline.
        var seed = SeedOwners();
        var p = "Alice";
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_parametrized_outer_predicate_composes_correctly_with_native_SelectMany));

        var result = db.Entities
            .Where(o => o.Name == p)
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .AsEnumerable()
            .OrderBy(r => r.Price)
            .ToList();

        var expected = seed
            .Where(o => o.Name == p)
            .SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price })
            .OrderBy(r => r.Price)
            .ToList();

        // Succeeding under NativeOnly is itself the "went native" signal.
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Whole_inner_entity_form_tracking_query_throws_InvalidOperationException_in_every_mode()
    {
        // Companion to Bare_owned_whole_inner_element_goes_native_all_three_spellings above, covering a
        // TRACKING query (no .AsNoTracking()) for the explicit-result-selector and query-syntax spellings —
        // the bare 1-arg spelling's tracking behavior is covered by
        // Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode. As of EF-347 Task 3,
        // this shape routes NATIVE at translation time (TranslateSelect sets UnwindSource.WholeElement), but a
        // TRACKING query still hits EF Core's OWN runtime "can't track an owned entity without its owner"
        // safeguard ("A tracking query is attempting to project an owned entity without a corresponding owner
        // in its result...", InvalidOperationException) at shaper-materializer injection time — BEFORE the
        // native-vs-driver-LINQ split has any bearing, so all three modes throw the identical
        // InvalidOperationException. This SUPERSEDES the pre-Task-3 behavior (a translation-time
        // NotSupportedException, unconditional on tracking) — the tracking contract is intentionally EF Core's
        // own, not something this provider enforces.
        var seed = SeedOwners();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var explicitDb = CreateContext(seed, mode,
                nameof(Whole_inner_entity_form_tracking_query_throws_InvalidOperationException_in_every_mode) + mode + "Explicit");
            Assert.Throws<InvalidOperationException>(() =>
                explicitDb.Entities.SelectMany(o => o.Items, (o, i) => i).ToList());

            using var querySyntaxDb = CreateContext(seed, mode,
                nameof(Whole_inner_entity_form_tracking_query_throws_InvalidOperationException_in_every_mode) + mode + "QuerySyntax");
            Assert.Throws<InvalidOperationException>(() =>
                (from o in querySyntaxDb.Entities from i in o.Items select i).ToList());
        }
    }

    [Fact]
    public void Reference_form_bare_entity_tracking_query_returns_tracked_entities()
    {
        // Unlike an owned collection element (EF refuses to track it without its owner — see
        // Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode), a reference entity is
        // an ordinary trackable entity with its own real key. A tracking query returns tracked instances.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_tracking_query_returns_tracked_entities), out _, out var items);

        var tracked = db.Owners.SelectMany(o => o.Refs).ToList(); // default tracking (no AsNoTracking)

        Assert.Equal(5, tracked.Count); // 2 from Alice + 1 from Carol + 2 from Dave; Bob (0 children) contributes 0
        Assert.Equal(5, db.ChangeTracker.Entries<RefItem>().Count());
        Assert.All(tracked, r => Assert.Equal(EntityState.Unchanged, db.Entry(r).State));

        // A mutation + SaveChanges round-trips (proves these are real tracked entities).
        var first = tracked[0];
        first.Tag = "MutatedTag";
        db.SaveChanges();

        // Re-verify persistence within the SAME context (a second context on the same collections needs no
        // helper): clear the tracker, then re-query — this re-reads from the database rather than trusting the
        // in-memory tracked instance.
        db.ChangeTracker.Clear();
        var tags = db.Refs.AsEnumerable().Select(r => r.Tag).ToList();
        Assert.Contains("MutatedTag", tags);
    }

    [Fact]
    public void Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode()
    {
        // A reference element that EAGER-LOADS a further navigation (EagerChild.Detail, a genuine cross-
        // collection nav, unlike an owned member) declines cleanly in every mode — confirmed empirically, not
        // assumed, and via a DIFFERENT mechanism than originally predicted for this Task. The sanity anchor for
        // this test is Reference_form_bare_entity_result_goes_native_all_three_spellings: RefItem's own back-
        // reference (Owner) is a plain LAZY inverse nav, never auto-included, so it does NOT block that shape —
        // only an EAGER-LOADED nav (EagerChild.Detail here) does.
        //
        // EMPIRICAL FINDING (diagnosed by instrumenting TranslateSelect during this Task, not assumed): this
        // does NOT reach IsWholeElementRepresentable's narrowed reference eager-nav guard at all. Materializing
        // a genuine CROSS-COLLECTION eager Include (unlike an owned/embedded member, which is structurally a
        // single-hop IncludeExpression(ti.Inner, ownedNav)) requires EF's nav-expansion to inject an ADDITIONAL
        // join step, producing a DOUBLY-NESTED TransparentIdentifier — the trailing selector body becomes
        // `Include(ti.Outer.Inner, ...)`, a TWO-hop member access, not the single-hop `ti.Inner` that
        // TryGetWholeEntityMemberAccess's unwrap-through-Include loop recognizes. So `wholeEntityMember` comes
        // back null, and the shape falls into the SAME "unrecognized whole-entity projection" bucket as a
        // computed leaf (see Reference_form_computed_leaf_hard_fails_in_every_mode) — TranslateSelect's ordinary
        // `else { MarkNotNativelyRepresentable(); }` branch, NOT the dedicated translation-time
        // NotSupportedException throw. The "graceful" fallback attempt this triggers then fails for the SAME
        // reason every other reference-SelectMany fallback does — no driver-LINQ baseline exists for a cross-
        // collection SelectMany — so Native/DriverLinq both throw the identical InvalidOperationException, and
        // NativeOnly (which forbids the fallback attempt) throws its own,
        // different NativeTranslationNotSupportedException first.
        //
        // CORRECTION (EF-379 fix round 1). This paragraph used to name that InvalidOperationException as the
        // provider's own "Unsupported cross-DbSet query". MEASURED — by capturing the message here, at this
        // branch's base commit AND at HEAD, byte-identical in both — it is EF Core's plain
        // TranslationFailed ("... could not be translated. Either rewrite the query ..."), with no provider
        // detail sentence at all. The "Unsupported cross-DbSet query" string does exist
        // (MongoEFToLinqTranslatingExpressionVisitor), but this shape never reaches it. The stale attribution
        // was pre-existing, not something EF-379 introduced, and the structural diagnosis above is confirmed
        // by the printed tree (`.Select(ti => Include(Entity: ti.Outer.Inner, Navigation: Detail, ti.Inner))`
        // over a doubly-nested TransparentIdentifier). NOTE for anyone re-reading this test as an EF-379
        // control: its hop-2 key selector IS `ti => EF.Property(ti.Inner, "DetailId")`, i.e. a TransitiveHop
        // at the FIRST TranslateJoinCore call — this is one of the shapes the withdrawn "TransitiveHop with
        // no candidate declines" rule hard-failed, and the bare Assert.Throws<InvalidOperationException>
        // below could not tell the two mechanisms apart. The end-to-end safety invariant this Task set
        // out to prove — an eager-loaded reference nav declines cleanly in every mode, never silently wrong data
        // — DOES hold; it just holds via the ordinary computed-leaf-style decline, not the dedicated
        // IsWholeElementRepresentable guard. This is a real, valid decline case in its own right — kept as-is —
        // but it does NOT prove the guard itself is reachable. See
        // Reference_form_bare_entity_with_owned_embedded_navigation_declines_cleanly_via_representability_guard
        // below for the shape that DOES reach IsWholeElementRepresentable's reference eager-nav guard (an
        // eager-loaded OWNED sub-navigation on the reference element, which is a single-hop IncludeExpression
        // rather than a doubly-nested join, so it doesn't hit this same detour).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateEagerRefContext(mode,
                nameof(Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode) + mode);
            Assert.Throws<InvalidOperationException>(() => db.Parents.SelectMany(p => p.EagerChildren).ToList());
        }

        using var nativeOnlyDb = CreateEagerRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode) + "NativeOnly");
        Assert.ThrowsAny<Exception>(() => nativeOnlyDb.Parents.SelectMany(p => p.EagerChildren).ToList());
    }

    [Fact]
    public void Reference_form_bare_entity_with_owned_embedded_navigation_declines_cleanly_via_representability_guard()
    {
        // HYPOTHESIS (review follow-up on the cross-collection AutoInclude test above): that test's shape does
        // NOT reach IsWholeElementRepresentable's reference eager-nav guard — EF's nav-expansion injects an
        // extra join for a genuine cross-collection eager Include, producing a doubly-nested TransparentIdentifier
        // (`ti.Outer.Inner`) that TryGetWholeEntityMemberAccess's unwrap-through-Include loop does not recognize,
        // so it declines via the ordinary MarkNotNativelyRepresentable() path instead. A reference element that
        // eager-loads an OWNED (embedded) sub-navigation, by contrast, is structurally a SINGLE-hop
        // IncludeExpression(ti.Inner, ownedNav) — TryGetWholeEntityMemberAccess's unwrap-through-Include loop
        // reduces it to the bare `ti.Inner` member access, so it DOES reach the whole-entity gate; owned
        // navigations are eager-loaded by EF Core convention, so IsWholeElementRepresentable's
        // `!innerEntityType.GetNavigations().Any(n => n.IsEagerLoaded)` check then declines it via the dedicated
        // translation-time NotSupportedException, BEFORE MongoQueryMode is even consulted — so the SAME exception
        // type fires in every mode, unlike the cross-collection test above (which throws InvalidOperationException
        // under Native/DriverLinq but a different NativeTranslationNotSupportedException-or-similar under
        // NativeOnly).
        //
        // OBSERVED (not assumed): running this test confirms the hypothesis — NotSupportedException fires in
        // Native, DriverLinq, and NativeOnly alike. See NativeSelectManyBinder.TryGetWholeEntityMemberAccess /
        // IsWholeElementRepresentable (Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs) for the
        // production mechanism this proves live (test-only change — no production code was modified to make
        // this pass).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateOwnedSubNavContext(mode,
                nameof(Reference_form_bare_entity_with_owned_embedded_navigation_declines_cleanly_via_representability_guard) + mode);
            Assert.Throws<NotSupportedException>(() => db.Parents.SelectMany(p => p.Children).ToList());
        }
    }

    [Fact]
    public void Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode) + mode, out _, out _);
            Assert.Throws<NotSupportedException>(() => db.Owners.SelectMany(o => o.Refs, (o, r) => o).ToList());
        }
    }

    [Fact]
    public void Reference_form_bare_entity_followed_by_Where_hard_fails_in_every_mode()
    {
        // Same per-mode split as Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode
        // above (verified empirically, not assumed): the trailing Where composes onto an already-native
        // reference SelectMany's ForceUnwind $lookup, and the "graceful" MarkNotNativelyRepresentable()
        // fallback it triggers has no driver-LINQ baseline to fall back to (the driver's own LINQ v3 provider
        // rejects any cross-collection SelectMany outright) — so Native/DriverLinq both surface the identical
        // "Unsupported cross-DbSet query" InvalidOperationException, while NativeOnly (which forbids the
        // fallback attempt in the first place) throws its own NativeTranslationNotSupportedException instead.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_bare_entity_followed_by_Where_hard_fails_in_every_mode) + mode, out _, out _);
            Assert.Throws<InvalidOperationException>(() => db.Owners.SelectMany(o => o.Refs).Where(r => r.Tag != "").ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_bare_entity_followed_by_Where_hard_fails_in_every_mode) + "NativeOnly", out _, out _);
        Assert.ThrowsAny<Exception>(() => nativeOnlyDb.Owners.SelectMany(o => o.Refs).Where(r => r.Tag != "").ToList());
    }

    private class NestedCollOwner
    {
        public ObjectId Id { get; set; }
        public List<NestedCollItem> Items { get; set; } = [];
    }

    private class NestedCollItem
    {
        public string Name { get; set; } = "";
        public List<NestedCollTag> Tags { get; set; } = [];
    }

    private class NestedCollTag
    {
        public string Label { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_nested_owned_collection_member_now_goes_native()
    {
        // EF-353 / EF-428 item 2, collection half. The blanket GetNavigations().Any() guard declined a nested
        // owned COLLECTION under the re-rooted element for the same reason as a nested single reference: the
        // element shaper bound through the OUTER entity's projection. With the element-rooted projection it
        // materializes correctly too — an owned collection is embedded as a BSON array in the element's own
        // document, so after $replaceRoot it is a direct top-level array of the re-rooted document and needs no
        // extra pipeline stage. This test pins the ARRAY CONTENTS (not merely non-empty) per element, which is
        // what discriminates a correctly re-rooted read from one that silently reads the wrong document.
        var seed = new[]
        {
            new NestedCollOwner
            {
                Id = ObjectId.GenerateNewId(),
                Items =
                [
                    new NestedCollItem { Name = "Widget", Tags = [new NestedCollTag { Label = "red" }, new NestedCollTag { Label = "blue" }] },
                    new NestedCollItem { Name = "Sprocket", Tags = [new NestedCollTag { Label = "green" }] }
                ],
            },
        };

        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_nested_owned_collection_member_now_goes_native))
            + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<NestedCollOwner>(collectionName);
        collection.InsertMany(seed);

        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<NestedCollOwner>()
                .OwnsMany(o => o.Items, ib => ib.OwnsMany(i => i.Tags)),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // Ordered client-side: an operator composed AFTER a native SelectMany hard-fails in every mode.
        var items = db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList().OrderBy(i => i.Name).ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(["Sprocket", "Widget"], items.Select(i => i.Name));
        Assert.Equal(["green"], items[0].Tags.Select(t => t.Label));
        Assert.Equal(["red", "blue"], items[1].Tags.Select(t => t.Label));
    }

    private class XRefOwner
    {
        public ObjectId Id { get; set; }
        public List<XRefItem> Items { get; set; } = [];
    }

    private class XRefItem
    {
        public string Name { get; set; } = "";
        public ObjectId? TargetId { get; set; }
        public XRefTarget? Target { get; set; }
    }

    private class XRefTarget
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class XRefDbContext(DbContextOptions options, string ownersCollection, string targetsCollection)
        : DbContext(options)
    {
        public DbSet<XRefOwner> Owners { get; set; } = null!;
        public DbSet<XRefTarget> Targets { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<XRefOwner>(b =>
            {
                b.ToCollection(ownersCollection);
                b.OwnsMany(o => o.Items, ib => ib.HasOne(i => i.Target).WithMany().HasForeignKey(i => i.TargetId));
            });
            modelBuilder.Entity<XRefTarget>(b => b.ToCollection(targetsCollection));
        }
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_cross_collection_navigation_still_declines_cleanly()
    {
        // The one navigation shape IsWholeElementRepresentable still rejects for an OWNED element after
        // EF-353: a NON-embedded (cross-collection) reference off the element. Unlike an owned nested member —
        // which lives inside the re-rooted document and needs no extra stage — this one needs a $lookup that
        // this shape's lowering never emits, so it declines at TRANSLATION time with the same clean
        // NotSupportedException every other unrepresentable whole-element shape gets. Discriminates the
        // narrowed guard from a blanket removal: if the guard were dropped entirely this shape would stop
        // declining.
        var ownersCollection = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Bare_owned_whole_inner_element_with_cross_collection_navigation_still_declines_cleanly))
            + Guid.NewGuid().ToString("N")[..8];
        var targetsCollection = ownersCollection + "_targets";

        var optionsBuilder = new DbContextOptionsBuilder<XRefDbContext>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
            .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        new MongoDbContextOptionsBuilder(optionsBuilder).UseQueryMode(MongoQueryMode.NativeOnly);

        using var db = new XRefDbContext(optionsBuilder.Options, ownersCollection, targetsCollection);

        var ex = Assert.Throws<NotSupportedException>(() =>
            db.Owners.AsNoTracking().SelectMany(o => o.Items).ToList());
        Assert.Contains("Projecting a whole entity other than an owned or reference collection element", ex.Message);
    }

}
