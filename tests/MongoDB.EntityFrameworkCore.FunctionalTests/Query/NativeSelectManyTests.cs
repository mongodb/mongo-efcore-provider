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
/// Native owned- and reference-collection <c>SelectMany</c>: <c>$unwind</c> + <c>$project</c> with inner-join
/// flatten semantics (an owner with an empty/absent collection contributes no rows). Covers the inner-<c>Select</c>
/// form <c>q.SelectMany(o =&gt; o.Items.Select(i =&gt; new {o.X, i.Y}))</c> and the explicit-result-selector /
/// query-syntax form, which normalizes to a bare-nav + trailing-<c>Select</c> tree.
/// <para>
/// Whole-inner-element results (<c>(o, i) =&gt; i</c>, <c>select i</c>, 1-arg <c>SelectMany(o =&gt; o.Items)</c>) go
/// native via <c>UnwindSource.WholeElement</c>: the lowerer emits <c>$unwind(includeArrayIndex)</c> +
/// <c>$replaceRoot</c>, for owned elements carrying owner key and ordinal under sentinel fields that the re-rooted
/// shaper reads back. Tracking such an owned query throws EF Core's own "owned entity without its owner" error.
/// </para>
/// <para>
/// Still declined: the whole-outer form (<c>select o</c>) throws <see cref="NotSupportedException"/> at translation
/// time, so the same in every mode. Where <see cref="NativeSelectManyBinder.TryBind"/> rejects a shape, EF's own
/// translation failure is reached directly and throws in every <see cref="MongoQueryMode"/>. Unrepresentable
/// computed leaves over owned collections fall back gracefully via <c>MarkNotNativelyRepresentable()</c>. Most
/// operators composed after a native SelectMany hard-fail in every mode, since the fallback can't rebuild a shaper
/// over the by-index projection; cardinality-only <c>Count</c> needs no shaper and falls back gracefully.
/// </para>
/// <para>
/// Reference navigations (<see cref="NativeSelectManyBinder.TryBindReferenceNavUnwind"/>: <c>ForceUnwind</c>
/// <c>$lookup</c> + <c>$unwind</c>) have no driver-LINQ baseline (the bridge rejects cross-collection SelectMany), so
/// their fallback paths still throw under Native/DriverLinq.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSelectManyTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";

        // Outer numeric member, so a computed leaf like `o.Rank * 2m` exercises the scope-0 ("$Rank") path, not just
        // scope 1 ("$Items.Price").
        public decimal Rank { get; set; }

        // Nullable outer property: pins that a correlated relational filter (o.NullableRank < 5) keeps the query
        // dialect's type-bracketing rather than $expr's total order, where null sorts below every number and would
        // wrongly satisfy $lt.
        public int? NullableRank { get; set; }

        // Value-converted bare outer bool, for the bare-boolean-member arm of MongoExpressionTranslator.TranslateNode
        // (`o.Items.Where(i => o.ConvertedFlag)`). Only Owned_correlated_beyond_outer_bare_converted_bool_... maps it
        // to "True"/"False" strings, both truthy in $expr; emitting a raw MongoOuterFieldExpression there would
        // silently include every owner's items.
        public bool ConvertedFlag { get; set; }

        public List<Item> Items { get; set; } = [];
    }

    private class Item
    {
        // Shares the "Name" member with Owner, to prove outer ("$Name") and inner ("$Items.Name") never conflate.
        public string Name { get; set; } = "";
        public decimal Price { get; set; }

        // For inner-scope date-part (i.Occurred.Year) and conditional computed leaves needing the "$Items." prefix;
        // MongoFieldPrefixRewriter must decline, not throw, on such cross-scope leaves.
        public DateTime Occurred { get; set; }
        public bool Flag { get; set; }
    }

    private static Owner[] SeedOwners() =>
    [
        new()
        {
            // NullableRank null with items: an un-type-bracketed $expr:{$lt:[null,5]} would wrongly include these.
            Id = ObjectId.GenerateNewId(), Name = "Alice", Rank = 3m, NullableRank = null,
            Items =
            [
                new Item { Name = "Widget", Price = 9.99m, Occurred = new DateTime(2020, 3, 15), Flag = true },
                new Item { Name = "Gadget", Price = 19.99m, Occurred = new DateTime(2021, 7, 4), Flag = false },
            ],
        },
        new() { Id = ObjectId.GenerateNewId(), Name = "Bob", Rank = 5m, NullableRank = 2, Items = [] }, // empty owned collection
        new()
        {
            // Non-null but not < 5: excluded for a value reason, not a null one.
            Id = ObjectId.GenerateNewId(), Name = "Carol", Rank = 7m, NullableRank = 10,
            Items = [new Item { Name = "Thing", Price = 5m, Occurred = new DateTime(2022, 12, 25), Flag = true }],
        },
        // Owner whose items include one matching its own Name, so correlated-equality tests (i.Name == o.Name)
        // don't pass vacuously.
        new()
        {
            // NullableRank < 5: a genuine value match, unlike Alice's null exclusion.
            Id = ObjectId.GenerateNewId(), Name = "Match", Rank = 11m, NullableRank = 3,
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

        // Succeeding under NativeOnly proves it went native.
        Assert.Equal(expected, result);

        var message = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery);
        Assert.Contains("{ \"$unwind\" : \"$Items\" }", message);
        Assert.Contains("\"Name\" : \"$Name\"", message);
        Assert.Contains("\"Price\" : \"$Items.Price\"", message);
    }

    [Fact]
    public void Inner_select_form_produces_correct_results_under_explicit_DriverLinq_mode()
    {
        // The same supported shape must produce identical results when forced through driver-LINQ.
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
        // The MemberInitExpression (named DTO) arm of NativeSelectManyBinder.TryReadProjection and
        // BuildSelectManyResultShaper; the tests above use anonymous types.
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

        // ItemDto has no Equals override, so compare member-wise.
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
        // Outer/inner values never conflate, except the "Match" owner's deliberately same-named "Match" item.
        Assert.All(result.Where(r => !(r.OuterName == "Match" && r.InnerName == "Match")),
            r => Assert.NotEqual(r.OuterName, r.InnerName));
        Assert.Contains(result, r => r.OuterName == "Match" && r.InnerName == "Match");
    }

    [Fact]
    public void Explicit_result_selector_form_goes_native_with_correct_results_and_mql()
    {
        // The explicit-result-selector form normalizes to the same tree as the query-syntax form below.
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
        // Excludes the deliberately same-named "Match"/"Match" row (see SeedOwners).
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
        // Inner-only arithmetic leaf binds natively via TryBindTransparentIdentifierProjection's arithmetic branch;
        // owned inner-only projections have a driver oracle, so assert parity plus NativeOnly success.
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
        // Root-scope (scope 0, unprefixed "$Rank") computed leaf end-to-end; one row per item, each with its owner's
        // Rank * 2.
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
        // The $multiply must reference "$Rank", never "$Items.Rank" (Item has no Rank).
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
        // +, -, % and decimal / (not integer division) over an inner-only leaf, in one projection.
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
        // MongoFieldPrefixRewriter must prefix a MongoDatePartExpression's operand with the unwind path
        // ("$Items.Occurred") rather than throwing in every mode.
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
        // MongoFieldPrefixRewriter must prefix a MongoConditionalExpression's operands with the unwind path. The
        // ternary is nested under a top-level `+` because IsArithmeticComputedLeaf only admits arithmetic top nodes.
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
        // Correlated beyond the owner/element pair: rendered as $expr in the post-$unwind $match. No driver-LINQ
        // oracle exists for correlated owned SelectMany, so compare against an in-memory oracle. Only the "Match"
        // owner's "Match" item satisfies it.
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
    public void Correlated_relational_operator_over_a_nullable_outer_property_still_excludes_null_rows()
    {
        // MongoOuterFieldExpression (no query-dialect form) must stay confined to the element-scope two-scope
        // translators (Count(pred)/quantifier), not this SelectMany translator. Otherwise this comparison would use
        // $expr's total order ({$lt: [null, 5]} is true) instead of type-bracketing, and Alice's null-NullableRank
        // items would wrongly appear (4 rows instead of 2).
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Correlated_relational_operator_over_a_nullable_outer_property_still_excludes_null_rows));

        var result = db.Entities.AsNoTracking()
            .SelectMany(o => o.Items.Where(i => o.NullableRank < 5))
            .AsEnumerable().OrderBy(i => i.Name).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => o.NullableRank < 5))
            .OrderBy(i => i.Name).ToList();

        Assert.Equal(expected.Select(i => i.Name), result.Select(i => i.Name));
        Assert.Equal(2, result.Count); // both of Match's items; Alice (null) and Carol (10) excluded
        Assert.DoesNotContain(result, i => i.Name is "Widget" or "Gadget" or "Thing");
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
        // Bare whole-element results require AsNoTracking() (owned element without owner).
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
        // The correlated conjunct renders as $expr comparing "$Items.Name" (unwind-path-prefixed; owned data has no
        // $lookup alias) to root-relative "$Name".
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
    public void Owned_correlated_beyond_outer_against_a_plain_value_goes_native_with_correct_rows()
    {
        // Outer member compared to a captured-local value (not another field). The outer member is a
        // MongoOuterFieldExpression, which IsQueryDialectRenderable excludes, so this renders as
        // {$expr: {$eq: ["$Name", ownerName]}}; correct because the $match runs after $unwind with the outer field at
        // the root.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_against_a_plain_value_goes_native_with_correct_rows));

        var ownerName = "Match";
        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => o.Name == ownerName), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => o.Name == ownerName), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        // The predicate ignores i.Name, so both of "Match"'s items are included.
        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal("Match", r.Name));
    }

    [Fact]
    public void Owned_correlated_beyond_outer_against_a_plain_constant_goes_native_with_correct_rows()
    {
        // Constant rather than parameter right-hand side for the MongoOuterFieldExpression comparison.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Owned_correlated_beyond_outer_against_a_plain_constant_goes_native_with_correct_rows));

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => o.Name == "Match"), (o, i) => new { o.Name, i.Price })
            .AsEnumerable().OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        var expected = seed
            .SelectMany(o => o.Items.Where(i => o.Name == "Match"), (o, i) => new { o.Name, i.Price })
            .OrderBy(x => x.Name).ThenBy(x => x.Price).ToList();

        Assert.Equal(expected, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Owned_correlated_beyond_outer_bare_converted_bool_uses_property_serializer_not_raw_truthiness()
    {
        // Silent wrong-data guard: a value-converted outer bool referenced bare (o.Items.Where(i => o.ConvertedFlag))
        // hits TranslateNode's bare-boolean-member default arm. Stored "True"/"False" are both truthy, so emitting
        // MongoOuterFieldExpression there ({$expr: "$ConvertedFlag"}) would include every owner's items instead of
        // just Carol's. Raw BsonDocument seeding controls the stored shape exactly.
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Owned_correlated_beyond_outer_bare_converted_bool_uses_property_serializer_not_raw_truthiness))
            + Guid.NewGuid().ToString("N")[..8];

        var rawCollection = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        rawCollection.InsertMany(
        [
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "ConvertedFlag", "False" },
                {
                    "Items", new BsonArray
                    { new BsonDocument { { "Name", "Widget" }, { "Price", new BsonDecimal128(9.99m) } } }
                },
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "ConvertedFlag", "True" },
                {
                    "Items", new BsonArray
                    { new BsonDocument { { "Name", "Thing" }, { "Price", new BsonDecimal128(5m) } } }
                },
            },
        ]);

        var collection = database.MongoDatabase.GetCollection<Owner>(collectionName);
        using var db = SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb =>
            {
                mb.Entity<Owner>().OwnsMany(o => o.Items);
                // Explicit converter: HasConversion<string>() would store "0"/"1", not the "True"/"False" seeded.
                mb.Entity<Owner>().Property(o => o.ConvertedFlag)
                    .HasConversion(v => v ? "True" : "False", v => v == "True");
            },
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        var result = db.Entities
            .SelectMany(o => o.Items.Where(i => o.ConvertedFlag), (o, i) => new { o.Name, i.Price })
            .ToList();

        Assert.Single(result);
        Assert.Equal("Carol", result[0].Name);
    }

    [Fact]
    public void Filtered_owned_computed_operator_hard_fails_in_every_mode()
    {
        // An unsupported filter operator (ToUpper over a computed receiver) declines normally; no oracle, so
        // hard-fail in every mode.
        var seed = SeedOwners();
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateContext(seed, mode,
                nameof(Filtered_owned_computed_operator_hard_fails_in_every_mode) + mode);
            Assert.ThrowsAny<Exception>(() =>
                db.Entities.SelectMany(o => o.Items.Where(i => i.Name.Trim().ToUpper() == "WIDGET"), (o, i) => new { o.Name, i.Price }).ToList());
        }
    }

    [Fact]
    public void Filtered_owned_computed_arithmetic_leaf_goes_native()
    {
        // Filtered owned SelectMany with an inner-only arithmetic leaf: $match before the computed $project. Parity
        // plus NativeOnly success.
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
        // Computed leaf mixing outer (o.Rank) and inner (i.Price) scopes: the cross-scope fallback translates each
        // operand against its own scope. Distinct Ranks and Prices make mis-scoping ("$Items.Rank", "$Price") show
        // up as wrong values. Has a driver oracle, so all modes must agree.
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
        // The $multiply must reference "$Rank" at the root and "$Items.Price" under the unwind path.
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
        // Cross-scope subtree as an operand ((o.Rank * i.Price) + 1): translation must recurse; `1` covers the
        // scope-free operand arm.
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
        // The cross-scope binder must reproduce TranslateOperand's numeric-type guard: `o.Name + i.Name` translates
        // as two string fields, but "$add" over strings is a server error. Must fall back instead.
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
        // One-arg `SelectMany(o => o.Items).Select(i => i.Price * 2)`: a bare arithmetic body with no member name,
        // bound under the reserved `_v` alias.
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
        // Bare body that is also cross-scope; needs the two-arg form to reach the outer element.
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
        // Only arithmetic bare bodies use the `_v` tier. A bare member access needs its own document path as alias
        // for a late fallback (NativeProjectionBinder's tier 1), so it declines and falls back correctly.
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
        // An inner filter through a further owned reference (i.Sub.City) must resolve scope-relatively ("Sub.City")
        // so TryBuildOwnedInnerFilter's single "Items" prefix yields "Items.Sub.City", not a double-prefixed
        // "Items.Items.Sub.City" that silently matches nothing.
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

        // Paired with RefItem.Score for a numeric correlated predicate (r.Score >= o.Threshold) exercising $expr $gte.
        public int Threshold { get; set; }
        public List<RefItem> Refs { get; set; } = [];
    }

    private class RefItem
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";

        // Shares "Name" with RefOwner, to prove outer ("$Name") and inner ("$_lookup_Refs.Name") never conflate.
        public string Name { get; set; } = "";

        // See RefOwner.Threshold.
        public int Score { get; set; }

        // Nullable and mostly null, so `r.Note != null` discriminates: it must not be absorbed into the FK
        // correlation's null-guard and dropped (EF-355), which would return every child.
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
    /// Reference (cross-collection, real FK) counterpart of <see cref="SeedOwners"/>: Alice (2 children), Bob (0,
    /// proving inner-join semantics), Carol (1), Dave (2).
    ///
    /// Dave makes the correlated predicates discriminating: Refs[0] (Tag="Dave", Name="DaveItem1") is the only row
    /// with r.Tag == o.Name; Refs[1] (Tag="DaveItem2Tag", Name="Dave") the only one with r.Name == o.Name.
    /// For r.Score >= o.Threshold: Alice 10 (Widget 5 out, Gadget 20 in); Carol 8 (Thing 8 in, the >= boundary);
    /// Dave 100 (Refs[0] 50 out, Refs[1] 150 in).
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

    // ── Nested (2-level) cross-collection reference SelectMany fixture ──────────────────────────────────
    // NestOwner --(Mids, FK OwnerId)--> NestMid --(Leaves, FK MidId)--> NestLeaf, all ToCollection references.

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

        // Numeric field at nested scope k=2, for Nested_reference_single_scope_computed_leaf_goes_native.
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
    /// OwnerA (2 Mids with Leaves), OwnerB (no Mids), OwnerC (1 Mid, no Leaves): exactly 3 joined triples —
    /// (OwnerA,A1,Red), (OwnerA,A1,Blue), (OwnerA,A2,Green).
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
        // No NestGrandLeaf rows: the 3-level decline test fails at translation time.
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
        // No driver-LINQ oracle for cross-collection SelectMany: Native + NativeOnly against an in-memory expectation.
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
        // Bespoke seed: two owners both contribute rows, so a dropped or mis-composed outer Where shows up.
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

            // Owner2's row, which the unfiltered query would include, is excluded.
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

        // Cross-collection AutoInclude. This doesn't reach IsWholeElementRepresentable's reference-nav guard (it
        // declines via a doubly-nested TransparentIdentifier); the owned-sub-navigation test is the one that does.
        public EagerDetail? Detail { get; set; }
    }

    private class EagerDetail
    {
        public ObjectId Id { get; set; }
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Like <see cref="RefOwnerItemDbContext"/>, plus a collection for the eager-loaded <see cref="EagerChild.Detail"/>.
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

        // Owned, hence eager-loaded by convention. A single-hop IncludeExpression(ti.Inner, ownedNav), which
        // TryGetWholeEntityMemberAccess unwraps back to `ti.Inner`, so it does reach the representability guard.
        public OwnedSubNavEmbedded Embedded { get; set; } = new();
    }

    private class OwnedSubNavEmbedded
    {
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Like <see cref="RefOwnerItemDbContext"/>, but the child owns an embedded sub-navigation
    /// (<see cref="OwnedSubNavChild.Embedded"/>), so a bare-entity SelectMany projects a child that eager-loads it.
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

    // Core reference-form success test; verified to translate identically on EF8, EF9 and EF10, so this suite has no
    // EF-version guard.
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
        // Bob has no RefItems: the force-unwind $lookup+$unwind is an inner join (preserve: false), unlike Include's
        // left join, so he contributes no rows.
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
        // Outer/inner values never conflate, except Dave's deliberately same-named "Dave" child (see SeedRefData).
        Assert.All(result.Where(r => !(r.OuterName == "Dave" && r.InnerName == "Dave")),
            r => Assert.NotEqual(r.OuterName, r.InnerName));
        Assert.Contains(result, r => r.OuterName == "Dave" && r.InnerName == "Dave");
    }

    [Fact]
    public void Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode()
    {
        // Unlike owned forms, the reference form has no driver-LINQ baseline: the driver-LINQ bridge rejects any
        // cross-collection SelectMany ("Unsupported cross-DbSet query..."), so forcing DriverLinq still throws.
        using var db = CreateRefContext(MongoQueryMode.DriverLinq,
            nameof(Reference_form_has_no_driver_linq_fallback_and_still_throws_under_explicit_DriverLinq_mode), out _, out _);

        Assert.Throws<InvalidOperationException>(() =>
            db.Owners.SelectMany(o => o.Refs, (o, r) => new { o.Name, r.Tag }).ToList());
    }

    [Fact]
    public void Reference_form_bare_entity_result_goes_native_all_three_spellings()
    {
        // Whole-inner-entity result over a reference unwind goes native: IsWholeElementRepresentable rejects only
        // eager-loaded navigations, and RefItem.Owner is a plain inverse (materializes as null). All three
        // spellings normalize to the same tree.
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
        // RefItem.Name shares its member name with RefOwner.Name; r.Name must be the item's, not the owner's.
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

        // The "Widget" row is excluded.
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
        // EF-355: a `!= null` inner filter looks like the FK correlation's own null-guard and must not be absorbed
        // into it (which would return every child). Only 2 of 5 children have a Note.
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
        // r.Tag == o.Name, correlated beyond the FK: $expr field comparison in the post-$unwind $match. No driver-LINQ
        // oracle for reference-form SelectMany, so Native + NativeOnly only. Only Dave.Refs[0] matches.
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
        // r.Name == o.Name with RefItem.Name shadowing RefOwner.Name: must compare item to owner, not item to itself.
        // Only Dave.Refs[1] matches.
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
        // r.Score >= o.Threshold via $expr $gte. In: Alice/Gadget, Carol/Thing (8 >= 8), Dave/Refs[1]. Out:
        // Alice/Widget, Dave/Refs[0].
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
        // Stacked Wheres must AND: r.Tag == o.Name alone matches Dave.Refs[0], whose Name ("DaveItem1") fails
        // r.Name == o.Name, so the result is empty. A dropped Where would return Dave.Refs[0].
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
        // Correlated conjunct (matches Dave.Refs[0]) ANDed with an inner-only conjunct matching nothing (r.Score < 0):
        // no rows, proving the second conjunct narrows. (String-concat correlated predicates hard-fail translation,
        // so aren't used here.)
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
        // Outer Where parameter composing with the correlated inner $match: excluding Dave removes the only matching
        // owner, so the result is empty; the sanity check below shows it's non-empty without the cutoff.
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

        var withoutCutoff = owners
            .SelectMany(o => o.Refs.Where(r => r.Tag == o.Name), (o, r) => new { o.Name, r.Tag })
            .ToList();
        Assert.NotEmpty(withoutCutoff);
    }

    [Fact]
    public void Reference_form_correlated_emits_expr_match_after_unwind()
    {
        // The correlated conjunct renders as $expr comparing the scope-prefixed inner field to root-relative "$Name".
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
        // Unsupported filter operator (ToUpper over a computed receiver): the inner-scope translator rejects it, so hard-fail in every
        // mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            using var db = CreateRefContext(mode,
                nameof(Reference_form_computed_filter_operator_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.ThrowsAny<Exception>(() =>
                db.Owners.SelectMany(o => o.Refs.Where(r => r.Tag.Trim().ToUpper() == "WIDGET"), (o, r) => new { o.Name, r.Tag }).ToList());
        }
    }

    [Fact]
    public void Filtered_reference_collection_count_still_falls_back()
    {
        // Not a SelectMany: a projected filtered Count (c.Refs.Count(pred)). Both the native Count binder and the
        // fallback TryBindProjectedCollectionNavigationCount require a predicate-free Count, so this fails with
        // InvalidOperationException in every mode (cf. QueryModeGateIncludeTests.Projected_collection_Count_with_
        // predicate_still_falls_back). Pins that the SelectMany filter mechanism doesn't leak into the Count binder.
        using var db = CreateRefContext(MongoQueryMode.Native,
            nameof(Filtered_reference_collection_count_still_falls_back), out _, out _);

        Assert.Throws<InvalidOperationException>(() =>
            db.Owners
                .Select(o => new { o.Name, N = o.Refs.Count(r => r.Tag != "Widget") })
                .AsEnumerable().OrderBy(x => x.Name).ToList());
    }

    // `r.Tag.ToUpper()` is a MethodCallExpression, so IsArithmeticComputedLeaf declines it. (String `+` now goes
    // native via $concat; see Reference_form_string_concat_computed_leaf_goes_native.)
    [Fact]
    public void Reference_form_computed_leaf_hard_fails_in_every_mode()
    {
        // TryBindReferenceNavUnwind succeeds on structure, then the trailing projection rejects the computed leaf and
        // calls MarkNotNativelyRepresentable(). Unlike the owned form, the fallback itself throws "Unsupported
        // cross-DbSet query" under Native and DriverLinq (no driver-LINQ baseline); NativeOnly throws its own
        // exception first.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateRefContext(mode, nameof(Reference_form_computed_leaf_hard_fails_in_every_mode) + mode, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                db.Owners.SelectMany(o => o.Refs, (o, r) => new { X = r.Tag.ToUpper() }).ToList());
        }

        using var nativeOnlyDb = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_computed_leaf_hard_fails_in_every_mode) + "NativeOnly", out _, out _);

        Assert.ThrowsAny<Exception>(() =>
            nativeOnlyDb.Owners.SelectMany(o => o.Refs, (o, r) => new { X = r.Tag.ToUpper() }).ToList());
    }

    [Fact]
    public void Reference_form_string_concat_computed_leaf_goes_native()
    {
        // String concatenation (r.Tag + "!") is a single-scope Add leaf and goes native via $concat.
        using var db = CreateRefContext(MongoQueryMode.NativeOnly,
            nameof(Reference_form_string_concat_computed_leaf_goes_native), out var owners, out var items);

        var actual = db.Owners
            .SelectMany(o => o.Refs, (o, r) => new { X = r.Tag + "!" })
            .AsEnumerable()
            .OrderBy(x => x.X).ToList();

        var expected = owners
            .SelectMany(o => items.Where(r => r.OwnerId == o.Id), (o, r) => new { X = r.Tag + "!" })
            .OrderBy(x => x.X).ToList();

        Assert.Equal(expected.Select(x => x.X), actual.Select(x => x.X));
    }

    [Fact]
    public void Reference_form_computed_arithmetic_leaf_goes_native()
    {
        // No driver oracle: NativeOnly + in-memory expectation, plus the computed $project MQL ($_lookup_Refs.Score).
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
        // Arithmetic leaf at the deepest scope (k=2) of a nested reference SelectMany: prefix _lookup_Leaves.Height.
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

    // The cross-scope binder is scope-kind-agnostic: an inner operand gets the source's InnerScopePath, which for a
    // reference source is the $lookup alias ("_lookup_Refs").
    [Fact]
    public void Cross_scope_computed_leaf_goes_native_for_the_reference_form_too_EF422()
    {
        // Five distinct non-zero products, so a mis-scoped operand ("$Score" at root, "$_lookup_Refs.Threshold")
        // surfaces as zeros/nulls. No DriverLinq: reference SelectMany has no driver-LINQ oracle.
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

    // Integral division renders as $trunc of $divide. Widget's 5/2 discriminates: raw $divide gives 2.5, which
    // can't deserialize into the int member. No DriverLinq (no driver-LINQ oracle).
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
        // Unlike the owned form, a Where after reference SelectMany hard-fails in every mode: the post-terminal guard
        // calls MarkNotNativelyRepresentable(), but the fallback chain still begins with a cross-collection SelectMany,
        // which the driver-LINQ bridge rejects under Native and DriverLinq. NativeOnly throws its
        // own exception first. The same applies to the OrderBy/Skip/Take/Count tests below.
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
        // See Reference_form_followed_by_Where_hard_fails_in_every_mode.
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
        // See Reference_form_followed_by_Where_hard_fails_in_every_mode.
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
        // See Reference_form_followed_by_Where_hard_fails_in_every_mode.
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
        // Unlike the owned form, even shaper-free Count hard-fails: the fallback still has to translate the
        // cross-collection SelectMany. See Reference_form_followed_by_Where_hard_fails_in_every_mode.
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
        // GroupBy hard-fails for both owned and reference forms, here via "Calling
        // 'ShapedQueryExpression.VisitChildren' is not allowed" (TranslateGroupBy's fallback can't rebuild the
        // by-index SelectMany shaper) rather than the cross-DbSet message. ThrowsAny across all modes.
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
        // The second SelectMany's collection selector (`new[] { x.Tag }`) matches no NativeSelectManyBinder entry
        // point, so TranslateSelectMany returns null without MarkNotNativelyRepresentable(): EF's translation
        // failure fires at translation time, identically in all modes.
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
        // Silent wrong-data guard: TryBindDistinctFromProjection must decline when UnwindSource is set. Otherwise
        // MongoSelectLowerer.Lower (which checks UnwindSource before Grouping) never emits the $group, and the flatten
        // $project reads a nonexistent "_id.Name", returning nulls even under NativeOnly. Declining puts this in the
        // "operator after reference SelectMany" family: hard-fail in every mode.
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
        // A parameterized Where before the reference SelectMany lowers into the pre-$lookup $match and substitutes
        // through PlaceholderTable.
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

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Explicit_result_selector_form_followed_by_Where_falls_back_gracefully_except_under_NativeOnly()
    {
        // Unlike the inner-Select form (whose captured chain has an EF-synthesized `.Select(ti => ti.Inner)` over an
        // internal TransparentIdentifier the driver can't map), this form's captured chain is one LINQ v3 translates,
        // so after the post-terminal guard marks it non-native the fallback returns correct results under
        // Native/DriverLinq; only NativeOnly throws.
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
        // Tracking bare `SelectMany(o => o.Items)`: routes native, but EF Core's own "owned entity without its owner"
        // guard fires at materializer injection, before the native/driver-LINQ split, so all modes throw the same
        // InvalidOperationException. The tracking contract is EF Core's, not the provider's.
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
        // A parameterized Where before the native SelectMany lowers into the pre-$unwind $match and substitutes
        // through PlaceholderTable.
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

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Absent_owned_collection_field_contributes_no_rows()
    {
        // Absent (no "Items" field) differs from empty; EF always writes at least `[]`, so seed raw BsonDocuments.
        // The plain `$unwind: "$Items"` must drop it like an empty array.
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
        // The native SelectMany's Select is terminal (HasTerminalOperator includes UnwindSource != null), so the
        // post-terminal guard marks this OrderBy non-native; there is no graceful fallback, so every mode throws.
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
        // Unlike Where/OrderBy/Skip/Take/GroupBy: TryBindAggregate's terminal guard marks it non-native, and Count
        // needs no shaper, so the driver-LINQ fallback returns the correct count under Native/DriverLinq. NativeOnly
        // throws NativeTranslationNotSupportedException, hence ThrowsAny.
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
        // TranslateGroupBy reads HasTerminalOperator directly (before setting IsGroupBy) and marks the query
        // non-native, but its fallback can't rebuild a shaper over the by-index SelectMany projection
        // ("Calling 'ShapedQueryExpression.VisitChildren' is not allowed"). Exception type differs under NativeOnly,
        // hence ThrowsAny.
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
        // `new[] { r.Name }` isn't a nested Queryable.Select, so NativeSelectManyBinder.TryBind rejects it: hard fail.
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
        // A Where after the native SelectMany hits TranslateSelect's post-terminal guard (as for GroupBy/Distinct; see
        // NativeGroupByTests/NativeDistinctTests ..._never_returns_silent_null_data). No fallback shaper can re-read
        // the by-index SelectMany projection (the ProjectionBindingExpression pass-through is Route == Projection
        // only), so it throws in every mode rather than returning stale data.
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
        // Bare whole-inner-element owned SelectMany, three spellings: $unwind(includeArrayIndex) +
        // $replaceRoot($mergeObjects) with owner key and ordinal in sentinel fields, shaped at the owned Item type.
        // Requires AsNoTracking. The trailing Select runs client-side, since an in-query Select would be post-terminal
        // composition and throw under NativeOnly.
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
        // Bob's empty Items contributes zero rows (inner-flatten).
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
        // Item shares "Name" with Owner: the re-rooted shaper must return item names, not owner names.
        var seed = SeedOwners();
        using var db = CreateContext(seed, MongoQueryMode.NativeOnly,
            nameof(Bare_owned_whole_inner_element_reads_root_relative_not_owner_scoped));

        var names = db.Entities.AsNoTracking().SelectMany(o => o.Items)
            .AsEnumerable().Select(i => i.Name).OrderBy(n => n).ToList();

        Assert.Equal(["Gadget", "Match", "NoMatch", "Thing", "Widget"], names);
        // "Match" is excluded from this list: it's also a real item name (see SeedOwners).
        Assert.DoesNotContain(names, n => n is "Alice" or "Bob" or "Carol");
    }

    [Fact]
    public void Whole_outer_entity_form_still_declines_cleanly_in_every_mode()
    {
        // Whole-outer form (`select o`): IsWholeInnerEntitySelector requires Member.Name == "Inner", so this declines
        // with NotSupportedException at translation time in every mode, regardless of tracking. $replaceRoot re-rooting
        // only targets the inner element, so there's no native form for this.
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
        // The explicit spelling `SelectMany(o => o.Items, (o, i) => o)` also gets the TransparentIdentifier wrap and
        // trailing `ti => ti.Outer` Select (verified on EF8/EF9/EF10), so it reaches the same whole-outer guard and
        // declines cleanly (EF-354). Reference counterpart:
        // Reference_form_whole_outer_result_still_declines_cleanly_in_every_mode.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq, MongoQueryMode.NativeOnly })
        {
            var seed = SeedOwners();

            using var db = CreateContext(seed, mode,
                nameof(Whole_outer_entity_explicit_method_call_form_declines_cleanly_in_every_mode) + mode);

            var ex = Assert.Throws<NotSupportedException>(() =>
                db.Entities.AsNoTracking().SelectMany(o => o.Items, (o, i) => o).ToList());
            Assert.Contains("Projecting a whole entity other than an owned or reference collection element", ex.Message);

            // Tracking too: the guard fires at translation time.
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

        // A real element named "__ord" (MongoReplaceRootStage.OrdinalField). Sentinels are nested under
        // MongoReplaceRootStage.ShadowField, so it no longer collides with the $mergeObjects output.
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

        // The only name that can still collide: the sentinel wrapper itself, which $mergeObjects would overwrite;
        // IsWholeElementRepresentable guards it.
        [MongoDB.Bson.Serialization.Attributes.BsonElement(MongoReplaceRootStage.ShadowField)]
        public int RealShadow { get; set; }
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_sentinel_collision_now_goes_native()
    {
        // A property stored as "__ord" goes native with its real value preserved, since sentinels live under the
        // single ShadowField wrapper rather than in the element's top-level namespace.
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

        // Ordered client-side: operators after a native SelectMany hard-fail.
        var elements = db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList()
            .OrderBy(i => i.Name).ToList();

        Assert.Equal(2, elements.Count);
        Assert.Equal(["Sprocket", "Widget"], elements.Select(e => e.Name));
        // The real stored "__ord" values, not synthesized array ordinals.
        Assert.Equal([43, 42], elements.Select(e => e.RealOrd));
    }

    [Fact]
    public void Bare_owned_whole_inner_element_colliding_with_the_shadow_wrapper_still_declines_cleanly()
    {
        // A real element named MongoReplaceRootStage.ShadowField would be overwritten by $mergeObjects, so
        // IsWholeElementRepresentable declines at translation time with NotSupportedException.
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
        // Re-rooted owned element with a nested owned reference (Detail). MongoQueryExpression.ReRootProjectionAt,
        // called when TranslateSelect sets UnwindSource.WholeElement, re-points the root ProjectionMember at the
        // element's entity type, so Detail's IncludeExpression binds against the element (a top-level field after
        // $replaceRoot) rather than the owner.
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

        // Ordered client-side: operators after a native SelectMany hard-fail.
        var items = db.Entities.AsNoTracking().SelectMany(o => o.Items).ToList()
            .OrderBy(i => i.Name).ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(["Sprocket", "Widget"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.NotNull(i.Detail));
        // Nested member leaves read the real values via "Detail.Note"/"Detail.Weight" on the re-rooted element.
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

    // [ComplexType] is the only way to put a complex property on an owned collection element:
    // OwnedNavigationBuilder has no ComplexProperty(...) overload.
    [System.ComponentModel.DataAnnotations.Schema.ComplexType]
    private class ComplexSentinelSub
    {
        public string Note { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_with_complex_property_sentinel_collision_declines_cleanly()
    {
        // The sentinel-collision guard must scan complex properties too: GetProperties() doesn't see them, yet a
        // complex property occupies one top-level field that $mergeObjects would silently overwrite if it's named
        // MongoReplaceRootStage.ShadowField. No builder API renames a complex property's element, so set the
        // Mongo:ElementName annotation directly. Declines at translation time with NotSupportedException.
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

                // Reach the [ComplexType]-discovered complex property via the model and rename its element to
                // collide with the sentinel wrapper.
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
        // The owner Id has an ObjectId<->string converter, which EF propagates to the element's shadow owner-FK, but
        // the __ownerKey sentinel is copied from the raw "$_id", bypassing it. IsWholeElementRepresentable declines via
        // NativeGroupByBinder.HasDefaultKeySerialization on the owned-key properties, with NotSupportedException at
        // translation time.
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
        // OwnsMany with an explicit user-defined key (rather than the default shadow owner-FK + ordinal) materializes
        // correctly through the re-rooted sentinel-read path.
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

    // Owned by two navigations on the same owner with no per-nav type name, forcing a shared-type owned entity type
    // (FindEntityType(typeof(SharedElement)) is null); cf. SharedClrTypeProjectionTests.MultiSameTypeOwner.
    private class SharedElement
    {
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Bare_owned_whole_inner_element_over_shared_clr_type_goes_native()
    {
        // VisitShapedQuery must check WholeElement before the FindEntityType-null early return: for a shared-type
        // owned element, the shaper is rooted at wholeElementUnwind.InnerEntityType (the navigation's target), which
        // works when FindEntityType can't resolve the CLR type. Both collections must materialize independently.
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

        // Confirms the fixture really is shared-type.
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
        // Like the Where case: the captured chain has no TransparentIdentifier unwrap, so the driver-LINQ fallback
        // returns correct ordered results under Native/DriverLinq; only NativeOnly throws.
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
        // Same graceful-fallback family. Without a $sort, results rely on natural order, which matches insertion
        // order for this small unindexed collection.
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
        // Same as Skip above (natural-order assumption).
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
        // Count needs no shaper, so it falls back gracefully for both SelectMany forms.
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
        // Owned sibling of Reference_form_followed_by_Distinct_hard_fails_in_every_mode. Without
        // TryBindDistinctFromProjection declining on UnwindSource, Native and NativeOnly silently returned
        // null names. Now it falls back and the driver-LINQ chain returns the correct distinct rows under
        // Native/DriverLinq (asserted, not just "no exception"); only NativeOnly throws.
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
        // Unlike Where/OrderBy/Skip/Take/Count, GroupBy hard-fails in every mode: TranslateGroupBy's fallback can't
        // rebuild a shaper over the by-index SelectMany projection, whichever form produced it.
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
        // A second SelectMany over `new[] { r.Name }` hard-fails in every mode: NativeSelectManyBinder.TryBind
        // rejects it, and LINQ v3 can't translate the array-literal shape under DriverLinq either.
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
        // A parameterized Where before the explicit-form SelectMany lowers into the pre-$unwind $match and substitutes
        // through PlaceholderTable.
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

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Whole_inner_entity_form_tracking_query_throws_InvalidOperationException_in_every_mode()
    {
        // Tracking explicit-result-selector and query-syntax spellings (1-arg is covered by
        // Bare_SelectMany_tracking_query_throws_InvalidOperationException_in_every_mode): EF Core's own owned-tracking
        // guard throws the same InvalidOperationException in every mode.
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
        // Unlike owned elements, reference entities have their own key and are tracked normally.
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

        // Clear the tracker and re-query so the check reads from the database.
        db.ChangeTracker.Clear();
        var tags = db.Refs.AsEnumerable().Select(r => r.Tag).ToList();
        Assert.Contains("MutatedTag", tags);
    }

    [Fact]
    public void Reference_form_bare_entity_with_cross_collection_autoinclude_declines_cleanly_in_every_mode()
    {
        // A reference element eager-loading a cross-collection navigation (EagerChild.Detail) declines in every
        // mode, but not via IsWholeElementRepresentable: nav-expansion adds a join, so the selector becomes a
        // two-hop `Include(ti.Outer.Inner, ...)` that TryGetWholeEntityMemberAccess doesn't recognize. It falls to
        // MarkNotNativelyRepresentable(), whose fallback fails with EF Core's plain TranslationFailed
        // InvalidOperationException under Native/DriverLinq; NativeOnly throws its own exception. The
        // owned-sub-navigation test below is the one that reaches the representability guard.
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
        // An owned sub-navigation on the reference element is a single-hop IncludeExpression(ti.Inner, ownedNav),
        // which TryGetWholeEntityMemberAccess unwraps, so IsWholeElementRepresentable's eager-nav check declines it
        // with NotSupportedException at translation time, identically in every mode.
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
        // A Where after the native reference SelectMany: the MarkNotNativelyRepresentable() fallback has no
        // driver-LINQ baseline, so Native/DriverLinq throw InvalidOperationException; NativeOnly throws its own.
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
        // Nested owned collection under the re-rooted element: after $replaceRoot it's a direct top-level array, so
        // no extra stage is needed. Asserts array contents per element, which catches reading the wrong document.
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

        // Ordered client-side: operators after a native SelectMany hard-fail.
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
        // A cross-collection reference off an owned element would need a $lookup this lowering never emits, so
        // IsWholeElementRepresentable declines at translation time with NotSupportedException. Distinguishes the
        // narrowed guard from its removal.
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

    // A whole-element owned SelectMany has Route == WholeEntity, but as a set-op operand the lowerer emits no
    // $unwind for source2 and runs source1's $unwind (and inner-element filter) after the $unionWith, over both
    // sides. Without IsPlainWholeEntitySelect's UnwindSource check, left-filtered Concat returned 4 rows (the
    // filter leaked onto the right side) and right-filtered Concat 10 (the right filter was dropped); the correct
    // answers are 7. Driver-LINQ can't run these shapes either, so the fix turns silent wrong rows into a throw.
    [Fact]
    public void Whole_owned_element_SelectMany_as_set_op_operand_does_not_go_native()
    {
        var seed = SeedOwners();

        // Price > 5m keeps Widget and Gadget; the unfiltered side is all five items. 2 + 5 = 7 either way round.
        Assert.Equal(7, seed.SelectMany(o => o.Items.Where(i => i.Price > 5m)).Concat(seed.SelectMany(o => o.Items)).Count());

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = CreateContext(
                seed, mode, nameof(Whole_owned_element_SelectMany_as_set_op_operand_does_not_go_native) + mode);
            var owners = db.Entities.AsNoTracking();

            var leftFiltered = Record.Exception(() => owners.SelectMany(o => o.Items.Where(i => i.Price > 5m))
                .Concat(owners.SelectMany(o => o.Items)).ToList());
            var rightFiltered = Record.Exception(() => owners.SelectMany(o => o.Items)
                .Concat(owners.SelectMany(o => o.Items.Where(i => i.Price > 5m))).ToList());

            if (mode == MongoQueryMode.NativeOnly)
            {
                Assert.IsType<NativeTranslationNotSupportedException>(leftFiltered);
                Assert.IsType<NativeTranslationNotSupportedException>(rightFiltered);
            }
            else
            {
                // The Native fallback reaches driver-LINQ, which rejects the shape; it must not return rows.
                Assert.NotNull(leftFiltered);
                Assert.NotNull(rightFiltered);
            }
        }
    }
}
