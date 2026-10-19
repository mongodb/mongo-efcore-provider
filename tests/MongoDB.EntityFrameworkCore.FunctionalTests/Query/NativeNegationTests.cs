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
/// <c>Not</c> over a subtree with no query-dialect form (e.g. a field-to-field comparison) renders via
/// <c>{ $expr: { $not: [...] } }</c> when <see cref="MongoAggregationExpressionRenderer.CanRender"/> admits the
/// operand; see <see cref="MongoQueryLanguageRenderer.RenderUnary"/>.
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

    // A field-to-field comparison has no query-dialect form, so Not over it renders via $expr.
    [Fact]
    public void NativeOnly_not_over_field_to_field_comparison_succeeds_with_expected_mql()
    {
        var (collection, logs) = Seed(nameof(NativeOnly_not_over_field_to_field_comparison_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

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

    // Mixed dialect: an indexable clause alongside the $expr-wrapped Not.
    [Fact]
    public void NativeOnly_not_over_field_to_field_comparison_composed_with_and_succeeds()
    {
        var (collection, logs) = Seed(nameof(NativeOnly_not_over_field_to_field_comparison_composed_with_and_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking().Where(x => x.A > 0 && !(x.A == x.B)).ToList();

        Assert.Equal(2, result.Count);
    }

    // A boolean ternary in predicate position (!(test ? false : true)), handled by TranslateNode and rendered
    // as $cond under the $expr Not fallback.
    [Fact]
    public void NativeOnly_not_over_boolean_ternary_succeeds_with_expected_mql()
    {
        var (collection, logs) = Seed(nameof(NativeOnly_not_over_boolean_ternary_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking().Where(x => !(x.A >= 2 ? false : true)).ToList();

        Assert.Single(result);

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("\"$cond\"", mql);
    }

    [Fact]
    public void Not_over_boolean_ternary_matches_driver_linq_results()
    {
        var (collection, logs) = Seed(nameof(Not_over_boolean_ternary_matches_driver_linq_results));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeIds = native.Entities.AsNoTracking().Where(x => !(x.A >= 2 ? false : true))
            .Select(x => x.Id).OrderBy(id => id).ToList();
        var driverIds = driver.Entities.AsNoTracking().Where(x => !(x.A >= 2 ? false : true))
            .Select(x => x.Id).OrderBy(id => id).ToList();

        Assert.Equal(driverIds, nativeIds);
    }

    // Not over $and/$or of bare fields: one $expr truthiness test would be unsafe for a value-converted bool stored as
    // "Y"/"N" (both truthy). MongoExpressionNegator's De Morgan complement pushes the negation to each field, and a
    // per-field Not renders as a converter-aware $ne in the query dialect, safe for any stored representation.

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
            // p2: Flag=false (stored "N", also truthy), Other=true -> Flag&&Other CLR-false -> !(...) = true
            // — a raw $and would see "N" as truthy and wrongly exclude this row.
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
    public void Not_over_and_of_a_value_converted_bare_bool_field_negates_exactly_via_per_field_ne()
    {
        var (collection, logs) = SeedLogicalFlag(
            nameof(Not_over_and_of_a_value_converted_bare_bool_field_negates_exactly_via_per_field_ne));

        // De Morgan's to !Flag || !Other (each a converter-aware $ne); never the truthiness-hazard answer a raw $and gives (only ["p3"]).
        using (var nativeOnly = CreateLogicalFlagContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking().Where(x => !(x.Flag && x.Other))
                .ToList().Select(x => x.Label).OrderBy(l => l).ToList();
            Assert.Equal(["p2", "p3"], nativeOnlyLabels);
        }

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

    // Same shape, OrElse form: !(x.Flag || x.Other). Truth table over the same seed:
    // p1 Flag=true,Other=true  -> Flag||Other CLR-true  -> !(...) = false
    // p2 Flag=false,Other=true -> Flag||Other CLR-true  -> !(...) = false
    // p3 Flag=false,Other=false -> Flag||Other CLR-false -> !(...) = true
    // De Morgan's to !Flag && !Other; a raw $or would return no rows ("N" is truthy too) instead of ["p3"].
    [Fact]
    public void Not_over_or_of_a_value_converted_bare_bool_field_negates_exactly_via_per_field_ne()
    {
        var (collection, logs) = SeedLogicalFlag(
            nameof(Not_over_or_of_a_value_converted_bare_bool_field_negates_exactly_via_per_field_ne));

        using (var nativeOnly = CreateLogicalFlagContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking().Where(x => !(x.Flag || x.Other))
                .ToList().Select(x => x.Label).OrderBy(l => l).ToList();
            Assert.Equal(["p3"], nativeOnlyLabels);
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

    public class NullableItem
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int? NullableInt { get; set; }
        public string Name { get; set; } = "";
    }

    // De Morgan'd per operand in the query dialect: {$not: {$lt: 5}} matches null/missing, as .NET's lifted
    // `!(null < 5)` is true. An aggregation $not wrap would instead order null below 5 and answer false.
    [Fact]
    public void Negated_conjunction_over_nullable_relational_matches_linq_to_objects()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Negated_conjunction_over_nullable_relational_matches_linq_to_objects)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(name);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "1a" }, { "NullableInt", 1 }, { "Name", "a" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "7a" }, { "NullableInt", 7 }, { "Name", "a" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "null-a" }, { "NullableInt", BsonNull.Value }, { "Name", "a" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing-a" }, { "Name", "a" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "1b" }, { "NullableInt", 1 }, { "Name", "b" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "null-b" }, { "NullableInt", BsonNull.Value }, { "Name", "b" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "missing-b" }, { "Name", "b" } },
        ]);
        var collection = database.MongoDatabase.GetCollection<NullableItem>(name);

        using var nativeOnly = SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
        });

        var oracle = nativeOnly.Entities.AsNoTracking().ToList()
            .Where(x => !(x.NullableInt < 5 && x.Name == "a")).Select(x => x.Label).OrderBy(l => l, StringComparer.Ordinal);
        var result = nativeOnly.Entities.AsNoTracking()
            .Where(x => !(x.NullableInt < 5 && x.Name == "a")).Select(x => x.Label).ToList()
            .OrderBy(l => l, StringComparer.Ordinal);

        Assert.Equal(oracle, result);
        Assert.Equal(["1b", "7a", "missing-a", "missing-b", "null-a", "null-b"], result);
    }
}
