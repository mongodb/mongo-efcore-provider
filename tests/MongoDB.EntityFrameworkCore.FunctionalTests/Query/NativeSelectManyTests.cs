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
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

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
/// narrower case (see the <c>Whole_inner_entity_form_*</c> / <c>Bare_SelectMany_*</c> tests below):
/// <see cref="NativeSelectManyBinder.TryBindBareNavUnwind"/> accepts the bare nav structurally (it cannot see
/// the trailing selector yet), so <c>UnwindSource</c> is set; the trailing <c>ti =&gt; ti.Inner</c> selector
/// then bypasses both the pending-SelectMany projection branch (it is not an anonymous/DTO construction) and
/// the post-terminal
/// <see cref="MongoQueryableMethodTranslatingExpressionVisitor.IsTransparentIdentifierMemberAccessSelector"/>
/// guard (it IS that shape). A dedicated guard in <c>TranslateSelect</c> — "<c>UnwindSource</c> set,
/// <c>Projection</c> still empty, AND the selector is the whole-inner-entity
/// <c>IsTransparentIdentifierMemberAccessSelector</c> shape" — hard-declines with a clean
/// <see cref="NotSupportedException"/> for exactly this case, rather than the generic
/// <c>MarkNotNativelyRepresentable()</c> graceful fallback every other unsupported SelectMany shape uses: that
/// "graceful" fallback does not actually work for this shape (the driver-LINQ path cannot materialize a bare
/// owned-collection element with no owner context either, so it used to crash with an internal
/// <see cref="System.Collections.Generic.KeyNotFoundException"/> instead of declining cleanly). Thrown at
/// TRANSLATION time — before <see cref="MongoQueryMode"/> is even consulted — so the SAME exception fires in
/// all three modes, and regardless of tracking (EF Core's own "can't track an owned entity without an owner"
/// safeguard never gets a chance to run for the tracking case, since this decline happens first). By contrast,
/// the computed-leaf case (e.g. <c>SelectMany(o =&gt; o.Items, (o, i) =&gt; new { X = i.Price * 2 })</c>) is NOT
/// this shape (its selector body is a <c>NewExpression</c>, not a bare member access) and still falls back
/// gracefully via <c>MarkNotNativelyRepresentable()</c> — its driver-LINQ fallback genuinely works. Composing a
/// FURTHER operator after an already-
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
        public List<Item> Items { get; set; } = [];
    }

    private class Item
    {
        // Deliberately shares its "Name" member name with Owner, to prove the two-scope binder never
        // conflates the outer (unprefixed "$Name") and inner ("$Items.Name") field refs.
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
    }

    private static Owner[] SeedOwners() =>
    [
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Alice",
            Items =
            [
                new Item { Name = "Widget", Price = 9.99m },
                new Item { Name = "Gadget", Price = 19.99m },
            ],
        },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Items = [] }, // empty owned collection
        new()
        {
            Id = ObjectId.GenerateNewId(), Name = "Carol",
            Items = [new Item { Name = "Thing", Price = 5m }],
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

        Assert.Equal(3, result.Count); // 2 from Alice + 1 from Carol; Bob (empty) contributes 0
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
        // Sanity: outer/inner never conflated (an owner's own name never leaks into InnerName and vice versa).
        Assert.All(result, r => Assert.NotEqual(r.OuterName, r.InnerName));
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
        Assert.All(result, r => Assert.NotEqual(r.OuterName, r.InnerName));
    }

    [Fact]
    public void Explicit_result_selector_form_empty_or_absent_owned_collection_contributes_no_rows()
    {
        var seed = SeedOwners(); // Bob has an empty Items list
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_empty_or_absent_owned_collection_contributes_no_rows));

        var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price }).ToList();

        Assert.Equal(3, result.Count); // 2 from Alice + 1 from Carol; Bob (empty) contributes 0
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
    public void Explicit_result_selector_form_computed_leaf_falls_back_gracefully_except_under_NativeOnly()
    {
        // EF-347 slice 4: DIFFERENT hard-fail mechanism from the inner-Select form. There, TryBind rejects the
        // computed leaf structurally BEFORE TranslateSelectMany ever returns, so it returns null and EF's own
        // translation-failure path is reached directly (no fallback attempt at all — see
        // Computed_leaf_hard_fails_in_every_mode above, the inner-Select-form sibling of this test). Here,
        // TranslateSelectMany's bare-nav bind (TryBindBareNavUnwind) succeeds on STRUCTURE alone (o => o.Items
        // is a valid owned-collection nav) before it can know the trailing projection is unsupported, so
        // UnwindSource is set unconditionally; only the SEPARATE trailing Select's
        // TryBindTransparentIdentifierProjection rejects the computed leaf (i.Price * 2 is not a bare
        // ti.Outer.<m>/ti.Inner.<m> access) and calls the ordinary MarkNotNativelyRepresentable() guard — the
        // SAME graceful-fallback mechanism Count_after_SelectMany_falls_back_gracefully_except_under_NativeOnly
        // demonstrates elsewhere. Empirically (verified against expected in-memory results, not just "no
        // exception") the driver-LINQ fallback rebuilds this shape correctly from the captured method chain, so
        // Native/DriverLinq succeed with the CORRECT computed values; only NativeOnly (which forbids the
        // fallback) throws.
        var seed = SeedOwners();
        var expected = seed.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2 })
            .OrderBy(r => r.X).ToList();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(seed, mode,
                nameof(Explicit_result_selector_form_computed_leaf_falls_back_gracefully_except_under_NativeOnly) + mode);

            var result = db.Entities.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2 })
                .AsEnumerable().OrderBy(r => r.X).ToList();
            Assert.Equal(expected, result);
        }

        using var nativeOnlyDb = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Explicit_result_selector_form_computed_leaf_falls_back_gracefully_except_under_NativeOnly) + "NativeOnly");

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Entities.SelectMany(o => o.Items, (o, i) => new { X = i.Price * 2 }).ToList());
    }

    private class RefOwner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
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
    /// Seeds three principals — Alice (2 children), Bob (0 children — proves inner-join semantics), Carol (1
    /// child) — mirroring <see cref="SeedOwners"/>'s owned-collection shape, but as a REFERENCE (cross-
    /// collection) relationship: <see cref="RefItem.OwnerId"/> is a real FK, not an embedded array.
    /// </summary>
    private static (RefOwner[] Owners, RefItem[] Items) SeedRefData()
    {
        var alice = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Alice" };
        var bob = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Bob" }; // no children
        var carol = new RefOwner { Id = ObjectId.GenerateNewId(), Name = "Carol" };

        var items = new[]
        {
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Widget", Name = "WidgetName", OwnerId = alice.Id },
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Gadget", Name = "GadgetName", OwnerId = alice.Id },
            new RefItem { Id = ObjectId.GenerateNewId(), Tag = "Thing", Name = "ThingName", OwnerId = carol.Id },
        };

        return ([alice, bob, carol], items);
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

        Assert.Equal(3, result.Count); // 2 from Alice + 1 from Carol; Bob (0 children) contributes 0
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
        // Sanity: outer/inner never conflated (an owner's own name never leaks into InnerName and vice versa).
        Assert.All(result, r => Assert.NotEqual(r.OuterName, r.InnerName));
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
    public void Reference_form_bare_entity_result_hard_fails_in_every_mode()
    {
        // A bare-nav owned SelectMany whose trailing selector projects the whole inner entity hard-declines
        // cleanly via TranslateSelect's dedicated whole-inner-entity guard (see
        // Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking above) — that guard reads only
        // UnwindSource != null && Projection.Count == 0 && the selector shape, all kind-agnostic, so the SAME
        // clean NotSupportedException fires for the REFERENCE form too, in every mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_bare_entity_result_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<NotSupportedException>(() => db.Owners.SelectMany(o => o.Refs, (o, r) => r).ToList());
        }
    }

    [Fact]
    public void Reference_form_bare_SelectMany_hard_fails_in_every_mode()
    {
        // The 1-arg overload (SelectMany(o => o.Refs)) normalizes to the identical whole-inner-entity tree as
        // the explicit (o, r) => r form above — same clean decline, in every mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_bare_SelectMany_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<NotSupportedException>(() => db.Owners.SelectMany(o => o.Refs).ToList());
        }
    }

    [Fact]
    public void Reference_form_filtered_inner_hard_fails_in_every_mode()
    {
        // A user-supplied filter on the inner collection (c.Refs.Where(userPred)) is an EXTRA predicate
        // conjunct beyond the FK correlation EF's nav-expansion itself injects — NativeCorrelationMatcher's
        // exactly-one-correlation guard rejects it (TryBindReferenceNavUnwind returns false), and — same as
        // every other native-SelectMany-binder rejection (see the class doc comment) — TranslateSelectMany
        // then falls through the remaining binders, fails all of them, and returns null with NO
        // MarkNotNativelyRepresentable() attempt at all, so EF's own translation-failure path is reached
        // directly: this hard-fails in every mode, not just NativeOnly.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_filtered_inner_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag != "Widget"), (o, r) => new { o.Name, r.Tag }).ToList());
        }
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
    public void Bare_SelectMany_hard_fails_in_every_mode()
    {
        // EF-347 slice 4 (refined, this pass): a bare, tracking `db.Entities.SelectMany(o => o.Items)`
        // normalizes (via EF's nav-expansion) to the SAME bare-nav + trivial TransparentIdentifier(Outer,Inner)
        // tree as the explicit whole-inner-entity form covered by the Whole_inner_entity_form_* tests above —
        // it is the identical shape, just user-authored as the 1-arg overload, and reaches the SAME
        // TranslateSelect guard. TranslateSelect throws its NotSupportedException at TRANSLATION time — before
        // EF Core's own runtime "can't track an owned entity without its owner" safeguard ever gets a chance to
        // run, and before MongoQueryMode is even consulted — so all three modes now throw the identical, clean
        // NotSupportedException, superseding the previous coincidental behavior (where Native/DriverLinq only
        // hard-failed because of EF Core's own tracking guard firing at materialization time).
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode, nameof(Bare_SelectMany_hard_fails_in_every_mode) + mode);

            Assert.Throws<NotSupportedException>(() => db.Entities.SelectMany(o => o.Items).ToList());
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
    public void Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking()
    {
        // EF-347 slice 4 (refined, this pass): a bare-nav owned SelectMany whose trailing selector projects the
        // WHOLE inner entity (SelectMany(o => o.Items, (o, i) => i) — equivalently `from o in q from i in
        // o.Items select i`) is neither the projected form Task 2 added (TryBindTransparentIdentifierProjection
        // rejects a bare MemberExpression, only anonymous/DTO constructions) nor the mandatory ti => ti.Inner
        // unwrap Task 1's shaper machinery expects to fold through — it IS exactly that shape structurally.
        //
        // A prior pass of this guard called MarkNotNativelyRepresentable() (a graceful fallback) for this
        // shape, on the theory the driver-LINQ path would pick it up under Native/DriverLinq. Empirically it did
        // NOT: the shaper built by TranslateSelectMany's BuildSelectManyWrappedShaper is a
        // StructuralTypeShaperExpression for the bare Item entity with no owning-entity bsonDoc context, and
        // MongoProjectionBindingRemovingExpressionVisitor cannot materialize it either — so the "graceful"
        // fallback ALSO crashed, under BOTH Native and DriverLinq, with an internal
        // KeyNotFoundException("The given key 'bsonDoc' was not present in the dictionary") — a confusing,
        // internal, provider-implementation-detail exception, not a clean decline. There being no working path
        // for this shape at all, TranslateSelect now hard-declines it directly (a plain NotSupportedException,
        // not the gate-mediated NativeTranslationNotSupportedException — see the comment at the throw site for
        // why) — thrown at TRANSLATION time, before MongoQueryMode is even consulted, so it is identical across
        // all three modes: Native, DriverLinq, and NativeOnly alike. The KEY regression this test locks in: NONE
        // of the three modes throws KeyNotFoundException any more.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            var seed = SeedOwners();

            using var explicitDb = CreateContext(seed, mode,
                nameof(Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking) + mode + "Explicit");
            var explicitEx = Assert.Throws<NotSupportedException>(() =>
                explicitDb.Entities.AsNoTracking().SelectMany(o => o.Items, (o, i) => i).ToList());
            Assert.IsNotType<KeyNotFoundException>(explicitEx);

            using var querySyntaxDb = CreateContext(seed, mode,
                nameof(Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking) + mode + "QuerySyntax");
            var querySyntaxEx = Assert.Throws<NotSupportedException>(() =>
                (from o in querySyntaxDb.Entities.AsNoTracking() from i in o.Items select i).ToList());
            Assert.IsNotType<KeyNotFoundException>(querySyntaxEx);
        }
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
        // returned the correct count=2 names=[Alice,Carol] (Bob's empty Items collection contributes no rows;
        // Alice's two items collapse to one distinct Name; Carol contributes one) — that DriverLinq run is the
        // baseline this test locks in.
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
        Assert.Equal(new[] { "Alice", "Carol" }, expected.Select(r => r.Name).ToArray()); // Bob (0 items) contributes no rows

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
    public void Whole_inner_entity_form_declines_cleanly_regardless_of_tracking()
    {
        // Companion to Whole_inner_entity_form_declines_cleanly_in_every_mode_AsNoTracking above, covering a
        // TRACKING query (no .AsNoTracking()) for the same shape. Before this pass, a tracking query hit EF
        // Core's OWN runtime "can't track an owned entity without its owner" safeguard first ("A tracking
        // query is attempting to project an owned entity without a corresponding owner in its result...",
        // InvalidOperationException) — reached at materialization time, downstream of this provider's
        // (previously graceful, Route-only) MarkNotNativelyRepresentable() call. Now that TranslateSelect
        // hard-declines this shape directly at TRANSLATION time — before EF Core's own tracking safeguard, and
        // before the query ever executes — tracking no longer matters: the SAME clean NotSupportedException
        // fires whether or not .AsNoTracking() is used, identically across Native and DriverLinq (NativeOnly is
        // covered by the AsNoTracking-flavored test above; the decline is unconditional on tracking state, so
        // testing it once more here for the tracking case is enough to confirm that).
        var seed = SeedOwners();

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var explicitDb = CreateContext(seed, mode,
                nameof(Whole_inner_entity_form_declines_cleanly_regardless_of_tracking) + mode + "Explicit");
            Assert.Throws<NotSupportedException>(() =>
                explicitDb.Entities.SelectMany(o => o.Items, (o, i) => i).ToList());

            using var querySyntaxDb = CreateContext(seed, mode,
                nameof(Whole_inner_entity_form_declines_cleanly_regardless_of_tracking) + mode + "QuerySyntax");
            Assert.Throws<NotSupportedException>(() =>
                (from o in querySyntaxDb.Entities from i in o.Items select i).ToList());
        }
    }
}
