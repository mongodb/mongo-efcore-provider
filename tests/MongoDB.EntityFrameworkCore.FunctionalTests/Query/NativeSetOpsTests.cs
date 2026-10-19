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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native set operations. <c>Union</c>/<c>Concat</c> lower to <c>$unionWith</c> (<c>Union</c> also dedups via
/// <c>$group{_id:"$$ROOT"}</c> + <c>$replaceRoot</c>); <c>Intersect</c>/<c>Except</c> lower to a source-tagging
/// <c>$unionWith</c> pipeline. Out-of-scope <c>Union</c>/<c>Concat</c> shapes fall back (correct under
/// <see cref="MongoQueryMode.Native"/>, <see cref="NativeTranslationNotSupportedException"/> under
/// <see cref="MongoQueryMode.NativeOnly"/>). <c>Intersect</c>/<c>Except</c> have no driver-LINQ fallback, so
/// out-of-scope shapes hard-fail in every mode and results are checked against in-memory LINQ.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSetOpsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Public: IntersectComposedOps's MemberData exposes Func<IQueryable<Item>, object> on a public test method,
    // which requires Item to be at least as accessible.
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    // Values 1..5; Where(<=3) and Where(>=3) overlap on Value==3 so Union dedups it (5 rows) while Concat keeps it (6).
    private static Item[] SeedItems() =>
    [
        new() { Id = ObjectId.GenerateNewId(), Name = "One", Value = 1 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Two", Value = 2 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Three", Value = 3 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Four", Value = 4 },
        new() { Id = ObjectId.GenerateNewId(), Name = "Five", Value = 5 },
    ];

    private IMongoCollection<Item> SeedCollection(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Item>(collectionName);
        collection.InsertMany(SeedItems());
        return collection;
    }

    // Two distinct entities sharing a Name — proves Union dedups by whole entity, so both survive a trailing
    // member-access Select.
    private IMongoCollection<Item> SeedCollectionWithDuplicateNames(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Item>(collectionName);
        collection.InsertMany(
        [
            new Item { Id = ObjectId.GenerateNewId(), Name = "Dup", Value = 1 },
            new Item { Id = ObjectId.GenerateNewId(), Name = "Dup", Value = 2 },
        ]);
        return collection;
    }

    private static SingleEntityDbContext<Item> Make(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<Item> MakeWithLogs(
        IMongoCollection<Item> collection, MongoQueryMode mode, List<string> logs)
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

    [Fact]
    public void Union_whole_entity_goes_native()
    {
        var collection = SeedCollection(nameof(Union_whole_entity_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3)).ToList();

        Assert.Equal(5, result.Count); // {1,2,3} U {3,4,5}, deduped on the shared Value==3 document

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);
        Assert.Contains("$group", mql);
        Assert.Contains("$replaceRoot", mql);
    }

    [Fact]
    public void Concat_whole_entity_goes_native()
    {
        var collection = SeedCollection(nameof(Concat_whole_entity_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 3).Concat(db.Entities.Where(i => i.Value >= 3)).ToList();

        Assert.Equal(6, result.Count); // Concat keeps the Value==3 duplicate: {1,2,3} + {3,4,5}

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);
        Assert.DoesNotContain("$group", mql); // no dedup stage for Concat
    }

    [Fact]
    public void Intersect_whole_entity_goes_native()
    {
        var collection = SeedCollection(nameof(Intersect_whole_entity_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // The set op must stay terminal (a queryable .OrderBy after it would fall back / throw under NativeOnly).
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3)).ToList();

        Assert.Equal([3], result.Select(i => i.Value).OrderBy(v => v)); // present in both {1,2,3} and {3,4,5}

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);
        Assert.Contains("$replaceRoot", mql);
        Assert.Contains("_doc", mql);   // the source-tagging shape
    }

    [Fact]
    public void Except_whole_entity_goes_native()
    {
        var collection = SeedCollection(nameof(Except_whole_entity_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3)).ToList();

        Assert.Equal([1, 2], result.Select(i => i.Value).OrderBy(v => v)); // in {1,2,3}, not in {3,4,5}

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);
        Assert.Contains("$replaceRoot", mql);
    }

    // Intersect/Except correctness: no driver-LINQ oracle, so results are checked against in-memory LINQ. The set op
    // must stay terminal (the driver can't do Intersect/Except), so sort the materialized list.

    [Fact]
    public void Intersect_disjoint_operands_yields_empty()
    {
        var collection = SeedCollection(nameof(Intersect_disjoint_operands_yields_empty));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value <= 2).Intersect(db.Entities.Where(i => i.Value >= 4)).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Except_disjoint_operands_yields_all_of_first()
    {
        var collection = SeedCollection(nameof(Except_disjoint_operands_yields_all_of_first));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value <= 2).Except(db.Entities.Where(i => i.Value >= 4)).ToList();
        Assert.Equal([1, 2], result.Select(i => i.Value).OrderBy(v => v));
    }

    [Fact]
    public void Intersect_full_overlap_yields_deduped_first()
    {
        var collection = SeedCollection(nameof(Intersect_full_overlap_yields_deduped_first));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value >= 1).Intersect(db.Entities.Where(i => i.Value >= 1)).ToList();
        Assert.Equal([1, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
    }

    [Fact]
    public void Except_whole_second_operand_yields_empty()
    {
        var collection = SeedCollection(nameof(Except_whole_second_operand_yields_empty));
        using var db = Make(collection, MongoQueryMode.Native);
        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 1)).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Intersect_parametrized_operand_predicate_substitutes()
    {
        var collection = SeedCollection(nameof(Intersect_parametrized_operand_predicate_substitutes));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        var threshold = 3;
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= threshold)).ToList();
        Assert.Equal([3], result.Select(i => i.Value).OrderBy(v => v)); // captured `threshold` substitutes inside the operand pipeline
    }

    // A bare projected operand is admitted like a wrapped one (array-leaf dedup-key hazard: HasArrayProjectionLeaf).
    // DriverLinq still hard-fails: the driver has no Intersect/Except translation.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Bare_scalar_operand_intersect_goes_native(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Bare_scalar_operand_intersect_goes_native) + mode);
        using var db = Make(collection, mode);
        // {1,2,3} intersect {3,4,5} = {3}.
        var result = db.Entities.Where(i => i.Value <= 3).Select(i => i.Value)
            .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => i.Value)).ToList();
        Assert.Equal([3], result.OrderBy(v => v));
    }

    [Fact]
    public void Bare_scalar_operand_intersect_still_hard_fails_under_explicit_driver_linq()
    {
        var collection = SeedCollection(nameof(Bare_scalar_operand_intersect_still_hard_fails_under_explicit_driver_linq));
        using var db = Make(collection, MongoQueryMode.DriverLinq);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => i.Value)
                .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => i.Value)).ToList());
    }

    // A trailing Where after Except goes native; checked against in-memory data plus NativeOnly.
    [Fact]
    public void Except_then_Where_goes_native()
    {
        var collection = SeedCollection(nameof(Except_then_Where_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Except {3,4,5} = {1,2}; Where(Value >= 2) = {2}. A $match before the set-difference would coincidentally
        // give {2} too; the paging/Count tests below discriminate that.
        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3))
            .Where(i => i.Value >= 2).ToList();
        Assert.Equal([2], result.Select(i => i.Value).OrderBy(v => v));
    }

    // Composition-seam hard-fail: the IsSetOp terminal gate rejects operators composed after Intersect/Except.

    public static IEnumerable<object[]> IntersectComposedOps() => new[]
    {
        // Only deferred operators remain here; OrderBy/Skip/Take are covered by Intersect_then_paging_goes_native.
        new object[] { "GroupBy", (Func<IQueryable<Item>, object>)(q => q.GroupBy(i => i.Value).Select(g => g.Key).ToList()) },
    };

    [Theory]
    [MemberData(nameof(IntersectComposedOps))]
    public void Intersect_then_op_hard_fails_under_native(string name, Func<IQueryable<Item>, object> compose)
    {
        var collection = SeedCollection(nameof(Intersect_then_op_hard_fails_under_native) + name);
        using var db = Make(collection, MongoQueryMode.Native);
        var q = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3));
        Assert.ThrowsAny<Exception>(() => compose(q));
    }

    [Fact]
    public void Intersect_then_paging_goes_native()
    {
        var collection = SeedCollection(nameof(Intersect_then_paging_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {2,3,4} = {2,3}; OrderBy(Value).Take(1) = {2}.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 2 && i.Value <= 4))
            .OrderBy(i => i.Value).Take(1).ToList();
        Assert.Equal([2], result.Select(i => i.Value));
    }

    // A Take before the reducer means two consecutive $limit stages, which compose correctly (monotonic
    // narrowing). Union has a driver-LINQ baseline, so assert Native == DriverLinq.
    [Fact]
    public void First_after_union_with_preceding_take_goes_native()
    {
        var collection = SeedCollection(nameof(First_after_union_with_preceding_take_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        // Union({1,2,3},{3,4,5}) deduped = {1,2,3,4,5}; ordered, Take(4) = {1,2,3,4}; First = 1.
        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value).Take(4).First().Value;

        var native = Run(nativeOnlyDb);
        Assert.Equal(1, native);
        Assert.Equal(Run(driverDb), native);
    }

    // Skip alone (no preceding Take) after Intersect. No driver-LINQ oracle, so assert the expected result
    // under NativeOnly.
    [Fact]
    public void Intersect_then_Skip_goes_native()
    {
        var collection = SeedCollection(nameof(Intersect_then_Skip_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {2,3,4} = {2,3}; OrderBy(Value).Skip(1) = {3}.
        var result = db.Entities.Where(i => i.Value <= 3)
            .Intersect(db.Entities.Where(i => i.Value >= 2 && i.Value <= 4))
            .OrderBy(i => i.Value).Skip(1).ToList();
        Assert.Equal([3], result.Select(i => i.Value));
    }

    [Fact]
    public void Union_then_parametrized_trailing_Where_goes_native()
    {
        var collection = SeedCollection(nameof(Union_then_parametrized_trailing_Where_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        var threshold = 4;
        var result = db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
            .Where(i => i.Value >= threshold).ToList();
        Assert.Equal([4, 5], result.Select(i => i.Value).OrderBy(v => v));
    }

    // Field-name collision: RenderSetDifference tags each side with sibling fields _a/_b beside a synthesized _doc
    // wrapper (MongoPipelineFactory). A real stored element named _a lives inside _doc and must not collide.
    private class TaggyItem
    {
        public ObjectId Id { get; set; }

        [MongoDB.Bson.Serialization.Attributes.BsonElement("_a")] // a real stored element literally named _a
        public int A { get; set; }

        public int Value { get; set; }
    }

    private IMongoCollection<TaggyItem> SeedTaggyCollection(string name, TaggyItem[] items)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<TaggyItem>(collectionName);
        collection.InsertMany(items);
        return collection;
    }

    private static SingleEntityDbContext<TaggyItem> MakeTaggy(IMongoCollection<TaggyItem> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Intersect_with_real_element_named_underscore_a_is_not_corrupted_by_the_source_tag()
    {
        var items = new[]
        {
            new TaggyItem { Id = ObjectId.GenerateNewId(), A = 100, Value = 1 },
            new TaggyItem { Id = ObjectId.GenerateNewId(), A = 200, Value = 2 },
            new TaggyItem { Id = ObjectId.GenerateNewId(), A = 300, Value = 3 },
        };
        var collection = SeedTaggyCollection(
            nameof(Intersect_with_real_element_named_underscore_a_is_not_corrupted_by_the_source_tag), items);
        using var db = MakeTaggy(collection, MongoQueryMode.Native);

        var result = db.Entities.Where(i => i.Value <= 2).Intersect(db.Entities.Where(i => i.Value >= 2)).ToList();

        var single = Assert.Single(result);
        Assert.Equal(2, single.Value);
        Assert.Equal(200, single.A); // the real _a element survives the $unionWith source-tag round-trip intact
    }

    [Fact]
    public void Union_matches_baseline()
    {
        var collection = SeedCollection(nameof(Union_matches_baseline));
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        var native = Run(nativeDb);
        Assert.Equal([1, 2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Concat_matches_baseline()
    {
        var collection = SeedCollection(nameof(Concat_matches_baseline));
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Concat(db.Entities.Where(i => i.Value >= 3))
                .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        var native = Run(nativeDb);
        Assert.Equal([1, 2, 3, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    // Graceful fallback: out-of-scope Union/Concat shapes throw under NativeOnly but return correct results under Native.

    [Fact]
    public void Projected_operand_union_bare_goes_native()
    {
        var collection = SeedCollection(nameof(Projected_operand_union_bare_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        var result = nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .ToList();
        Assert.Equal(5, result.Count); // {One,Two,Three} ∪ {Three,Four,Five} = 5 distinct Names
    }

    // No mismatched-entity-type fallback test: EF rejects incompatible Union/Concat sources during preprocessing, so
    // TryTranslateSetOperation's EntityType check is defense in depth only.

    // A different-collection projected Union goes native, bypassing the driver-LINQ bridge (whose cross-DbSet guard
    // would throw). No driver oracle, so assert the exact expected set.
    [Fact]
    public void Different_collection_projected_operand_union_goes_native()
    {
        using var db = MakeTwoEntity(MongoQueryMode.NativeOnly);
        // Two different entity types / collections projecting to the same anonymous shape {string Label}.
        var result = db.Lefts.Select(l => new { Label = l.Name })
            .Union(db.Rights.Select(r => new { Label = r.Title }))
            .ToList()
            .Select(x => x.Label).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "a", "b", "c" }, result); // Lefts {a,b} U Rights {b,c} = {a,b,c}
    }

    [Fact]
    public void Different_collection_projected_operand_intersect_goes_native_result_set()
    {
        using var db = MakeTwoEntity(MongoQueryMode.NativeOnly);
        var result = db.Lefts.Select(l => new { Label = l.Name })
            .Intersect(db.Rights.Select(r => new { Label = r.Title }))
            .ToList()
            .Select(x => x.Label).ToList();
        Assert.Equal(new[] { "b" }, result); // Lefts {a,b} ∩ Rights {b,c} = {b}
    }

    [Fact]
    public void Different_collection_projected_operand_except_goes_native_result_set()
    {
        using var db = MakeTwoEntity(MongoQueryMode.NativeOnly);
        var result = db.Lefts.Select(l => new { Label = l.Name })
            .Except(db.Rights.Select(r => new { Label = r.Title }))
            .ToList()
            .Select(x => x.Label).ToList();
        Assert.Equal(new[] { "a" }, result); // Lefts {a,b} \ Rights {b,c} = {a}
    }

    // A projected-operand Union over {Name} dedups the projected value: two entities sharing Name "Dup" yield one row,
    // unlike whole-entity Union then projection (see Union_dedups_entities_then_projects_keeping_duplicate_projected_values).
    [Fact]
    public void Projected_operand_union_dedups_over_projected_values_not_whole_entities()
    {
        var collection = SeedCollectionWithDuplicateNames(
            nameof(Projected_operand_union_dedups_over_projected_values_not_whole_entities));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        var result = db.Entities.Select(i => new { i.Name })
            .Union(db.Entities.Select(i => new { i.Name }))
            .ToList();
        Assert.Single(result);
        Assert.Equal("Dup", result[0].Name);
    }

    // Each operand's Where lowers ahead of its $project, and a captured local in an operand substitutes.
    [Fact]
    public void Projected_operand_union_with_per_operand_filter_and_parameter_goes_native()
    {
        var collection = SeedCollection(nameof(Projected_operand_union_with_per_operand_filter_and_parameter_goes_native));
        var lo = 2;
        var hi = 4;
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= lo).Select(i => new { i.Name })
                .Union(db.Entities.Where(i => i.Value >= hi).Select(i => new { i.Name }))
                .ToList()
                .Select(x => x.Name).OrderBy(n => n).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal(4, native.Count); // Value<=2 (One,Two) U Value>=4 (Four,Five) = 4 distinct
        Assert.Equal(Run(driverDb), native);
    }

    public class Left { public ObjectId Id { get; set; } public string Name { get; set; } = ""; }
    public class Right { public ObjectId Id { get; set; } public string Title { get; set; } = ""; }

    private class TwoEntityDbContext : DbContext
    {
        private readonly string _lefts;
        private readonly string _rights;
        private readonly MongoQueryMode _mode;

        public TwoEntityDbContext(TemporaryDatabaseFixture db, string lefts, string rights, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<TwoEntityDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreTwoEntityCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _lefts = lefts;
            _rights = rights;
            _mode = mode;
        }

        public DbSet<Left> Lefts { get; set; } = null!;
        public DbSet<Right> Rights { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Left>().ToCollection(_lefts);
            modelBuilder.Entity<Right>().ToCollection(_rights);
        }

        private sealed class IgnoreTwoEntityCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    private TwoEntityDbContext MakeTwoEntity(MongoQueryMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var leftsName = TemporaryDatabaseFixtureBase.CreateCollectionName("C1Lefts") + suffix;
        var rightsName = TemporaryDatabaseFixtureBase.CreateCollectionName("C1Rights") + suffix;
        database.MongoDatabase.GetCollection<Left>(leftsName).InsertMany(
        [
            new Left { Id = ObjectId.GenerateNewId(), Name = "a" },
            new Left { Id = ObjectId.GenerateNewId(), Name = "b" },
        ]);
        database.MongoDatabase.GetCollection<Right>(rightsName).InsertMany(
        [
            new Right { Id = ObjectId.GenerateNewId(), Title = "b" },
            new Right { Id = ObjectId.GenerateNewId(), Title = "c" },
        ]);
        return new TwoEntityDbContext(database, leftsName, rightsName, mode);
    }

    private class LinkedItem
    {
        public ObjectId Id { get; set; }
        public int Value { get; set; }
        public List<LinkedDetail> Details { get; set; } = [];
    }

    private class LinkedDetail
    {
        public ObjectId Id { get; set; }
        public string Text { get; set; } = "";
        public ObjectId LinkedItemId { get; set; }

        // Inverse reference navigation, used only by Reference_include_after_union_still_falls_back.
        public LinkedItem? Item { get; set; }
    }

    private class LinkedItemDbContext : DbContext
    {
        private readonly string _items;
        private readonly string _details;

        public LinkedItemDbContext(TemporaryDatabaseFixture db, string items, string details, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<LinkedItemDbContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _items = items;
            _details = details;
        }

        public DbSet<LinkedItem> Items { get; set; } = null!;
        public DbSet<LinkedDetail> Details { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LinkedItem>(b =>
            {
                b.ToCollection(_items);
                b.HasMany(i => i.Details).WithOne(d => d.Item).HasForeignKey(d => d.LinkedItemId);
            });
            modelBuilder.Entity<LinkedDetail>(b => b.ToCollection(_details));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    // Seeds items Value 1/2/3 with 2/1/0 details. Every item has a different detail count, so a row joined
    // from the wrong row (or not at all) can't pass by coincidence, and the zero case is genuinely empty.
    private (string Items, string Details) SeedLinked(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var itemsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "I" + suffix;
        var detailsName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + "D" + suffix;

        var item1 = ObjectId.GenerateNewId();
        var item2 = ObjectId.GenerateNewId();
        var item3 = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<LinkedItem>(itemsName).InsertMany(
        [
            new LinkedItem { Id = item1, Value = 1 },
            new LinkedItem { Id = item2, Value = 2 },
            new LinkedItem { Id = item3, Value = 3 },
        ]);
        database.MongoDatabase.GetCollection<LinkedDetail>(detailsName).InsertMany(
        [
            new LinkedDetail { Id = ObjectId.GenerateNewId(), Text = "a1", LinkedItemId = item1 },
            new LinkedDetail { Id = ObjectId.GenerateNewId(), Text = "a2", LinkedItemId = item1 },
            new LinkedDetail { Id = ObjectId.GenerateNewId(), Text = "b1", LinkedItemId = item2 },
        ]);

        return (itemsName, detailsName);
    }

    // A collection Include with a set operation goes native. EF hoists the Include after the combinator
    // ("Union(A, B).Select(x => Include(x))"); the lowerer defers the lookup block past the set-op stage so the join
    // runs once over the combined result (before $unionWith, operand rows would come back with empty collections).
    // The discriminator is the Value == 2 row, which the union operand contributes.

    [Fact]
    public void Union_with_collection_include_goes_native_and_joins_both_operands()
    {
        var (itemsName, detailsName) = SeedLinked(nameof(Union_with_collection_include_goes_native_and_joins_both_operands));

        List<LinkedItem> Run(MongoQueryMode mode)
        {
            using var db = new LinkedItemDbContext(database, itemsName, detailsName, mode);
            return db.Items.Where(i => i.Value == 1).Include(i => i.Details)
                .Union(db.Items.Where(i => i.Value >= 2).Include(i => i.Details))
                .OrderBy(i => i.Value)
                .ToList();
        }

        var native = Run(MongoQueryMode.NativeOnly); // NativeOnly succeeding is the "went native" signal

        Assert.Equal([1, 2, 3], native.Select(i => i.Value));
        Assert.Equal(["a1", "a2"], native.Single(i => i.Value == 1).Details.Select(d => d.Text).Order());
        // The operand-contributed row.
        Assert.Equal(["b1"], native.Single(i => i.Value == 2).Details.Select(d => d.Text));
        Assert.Empty(native.Single(i => i.Value == 3).Details);

        var driver = Run(MongoQueryMode.DriverLinq);
        Assert.Equal(
            driver.Select(i => (i.Value, string.Join(",", i.Details.Select(d => d.Text).Order()))),
            native.Select(i => (i.Value, string.Join(",", i.Details.Select(d => d.Text).Order()))));
    }

    [Fact]
    public void Concat_with_collection_include_goes_native_and_keeps_duplicates()
    {
        // Concat has no dedup, so the overlapping Value == 2 row appears twice and both copies must carry the joined details.
        var (itemsName, detailsName) = SeedLinked(nameof(Concat_with_collection_include_goes_native_and_keeps_duplicates));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Where(i => i.Value <= 2).Include(i => i.Details)
            .Concat(db.Items.Where(i => i.Value >= 2).Include(i => i.Details))
            .AsNoTracking() // no identity resolution: keep the duplicate as two distinct instances
            .ToList();

        Assert.Equal(4, result.Count); // {1,2} ++ {2,3}
        var twos = result.Where(i => i.Value == 2).ToList();
        Assert.Equal(2, twos.Count);
        Assert.All(twos, i => Assert.Equal(["b1"], i.Details.Select(d => d.Text)));
    }

    [Fact]
    public void Union_with_collection_include_dedups_by_entity_not_by_joined_children()
    {
        // Both operands select the same two rows, so Union returns 2; dedup must run over pre-join documents.
        var (itemsName, detailsName) = SeedLinked(nameof(Union_with_collection_include_dedups_by_entity_not_by_joined_children));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Where(i => i.Value <= 2).Include(i => i.Details)
            .Union(db.Items.Where(i => i.Value <= 2).Include(i => i.Details))
            .OrderBy(i => i.Value)
            .ToList();

        Assert.Equal([1, 2], result.Select(i => i.Value));
        Assert.Equal(2, result.Single(i => i.Value == 1).Details.Count);
        Assert.Single(result.Single(i => i.Value == 2).Details);
    }

    [Fact]
    public void Include_composes_with_paging_after_the_union()
    {
        // The deferred lookup block follows TrailingOps, so OrderBy/Skip/Take page the combined stream first.
        // Skip(1).Take(1) over {1,2,3} keeps the operand-contributed row 2.
        var (itemsName, detailsName) = SeedLinked(nameof(Include_composes_with_paging_after_the_union));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Where(i => i.Value == 1).Include(i => i.Details)
            .Union(db.Items.Where(i => i.Value >= 2).Include(i => i.Details))
            .OrderBy(i => i.Value).Skip(1).Take(1)
            .ToList();

        var only = Assert.Single(result);
        Assert.Equal(2, only.Value);
        Assert.Equal(["b1"], only.Details.Select(d => d.Text));
    }

    [Fact]
    public void Intersect_with_collection_include_goes_native()
    {
        // Intersect/Except use the source-tagging pipeline and have no driver-LINQ oracle; the asymmetric
        // detail counts prove each surviving row got its own children.
        var (itemsName, detailsName) = SeedLinked(nameof(Intersect_with_collection_include_goes_native));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Where(i => i.Value <= 2).Include(i => i.Details)
            .Intersect(db.Items.Where(i => i.Value >= 2).Include(i => i.Details))
            .ToList();

        var only = Assert.Single(result);
        Assert.Equal(2, only.Value);
        Assert.Equal(["b1"], only.Details.Select(d => d.Text));
    }

    [Fact]
    public void Except_with_collection_include_goes_native()
    {
        var (itemsName, detailsName) = SeedLinked(nameof(Except_with_collection_include_goes_native));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Where(i => i.Value <= 2).Include(i => i.Details)
            .Except(db.Items.Where(i => i.Value >= 2).Include(i => i.Details))
            .ToList();

        var only = Assert.Single(result);
        Assert.Equal(1, only.Value);
        Assert.Equal(["a1", "a2"], only.Details.Select(d => d.Text).Order());
    }

    [Fact]
    public void Reference_include_after_union_still_falls_back()
    {
        // Only the collection-Include branch is relaxed. A reference Include is declined earlier
        // (TryConfirmReferenceInclude's HasTerminalOperator conjunct), so it must still fall back.
        var (itemsName, detailsName) = SeedLinked(nameof(Reference_include_after_union_still_falls_back));

        using (var nativeOnlyDb = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Details.Where(d => d.Text == "a1").Include(d => d.Item)
                    .Union(nativeOnlyDb.Details.Where(d => d.Text == "b1").Include(d => d.Item))
                    .ToList());
        }

        using var nativeDb = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.Native);
        var result = nativeDb.Details.Where(d => d.Text == "a1").Include(d => d.Item)
            .Union(nativeDb.Details.Where(d => d.Text == "b1").Include(d => d.Item))
            .OrderBy(d => d.Text)
            .ToList();

        Assert.Equal(["a1", "b1"], result.Select(d => d.Text));
        Assert.Equal([1, 2], result.Select(d => d.Item!.Value));
    }

    [Fact]
    public void Collection_count_in_a_trailing_projection_after_a_union_counts_every_row()
    {
        // A collection-nav Count in a trailing projection after a set op registers its own
        // IsNativeCollectionLookup $lookup, which must also run after the combine or operand rows count 0.
        var (itemsName, detailsName) = SeedLinked(nameof(Collection_count_in_a_trailing_projection_after_a_union_counts_every_row));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Where(i => i.Value == 1)
            .Union(db.Items.Where(i => i.Value >= 2))
            .Select(i => new { i.Value, Count = i.Details.Count })
            .OrderBy(x => x.Value)
            .ToList();

        Assert.Equal([(1, 2), (2, 1), (3, 0)], result.Select(x => (x.Value, x.Count)));
    }

    [Fact]
    public void Operand_carrying_its_own_include_is_unaffected_by_the_relaxation()
    {
        // IsPlainWholeEntitySelect's `Lookups.Count == 0` decline is untouched; for a collection Include the
        // hoist means it isn't the deciding site, so this goes native via the hoisted path. Baseline for any
        // future change to that check.
        var (itemsName, detailsName) = SeedLinked(nameof(Operand_carrying_its_own_include_is_unaffected_by_the_relaxation));

        using var db = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly);
        var result = db.Items.Include(i => i.Details).Where(i => i.Value == 1)
            .Union(db.Items.Include(i => i.Details).Where(i => i.Value == 2))
            .OrderBy(i => i.Value)
            .ToList();

        Assert.Equal([1, 2], result.Select(i => i.Value));
        Assert.Equal(2, result[0].Details.Count);
        Assert.Single(result[1].Details);
    }

    [Fact]
    public void Projected_operand_union_over_collection_navigation_count_goes_native()
    {
        // The left operand projects i.Details.Count, registering an InjectAfterRoot $lookup that belongs to
        // source1's own pre-combine pipeline: it must run before source1's $project and before $unionWith.
        var (itemsName, detailsName) = SeedLinked(nameof(Projected_operand_union_over_collection_navigation_count_goes_native));

        List<int> Run(MongoQueryMode mode)
        {
            using var db = new LinkedItemDbContext(database, itemsName, detailsName, mode);
            return db.Items.Select(i => i.Details.Count())
                .Union(db.Items.Select(i => 8))
                .ToList();
        }

        var native = Run(MongoQueryMode.NativeOnly); // NativeOnly succeeding is the "went native" signal

        Assert.Equal([0, 1, 2, 8], native.Order());

        var driver = Run(MongoQueryMode.DriverLinq);
        Assert.Equal(driver.Order(), native.Order());
    }

    [Fact]
    public void Constant_projected_operand_as_source1_goes_native_and_reads_each_row_from_the_document()
    {
        // Mirror of the projected-collection-Count test with a constant leaf (`Select(i => 8)`) on source1. The combined
        // stream uses source1's shaper, which on its own embeds a bare constant leaf as a literal, so every row would read
        // back 8. CanRebindConstantLeafToDocument rebinds that leaf to read the `$literal`-projected value per document
        // (see NativeSetOperationProjectionTests for the full matrix).
        var collection = SeedCollection(
            nameof(Constant_projected_operand_as_source1_goes_native_and_reads_each_row_from_the_document));

        List<int> Run(MongoQueryMode mode)
        {
            using var db = Make(collection, mode);
            return db.Entities.Select(i => 8).Union(db.Entities.Select(i => i.Value + 1)).ToList().Order().ToList();
        }

        var native = Run(MongoQueryMode.NativeOnly);

        // {1..5} -> right operand {2,3,4,5,6}; left operand is 8 for every row, deduped to one; Union of the two.
        Assert.Equal([2, 3, 4, 5, 6, 8], native);
        Assert.Equal(Run(MongoQueryMode.DriverLinq), native);
    }

    // Composition seams: every operator after a Union/Concat must go native correctly or fall back. Every
    // post-terminal entry point must gate on HasTerminalOperator, or a post-union operator resolves against the base
    // entity and emits a pre-$unionWith stage.

    [Fact]
    public void Where_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Where_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Where(i => i.Value >= 2)
                .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void OrderBy_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(OrderBy_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value)
                .ToList().Select(i => i.Value).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([1, 2, 3, 4, 5], native); // already sorted by the native $sort, no in-memory re-sort
        Assert.Equal(Run(driverDb), native);
    }

    // Paging the combined ordered stream differs from paging source1.
    [Fact]
    public void Paging_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Paging_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        // Union = {1,2,3,4,5} ordered; Skip(1).Take(2) = {2,3}. Paging source1 would also give {2,3}; the Count test below discriminates.
        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value).Skip(1).Take(2)
                .ToList().Select(i => i.Value).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3], native);
        Assert.Equal(Run(driverDb), native);
    }

    // Count over the combined union (5) differs from source1's (3), so a pre-$unionWith $count would be wrong.
    [Fact]
    public void Count_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Count_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3)).Count();

        var native = Run(nativeOnlyDb);
        Assert.Equal(5, native); // {1,2,3} U {3,4,5} deduped
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Count_after_intersect_goes_native()
    {
        var collection = SeedCollection(nameof(Count_after_intersect_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; Count = 1 (source1 count would be 3).
        var count = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3)).Count();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Count_with_predicate_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Count_with_predicate_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3)).Count(i => i.Value >= 3);

        var native = Run(nativeOnlyDb);
        Assert.Equal(3, native); // {1,2,3,4,5}, Value>=3 → {3,4,5}
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void First_after_union_ordered_goes_native()
    {
        var collection = SeedCollection(nameof(First_after_union_ordered_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value).First().Value;

        var native = Run(nativeOnlyDb);
        Assert.Equal(1, native);
        Assert.Equal(Run(driverDb), native);
    }

    // A left-nested whole-entity Union chain goes native as a chain of $unionWith links.
    [Fact]
    public void Chained_union_goes_native()
    {
        var collection = SeedCollection(nameof(Chained_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);

        var result = nativeOnlyDb.Entities.Where(i => i.Value <= 2)
            .Union(nativeOnlyDb.Entities.Where(i => i.Value == 3))
            .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 4))
            .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        Assert.Equal([1, 2, 3, 4, 5], result); // three disjoint sets {1,2} U {3} U {4,5}
    }

    // Bool-keyed companion to GroupBy_after_Union_goes_native.
    [Fact]
    public void GroupBy_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(GroupBy_after_union_goes_native));

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        var result = nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
            .GroupBy(i => i.Value % 2 == 0)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList().OrderBy(g => g.Key).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result.Single(g => !g.Key).Count); // {1,3,5}
        Assert.Equal(2, result.Single(g => g.Key).Count); // {2,4}
    }

    // OfType after a Union: its discriminator conjunct would land in the outer Predicate (a pre-$unionWith $match),
    // leaving the operand unfiltered so base rows leak in. It must fall back.

    private class SetOpBase
    {
        public ObjectId Id { get; set; }

        // Nullable with no default: EF only auto-populates the discriminator when the property holds its type
        // default. A non-null default like "" would suppress it, giving every row the same wrong value.
        public string? EntityType { get; set; }

        public int Value { get; set; }
    }

    private class SetOpDerived : SetOpBase
    {
        public string Extra { get; set; } = "";
    }

    private static void SetOpTphModel(ModelBuilder mb) =>
        mb.Entity<SetOpBase>()
            .HasDiscriminator(e => e.EntityType)
            .HasValue<SetOpBase>("Base")
            .HasValue<SetOpDerived>("Derived");

    private static SingleEntityDbContext<SetOpBase> MakeTph(IMongoCollection<SetOpBase> collection, MongoQueryMode mode) =>
        SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: SetOpTphModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static void SeedSetOpTphData(DbContext db)
    {
        db.Add(new SetOpBase { Value = 1 });
        db.Add(new SetOpDerived { Value = 2, Extra = "x" });
        db.Add(new SetOpDerived { Value = 3, Extra = "y" });
        db.SaveChanges();
        db.Dispose();
    }

    [Fact]
    public void OfType_after_union_falls_back()
    {
        var collection = database.CreateCollection<SetOpBase>();
        SeedSetOpTphData(MakeTph(collection, MongoQueryMode.Native));

        // OfType after a Union must not go native (the discriminator can't reach the operand): NativeOnly throws.
        using (var nativeOnlyDb = MakeTph(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Union(nativeOnlyDb.Entities).OfType<SetOpDerived>().ToList());
        }

        // Native == DriverLinq: only derived rows, never a leaked base row.
        using var nativeDb = MakeTph(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Union(nativeDb.Entities).OfType<SetOpDerived>().ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.IsType<SetOpDerived>(e));
    }

    // A trailing projection after Union goes native.
    [Fact]
    public void Select_after_union_goes_native_parity()
    {
        var collection = SeedCollection(nameof(Select_after_union_goes_native_parity));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { i.Name })
                .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal(5, native.Count); // 5 distinct entities → 5 projected rows
        Assert.Equal(Run(driverDb), native);
    }

    // A trailing Distinct after a whole-entity set op's trailing projection (OperandsProjected == false) must go
    // native; contrast Distinct_after_projected_operand_union_falls_back_gracefully.
    [Fact]
    public void Trailing_distinct_after_whole_entity_union_still_goes_native()
    {
        var collection = SeedCollection(nameof(Trailing_distinct_after_whole_entity_union_still_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { i.Name })
            .Distinct()
            .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Two" }, result);
    }

    // A projected Distinct() as a Union operand goes native: its $group + flattening $project are part of its
    // pre-combine pipeline, so the Union dedup runs over flattened values from both operands.
    [Fact]
    public void Distinct_operand_union_goes_native()
    {
        var collection = SeedCollection(nameof(Distinct_operand_union_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name }).Distinct()
            .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Two" }, result); // {1,2,3} U {3,4,5} by Name, deduped
    }

    [Fact]
    public void Distinct_operand_union_matches_driver_linq()
    {
        var collection = SeedCollection(nameof(Distinct_operand_union_matches_driver_linq));
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name }).Distinct()
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Two" }, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Both_operands_distinct_union_goes_native()
    {
        var collection = SeedCollection(nameof(Both_operands_distinct_union_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name }).Distinct()
            .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }).Distinct())
            .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Two" }, result);
    }

    // A GroupBy(key).Select(aggregate) Union operand goes native: the OperandsProjected branch emits the operand's
    // $group + flattening $project and the dedup runs over the flattened {Key, Count} values.
    [Fact]
    public void GroupBy_select_operand_union_goes_native()
    {
        var collection = SeedCollection(nameof(GroupBy_select_operand_union_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.Where(i => i.Value <= 3).GroupBy(i => i.Value)
                .Select(g => new { Key = g.Key, Count = g.Count() })
            .Union(db.Entities.Where(i => i.Value >= 3).GroupBy(i => i.Value)
                .Select(g => new { Key = g.Key, Count = g.Count() }))
            .ToList().OrderBy(x => x.Key).ToList();

        // {1,2,3} U {3,4,5} grouped by Value (Count == 1 each) -> 5 distinct {Key, Count} rows; the shared
        // Value==3 group dedups away.
        Assert.Equal(
            new[] { (1, 1), (2, 1), (3, 1), (4, 1), (5, 1) },
            result.Select(x => (x.Key, x.Count)));
    }

    [Fact]
    public void GroupBy_select_operand_union_matches_driver_linq()
    {
        var collection = SeedCollection(nameof(GroupBy_select_operand_union_matches_driver_linq));
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<(int Key, int Count)> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).GroupBy(i => i.Value)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                .Union(db.Entities.Where(i => i.Value >= 3).GroupBy(i => i.Value)
                    .Select(g => new { Key = g.Key, Count = g.Count() }))
                .ToList().OrderBy(x => x.Key).Select(x => (x.Key, x.Count)).ToList();

        var native = Run(nativeDb);
        Assert.Equal(new[] { (1, 1), (2, 1), (3, 1), (4, 1), (5, 1) }, native);
        Assert.Equal(Run(driverDb), native);
    }

    // An operand carrying GroupPagingOps/GroupHavingPredicate/GroupOrderOp must decline: the OperandsProjected path
    // only emits $group + $project, so admitting it would silently drop the paging/HAVING/ordering. Each throws under
    // NativeOnly while Native and DriverLinq return the correct result.

    [Fact]
    public void GroupBy_operand_with_skip_before_terminal_select_declines_and_stays_correct()
    {
        // GroupPagingOps: operand1 Where(<=3).GroupBy(Value).OrderBy(Key).Skip(1) -> {2,3}; operand2 -> {3,4,5}.
        // Concat is 5 rows; dropping operand1's Skip would give 6.
        var collection = SeedCollection(nameof(GroupBy_operand_with_skip_before_terminal_select_declines_and_stays_correct));

        (int Key, int Count)[] Query(SingleEntityDbContext<Item> db)
            => db.Entities.Where(i => i.Value <= 3).GroupBy(i => i.Value)
                    .OrderBy(g => g.Key).Skip(1)
                    .Select(g => new { g.Key, Count = g.Count() })
                .Concat(db.Entities.Where(i => i.Value >= 3).GroupBy(i => i.Value)
                    .Select(g => new { g.Key, Count = g.Count() }))
                .ToList().OrderBy(x => x.Key).Select(x => (x.Key, x.Count)).ToArray();

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Query(nativeOnlyDb));

        var expected = new[] { (2, 1), (3, 1), (3, 1), (4, 1), (5, 1) };

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        Assert.Equal(expected, Query(nativeDb));

        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(expected, Query(driverDb));
    }

    [Fact]
    public void GroupBy_operand_with_having_predicate_before_terminal_select_declines_and_stays_correct()
    {
        // GroupHavingPredicate: group by (Value <= 2) so group sizes differ and HAVING actually removes one.
        // Operand1: {1,2,3} -> {true: Count=2, false: Count=1}; Where(g.Count() >= 2) keeps {true, 2}.
        // Operand2: {3,4,5} -> {false: Count=3}. Concat is 2 rows; dropping HAVING would give 3.
        var collection = SeedCollection(nameof(GroupBy_operand_with_having_predicate_before_terminal_select_declines_and_stays_correct));

        (bool Key, int Count)[] Query(SingleEntityDbContext<Item> db)
            => db.Entities.Where(i => i.Value <= 3).GroupBy(i => i.Value <= 2)
                    .Where(g => g.Count() >= 2)
                    .Select(g => new { g.Key, Count = g.Count() })
                .Concat(db.Entities.Where(i => i.Value >= 3).GroupBy(i => i.Value <= 2)
                    .Select(g => new { g.Key, Count = g.Count() }))
                .ToList().OrderBy(x => x.Key).Select(x => (x.Key, x.Count)).ToArray();

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Query(nativeOnlyDb));

        var expected = new[] { (false, 3), (true, 2) };

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        Assert.Equal(expected, Query(nativeDb));

        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(expected, Query(driverDb));
    }

    [Fact]
    public void GroupBy_operand_with_order_only_before_terminal_select_declines_and_stays_correct()
    {
        // GroupOrderOp without paging: NativeGroupByBinder sets it whenever orderings resolve, so it's reachable
        // independently. Ordering isn't preserved through $unionWith, so only the row set is asserted.
        var collection = SeedCollection(nameof(GroupBy_operand_with_order_only_before_terminal_select_declines_and_stays_correct));

        (int Key, int Count)[] Query(SingleEntityDbContext<Item> db)
            => db.Entities.Where(i => i.Value <= 3).GroupBy(i => i.Value)
                    .OrderBy(g => g.Key)
                    .Select(g => new { g.Key, Count = g.Count() })
                .Concat(db.Entities.Where(i => i.Value >= 3).GroupBy(i => i.Value)
                    .Select(g => new { g.Key, Count = g.Count() }))
                .ToList().OrderBy(x => x.Key).Select(x => (x.Key, x.Count)).ToArray();

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Query(nativeOnlyDb));

        var expected = new[] { (1, 1), (2, 1), (3, 1), (3, 1), (4, 1), (5, 1) };

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        Assert.Equal(expected, Query(nativeDb));

        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(expected, Query(driverDb));
    }

    [Fact]
    public void Distinct_operand_concat_goes_native()
    {
        // Concat doesn't dedup, but operand1's own Distinct still applies in its pre-combine pipeline.
        var collection = SeedCollection(nameof(Distinct_operand_concat_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name }).Distinct()
            .Concat(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        // operand1 = 3 rows, operand2 = 3 rows; Concat keeps both, "Three" twice.
        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Three", "Two" }, result);
    }

    [Fact]
    public void Distinct_operand_union_on_renamed_member_dedups_by_projected_source_not_colliding_entity_property()
    {
        // `new { Value = i.Name }` reuses the entity's int "Value" name for a string source; resolving by name against
        // the entity would crash or compare the wrong field.
        var collection = SeedCollection(
            nameof(Distinct_operand_union_on_renamed_member_dedups_by_projected_source_not_colliding_entity_property));
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { Value = i.Name }).Distinct()
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { Value = i.Name }))
                .ToList().Select(x => x.Value).OrderBy(n => n).ToList();

        var native = Run(nativeDb);
        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Two" }, native); // by Name's value, not the real int Value
        Assert.Equal(Run(driverDb), native);
    }

    // A trailing projection after Intersect goes native.
    [Fact]
    public void Select_after_intersect_goes_native_result_set()
    {
        var collection = SeedCollection(nameof(Select_after_intersect_goes_native_result_set));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; projected to Name.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { i.Name })
            .ToList();
        var single = Assert.Single(result);
        Assert.Equal("Three", single.Name);
    }

    // A trailing member-access Select after a whole-entity set op goes native ($project after the set-op stage).
    [Fact]
    public void Select_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Select_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { N = i.Value })
                .ToList().Select(x => x.N).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([1, 2, 3, 4, 5], native); // {1,2,3} U {3,4,5} deduped, projected to Value
        Assert.Equal(Run(driverDb), native);
    }

    // No driver-LINQ oracle for Intersect/Except: assert the literal expected set.
    [Fact]
    public void Select_after_intersect_goes_native()
    {
        var collection = SeedCollection(nameof(Select_after_intersect_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; projected to Value.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { N = i.Value })
            .ToList();
        Assert.Equal([3], result.Select(x => x.N).OrderBy(v => v));
    }

    // Intersect analog of Computed_leaf_projection_after_union_goes_native.
    [Fact]
    public void Computed_leaf_projection_after_intersect_goes_native_result_set()
    {
        var collection = SeedCollection(nameof(Computed_leaf_projection_after_intersect_goes_native_result_set));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Intersect {3,4,5} = {3}; doubled = {6}.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { Doubled = i.Value * 2 })
            .ToList();
        Assert.Equal([6], result.Select(x => x.Doubled).OrderBy(v => v));
    }

    // A trailing Where composes with a trailing projection: $match lands before $project, both after the set op.
    [Fact]
    public void Where_then_Select_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Where_then_Select_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Where(i => i.Value >= 2).Select(i => new { N = i.Value })
                .ToList().Select(x => x.N).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    // Union dedups whole entities before the projection, so two entities projecting to the same value both survive
    // (as BCL Union(...).Select(...) would). Two items sharing a Name create the collision.
    [Fact]
    public void Union_dedups_entities_then_projects_keeping_duplicate_projected_values()
    {
        var collection = SeedCollectionWithDuplicateNames(nameof(Union_dedups_entities_then_projects_keeping_duplicate_projected_values));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value == 1).Union(db.Entities.Where(i => i.Value == 2))
                .Select(i => new { i.Name })
                .ToList().Select(x => x.Name).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal(2, native.Count); // both distinct entities survive Union's whole-document dedup
        Assert.All(native, n => Assert.Equal("Dup", n)); // and both project to the SAME Name (no accidental dedup)
        Assert.Equal(Run(driverDb).Count, native.Count);
    }

    // A Where through the projection's member, or a second pure member-remapping Select, never reaches the
    // post-projection seam: EF's pending-selector mechanism pushes the predicate before the Select, so these go fully
    // native in every mode.
    [Fact]
    public void Where_after_trailing_projection_on_union_goes_native_via_ef_predicate_pushdown()
    {
        var collection = SeedCollection(nameof(Where_after_trailing_projection_on_union_goes_native_via_ef_predicate_pushdown));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { N = i.Value }).Where(x => x.N >= 2)
                .ToList().Select(x => x.N).OrderBy(v => v).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Where_after_trailing_projection_on_intersect_goes_native_via_ef_predicate_pushdown(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Where_after_trailing_projection_on_intersect_goes_native_via_ef_predicate_pushdown) + mode);
        using var db = Make(collection, mode);
        // {1,2,3} Intersect {3,4,5} = {3}; the pushed-down predicate (Value >= 2) keeps it.
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
            .Select(i => new { N = i.Value }).Where(x => x.N >= 2).ToList();
        var single = Assert.Single(result);
        Assert.Equal(3, single.N);
    }

    // Intersect/Except have no driver-LINQ translation, so explicit DriverLinq hard-fails whatever follows the set op.
    [Fact]
    public void Where_after_trailing_projection_on_intersect_still_hard_fails_under_explicit_DriverLinq()
    {
        var collection = SeedCollection(nameof(Where_after_trailing_projection_on_intersect_still_hard_fails_under_explicit_DriverLinq));
        using var db = Make(collection, MongoQueryMode.DriverLinq);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { N = i.Value }).Where(x => x.N >= 2).ToList());
    }

    [Fact]
    public void Second_projection_after_union_goes_native_because_ef_fuses_the_two_selects()
    {
        var collection = SeedCollection(nameof(Second_projection_after_union_goes_native_because_ef_fuses_the_two_selects));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        var result = nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
            .Select(i => new { N = i.Value }).Select(x => new { M = x.N }).ToList();
        Assert.Equal(5, result.Count); // NativeOnly succeeding proves native — a single fused $project, no seam
        Assert.Equal([1, 2, 3, 4, 5], result.Select(r => r.M).OrderBy(v => v));
    }

    // A bare-scalar trailing projection after a whole-entity set op goes native: dedup runs over whole entities before
    // the $project (contrast Bare_scalar_operand_union_goes_native, where the projected value is the dedup key).
    // NativeBareProjectionTests.Bare_projection_after_a_union_or_concat_goes_native covers shared values.
    [Fact]
    public void Bare_scalar_projection_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Bare_scalar_projection_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        var result = nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
            .Select(i => i.Value).ToList().OrderBy(v => v).ToList();
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    // An arithmetic computed leaf (i.Value * 2) after a whole-entity set op goes native.
    [Fact]
    public void Computed_leaf_projection_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Computed_leaf_projection_after_union_goes_native));

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .Select(i => new { Doubled = i.Value * 2 })
                .ToList().Select(x => x.Doubled).OrderBy(v => v).ToList();

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 4, 6, 8, 10], native); // {1,2,3} U {3,4,5} deduped (5 rows), each doubled

        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(Run(driverDb), native);
    }

    // No Concat().OfType<T>() variant: the driver's ConcatMethodToPipelineTranslator throws NullReferenceException
    // on the fallback leg. The Union variant covers the same guard.

    // Parametrized operand predicate: the operand's nested $unionWith renders into the same PlaceholderTable as the
    // outer query (MongoPipelineFactory.RenderUnionWith), so a captured local must substitute end to end.

    [Fact]
    public void Union_with_parametrized_operand_predicate()
    {
        var collection = SeedCollection(nameof(Union_with_parametrized_operand_predicate));
        var threshold = 3; // captured local -- feeds the SECOND operand's predicate

        var logs = new List<string>();
        using var nativeOnlyDb = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var native = nativeOnlyDb.Entities.Where(i => i.Value <= 2)
            .Union(nativeOnlyDb.Entities.Where(i => i.Value >= threshold))
            .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        Assert.Equal([1, 2, 3, 4, 5], native); // {1,2} U {3,4,5}, disjoint

        var mql = Mql(logs);
        Assert.Contains("$unionWith", mql);

        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);
        var driverResult = driverDb.Entities.Where(i => i.Value <= 2)
            .Union(driverDb.Entities.Where(i => i.Value >= threshold))
            .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        Assert.Equal(driverResult, native);
    }

    [Fact]
    public void Projected_operand_union_goes_native()
    {
        var collection = SeedCollection(nameof(Projected_operand_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        static List<string> Q(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                .ToList()
                .Select(x => x.Name).OrderBy(s => s).ToList();

        var native = Q(nativeOnlyDb);
        Assert.Equal(Q(driverDb), native);
    }

    [Fact]
    public void Projected_operand_concat_goes_native()
    {
        var collection = SeedCollection(nameof(Projected_operand_concat_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        static List<string> Q(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                .Concat(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                .ToList()
                .Select(x => x.Name).OrderBy(s => s).ToList();

        var native = Q(nativeOnlyDb);
        Assert.Equal(Q(driverDb), native);
    }

    [Fact]
    public void Projected_operand_intersect_goes_native_result_set()
    {
        var collection = SeedCollection(nameof(Projected_operand_intersect_goes_native_result_set));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .ToList().Select(x => x.Name).ToList();
        Assert.Equal(new[] { "Three" }, result); // only Value==3 (Name "Three") is in both operands
    }

    [Fact]
    public void Projected_operand_except_goes_native_result_set()
    {
        var collection = SeedCollection(nameof(Projected_operand_except_goes_native_result_set));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Except(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .ToList().Select(x => x.Name).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "One", "Two" }, result); // Value 1,2 (<=3) minus Value 3 (in second) = One, Two
    }

    // A bare-scalar operand (Select(i => i.Name)) populates Projection (NativeProjectionBinder's bare-body admission),
    // so Union goes native. A mixed whole-entity / projected pair doesn't compile, so isn't tested.

    [Fact]
    public void Bare_scalar_operand_union_goes_native()
    {
        var collection = SeedCollection(nameof(Bare_scalar_operand_union_goes_native));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            var namesOnly = nativeOnlyDb.Entities.Select(i => i.Name)
                .Union(nativeOnlyDb.Entities.Select(i => i.Name)).ToList();
            Assert.Equal(5, namesOnly.Count);
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        Assert.Equal(5, nativeDb.Entities.Select(i => i.Name)
            .Union(nativeDb.Entities.Select(i => i.Name)).ToList().Count);
    }

    // A computed-leaf operand (`new { Doubled = i.Value * 2 }`) populates Projection, so the set op goes native;
    // projected-value dedup collapses 10 rows to 5.
    [Fact]
    public void Computed_leaf_operand_union_goes_native()
    {
        var collection = SeedCollection(nameof(Computed_leaf_operand_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        static List<int> Q(SingleEntityDbContext<Item> db) =>
            db.Entities.Select(i => new { Doubled = i.Value * 2 })
                .Union(db.Entities.Select(i => new { Doubled = i.Value * 2 }))
                .ToList().Select(x => x.Doubled).OrderBy(v => v).ToList();

        var native = Q(nativeOnlyDb);
        Assert.Equal([2, 4, 6, 8, 10], native);
        Assert.Equal(Q(driverDb), native);
    }

    // Composition directly after a projected-operand set op is always terminal (Projection.Count > 0 at attach) and
    // rejected by every post-terminal entry point. Each shape must give the correct result or throw, never wrong data:
    // Where/OrderBy/Skip/Take/Count/GroupBy/chained Union/Distinct fall back gracefully (NativeOnly throws; Native and
    // DriverLinq are correct; Distinct declines in NativeGroupByBinder.TryBindDistinctFromProjection); a second Select
    // and Intersect + Where hard-fail in every mode. Hard-fail cases assert only ThrowsAny<Exception>.

    [Fact]
    public void Where_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Where_after_projected_operand_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .Where(x => x.Value > 2).ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .Where(x => x.Value > 2)
                .ToList().Select(x => x.Value).OrderBy(v => v).ToList();

        var native = Run(nativeDb);
        Assert.Equal([3, 4, 5], native); // {1,2,3} U {3,4,5} deduped = {1,2,3,4,5}; Value>2 = {3,4,5}
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void OrderBy_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(OrderBy_after_projected_operand_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .OrderBy(x => x.Value).ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .OrderBy(x => x.Value)
                .ToList().Select(x => x.Value).ToList();

        var native = Run(nativeDb);
        Assert.Equal([1, 2, 3, 4, 5], native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Paging_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Paging_after_projected_operand_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .OrderBy(x => x.Value).Skip(1).ToList());
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .OrderBy(x => x.Value).Take(2).ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> RunSkip(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .OrderBy(x => x.Value).Skip(1)
                .ToList().Select(x => x.Value).ToList();

        List<int> RunTake(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .OrderBy(x => x.Value).Take(2)
                .ToList().Select(x => x.Value).ToList();

        var nativeSkip = RunSkip(nativeDb);
        Assert.Equal([2, 3, 4, 5], nativeSkip);
        Assert.Equal(RunSkip(driverDb), nativeSkip);

        var nativeTake = RunTake(nativeDb);
        Assert.Equal([1, 2], nativeTake);
        Assert.Equal(RunTake(driverDb), nativeTake);
    }

    [Fact]
    public void Count_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Count_after_projected_operand_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .Count());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .Count();

        var native = Run(nativeDb);
        Assert.Equal(5, native);
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void GroupBy_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(GroupBy_after_projected_operand_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() }).ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<(int Key, int Count)> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .GroupBy(x => x.Value).Select(g => new { g.Key, Count = g.Count() })
                .ToList().Select(g => (g.Key, g.Count)).OrderBy(g => g.Key).ToList();

        var native = Run(nativeDb);
        Assert.Equal(5, native.Count);
        Assert.All(native, g => Assert.Equal(1, g.Count)); // 5 distinct Values, one row each
        Assert.Equal(Run(driverDb), native);
    }

    [Fact]
    public void Chained_set_op_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Chained_set_op_after_projected_operand_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value == 1).Select(i => new { i.Name, i.Value }))
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .Union(db.Entities.Where(i => i.Value == 1).Select(i => new { i.Name, i.Value }))
                .ToList().Select(x => x.Value).OrderBy(v => v).ToList();

        var native = Run(nativeDb);
        Assert.Equal([1, 2, 3, 4, 5], native); // third operand ({1}) already present, no change
        Assert.Equal(Run(driverDb), native);
    }

    // Distinct atop a projected-operand Union must fall back: select.Projection is operand 1's own projection, and
    // TryBindDistinctFromProjection overwriting it with $group flatten-refs would corrupt operand 1's $project. The
    // `select.SetOperation is { OperandsProjected: true }` guard declines instead.
    [Fact]
    public void Distinct_after_projected_operand_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Distinct_after_projected_operand_union_falls_back_gracefully));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                    .Distinct().ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        int Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .Distinct().ToList().Count;

        var native = Run(nativeDb);
        Assert.Equal(5, native); // 5 distinct entities -> 5 distinct projected rows, none collide
        Assert.Equal(Run(driverDb), native);
    }

    // The guard is kind-agnostic, so it declines for Concat too (6 rows, 5 after Distinct). NativeOnly throwing proves the decline.
    [Fact]
    public void Distinct_after_projected_operand_concat_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Distinct_after_projected_operand_concat_falls_back_gracefully));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                    .Concat(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                    .Distinct().ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        List<string> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                .Concat(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                .Distinct().ToList().Select(x => x.Name).OrderBy(n => n).ToList();

        var native = Run(nativeDb);
        // Concat's 6 rows (One,Two,Three,Three,Four,Five) deduped by the trailing Distinct -> 5 distinct names.
        Assert.Equal(new[] { "Five", "Four", "One", "Three", "Two" }, native);
        Assert.Equal(Run(driverDb), native);
    }

    // No driver-LINQ oracle for Intersect: the same Distinct guard's decline hard-fails in every mode.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Distinct_after_projected_operand_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Distinct_after_projected_operand_intersect_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                .Distinct().ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Distinct_after_projected_operand_except_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Distinct_after_projected_operand_except_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                .Except(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                .Distinct().ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Second_select_after_projected_operand_union_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Second_select_after_projected_operand_union_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .Select(x => new { x.Value }).ToList());
    }

    // Post-composition after a projected-operand Intersect hard-fails in every mode (see Bare_scalar_operand_intersect_goes_native).
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Op_after_projected_operand_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Op_after_projected_operand_intersect_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value })
                .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
                .Where(x => x.Value > 2).ToList());
    }

    // Left-nested whole-entity Concat/Union chain. A.Concat(B).Concat(C) hands the outer set op a source1 that already
    // carries the inner one; the lowerer emits one $unionWith per link, each Union link's dedup right after its own.
    // Operand sets make duplicate counts distinguish the orderings:
    //   A = Value <= 2  -> {1,2}      B = Value >= 2 -> {2,3,4,5}      C = Value == 2 -> {2}

    [Fact]
    public void Concat_chain_goes_native()
    {
        var collection = SeedCollection(nameof(Concat_chain_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 2))
            .Concat(db.Entities.Where(i => i.Value == 2))
            .ToList();

        // {1,2} + {2,3,4,5} + {2} — no dedup anywhere, so Value==2 appears three times.
        Assert.Equal([1, 2, 2, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));

        var mql = Mql(logs);
        Assert.Equal(2, CountOccurrences(mql, "$unionWith"));
        Assert.DoesNotContain("$group", mql); // no dedup stage anywhere in an all-Concat chain
    }

    [Fact]
    public void Union_chain_goes_native()
    {
        var collection = SeedCollection(nameof(Union_chain_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 2)
            .Union(db.Entities.Where(i => i.Value >= 2))
            .Union(db.Entities.Where(i => i.Value == 2))
            .ToList();

        Assert.Equal([1, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));

        var mql = Mql(logs);
        Assert.Equal(2, CountOccurrences(mql, "$unionWith"));
        // One dedup per Union link, right after its own $unionWith. Redundant here (dedup is idempotent), but it is what makes the mixed chain correct.
        Assert.Equal(2, CountOccurrences(mql, "$replaceRoot"));
    }

    // Concat(Union(A,B), C) must dedup A,B before C joins; hoisting the dedup after the chain would give 5 rows
    // instead of 6.
    [Fact]
    public void Union_then_Concat_chain_dedups_only_the_inner_union()
    {
        var collection = SeedCollection(nameof(Union_then_Concat_chain_dedups_only_the_inner_union));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 2)
            .Union(db.Entities.Where(i => i.Value >= 2))
            .Concat(db.Entities.Where(i => i.Value == 2))
            .ToList();

        var seed = SeedItems();
        var oracle = seed.Where(i => i.Value <= 2)
            .Union(seed.Where(i => i.Value >= 2))
            .Concat(seed.Where(i => i.Value == 2))
            .Select(i => i.Value).OrderBy(v => v);

        Assert.Equal([1, 2, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(oracle, result.Select(i => i.Value).OrderBy(v => v));

        var mql = Mql(logs);
        Assert.Equal(2, CountOccurrences(mql, "$unionWith"));
        Assert.Equal(1, CountOccurrences(mql, "$replaceRoot")); // only the Union link dedups
    }

    [Fact]
    public void Concat_then_Union_chain_dedups_the_whole_result()
    {
        var collection = SeedCollection(nameof(Concat_then_Union_chain_dedups_the_whole_result));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 2))
            .Union(db.Entities.Where(i => i.Value == 2))
            .ToList();

        // The trailing Union's dedup runs over the already-concatenated stream, collapsing the duplicate
        // Value==2 the Concat introduced.
        Assert.Equal([1, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));

        var mql = Mql(logs);
        Assert.Equal(2, CountOccurrences(mql, "$unionWith"));
        Assert.Equal(1, CountOccurrences(mql, "$replaceRoot"));
    }

    [Fact]
    public void Chain_with_a_whole_entity_Distinct_operand_goes_native()
    {
        var collection = SeedCollection(nameof(Chain_with_a_whole_entity_Distinct_operand_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // The spec suite's Nested_concat_with_distinct_in_the_middle_and_pruning: a whole-entity Distinct() on the middle
        // operand is an ordinary MongoDistinctOp in its PipelineOps.
        var result = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 2).Distinct())
            .Concat(db.Entities.Where(i => i.Value == 2))
            .ToList();

        Assert.Equal([1, 2, 2, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(2, CountOccurrences(Mql(logs), "$unionWith"));
    }

    // RIGHT-nested whole-entity set ops. A.Concat(B.Union(C)) cannot be flattened into a left chain (that would dedup
    // A's rows too), so the operand keeps its own chain and is emitted as a $unionWith nested inside the outer one.

    [Fact]
    public void Union_inside_Concat_goes_native()
    {
        var collection = SeedCollection(nameof(Union_inside_Concat_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // A = {1,2}; inner Union(B={2,3,4,5}, C={2}) = {2,3,4,5}; outer Concat keeps A's own Value==2.
        var result = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 2).Union(db.Entities.Where(i => i.Value == 2)))
            .ToList();

        var seed = SeedItems();
        var oracle = seed.Where(i => i.Value <= 2)
            .Concat(seed.Where(i => i.Value >= 2).Union(seed.Where(i => i.Value == 2)))
            .Select(i => i.Value).OrderBy(v => v);

        Assert.Equal([1, 2, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(oracle, result.Select(i => i.Value).OrderBy(v => v));

        // The inner $unionWith and its dedup sit inside the outer pipeline; a dedup at its end would wrongly dedup A's rows.
        var mql = Mql(logs);
        var outerUnion = mql.IndexOf("$unionWith", StringComparison.Ordinal);
        var innerUnion = mql.IndexOf("$unionWith", outerUnion + 1, StringComparison.Ordinal);
        var dedup = mql.IndexOf("$replaceRoot", StringComparison.Ordinal);
        Assert.True(innerUnion > outerUnion, "expected a nested $unionWith");
        Assert.True(dedup > innerUnion, "expected the dedup to follow the INNER $unionWith");
        Assert.Equal(1, CountOccurrences(mql, "$replaceRoot")); // exactly one dedup, the inner Union's
    }

    // The flattening trap as a result: Concat(A, Union(B,C)) and Union(Concat(A,B), C) differ (6 rows vs 5).
    [Fact]
    public void Right_nested_union_is_not_flattened_into_a_left_chain()
    {
        var collection = SeedCollection(nameof(Right_nested_union_is_not_flattened_into_a_left_chain));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var rightNested = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 2).Union(db.Entities.Where(i => i.Value == 2)))
            .ToList();

        var leftChain = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 2))
            .Union(db.Entities.Where(i => i.Value == 2))
            .ToList();

        Assert.Equal(6, rightNested.Count);
        Assert.Equal(5, leftChain.Count);
    }

    [Fact]
    public void Nesting_on_both_sides_goes_native()
    {
        var collection = SeedCollection(nameof(Nesting_on_both_sides_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // A left chain whose NEXT link's operand is itself a set op — the two directions composing.
        var result = db.Entities.Where(i => i.Value == 1)
            .Concat(db.Entities.Where(i => i.Value == 2))
            .Concat(db.Entities.Where(i => i.Value >= 4).Union(db.Entities.Where(i => i.Value == 5)))
            .ToList();

        var seed = SeedItems();
        var oracle = seed.Where(i => i.Value == 1)
            .Concat(seed.Where(i => i.Value == 2))
            .Concat(seed.Where(i => i.Value >= 4).Union(seed.Where(i => i.Value == 5)))
            .Select(i => i.Value).OrderBy(v => v);

        Assert.Equal([1, 2, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(oracle, result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(3, CountOccurrences(Mql(logs), "$unionWith")); // two top-level links + one nested
    }

    [Fact]
    public void Deeply_right_nested_chain_goes_native()
    {
        var collection = SeedCollection(nameof(Deeply_right_nested_chain_goes_native));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // Three levels: the recursion has no depth limit (IsWholeEntitySetOpOperandSelect / AppendSetOpOperandStages).
        var result = db.Entities.Where(i => i.Value == 1)
            .Concat(db.Entities.Where(i => i.Value == 2)
                .Concat(db.Entities.Where(i => i.Value == 3)
                    .Union(db.Entities.Where(i => i.Value >= 4))))
            .ToList();

        Assert.Equal([1, 2, 3, 4, 5], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(3, CountOccurrences(Mql(logs), "$unionWith"));
    }

    [Fact]
    public void Right_nested_operand_with_a_parameter_substitutes_correctly()
    {
        var collection = SeedCollection(nameof(Right_nested_operand_with_a_parameter_substitutes_correctly));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        // The nested operand shares the outer placeholder table, so a captured variable inside it must still
        // substitute at Build time.
        var threshold = 4;
        var result = db.Entities.Where(i => i.Value == 1)
            .Concat(db.Entities.Where(i => i.Value >= threshold).Union(db.Entities.Where(i => i.Value == 5)))
            .ToList();

        Assert.Equal([1, 4, 5], result.Select(i => i.Value).OrderBy(v => v));

        threshold = 5;
        var reRun = db.Entities.Where(i => i.Value == 1)
            .Concat(db.Entities.Where(i => i.Value >= threshold).Union(db.Entities.Where(i => i.Value == 5)))
            .ToList();

        Assert.Equal([1, 5], reRun.Select(i => i.Value).OrderBy(v => v));
    }

    // Paging BETWEEN two links (per-link PrecedingOps). Ops recorded after one link and before the next land in
    // TrailingOps but belong BEFORE the new link; AppendSetOperation hands them to the new link as PrecedingOps.
    //   A = Value <= 2 -> {1,2}     B = Value >= 4 -> {4,5}     C = Value == 3 -> {3}
    // A.Union(B).OrderBy(Value).Take(2) = {1,2}, then .Union(C) = {1,2,3} (three rows); a misplaced Take gives two.

    [Fact]
    public void Take_between_two_set_ops_pages_the_partial_combine()
    {
        var collection = SeedCollection(nameof(Take_between_two_set_ops_pages_the_partial_combine));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        var result = db.Entities.Where(i => i.Value <= 2)
            .Union(db.Entities.Where(i => i.Value >= 4))
            .OrderBy(i => i.Value).Take(2)
            .Union(db.Entities.Where(i => i.Value == 3))
            .ToList();

        var seed = SeedItems();
        var oracle = seed.Where(i => i.Value <= 2)
            .Union(seed.Where(i => i.Value >= 4))
            .OrderBy(i => i.Value).Take(2)
            .Union(seed.Where(i => i.Value == 3))
            .Select(i => i.Value).OrderBy(v => v);

        Assert.Equal([1, 2, 3], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(oracle, result.Select(i => i.Value).OrderBy(v => v));

        // The $limit sits BETWEEN the two $unionWith stages, not after both.
        var mql = Mql(logs);
        var firstUnion = mql.IndexOf("$unionWith", StringComparison.Ordinal);
        var secondUnion = mql.IndexOf("$unionWith", firstUnion + 1, StringComparison.Ordinal);
        var limit = mql.IndexOf("$limit", StringComparison.Ordinal);
        Assert.InRange(limit, firstUnion, secondUnion);
    }

    [Fact]
    public void Take_after_the_last_link_still_pages_the_whole_combine()
    {
        var collection = SeedCollection(nameof(Take_after_the_last_link_still_pages_the_whole_combine));
        var logs = new List<string>();
        using var db = MakeWithLogs(collection, MongoQueryMode.NativeOnly, logs);

        // With no op recorded between the links, TrailingOps still means "after the LAST link".
        var result = db.Entities.Where(i => i.Value <= 2)
            .Union(db.Entities.Where(i => i.Value >= 4))
            .Union(db.Entities.Where(i => i.Value == 3))
            .OrderBy(i => i.Value).Take(2)
            .ToList();

        Assert.Equal([1, 2], result.Select(i => i.Value));

        var mql = Mql(logs);
        Assert.True(
            mql.LastIndexOf("$limit", StringComparison.Ordinal)
            > mql.LastIndexOf("$unionWith", StringComparison.Ordinal),
            "expected the $limit after both $unionWith stages");
    }

    [Fact]
    public void Paging_between_every_link_of_a_three_link_chain()
    {
        var collection = SeedCollection(nameof(Paging_between_every_link_of_a_three_link_chain));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.Where(i => i.Value == 1)
            .Union(db.Entities.Where(i => i.Value == 2))
            .OrderBy(i => i.Value).Take(2)
            .Union(db.Entities.Where(i => i.Value == 3))
            .OrderBy(i => i.Value).Take(3)
            .Union(db.Entities.Where(i => i.Value == 4))
            .OrderBy(i => i.Value).Take(4)
            .ToList();

        var seed = SeedItems();
        var oracle = seed.Where(i => i.Value == 1)
            .Union(seed.Where(i => i.Value == 2))
            .OrderBy(i => i.Value).Take(2)
            .Union(seed.Where(i => i.Value == 3))
            .OrderBy(i => i.Value).Take(3)
            .Union(seed.Where(i => i.Value == 4))
            .OrderBy(i => i.Value).Take(4)
            .Select(i => i.Value);

        Assert.Equal([1, 2, 3, 4], result.Select(i => i.Value));
        Assert.Equal(oracle, result.Select(i => i.Value));
    }

    [Fact]
    public void Right_nested_operand_with_its_own_trailing_paging_goes_native()
    {
        var collection = SeedCollection(nameof(Right_nested_operand_with_its_own_trailing_paging_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        // The operand's own post-combine paging closes out ITS nested sub-pipeline, mirroring where the
        // outer select's TrailingOps land in the outer pipeline.
        var result = db.Entities.Where(i => i.Value <= 2)
            .Concat(db.Entities.Where(i => i.Value >= 4)
                .Union(db.Entities.Where(i => i.Value == 3))
                .OrderBy(i => i.Value).Take(1))
            .ToList();

        var seed = SeedItems();
        var oracle = seed.Where(i => i.Value <= 2)
            .Concat(seed.Where(i => i.Value >= 4)
                .Union(seed.Where(i => i.Value == 3))
                .OrderBy(i => i.Value).Take(1))
            .Select(i => i.Value).OrderBy(v => v);

        Assert.Equal([1, 2, 3], result.Select(i => i.Value).OrderBy(v => v));
        Assert.Equal(oracle, result.Select(i => i.Value).OrderBy(v => v));
    }

    [Fact]
    public void Intersect_after_a_set_op_is_not_chained()
    {
        var collection = SeedCollection(nameof(Intersect_after_a_set_op_is_not_chained));
        using var db = Make(collection, MongoQueryMode.Native);

        // Intersect/Except lower to MongoSetDifferenceStage with no driver-LINQ oracle, so they're excluded from chains and hard-fail.
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 2)
                .Union(db.Entities.Where(i => i.Value >= 2))
                .Intersect(db.Entities.Where(i => i.Value == 2))
                .ToList());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void GroupBy_after_Union_goes_native(MongoQueryMode mode)
    {
        // Union_simple_groupby's shape. The Union dedups Value==3, giving 5 items with unique Names, so
        // GroupBy(Name) yields 5 groups of 1.
        var collection = SeedCollection(nameof(GroupBy_after_Union_goes_native) + mode);
        using var db = Make(collection, mode);

        // Sort client-side: an OrderBy after GroupBy(key).Select(aggregate) hits NativeSlotPopulator's post-group guard (orthogonal limitation).
        var result = db.Entities
            .Where(i => i.Value <= 3)
            .Union(db.Entities.Where(i => i.Value >= 3))
            .GroupBy(i => i.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToArray();

        Assert.Equal(5, result.Length);
        Assert.All(result, r => Assert.Equal(1, r.Total));
        Assert.Equal(["Five", "Four", "One", "Three", "Two"], result.Select(r => r.Key).ToArray());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Union_then_GroupBy_OrderBy_Skip_Select_goes_native(MongoQueryMode mode)
    {
        // GroupBy after a Union plus OrderBy/Skip on the bare GroupBy result before the terminal Select. Ordered
        // Five, Four, One, Three, Two; Skip(1) drops "Five".
        var collection = SeedCollection(nameof(Union_then_GroupBy_OrderBy_Skip_Select_goes_native) + mode);
        using var db = Make(collection, mode);

        var result = db.Entities
            .Where(i => i.Value <= 3)
            .Union(db.Entities.Where(i => i.Value >= 3))
            .GroupBy(i => i.Name)
            .OrderBy(g => g.Key)
            .Skip(1)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToArray();

        Assert.Equal(
            [("Four", 1), ("One", 1), ("Three", 1), ("Two", 1)],
            result.Select(r => (r.Key, r.Total)).ToArray());
    }

    [Fact]
    public void GroupBy_after_Union_of_plain_projected_operand_declines_and_stays_correct()
    {
        // A plain projected operand (OperandsProjected: true, no operand-side Grouping) declines via
        // `hadTerminalGrouping && !hasFinalizedPriorGrouping`; the fallback is correct.
        var collection = SeedCollection(nameof(GroupBy_after_Union_of_plain_projected_operand_declines_and_stays_correct));

        (string Key, int Total)[] Query(SingleEntityDbContext<Item> db)
            => db.Entities.Select(i => new { i.Name })
                .Union(db.Entities.Select(i => new { i.Name }))
                .GroupBy(x => x.Name)
                .Select(g => new { g.Key, Total = g.Count() })
                .AsEnumerable()
                .Select(r => (r.Key, r.Total))
                .ToArray();

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => Query(nativeOnlyDb));

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = Query(nativeDb);
        Assert.Equal(5, result.Length);
        Assert.All(result, r => Assert.Equal(1, r.Total));
    }

    // A Grouping-bearing source1 has its $group/$project snapshotted into PriorGrouping when an outer GroupBy composes
    // after the set op. The lowerer must emit source1's own $group/$project before $unionWith and the outer $group
    // after it (the reverse failed with "Document element '...' is missing but required").
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_after_Union_of_projected_Distinct_operand_returns_correct_groups(MongoQueryMode mode)
    {
        // Operand 1 is a projected Distinct ({1,2,3} -> 3 names); operand 2 is a plain projection ({3,4,5}).
        // Union dedups the shared "Three", so GroupBy(Name) yields 5 groups of 1.
        var collection = SeedCollection(nameof(GroupBy_after_Union_of_projected_Distinct_operand_returns_correct_groups) + mode);
        using var db = Make(collection, mode);

        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name }).Distinct()
            .Union(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .GroupBy(x => x.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Total))
            .ToArray();

        Assert.Equal(
            [("Five", 1), ("Four", 1), ("One", 1), ("Three", 1), ("Two", 1)],
            result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void GroupBy_after_Concat_of_projected_Distinct_operand_counts_the_duplicate(MongoQueryMode mode)
    {
        // Concat keeps the "Three" from both operands, so its group has 2 rows. Grouping by a different
        // member than the Distinct also proves the outer $group runs over the combined, flattened rows.
        var collection = SeedCollection(nameof(GroupBy_after_Concat_of_projected_Distinct_operand_counts_the_duplicate) + mode);
        using var db = Make(collection, mode);

        var result = db.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name, i.Value }).Distinct()
            .Concat(db.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name, i.Value }))
            .GroupBy(x => x.Value)
            .Select(g => new { g.Key, Total = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .Select(r => (r.Key, r.Total))
            .ToArray();

        Assert.Equal([(1, 1), (2, 1), (3, 2), (4, 1), (5, 1)], result);
    }

    // Variants of the Grouping-bearing-source1 + outer GroupBy shape; each must match in-memory LINQ.
    public static TheoryData<string, Func<IQueryable<Item>, IQueryable<Item>, IEnumerable<(int Key, int Total)>>>
        OuterGroupByOverGroupedSource1Shapes()
        => new()
        {
            {
                "GroupBy operand, outer GroupBy by Count",
                (a, b) => a.Where(i => i.Value <= 3).GroupBy(i => i.Value).Select(g => new { g.Key, Count = g.Count() })
                    .Union(b.Where(i => i.Value >= 2).GroupBy(i => i.Value).Select(g => new { g.Key, Count = g.Count() }))
                    .GroupBy(x => x.Count).Select(g => new { g.Key, Total = g.Count() })
                    .AsEnumerable().Select(r => (r.Key, r.Total))
            },
            {
                "Distinct operand, Where between Distinct and Union",
                (a, b) => a.Select(i => new { i.Value }).Distinct().Where(x => x.Value > 1)
                    .Concat(b.Where(i => i.Value >= 4).Select(i => new { i.Value }))
                    .GroupBy(x => x.Value).Select(g => new { g.Key, Total = g.Count() })
                    .AsEnumerable().Select(r => (r.Key, r.Total))
            },
            {
                "Distinct operand, Where between Union and GroupBy",
                (a, b) => a.Select(i => new { i.Value }).Distinct()
                    .Concat(b.Where(i => i.Value >= 4).Select(i => new { i.Value }))
                    .Where(x => x.Value != 2)
                    .GroupBy(x => x.Value).Select(g => new { g.Key, Total = g.Count() })
                    .AsEnumerable().Select(r => (r.Key, r.Total))
            },
            {
                "Distinct operand, outer group ordering and paging",
                (a, b) => a.Select(i => new { i.Value }).Distinct()
                    .Concat(b.Where(i => i.Value >= 4).Select(i => new { i.Value }))
                    .GroupBy(x => x.Value).OrderByDescending(g => g.Key).Skip(1)
                    .Select(g => new { g.Key, Total = g.Count() })
                    .AsEnumerable().Select(r => (r.Key, r.Total))
            },
            {
                "Distinct operand, outer group HAVING",
                (a, b) => a.Select(i => new { i.Value }).Distinct()
                    .Concat(b.Where(i => i.Value >= 4).Select(i => new { i.Value }))
                    .GroupBy(x => x.Value).Where(g => g.Count() > 1)
                    .Select(g => new { g.Key, Total = g.Count() })
                    .AsEnumerable().Select(r => (r.Key, r.Total))
            },
            {
                "Distinct operand, Intersect then GroupBy",
                (a, b) => a.Select(i => new { i.Value }).Distinct()
                    .Intersect(b.Where(i => i.Value >= 3).Select(i => new { i.Value }))
                    .GroupBy(x => x.Value).Select(g => new { g.Key, Total = g.Count() })
                    .AsEnumerable().Select(r => (r.Key, r.Total))
            },
        };

    [Theory]
    [MemberData(nameof(OuterGroupByOverGroupedSource1Shapes))]
    public void Outer_GroupBy_over_grouped_source1_set_op_matches_in_memory(
        string name,
        Func<IQueryable<Item>, IQueryable<Item>, IEnumerable<(int Key, int Total)>> query)
    {
        var collection = SeedCollection(nameof(Outer_GroupBy_over_grouped_source1_set_op_matches_in_memory) + name.GetHashCode());
        var items = SeedItems().AsQueryable();
        var expected = query(items, items).OrderBy(r => r.Key).ToArray();

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        Assert.Equal(expected, query(nativeDb.Entities, nativeDb.Entities).OrderBy(r => r.Key).ToArray());

        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        (int Key, int Total)[]? nativeOnly = null;
        try
        {
            nativeOnly = query(nativeOnlyDb.Entities, nativeOnlyDb.Entities).OrderBy(r => r.Key).ToArray();
        }
        catch (NativeTranslationNotSupportedException)
        {
            // Declined to the fallback; the Native assertion above covers correctness.
        }

        if (nativeOnly != null)
            Assert.Equal(expected, nativeOnly);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Where_between_projected_Distinct_and_Concat_filters_source1(MongoQueryMode mode)
    {
        // The Where after the Distinct lands in source1's PostGroupOps and must run before the $unionWith.
        var collection = SeedCollection(nameof(Where_between_projected_Distinct_and_Concat_filters_source1) + mode);
        using var db = Make(collection, mode);

        var result = db.Entities.Select(i => new { i.Value }).Distinct().Where(x => x.Value > 1)
            .Concat(db.Entities.Where(i => i.Value >= 4).Select(i => new { i.Value }))
            .AsEnumerable().Select(x => x.Value).OrderBy(v => v).ToArray();

        Assert.Equal([2, 3, 4, 4, 5, 5], result);
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Ops_after_projected_Distinct_in_second_operand_filter_that_operand(MongoQueryMode mode)
    {
        // The second operand's post-Distinct Where/OrderBy/Take must run inside its $unionWith pipeline.
        var collection = SeedCollection(nameof(Ops_after_projected_Distinct_in_second_operand_filter_that_operand) + mode);
        using var db = Make(collection, mode);

        var result = db.Entities.Where(i => i.Value <= 2).Select(i => new { i.Value })
            .Concat(db.Entities.Select(i => new { i.Value }).Distinct().Where(x => x.Value > 1)
                .OrderByDescending(x => x.Value).Take(2))
            .AsEnumerable().Select(x => x.Value).OrderBy(v => v).ToArray();

        Assert.Equal([1, 2, 4, 5], result);
    }

    [Fact]
    public void Where_after_GroupBy_operand_before_Union_filters_that_operand()
    {
        // Declines today (a Where after GroupBy.Select isn't admitted as a set-op operand); pins the result.
        var collection = SeedCollection(nameof(Where_after_GroupBy_operand_before_Union_filters_that_operand));
        using var db = Make(collection, MongoQueryMode.Native);

        var result = db.Entities.GroupBy(i => i.Value).Select(g => new { g.Key, Count = g.Count() })
            .Where(x => x.Key >= 4)
            .Union(db.Entities.GroupBy(i => i.Value).Select(g => new { g.Key, Count = g.Count() })
                .Where(x => x.Key == 1))
            .AsEnumerable().Select(x => x.Key).OrderBy(v => v).ToArray();

        Assert.Equal([1, 4, 5], result);
    }

    [Fact]
    public void GroupBy_after_Intersect_goes_native()
    {
        // {1,2,3} ∩ {2,3,4} = {2,3} — 2 groups of 1. IsSetOpTerminalOnly covers every set-op kind, so GroupBy after
        // Intersect goes native too.
        var collection = SeedCollection(nameof(GroupBy_after_Intersect_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities
            .Where(i => i.Value <= 3)
            .Intersect(db.Entities.Where(i => i.Value >= 2 && i.Value <= 4))
            .GroupBy(i => i.Name)
            .Select(g => new { g.Key, Total = g.Count() })
            .AsEnumerable()
            .OrderBy(r => r.Key)
            .ToArray();

        Assert.Equal(
            [("Three", 1), ("Two", 1)],
            result.Select(r => (r.Key, r.Total)).ToArray());
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
