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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-396 coverage: <c>Not</c> over a subtree with no query-dialect form (e.g. a field-to-field
/// comparison) used to hard-decline in <see cref="MongoQueryLanguageRenderer.RenderUnary"/>
/// (<c>NativeTranslationNotSupportedException</c>), forcing a fallback to driver-LINQ. It now renders via
/// <c>{ $expr: { $not: [...] } }</c> whenever <see cref="MongoAggregationExpressionRenderer.CanRender"/>
/// admits the operand — see <see cref="MongoQueryLanguageRenderer.RenderUnary"/>'s new fallback branch.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeNegationTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Entity
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public int B { get; set; }
    }

    // Row1: A=1, B=1 (A == B)   Row2: A=1, B=2 (A != B)   Row3: A=3, B=2 (A != B)
    private (IMongoCollection<Entity> collection, List<string> logs) Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "A", 1 }, { "B", 1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "A", 1 }, { "B", 2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "A", 3 }, { "B", 2 } },
        ]);
        return (database.MongoDatabase.GetCollection<Entity>(collectionName), []);
    }

    private static SingleEntityDbContext<Entity> CreateContext(
        IMongoCollection<Entity> collection, List<string> logs, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.LogTo(logs.Add)
                    .EnableSensitiveDataLogging()
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static string Mql(List<string> logs)
        => Assert.Single(logs, l => l.Contains("Executed MQL query"));

    // Not over a field-to-field comparison has no query-dialect form at all (RenderComparison's
    // IsQueryNativeComparison requires a bare-field/constant-or-parameter shape) — before this task,
    // RenderUnary's final decline branch threw for this operand shape. It now falls to $expr via
    // MongoAggregationExpressionRenderer, whose own Not arm renders it as { $not: [ { $eq: [...] } ] }.
    [Fact]
    public void NativeOnly_not_over_field_to_field_comparison_succeeds_with_expected_mql()
    {
        var (collection, logs) = Seed(nameof(NativeOnly_not_over_field_to_field_comparison_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // !(A == B): excludes Row1 (A==B==1), includes Row2 (1,2) and Row3 (3,2).
        var result = db.Entities.AsNoTracking().Where(x => !(x.A == x.B)).ToList();

        Assert.Equal(2, result.Count);

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("\"$not\"", mql);
        Assert.Contains("\"$eq\" : [\"$A\", \"$B\"]", mql);
    }

    [Fact]
    public void Not_over_field_to_field_comparison_matches_driver_linq_results()
    {
        var (collection, logs) = Seed(nameof(Not_over_field_to_field_comparison_matches_driver_linq_results));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeIds = native.Entities.AsNoTracking().Where(x => !(x.A == x.B))
            .Select(x => x.Id).OrderBy(id => id).ToList();
        var driverIds = driver.Entities.AsNoTracking().Where(x => !(x.A == x.B))
            .Select(x => x.Id).OrderBy(id => id).ToList();

        Assert.Equal(driverIds, nativeIds);
    }

    // Same operand shape, but composed inside a larger conjunction, to confirm the mixed-dialect
    // combination (an indexable clause alongside the $expr-wrapped Not) still renders and executes.
    [Fact]
    public void NativeOnly_not_over_field_to_field_comparison_composed_with_and_succeeds()
    {
        var (collection, logs) = Seed(nameof(NativeOnly_not_over_field_to_field_comparison_composed_with_and_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // A > 0 (true for all three rows) && !(A == B) (excludes Row1) -> Row2, Row3.
        var result = db.Entities.AsNoTracking().Where(x => x.A > 0 && !(x.A == x.B)).ToList();

        Assert.Equal(2, result.Count);
    }

    // ── Whole-branch-review fix (EF-396): Not over a conjunction/disjunction of BARE fields ──────────
    //
    // The reviewer reproduced a silent-wrong-data regression: RenderUnary's new fallback branch (above)
    // was the first Where-predicate caller of MongoAggregationExpressionRenderer.CanRender/Render.
    // CanRender's MongoBinaryExpression{AndAlso/OrElse} arm recursed into CanRender(Left)/CanRender(Right),
    // which for a bare MongoFieldExpression operand unconditionally answered `true` — with NO check that
    // the field uses default BSON serialization. $and/$or evaluate a bare operand by TRUTHINESS, not CLR
    // boolean value, so `!(x.Flag && x.Other)` where `Flag` is value-converted (e.g. HasConversion<string>()
    // storing "Y"/"N", both non-empty/truthy strings) rendered successfully via
    // `{ $expr: { $not: [ { $and: ["$Flag", "$Other"] } ] } }` and silently answered the WRONG boolean for
    // any row where the converted Flag string doesn't happen to be falsy.
    //
    // Mirrors NativeComputedSortTests.Computed_sort_key_using_Not_over_a_value_converted_bool_declines_
    // instead_of_answering_wrong's fixture/rigor: a custom converter maps BOTH true and false to non-empty
    // ("Y"/"N") strings, so a raw-field $and is wrong for a false-Flag row, not just some of them.

    public class LogicalFlagItem
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public bool Flag { get; set; }
        public bool Other { get; set; }
    }

    private static readonly Action<ModelBuilder> LogicalFlagModel =
        mb => mb.Entity<LogicalFlagItem>().Property(x => x.Flag)
            .HasConversion(v => v ? "Y" : "N", v => v == "Y");

    private (IMongoCollection<LogicalFlagItem> collection, List<string> logs) SeedLogicalFlag(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            // p1: Flag=true (stored "Y", truthy), Other=true  -> Flag&&Other CLR-true  -> !(...) = false
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p1" }, { "Flag", "Y" }, { "Other", true } },
            // p2: Flag=false (stored "N", ALSO truthy), Other=true -> Flag&&Other CLR-false -> !(...) = true
            // — this is the row the pre-fix code silently dropped: MQL's $and sees "N" as truthy, so it
            // computed Flag&&Other = true and excluded p2, when the CLR-correct answer is true (include).
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p2" }, { "Flag", "N" }, { "Other", true } },
            // p3: Flag=false (stored "N"), Other=false -> Flag&&Other CLR-false -> !(...) = true
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p3" }, { "Flag", "N" }, { "Other", false } },
        ]);
        return (database.MongoDatabase.GetCollection<LogicalFlagItem>(collectionName), []);
    }

    private static SingleEntityDbContext<LogicalFlagItem> CreateLogicalFlagContext(
        IMongoCollection<LogicalFlagItem> collection, List<string> logs, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: LogicalFlagModel,
            optionsBuilderAction: b =>
            {
                b.LogTo(logs.Add)
                    .EnableSensitiveDataLogging()
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Not_over_and_of_a_value_converted_bare_bool_field_declines_instead_of_answering_wrong()
    {
        var (collection, logs) = SeedLogicalFlag(
            nameof(Not_over_and_of_a_value_converted_bare_bool_field_declines_instead_of_answering_wrong));

        // NativeOnly: a clean decline (NativeTranslationNotSupportedException), NEVER silently-wrong data —
        // this is the load-bearing assertion the whole-branch review flagged. Before the fix, this line
        // succeeded and silently returned only ["p3"], dropping "p2" (see the seed comment above).
        using (var nativeOnly = CreateLogicalFlagContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking().Where(x => !(x.Flag && x.Other)).ToList());
        }

        // Native falls back and must agree with DriverLinq, and both must equal the CLR-correct answer
        // (["p2", "p3"]) — asserted as real rows, so a decline that silently returned nothing, everything,
        // or the wrong subset fails here rather than passing vacuously.
        using (var native = CreateLogicalFlagContext(collection, [], MongoQueryMode.Native))
        using (var driver = CreateLogicalFlagContext(collection, [], MongoQueryMode.DriverLinq))
        {
            var nativeLabels = native.Entities.AsNoTracking().Where(x => !(x.Flag && x.Other))
                .ToList().Select(x => x.Label).OrderBy(l => l).ToList();
            var driverLabels = driver.Entities.AsNoTracking().Where(x => !(x.Flag && x.Other))
                .ToList().Select(x => x.Label).OrderBy(l => l).ToList();

            Assert.Equal(driverLabels, nativeLabels);
            Assert.Equal(["p2", "p3"], nativeLabels);
        }
    }

    // Same hazard, OrElse form: !(x.Flag || x.Other). Truth table over the same seed:
    // p1 Flag=true,Other=true  -> Flag||Other CLR-true  -> !(...) = false
    // p2 Flag=false,Other=true -> Flag||Other CLR-true  -> !(...) = false
    // p3 Flag=false,Other=false -> Flag||Other CLR-false -> !(...) = true
    // A raw-field $or over "N" (truthy) would compute Flag||Other = true for EVERY row (since "N" is
    // always truthy regardless of the CLR value), so !(...) would be false for every row — silently
    // returning an EMPTY result instead of the correct ["p3"].
    [Fact]
    public void Not_over_or_of_a_value_converted_bare_bool_field_declines_instead_of_answering_wrong()
    {
        var (collection, logs) = SeedLogicalFlag(
            nameof(Not_over_or_of_a_value_converted_bare_bool_field_declines_instead_of_answering_wrong));

        using (var nativeOnly = CreateLogicalFlagContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking().Where(x => !(x.Flag || x.Other)).ToList());
        }

        using (var native = CreateLogicalFlagContext(collection, [], MongoQueryMode.Native))
        using (var driver = CreateLogicalFlagContext(collection, [], MongoQueryMode.DriverLinq))
        {
            var nativeLabels = native.Entities.AsNoTracking().Where(x => !(x.Flag || x.Other))
                .ToList().Select(x => x.Label).OrderBy(l => l).ToList();
            var driverLabels = driver.Entities.AsNoTracking().Where(x => !(x.Flag || x.Other))
                .ToList().Select(x => x.Label).OrderBy(l => l).ToList();

            Assert.Equal(driverLabels, nativeLabels);
            Assert.Equal(["p3"], nativeLabels);
        }
    }
}
