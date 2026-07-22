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
/// EF-347 (Task 4, slice 2) native <c>Union</c>/<c>Concat</c> -&gt; a <c>$unionWith</c> stage (<c>Union</c>
/// additionally dedups via <c>$group{_id:"$$ROOT"}</c> + <c>$replaceRoot</c>), and (Task 2 of the
/// Intersect/Except sub-project) native <c>Intersect</c>/<c>Except</c> -&gt; a source-tagging
/// <c>$unionWith</c> pipeline (each side deduped and tagged, re-unified by full document, discriminated by
/// a final <c>$match</c>, unwrapped via <c>$replaceRoot</c>). Proves that a supported whole-entity, terminal
/// <c>Union</c>/<c>Concat</c>/<c>Intersect</c>/<c>Except</c> over the SAME entity type executes as a native
/// aggregation pipeline (rather than falling back to driver-LINQ, or -- for Intersect/Except -- hard-failing
/// translation), with correct dedup/duplicate-keeping/intersection/difference semantics, and that
/// out-of-scope shapes (a projected union, an operand carrying an <c>Include</c>, and mismatched operand
/// entity types) fall back to driver-LINQ gracefully -- correct results under
/// <see cref="MongoQueryMode.Native"/>, throwing <see cref="NativeTranslationNotSupportedException"/> only
/// under <see cref="MongoQueryMode.NativeOnly"/> (the "went native" signal). Unlike plain filter/sort/paging
/// shapes (see the Query area AGENTS.md "MQL shape cannot prove native" pitfall), the
/// <c>$unionWith</c>/<c>$group</c>/<c>$replaceRoot</c> shape IS distinctive versus the driver-LINQ fallback,
/// so both the MQL shape AND the <c>NativeOnly</c> signal are asserted for the native-success tests. Unlike
/// Union/Concat, an out-of-scope Intersect/Except shape has NO driver-LINQ fallback at all (confirmed by a
/// prior spike), so it hard-fails translation in every mode rather than falling back gracefully.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSetOpsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    // Public (not private): IntersectComposedOps's MemberData exposes Func<IQueryable<Item>, object> on a
    // public test method parameter, which requires Item to be at least as accessible as that method.
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Value { get; set; }
    }

    // Values 1..5; Where(<=3) and Where(>=3) overlap on the Value==3 document so Union dedups it away
    // (5 distinct rows) while Concat keeps the duplicate (6 rows).
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

    // Two DISTINCT entities (different _id/Value) sharing the same Name — used to prove Union dedups by
    // WHOLE ENTITY, not by a projected member, so both survive a trailing member-access Select.
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

    // ── Native success (the "went native" proof): NativeOnly succeeds + the emitted MQL has the
    // distinctive $unionWith (+ $group/$replaceRoot dedup for Union only) shape ─────────────────────

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

        // Set op stays TERMINAL (a queryable .OrderBy after it composes past the terminal gate and would
        // fall back / throw under NativeOnly). Result order is not guaranteed, so sort the materialized list.
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

    // ── Intersect/Except result-set correctness (EF-347 Task 3): NO driver-LINQ oracle exists for these
    // operators (confirmed by a prior spike), so results are verified against expected in-memory
    // (LINQ-to-Objects) data rather than Native == DriverLinq parity. The set op MUST stay TERMINAL in
    // every one of these -- a queryable .OrderBy/.Where/.Count() applied AFTER Intersect/Except composes
    // past the terminal gate, falls back, and the driver can't do Intersect/Except either, so it throws
    // (see the composition-seam tests below). Sort the MATERIALIZED list in memory instead. ─────────────

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
        using var db = Make(collection, MongoQueryMode.NativeOnly); // NativeOnly => proves it went native (would throw on fallback)
        var threshold = 3;
        var result = db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= threshold)).ToList();
        Assert.Equal([3], result.Select(i => i.Value).OrderBy(v => v)); // captured `threshold` substitutes inside the operand pipeline
    }

    // ── Guard decline: out-of-scope Intersect/Except must hard-fail in EVERY mode (no graceful fallback --
    // there is no driver-LINQ oracle for Intersect/Except at all) ──────────────────────────────────────

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Projected_intersect_hard_fails_in_every_mode(MongoQueryMode mode)
    {
        var collection = SeedCollection(nameof(Projected_intersect_hard_fails_in_every_mode) + mode);
        using var db = Make(collection, mode);
        Assert.ThrowsAny<Exception>(() =>
            db.Entities.Where(i => i.Value <= 3).Select(i => i.Value)
                .Intersect(db.Entities.Where(i => i.Value >= 3).Select(i => i.Value)).ToList());
    }

    // EF-347 slice B: a trailing Where after Except now goes native (no driver-LINQ baseline for
    // Intersect/Except, so assert the result set vs expected in-memory data + prove native via NativeOnly).
    [Fact]
    public void Except_then_Where_goes_native()
    {
        var collection = SeedCollection(nameof(Except_then_Where_goes_native));
        using var db = Make(collection, MongoQueryMode.NativeOnly);
        // {1,2,3} Except {3,4,5} = {1,2}; then Where(Value >= 2) = {2}. If the $match wrongly emitted BEFORE
        // the set-difference stage this would still be {2} by coincidence, so this test is backed by the
        // seam-discriminating paging/Count tests below — it exists to prove Except+Where goes native at all.
        var result = db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3))
            .Where(i => i.Value >= 2).ToList();
        Assert.Equal([2], result.Select(i => i.Value).OrderBy(v => v));
    }

    // ── Composition-seam hard-fail (EF-347 Task 3): the IsSetOp terminal gate rejects every operator
    // composed after Intersect/Except. Because there is no driver fallback for Intersect/Except, these
    // hard-fail in every mode rather than falling back gracefully like the Union/Concat seam tests above. ──

    public static IEnumerable<object[]> IntersectComposedOps() => new[]
    {
        // Only DEFERRED operators remain here (EF-347 slice B): GroupBy after a set op still hard-fails.
        // OrderBy/Skip/Take moved to Intersect_then_paging_goes_native below.
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

    // EF-347 slice B review follow-up: exercises the NativeCardinalityBinder.TryBindReducer HasLimit guard
    // when a Take already recorded a trailing limit before the reducer runs -- HasLimit deliberately scans
    // PipelineOps only, not TrailingOps, so it does not see this preceding Take and the reducer appends its
    // OWN trailing $limit alongside it (two consecutive $limit stages, which compose correctly). Union has a
    // driver-LINQ baseline, so assert Native == DriverLinq parity.
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

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves it went native
        Assert.Equal(1, native);
        Assert.Equal(Run(driverDb), native);
    }

    // EF-347 slice B review follow-up: Skip alone (no preceding Take) after Intersect -- the one uncovered
    // paging shape (Intersect_then_paging_goes_native above covers OrderBy+Take only). No driver-LINQ oracle
    // for Intersect, so assert the literal expected result under NativeOnly.
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

    // ── Field-name-collision isolation (EF-347 Task 3): RenderSetDifference tags each side of the
    // $unionWith with sibling fields _a/_b under a synthesized _doc wrapper (see MongoPipelineFactory).
    // A real stored element literally named _a must not collide with that tag -- it lives INSIDE _doc
    // (_doc.<realField>), never as a sibling of it. Confirmed [BsonElement] is honored via
    // BsonElementAttributeConvention (Metadata/Conventions/BsonAttributes) -- same mechanism as the
    // driver's own attribute, and distinct from the fluent .HasElementName(...) used elsewhere in this
    // file/CrossCollection*Tests.cs; either forces a non-default stored element name. ──────────────────

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

    // ── Parity: Native == DriverLinq (the driver's own LINQ provider already implements Union/Concat) ──

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

    // ── Graceful fallback: out-of-scope shapes throw under NativeOnly, but return correct results
    // under the default Native mode (TryTranslateSetOperation ALWAYS returns non-null, so these never
    // hard-fail translation -- they simply mark source1 non-native and let driver-LINQ take over) ────

    // NOTE: Intersect/Except's whole-entity, terminal shape now goes NATIVE as of this task (EF-347 Task 2,
    // "core native translation") -- see Intersect_whole_entity_goes_native / Except_whole_entity_goes_native
    // above, which supersede the pre-Task-2 Intersect_falls_back / Except_falls_back tests that used to
    // document TranslateIntersect/TranslateExcept unconditionally returning null. Unlike Union/Concat,
    // Intersect/Except have NO driver-LINQ fallback at all (Task 1's spike confirmed the driver's own LINQ v3
    // provider cannot translate a cross-view Intersect/Except), so an out-of-scope shape still hard-fails --
    // just via TryTranslateSetOperation returning null (reaching EF's own NotTranslatedExpression path)
    // rather than the never-attempted TranslateIntersect/TranslateExcept of before.

    [Fact]
    public void Projected_union_falls_back()
    {
        var collection = SeedCollection(nameof(Projected_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Select(i => new { i.Name })
            .Union(nativeDb.Entities.Where(i => i.Value >= 3).Select(i => new { i.Name }))
            .ToList();
        Assert.Equal(5, result.Count);
    }

    // NOTE: a "different entity type" fallback test (two operands with mismatched CollectionExpression
    // .EntityType, e.g. two SharedTypeEntity mappings of the same CLR type as distinct named entity types)
    // was attempted and removed: EF Core's OWN NavigationExpandingExpressionVisitor.ValidateExpressionCompatibility
    // rejects incompatible Union/Concat sources at the preprocessing stage, BEFORE MongoQueryableMethodTranslatingExpressionVisitor
    // ever runs (verified empirically -- it throws "Incompatible sources used for set operation", a plain
    // InvalidOperationException, regardless of MongoQueryMode). So TryTranslateSetOperation's
    // CollectionExpression.EntityType equality check is defensive-in-depth against a shape EF Core itself
    // already blocks upstream via ordinary LINQ usage -- not a reachable graceful-fallback scenario like
    // the ones below. Kept as a defensive guard (matching the brief) but not exercised by a functional test.

    // ── An operand carrying an Include (cross-collection $lookup) must fall back ────────────────────

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
                b.HasMany(i => i.Details).WithOne().HasForeignKey(d => d.LinkedItemId);
            });
            modelBuilder.Entity<LinkedDetail>(b => b.ToCollection(_details));
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }

    [Fact]
    public void Operand_with_include_falls_back()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var itemsName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Operand_with_include_falls_back)) + "I" + suffix;
        var detailsName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Operand_with_include_falls_back)) + "D" + suffix;

        var item1 = ObjectId.GenerateNewId();
        var item2 = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<LinkedItem>(itemsName).InsertMany(
        [
            new LinkedItem { Id = item1, Value = 1 },
            new LinkedItem { Id = item2, Value = 2 },
        ]);
        database.MongoDatabase.GetCollection<LinkedDetail>(detailsName).InsertMany(
        [
            new LinkedDetail { Id = ObjectId.GenerateNewId(), Text = "d1", LinkedItemId = item2 },
        ]);

        // EF Core's own NavigationExpandingExpressionVisitor requires both set-operation operands to carry
        // the SAME Include (mismatched Includes are rejected upstream, before this provider's translator
        // ever runs) -- so both sides Include(Details) here. EF then HOISTS that shared Include to apply
        // AFTER the Union combinator (this reaches TranslateSelect as Union(A, B).Select(x => Include(x))),
        // so it is TranslateSelect's post-terminal HasTerminalOperator guard -- not
        // IsPlainWholeEntitySelect's Lookups check -- that trips the fallback here (see the comment on that
        // guard for how this was discovered empirically: without it, the union-appended operand's rows come
        // back with an EMPTY Include collection instead of falling back).
        using (var nativeOnlyDb = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Items.Where(i => i.Value == 1).Include(i => i.Details)
                    .Union(nativeOnlyDb.Items.Where(i => i.Value == 2).Include(i => i.Details))
                    .ToList());
        }

        using var nativeDb = new LinkedItemDbContext(database, itemsName, detailsName, MongoQueryMode.Native);
        var result = nativeDb.Items.Where(i => i.Value == 1).Include(i => i.Details)
            .Union(nativeDb.Items.Where(i => i.Value == 2).Include(i => i.Details))
            .ToList();

        Assert.Equal(2, result.Count);
        Assert.Empty(result.Single(i => i.Value == 1).Details);
        Assert.Single(result.Single(i => i.Value == 2).Details);
    }

    // ── Composition-seam regression tests (EF-347 Task 5, updated by slice B Task 3): a set operation is
    // TERMINAL-ONLY, so every operator applied AFTER a Union/Concat must either go native correctly or
    // fall back gracefully -- throw under NativeOnly for a genuinely-deferred shape (the "went native"
    // signal), return correct results under the default Native mode either way. This is the SAME
    // recurring post-terminal hazard as GroupBy/Distinct (see the Query area AGENTS.md
    // "HasTerminalOperator" invariant): every post-terminal entry point (NativeSlotPopulator's seven slot
    // operators, NativeCardinalityBinder's aggregates/reducers, TranslateGroupBy, and TranslateSelect's
    // hoisted-Include/projection guards) must gate on it, or a post-union operator would resolve against
    // the base entity and silently emit a pre-$unionWith stage instead of falling back. EF-347 slice B
    // relaxed the NativeSlotPopulator gate for the seven slot operators specifically (Where/OrderBy/
    // ThenBy/Skip/Take), so those now go native -- see the *_goes_native tests immediately below. The
    // remaining seams (Count, chained Union, GroupBy, OfType) are still deferred and are NOT expected to
    // go native -- if one unexpectedly does, that is a real gap in the HasTerminalOperator guard, not
    // something to relax the assertion for. ─────────────────────────────────────────────────────────────

    // EF-347 slice B: a trailing Where after Union now goes native. Union HAS a driver-LINQ baseline, so
    // assert Native == DriverLinq parity.
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

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves it went native
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

    // EF-347 slice B: rename from Skip_take_after_union_falls_back -> Paging_after_union_goes_native (this is
    // the hazard-1 discriminator -- paging the combined ordered stream differs completely from paging source1).
    [Fact]
    public void Paging_after_union_goes_native()
    {
        var collection = SeedCollection(nameof(Paging_after_union_goes_native));
        using var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly);
        using var driverDb = Make(collection, MongoQueryMode.DriverLinq);

        // Union = {1,2,3,4,5} ordered; Skip(1).Take(2) = {2,3}. Paging source1 ({1,2,3}) would give {2,3}
        // too here — so ALSO assert the Count discriminator in Task 4. This case proves paging composes.
        List<int> Run(SingleEntityDbContext<Item> db) =>
            db.Entities.Where(i => i.Value <= 3).Union(db.Entities.Where(i => i.Value >= 3))
                .OrderBy(i => i.Value).Skip(1).Take(2)
                .ToList().Select(i => i.Value).ToList();

        var native = Run(nativeOnlyDb);
        Assert.Equal([2, 3], native);
        Assert.Equal(Run(driverDb), native);
    }

    // EF-347 slice B, hazard-1 discriminator: Count over the COMBINED union (5) differs from source1's
    // count (3), so a mis-placed pre-$unionWith $count would return the wrong number. Union has a baseline.
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

    [Fact]
    public void Chained_union_falls_back()
    {
        var collection = SeedCollection(nameof(Chained_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 2)
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value == 3))
                    .Union(nativeOnlyDb.Entities.Where(i => i.Value >= 4))
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 2)
            .Union(nativeDb.Entities.Where(i => i.Value == 3))
            .Union(nativeDb.Entities.Where(i => i.Value >= 4))
            .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        Assert.Equal([1, 2, 3, 4, 5], result); // three disjoint sets {1,2} U {3} U {4,5}
    }

    [Fact]
    public void GroupBy_after_union_falls_back()
    {
        var collection = SeedCollection(nameof(GroupBy_after_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .GroupBy(i => i.Value % 2 == 0)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .GroupBy(i => i.Value % 2 == 0)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToList().OrderBy(g => g.Key).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(3, result.Single(g => !g.Key).Count); // {1,3,5}
        Assert.Equal(2, result.Single(g => g.Key).Count); // {2,4}
    }

    // ── OfType after a native terminal (final-review Critical, EF-347 slice 2 follow-up): OfType has its
    // OWN Translate override (TranslateOfType) and, before the fix, was NOT gated on HasTerminalOperator.
    // Applied after a Union, it added the discriminator conjunct to the OUTER select's Predicate, which the
    // lowerer emits as a pre-$unionWith $match -- filtering only the outer rows. The $unionWith operand's own
    // nested pipeline got NO discriminator filter, so sibling-type (base) rows from the operand leaked into
    // the result: silent wrong data. This is the same recurring "own-Translate-override operator after a new
    // terminal" hazard Select/GroupBy already guard against -- OfType is the third, and Union/Concat (this
    // slice) is the first native terminal that preserves a whole-entity shaper, making it newly reachable. ──

    private class SetOpBase
    {
        public ObjectId Id { get; set; }

        // Deliberately nullable / no non-null default initializer: EF's discriminator value generator only
        // auto-populates the discriminator on insert when the CLR property still holds its type default
        // (null for a reference type). A non-null default (e.g. "") would look like an explicitly-assigned
        // value and suppress auto-population, leaving every row with the SAME (wrong) discriminator value.
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

        // NativeOnly is the "went native" signal for the guard itself: with the guard in place, OfType after
        // a Union must NOT go native (the discriminator conjunct can't reach the $unionWith operand), so this
        // must throw. (Pre-guard, this reached native -- and leaked the base row -- instead; see the fix report.)
        using (var nativeOnlyDb = MakeTph(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Union(nativeOnlyDb.Entities).OfType<SetOpDerived>().ToList());
        }

        // Native == DriverLinq: with the guard forcing fallback, results must be correct -- ONLY the derived
        // rows, never a leaked base row (the wrong-data hazard this guard prevents).
        using var nativeDb = MakeTph(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Union(nativeDb.Entities).OfType<SetOpDerived>().ToList();

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.IsType<SetOpDerived>(e));
    }

    // EF-347 slice C2: a trailing projection after Union now goes native (was a graceful fallback in slice B).
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

    // EF-347 slice C2: a trailing projection after Intersect now goes native (was a hard-fail in slice B).
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

    // EF-347 slice C2: a trailing anonymous/DTO member-access Select after a whole-entity set op now goes
    // native (a $project after the set-op stage). Union has a driver-LINQ baseline → assert Native==DriverLinq
    // parity; NativeOnly succeeding proves the native path was taken.
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

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves native
        Assert.Equal([1, 2, 3, 4, 5], native); // {1,2,3} U {3,4,5} deduped, projected to Value
        Assert.Equal(Run(driverDb), native);
    }

    // No driver-LINQ oracle for Intersect/Except → assert the literal expected set under NativeOnly.
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

    // Slice-B trailing Where composes with a slice-C2 trailing projection: filter the combined result, then
    // project. $match (trailing) lands before $project (both after the set-op stage).
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

    // Union dedups WHOLE ENTITIES before the projection, so two DISTINCT entities that happen to project to
    // the same value both survive (a duplicate projected value) — matching BCL Union(...).Select(...))
    // (Select never dedups). A constant projection (Select(i => new { K = 1 })) cannot be used to demonstrate
    // this: a constant leaf is not natively representable at all — SP3's projection binder accepts only
    // top-level member accesses (see Computed_leaf_projection_after_union_falls_back_gracefully below) — so it
    // falls back under Native and throws under NativeOnly for a reason unrelated to set-op dedup semantics.
    // Two items sharing the same Name (but distinct Value, hence distinct whole documents) give a real,
    // natively-representable (plain member-access) collision instead.
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

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves native
        Assert.Equal(2, native.Count); // both distinct entities survive Union's whole-document dedup
        Assert.All(native, n => Assert.Equal("Dup", n)); // and both project to the SAME Name (no accidental dedup)
        Assert.Equal(Run(driverDb).Count, native.Count);
    }

    // EF-347 slice C2 seam finding (empirically verified via captured MQL, not assumed): a Where whose
    // predicate is a pure pass-through of the trailing projection's member (x.N -> i.Value), and a SECOND
    // trailing Select that is itself a pure member-remapping, do NOT reach the composition-after-projection
    // seam at all for Union/Concat/Intersect/Except — EF Core's own query compiler (the NavigationExpanding
    // pending-selector mechanism) either pushes the predicate BEFORE the Select (fusing "Select(...).Where(...)"
    // into the equivalent "Where(...).Select(...)") or fuses two consecutive member-access Selects into ONE,
    // before this provider's translator ever sees two separate operators composed after the projection. This
    // happens for ANY operator whose predicate/selector is invertible through the projection (confirmed with
    // Where AND with Sum(x => x.N) too) — it is a general EF Core LINQ-compilation behavior, not something this
    // provider controls, and it is NOT specific to set operations (the SAME fusion is why
    // Where_then_Select_after_union_goes_native above already goes fully native). So — contrary to the original
    // design assumption — these two shapes are NOT reachable examples of "post-projection composition falls
    // back"; they are ordinary, already-in-scope compositions and go fully native with correct results in
    // every mode Union/Concat support. Captured MQL confirms it: for
    // `.Union(...).Select(i => new { N = i.Value }).Where(x => x.N >= 2)`, EF Core emits the pipeline
    // [$match(Value<=3), $unionWith(...), $group{_id:$$ROOT}, $replaceRoot, $match(Value>=2), $project(N:$Value)]
    // — the $match for the post-projection predicate lands BEFORE the $project and references the raw Value
    // field (not the projected alias N), proving EF's NavigationExpanding pending-selector mechanism pushed the
    // predicate back through the projection before the provider's translator ever saw it.
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

        var native = Run(nativeOnlyDb); // NativeOnly succeeding proves native
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

    // Unrelated to the seam: Intersect/Except have NO driver-LINQ baseline at all (the C# driver's own LINQ v3
    // provider cannot translate a cross-view Intersect/Except — see the set-ops slice A AGENTS.md note), so
    // explicit DriverLinq mode still hard-fails regardless of what is composed after the set op.
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

    // Deferred (unchanged): a BARE-SCALAR trailing projection is never pushed down (SP3 does not push a bare
    // scalar), so it falls back gracefully after Union.
    [Fact]
    public void Bare_scalar_projection_after_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Bare_scalar_projection_after_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => i.Value).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => i.Value).OrderBy(v => v).ToList();
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    // Deferred (unchanged): a COMPUTED-leaf trailing projection is not SP3-representable → graceful fallback.
    [Fact]
    public void Computed_leaf_projection_after_union_falls_back_gracefully()
    {
        var collection = SeedCollection(nameof(Computed_leaf_projection_after_union_falls_back_gracefully));
        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Select(i => new { Doubled = i.Value * 2 }).ToList());
        }
        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Select(i => new { Doubled = i.Value * 2 }).ToList();
        Assert.Equal(5, result.Count);
    }

    // NOTE: a Concat().OfType<T>() variant was attempted here too, but it hits an UNRELATED pre-existing bug
    // in the MongoDB C# driver's own LINQ v3 fallback translator: ConcatMethodToPipelineTranslator throws a
    // bare NullReferenceException when translating Concat().OfType() to driver-LINQ (verified empirically --
    // the NativeOnly half of that test, which exercises THIS guard, passes cleanly; only the driver-LINQ
    // fallback execution under the default Native mode fails, entirely inside the driver, several frames
    // below this provider's code). Since the guard's job here is only to force fallback, and the driver's own
    // fallback path is what breaks for that specific combination, this is out of scope for this fix --
    // covered by the Union variant instead, which fully exercises the same guard without hitting that bug.

    // ── Parametrized operand predicate: proves the shared PlaceholderTable (the operand's nested
    // $unionWith pipeline is rendered into the SAME placeholder table as the outer query -- see
    // MongoPipelineFactory.RenderUnionWith) correctly substitutes a captured local variable used INSIDE
    // the second operand's own predicate, through real end-to-end execution. ─────────────────────────

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
}
