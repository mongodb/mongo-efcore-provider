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
/// owned collection contributes no rows). Every other SelectMany shape (explicit result-selector, bare
/// SelectMany, a computed leaf) is out of scope for this slice and keeps hard-failing translation exactly as
/// before this change (no regression) — <see cref="NativeSelectManyBinder.TryBind"/> returning
/// <see langword="false"/> means EF's own translation-failure path is reached directly, with no
/// <c>MarkNotNativelyRepresentable</c>-style graceful degradation attempt at all, so these throw in every
/// <see cref="MongoQueryMode"/>, not just <see cref="MongoQueryMode.NativeOnly"/>. Composing a FURTHER
/// operator after an already-native SelectMany is a separate, narrower story (see the
/// <c>*_after_SelectMany_hard_fails_in_every_mode</c> / <c>Count_after_SelectMany_falls_back_gracefully...</c>
/// tests below): most such operators still hard-fail in every mode too (their own guards reach
/// <see cref="Expressions.MongoSelectDefinition.HasTerminalOperator"/>, but the driver-LINQ fallback the guard
/// would normally allow cannot rebuild a shaper that re-reads the SelectMany's already-by-index-resolved
/// projection) — except a cardinality-only operator like <c>Count</c>, which needs no shaper at all and so
/// falls back gracefully (and correctly) under <see cref="MongoQueryMode.Native"/>/<see cref="MongoQueryMode.DriverLinq"/>,
/// throwing only under <see cref="MongoQueryMode.NativeOnly"/> like any other graceful-fallback shape.
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
    public void Explicit_result_selector_form_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode,
                nameof(Explicit_result_selector_form_hard_fails_in_every_mode) + mode);

            Assert.Throws<InvalidOperationException>(() =>
                db.Entities.SelectMany(o => o.Items, (o, i) => new { o.Name, i.Price }).ToList());
        }
    }

    [Fact]
    public void Bare_SelectMany_hard_fails_in_every_mode()
    {
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(SeedOwners(), mode, nameof(Bare_SelectMany_hard_fails_in_every_mode) + mode);

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
}
