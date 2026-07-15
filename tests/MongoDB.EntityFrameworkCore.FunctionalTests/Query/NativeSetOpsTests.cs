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
/// additionally dedups via <c>$group{_id:"$$ROOT"}</c> + <c>$replaceRoot</c>). Proves that a supported
/// whole-entity, terminal <c>Union</c>/<c>Concat</c> over the SAME entity type executes as a native
/// aggregation pipeline (rather than falling back to driver-LINQ), with correct dedup/duplicate-keeping
/// semantics, and that out-of-scope shapes (<c>Intersect</c>/<c>Except</c>, a projected union, an operand
/// carrying an <c>Include</c>, and mismatched operand entity types) fall back to driver-LINQ gracefully --
/// correct results under <see cref="MongoQueryMode.Native"/>, throwing
/// <see cref="NativeTranslationNotSupportedException"/> only under <see cref="MongoQueryMode.NativeOnly"/>
/// (the "went native" signal). Unlike plain filter/sort/paging shapes (see the Query area AGENTS.md "MQL
/// shape cannot prove native" pitfall), the <c>$unionWith</c>/<c>$group</c>/<c>$replaceRoot</c> shape IS
/// distinctive versus the driver-LINQ fallback, so both the MQL shape AND the <c>NativeOnly</c> signal are
/// asserted for the native-success tests.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSetOpsTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class Item
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

    // Intersect/Except are NOT touched by this task -- they stay in the pre-existing "not supported, but
    // bubble through for a clearer error message" switch group (TranslateIntersect/TranslateExcept always
    // return null, unconditionally). Unlike Union/Concat, that failure happens at translation time
    // (QMTEV.Visit throws a generic EF CoreStrings.TranslationFailed InvalidOperationException) and is
    // NOT gated on MongoQueryMode.NativeOnly -- it fails the same way under every mode, including the
    // default Native. These two tests document that this task leaves that scope boundary unchanged.
    [Fact]
    public void Intersect_falls_back()
    {
        var collection = SeedCollection(nameof(Intersect_falls_back));
        using var db = Make(collection, MongoQueryMode.Native);

        Assert.Throws<InvalidOperationException>(() =>
            db.Entities.Where(i => i.Value <= 3).Intersect(db.Entities.Where(i => i.Value >= 3)).ToList());
    }

    [Fact]
    public void Except_falls_back()
    {
        var collection = SeedCollection(nameof(Except_falls_back));
        using var db = Make(collection, MongoQueryMode.Native);

        Assert.Throws<InvalidOperationException>(() =>
            db.Entities.Where(i => i.Value <= 3).Except(db.Entities.Where(i => i.Value >= 3)).ToList());
    }

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

    // ── Composition-seam regression tests (EF-347 Task 5): a set operation is TERMINAL-ONLY, so ANY
    // operator applied AFTER a Union/Concat must fall back gracefully -- throw under NativeOnly (the
    // "went native" signal), return correct results under the default Native mode. This is the SAME
    // recurring post-terminal hazard as GroupBy/Distinct (see the Query area AGENTS.md
    // "HasTerminalOperator" invariant): every post-terminal entry point (NativeSlotPopulator's seven slot
    // operators, NativeCardinalityBinder's aggregates/reducers, TranslateGroupBy, and TranslateSelect's
    // hoisted-Include/projection guards) must gate on it, or a post-union operator would resolve against
    // the base entity and silently emit a pre-$unionWith stage instead of falling back. None of the seams
    // below are expected to go native -- if one unexpectedly does, that is a real gap in the
    // HasTerminalOperator guard, not something to relax the assertion for. ─────────────────────────────

    [Fact]
    public void Where_after_union_falls_back()
    {
        var collection = SeedCollection(nameof(Where_after_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Where(i => i.Value > 2)
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Where(i => i.Value > 2)
            .ToList().Select(i => i.Value).OrderBy(v => v).ToList();

        Assert.Equal([3, 4, 5], result);
    }

    [Fact]
    public void OrderBy_after_union_falls_back()
    {
        var collection = SeedCollection(nameof(OrderBy_after_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .OrderByDescending(i => i.Value)
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .OrderByDescending(i => i.Value)
            .ToList().Select(i => i.Value).ToList();

        Assert.Equal([5, 4, 3, 2, 1], result);
    }

    [Fact]
    public void Skip_take_after_union_falls_back()
    {
        var collection = SeedCollection(nameof(Skip_take_after_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .OrderBy(i => i.Value)
                    .Skip(1)
                    .Take(2)
                    .ToList());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var result = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .OrderBy(i => i.Value)
            .Skip(1)
            .Take(2)
            .ToList().Select(i => i.Value).ToList();

        Assert.Equal([2, 3], result);
    }

    [Fact]
    public void Count_after_union_falls_back()
    {
        var collection = SeedCollection(nameof(Count_after_union_falls_back));

        using (var nativeOnlyDb = Make(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnlyDb.Entities.Where(i => i.Value <= 3).Union(nativeOnlyDb.Entities.Where(i => i.Value >= 3))
                    .Count());
        }

        using var nativeDb = Make(collection, MongoQueryMode.Native);
        var count = nativeDb.Entities.Where(i => i.Value <= 3).Union(nativeDb.Entities.Where(i => i.Value >= 3))
            .Count();

        Assert.Equal(5, count); // {1,2,3} U {3,4,5} deduped -> 5
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
