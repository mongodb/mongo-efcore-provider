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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Computed (non-field) <c>OrderBy</c>/<c>ThenBy</c> keys. <c>$sort</c> only accepts field paths, so
/// <see cref="MongoDB.EntityFrameworkCore.Query.NativeTranslation.MongoSelectLowerer"/> brackets the sort in a
/// synthetic <c>$set</c> ... <c>$unset</c> pair; these tests run that end to end against a real server.
/// </summary>
/// <remarks>
/// Every case asserts order, never just a row count: a dropped <c>$sort</c> still returns the right rows, in
/// insertion order.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeComputedSortTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class SortItem
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public int B { get; set; }
        public string Label { get; set; } = "";

        // Used by case 24 (Not); false for every other seeded row.
        public bool Flag { get; set; }
    }

    // TPH pair for case 2: a derived sibling makes StreamingEligibility.IsEligible false for the base type, so the
    // DOM shaper is exercised.
    public class SortDomItem
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public int B { get; set; }
        public string Label { get; set; } = "";
    }

    public class SortDomItemDerived : SortDomItem
    {
        public string Extra { get; set; } = "";
    }

    // Case 17: an unfiltered owned-collection Count as a computed sort key.
    public class PostOwner
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public List<PostItem> Posts { get; set; } = null!;
    }

    public class PostItem
    {
        public int PostId { get; set; }
        public string Heading { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> PostOwnerModel =
        mb => mb.Entity<PostOwner>().OwnsMany(x => x.Posts, p => p.HasKey(i => i.PostId));

    // Case 20: the element's int Code is stored as a string, so a filtered count's element predicate compares the
    // stored representation lexicographically. Kept separate so case 17's fixture keeps default serialization.
    public class CodeOwner
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public List<CodeItem> Posts { get; set; } = null!;
    }

    public class CodeItem
    {
        public int ItemId { get; set; }
        public int Code { get; set; }
    }

    private static readonly Action<ModelBuilder> CodeOwnerModel =
        mb => mb.Entity<CodeOwner>().OwnsMany(x => x.Posts, p =>
        {
            p.HasKey(i => i.ItemId);
            p.Property(i => i.Code).HasBsonRepresentation(BsonType.String);
        });

    // Case 15: a struct with no BSON representation (BsonValue.Create rejects it). A captured local would take
    // EF's parameter path, so the query is built by hand with Expression.Constant.
    private struct UnrenderableSortKey
    {
        public int X { get; set; }
    }

    // ── The main fixture ────────────────────────────────────────────────────────────────────────
    //
    // Chosen so the five orders below are mutually distinct; if two coincided, a dropped $sort or a sort on the
    // wrong key could still pass:
    //
    //   insertion order (as inserted)     : R1, R2, R3, R4   -> Label: pC, pA, pD, pB
    //   A     order (ascending A)         : R3, R2, R1, R4   -> Label: pD, pA, pC, pB
    //   B     order (ascending B)         : R1, R3, R4, R2   -> Label: pC, pD, pB, pA
    //   sum   order (ascending A+B)       : R3, R1, R4, R2   -> Label: pD, pC, pB, pA
    //   label order (ascending, alpha)    : R2, R4, R1, R3   -> Label: pA, pB, pC, pD
    //
    // Every Label starts with "p", so case 13's Where(x => x.Label.StartsWith(prefix)) selects all four rows.
    //
    //   Row   A    B    Label   A+B
    //   R1    9    1    pC      10
    //   R2    2   23    pA      25
    //   R3    1    2    pD       3
    //   R4   14    3    pB      17
    private static readonly (int A, int B, string Label)[] MainRows =
    [
        (9, 1, "pC"),
        (2, 23, "pA"),
        (1, 2, "pD"),
        (14, 3, "pB")
    ];

    // sum-ascending expectation for MainRows, by Label: R3, R1, R4, R2.
    private static readonly string[] MainSumOrderLabels = ["pD", "pC", "pB", "pA"];
    private static readonly int[] MainSumOrderA = [1, 9, 14, 2];

    // A-ascending expectation for MainRows, by Label: R3, R2, R1, R4 (case 14).
    private static readonly string[] MainAOrderLabels = ["pD", "pA", "pC", "pB"];

    // insertion-order expectation for MainRows.
    private static readonly string[] MainInsertionOrderLabels = ["pC", "pA", "pD", "pB"];

    // label-ascending expectation for MainRows; also Label.ToUpper() ascending over these labels.
    private static readonly string[] MainLabelOrderLabels = ["pA", "pB", "pC", "pD"];

    // ── The tie fixture (cases 6, 7, 8) ─────────────────────────────────────────────────────────
    //
    // Two deliberate ties, so each secondary key is actually exercised: T2/T4 tie on A (=3, case 6) and T1/T3 tie
    // on A+B (=10, cases 7/8).
    //
    //   Row   A    B    Label   A+B   A*B
    //   T1    6    4    tC      10    24
    //   T2    3    8    tD      11    24
    //   T3    9    1    tA      10     9
    //   T4    3    2    tB       5     6
    private static readonly (int A, int B, string Label)[] TieRows =
    [
        (6, 4, "tC"),
        (3, 8, "tD"),
        (9, 1, "tA"),
        (3, 2, "tB")
    ];

    // ── 1. Computed sort over a whole entity, whole-entity streaming shaper ────────────────────────

    [Fact]
    public void Computed_sort_over_a_whole_entity_streams()
    {
        var collection = Seed(nameof(Computed_sort_over_a_whole_entity_streams));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // The one-pass materializer's forward name-dispatch must SkipValue() the synthetic sort field.
        var entityType = db.Model.FindEntityType(typeof(SortItem))!;
        Assert.True(StreamingEligibility.IsEligible(entityType));

        var result = db.Entities.AsNoTracking().OrderBy(x => x.A + x.B).ToList();

        Assert.Equal(MainSumOrderLabels, result.Select(x => x.Label));
    }

    // ── 2. Computed sort over a TPH (DOM-shaper) entity ────────────────────────────────────────────

    [Fact]
    public void Computed_sort_over_a_DOM_entity()
    {
        var collection = SeedDom(nameof(Computed_sort_over_a_DOM_entity));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, b => b.Entity<SortDomItemDerived>());

        // A derived sibling makes the base type ineligible for streaming, so this exercises the DOM shaper.
        var entityType = db.Model.FindEntityType(typeof(SortDomItem))!;
        Assert.True(entityType.GetDirectlyDerivedTypes().Any());
        Assert.False(StreamingEligibility.IsEligible(entityType));

        var result = db.Entities.AsNoTracking().OrderBy(x => x.A + x.B).ToList();

        Assert.Equal(MainSumOrderLabels, result.Select(x => x.Label));
    }

    // ── 3. Computed sort then projection — order survives, values right, no synthetic leak ────────

    [Fact]
    public void Computed_sort_then_projection()
    {
        var collection = Seed(nameof(Computed_sort_then_projection));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, out var spy);

        var result = db.Entities.AsNoTracking()
            .OrderBy(x => x.A + x.B)
            .Select(x => new { x.Label, x.A })
            .ToList();

        Assert.Equal(MainSumOrderLabels, result.Select(r => r.Label));
        Assert.Equal(MainSumOrderA, result.Select(r => r.A));

        // No synthetic field leaks: the $unset precedes the final $project, which has no "__sort*" key.
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("$set", mql);
        Assert.Contains("$unset", mql);
        Assert.True(mql.IndexOf("$unset", StringComparison.Ordinal) < mql.LastIndexOf("$project", StringComparison.Ordinal));
        var projectStage = mql[mql.LastIndexOf("$project", StringComparison.Ordinal)..];
        Assert.DoesNotContain("__sort", projectStage);
    }

    // ── 4. Computed sort then paging — the sum-ordered page, a different pair from insertion's ────

    [Fact]
    public void Computed_sort_then_paging()
    {
        var collection = Seed(nameof(Computed_sort_then_paging));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking()
            .OrderBy(x => x.A + x.B)
            .Skip(1).Take(2)
            .ToList();

        // Sum order [pD, pC, pB, pA] → [pC, pB]. Insertion order would give a different pair ([pA, pD]), so a
        // dropped $sort can't pass by accident.
        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(["pC", "pB"], labels);
        Assert.Equal([9, 14], result.Select(x => x.A));
        Assert.NotEqual(MainInsertionOrderLabels.Skip(1).Take(2), labels);
    }

    // ── 5. Tracking round trip — identity resolution, and no __sort* element written back ──────────

    [Fact]
    public void Computed_sort_tracking_round_trip()
    {
        var collection = Seed(nameof(Computed_sort_tracking_round_trip));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var first = db.Entities.OrderBy(x => x.A + x.B).ToList();
        Assert.Equal(MainSumOrderLabels, first.Select(x => x.Label));

        var entries = db.ChangeTracker.Entries<SortItem>().ToList();
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.Equal(EntityState.Unchanged, e.State));

        // Re-running returns the same instances (identity resolution).
        var second = db.Entities.OrderBy(x => x.A + x.B).ToList();
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Same(first[i], second[i]);

        // SaveChanges must not write a synthetic sort element back to the document.
        var mutated = first[0];
        var mutatedId = mutated.Id;
        mutated.Label += "_mutated";
        db.SaveChanges();

        var rawCollection = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var rawDoc = rawCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", mutatedId)).Single();
        Assert.DoesNotContain(rawDoc.Names, name => name.StartsWith("__sort", StringComparison.Ordinal));
        Assert.Equal(mutated.Label, rawDoc["Label"].AsString);
    }

    // ── 6. Mixed sort: OrderBy(field).ThenBy(computed) — ties on the field make the secondary load-bearing ──

    [Fact]
    public void Mixed_sort_keeps_the_field_key_as_a_plain_path()
    {
        var collection = SeedTies(nameof(Mixed_sort_keeps_the_field_key_as_a_plain_path));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking()
            .OrderBy(x => x.A)
            .ThenBy(x => x.A + x.B)
            .ToList();

        // A ascending: {T2,T4} tie at 3, then T1(6), T3(9). Tie broken by A+B ascending: T4(5) before T2(11).
        Assert.Equal(["tB", "tD", "tC", "tA"], result.Select(x => x.Label));
    }

    // ── 7. Computed primary then field secondary — ties on the primary; order differs from case 6 ──

    [Fact]
    public void Computed_primary_then_field_secondary()
    {
        var collection = SeedTies(nameof(Computed_primary_then_field_secondary));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking()
            .OrderBy(x => x.A + x.B)
            .ThenBy(x => x.Label)
            .ToList();

        // A+B ascending: T4(5), {T1,T3} tie at 10, then T2(11). Tie broken by Label ascending: T3("tA") before T1("tC").
        var order = result.Select(x => x.Label).ToList();
        Assert.Equal(["tB", "tA", "tC", "tD"], order);

        // Differs from case 6's order over the same four rows.
        Assert.NotEqual(["tB", "tD", "tC", "tA"], order);
    }

    // ── 8. Two computed keys chained — the secondary is genuinely exercised by a tie on the primary ──

    [Fact]
    public void Two_computed_keys()
    {
        var collection = SeedTies(nameof(Two_computed_keys));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking()
            .OrderBy(x => x.A + x.B)
            .ThenByDescending(x => x.A * x.B)
            .ToList();

        // A+B ascending: T4(5), {T1,T3} tie at 10, then T2(11). Tie broken by A*B DESCENDING: T1(24) before T3(9).
        Assert.Equal(["tB", "tC", "tA", "tD"], result.Select(x => x.Label));
    }

    // ── 9. Constant sort key goes native ──────────────────────────────────────────────────────────

    [Fact]
    public void Constant_sort_key_goes_native()
    {
        var collection = Seed(nameof(Constant_sort_key_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // The .ThenBy(Label) is load-bearing: every row ties on the constant key, so without a secondary a
        // dropped $sort is indistinguishable (a fresh collection returns insertion order). With it, rows come back
        // in label order, which this fixture makes differ from insertion order.
        var result = db.Entities.AsNoTracking().OrderBy(x => 1).ThenBy(x => x.Label).ToList();

        Assert.Equal(MainLabelOrderLabels, result.Select(x => x.Label));
    }

    // ── 10. Parameterized sort key goes native — the placeholder table is threaded through RenderAddFields ──

    [Fact]
    public void Parameterized_sort_key_goes_native()
    {
        var collection = Seed(nameof(Parameterized_sort_key_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // As in case 9, the .ThenBy(Label) makes a dropped $sort observable.
        var capturedLocal = 7;
        var result = db.Entities.AsNoTracking().OrderBy(x => capturedLocal).ThenBy(x => x.Label).ToList();

        Assert.Equal(MainLabelOrderLabels, result.Select(x => x.Label));
    }

    // ── 11. Field sort emits no $set — a STAGE-SHAPE pin, not a routing proof ──────────────────────

    [Fact]
    public void Field_sort_emits_no_set()
    {
        var collection = Seed(nameof(Field_sort_emits_no_set));
        using var db = CreateContextWithLogging(collection, MongoQueryMode.Native, out var spy);

        _ = db.Entities.AsNoTracking().OrderBy(x => x.A).ToList();

        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.DoesNotContain("$set", mql);
    }

    // ── 12. Unsupported computed key declines and returns correct rows ────────────────────────────

    [Fact]
    public void Unsupported_computed_key_declines_and_returns_correct_rows()
    {
        var collection = Seed(nameof(Unsupported_computed_key_declines_and_returns_correct_rows));

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var result = native.Entities.AsNoTracking().OrderBy(x => x.Label.ToUpper()).ToList();
        Assert.Equal(MainLabelOrderLabels, result.Select(x => x.Label));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label.ToUpper()).ToList());
    }

    // ── 13. Parameterized Where leg alongside a computed sort ─────────────────────────────────────

    [Fact]
    public void Parameterized_where_leg()
    {
        var collection = Seed(nameof(Parameterized_where_leg));

        // A captured local in StartsWith is natively representable (placeholder sentinel resolved at Build
        // time), so this goes fully native under default Native mode.
        var prefix = "p";
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var result = db.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix))
            .OrderBy(x => x.A + x.B)
            .ToList();

        // The "p" prefix selects all four rows, so the expectation matches the unfiltered case.
        Assert.Equal(MainSumOrderLabels, result.Select(x => x.Label));
        Assert.Equal(MainSumOrderA, result.Select(x => x.A));
    }

    // ── 14. A "$"-prefixed string constant sort key ties, it does not sort by the named field ──────
    // (the $literal wrap in MongoPipelineFactory.RenderAddFields)

    [Fact]
    public void Dollar_prefixed_string_sort_key_does_not_get_interpreted_as_a_field_path()
    {
        var collection = Seed(nameof(Dollar_prefixed_string_sort_key_does_not_get_interpreted_as_a_field_path));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // "$Label" is a literal string, so every row ties and A decides the order. Unwrapped, MongoDB would read it
        // as a field path and sort by Label, a different sequence over this fixture.
        var result = db.Entities.AsNoTracking().OrderBy(x => "$Label").ThenBy(x => x.A).ToList();

        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(MainAOrderLabels, labels);
        Assert.NotEqual(MainLabelOrderLabels, labels);
    }

    // ── 15. A bare constant whose CLR type has no BSON representation declines instead of throwing ──
    // (BsonValue.Create throws ArgumentException for a custom struct; an enum is fine)

    [Fact]
    public void Unrenderable_constant_type_sort_key_declines_instead_of_throwing()
    {
        var collection = Seed(nameof(Unrenderable_constant_type_sort_key_declines_instead_of_throwing));

        // X = 5, not 0: a falsy 0 trips the driver's unrelated "exclusion on field X in inclusion projection"
        // error on the fallback leg.
        var param = Expression.Parameter(typeof(SortItem), "x");
        var keySelector = Expression.Lambda<Func<SortItem, UnrenderableSortKey>>(
            Expression.Constant(new UnrenderableSortKey { X = 5 }), param);

        // Native: declines and falls back to correct rows (all tie, so order is unconstrained) rather than
        // letting BsonValue.Create's ArgumentException escape at pipeline-build time.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = Queryable.OrderBy(native.Entities.AsNoTracking(), keySelector).ToList();
        Assert.Equal(4, nativeResult.Count);

        // NativeOnly: a clean decline, never the raw ArgumentException.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => Queryable.OrderBy(nativeOnly.Entities.AsNoTracking(), keySelector).ToList());
    }

    // ── 16. A value-sensitive parameterized computed key — pins substitution AND the rendered value ──
    // (cases 9/10's ThenBy(Label) would mask a parameter sentinel that was never substituted)

    [Fact]
    public void Parameterized_computed_sort_key_value_is_correctly_substituted()
    {
        var collection = Seed(nameof(Parameterized_computed_sort_key_value_is_correctly_substituted));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // factor = -1 gives A-descending. An unsubstituted sentinel would make every row tie, yielding insertion
        // order instead, which differs from both A orders here.
        var factor = -1;
        var result = db.Entities.AsNoTracking().OrderBy(x => x.A * factor).ToList();

        // A-descending is the exact reverse of MainAOrderLabels (A-ascending).
        var expected = MainAOrderLabels.Reverse().ToList();
        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(expected, labels);
        Assert.NotEqual(MainInsertionOrderLabels, labels);
    }

    // ── 17. An unfiltered owned-collection Count as a computed sort key ────────────────────────────

    [Fact]
    public void Unfiltered_owned_collection_count_sort_key_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<PostOwner>(
            UniqueCollectionName(nameof(Unfiltered_owned_collection_count_sort_key_goes_native)));

        // Seeded via SaveChanges: the owned collection's shadow owner-key element is written by the provider's
        // serializer, which a raw driver InsertMany bypasses.
        using (var seedDb = CreateContext(collection, MongoQueryMode.Native, PostOwnerModel))
        {
            seedDb.Entities.AddRange(
                new PostOwner { Label = "pB", Posts = [new PostItem { PostId = 1 }, new PostItem { PostId = 2 }] },
                new PostOwner { Label = "pA", Posts = [] },
                new PostOwner { Label = "pC", Posts = [new PostItem { PostId = 3 }] });
            seedDb.SaveChanges();
        }

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PostOwnerModel);

        // Insertion order [pB, pA, pC] differs from Count-ascending [pA, pC, pB].
        var result = db.Entities.AsNoTracking().OrderBy(x => x.Posts.Count).ToList();

        Assert.Equal(["pA", "pC", "pB"], result.Select(x => x.Label));
    }

    // ── 18. A parameterized Where leg over the projection shape ────────────────────────────────────
    // A projection is where an alias miss fails silently (see NativeBareProjectionTests), so the
    // computed-sort-then-projection shape gets its own late-decline leg alongside case 13.

    [Fact]
    public void Parameterized_where_leg_for_computed_sort_then_projection()
    {
        var collection = Seed(nameof(Parameterized_where_leg_for_computed_sort_then_projection));
        var prefix = "p";
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var result = db.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix))
            .OrderBy(x => x.A + x.B)
            .Select(x => new { x.Label, x.A })
            .ToList();

        Assert.Equal(MainSumOrderLabels, result.Select(r => r.Label));
        Assert.Equal(MainSumOrderA, result.Select(r => r.A));
    }

    // ── 19. A reference-type parameter sort key declines instead of throwing at execution time ─────
    // The probe guard can't see a parameter's runtime value, so a Uri/Version/custom-class parameter would reach
    // MongoPipelineFactory.SerializeParameter -> BsonValue.Create and throw ArgumentException at execution, past
    // any fallback. The guard declines a bare reference-type parameter unless it's a string (or BsonValue).

    [Fact]
    public void Reference_type_parameter_sort_key_declines_instead_of_throwing_at_execution_time()
    {
        var collection = Seed(nameof(Reference_type_parameter_sort_key_declines_instead_of_throwing_at_execution_time));

        var uri = new Uri("http://example.com/");

        // Native: declines and falls back. Every row ties on the key, so ThenBy(Label) fixes the order.
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            var labels = native.Entities.AsNoTracking()
                .OrderBy(x => uri).ThenBy(x => x.Label)
                .Select(x => x.Label).ToList();
            Assert.Equal(MainLabelOrderLabels, labels);
        }

        // NativeOnly: a clean decline, never the raw ArgumentException.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking()
                    .OrderBy(x => uri).ThenBy(x => x.Label)
                    .Select(x => x.Label).ToList());
        }

        // Control: a string parameter is allowlisted and still goes native, so the guard can't silently widen
        // to "decline every reference type".
        var label = "zz";
        using (var stringNativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            var labels = stringNativeOnly.Entities.AsNoTracking()
                .OrderBy(x => label).ThenBy(x => x.Label)
                .Select(x => x.Label).ToList();
            Assert.Equal(MainLabelOrderLabels, labels);
        }
    }

    // ── 20. OrderByDescending with a computed PRIMARY key ─────────────────────────────────────────
    // Breadth: the only other descending case is case 8's ThenByDescending.

    [Fact]
    public void Computed_sort_descending_primary_key()
    {
        var collection = Seed(nameof(Computed_sort_descending_primary_key));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking().OrderByDescending(x => x.A + x.B).ToList();

        // A+B descending: R2(25), R4(17), R1(10), R3(3).
        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(MainSumOrderLabels.Reverse(), labels);

        // Distinct from both ascending order (ignored direction) and insertion order (dropped $sort).
        Assert.NotEqual(MainSumOrderLabels, labels);
        Assert.NotEqual(MainInsertionOrderLabels, labels);
    }

    // ── 21. A FILTERED owned-collection Count over a string-represented operand is refused ───────────
    // The element predicate `p.Code > 5` would compare the raw stored strings ("10" < "5"), so both server paths
    // used to return [cA, cB, cC] where C# answers [cB, cC, cA]. That was once accepted as a Native == DriverLinq
    // divergence; EF-337 (StoredOrdering) refuses the relational comparison on every path instead.
    [Fact]
    public void Filtered_owned_collection_count_sort_key_over_a_string_represented_operand_is_refused()
    {
        var collection = database.MongoDatabase.GetCollection<CodeOwner>(
            UniqueCollectionName(nameof(Filtered_owned_collection_count_sort_key_over_a_string_represented_operand_is_refused)));

        // Code is stored as a string; for `Code > 5`, "10" < "5" lexically while 10 > 5 numerically, so each
        // owner's count differs between the two semantics, with no ties under either:
        //
        //   Owner   codes          CLR count (Code > 5)   raw-string count ("Code" > "5")
        //   cA      10, 10, 10     3                      0
        //   cB      6              1                      1
        //   cC      6, 6           2                      2
        //
        //   insertion order          : cC, cA, cB
        //   IN-MEMORY (CLR)      asc : cB(1), cC(2), cA(3)  ->  [cB, cC, cA]
        //   SERVER-SIDE (raw)    asc : cA(0), cB(1), cC(2)  ->  [cA, cB, cC]
        //
        // Seeded via SaveChanges (like case 17) so the provider's serializer writes the owner key and string
        // representation.
        using (var seedDb = CreateContext(collection, MongoQueryMode.Native, CodeOwnerModel))
        {
            seedDb.Entities.AddRange(
                new CodeOwner
                {
                    Label = "cC",
                    Posts = [new CodeItem {ItemId = 1, Code = 6}, new CodeItem {ItemId = 2, Code = 6}]
                },
                new CodeOwner
                {
                    Label = "cA",
                    Posts =
                    [
                        new CodeItem {ItemId = 3, Code = 10}, new CodeItem {ItemId = 4, Code = 10},
                        new CodeItem {ItemId = 5, Code = 10}
                    ]
                },
                new CodeOwner {Label = "cB", Posts = [new CodeItem {ItemId = 6, Code = 6}]});
            seedDb.SaveChanges();
        }

        // Premise: Code really is stored as a BSON string, otherwise the test passes vacuously.
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var storedCodes = raw.Find(Builders<BsonDocument>.Filter.Empty).ToList()
            .SelectMany(d => d["Posts"].AsBsonArray.Select(p => p["Code"])).ToList();
        Assert.All(storedCodes, c => Assert.Equal(BsonType.String, c.BsonType));

        // Leg 1 — NativeOnly declines (no stored-form answer).
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, CodeOwnerModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => nativeOnly.Entities.AsNoTracking()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList());
        }

        // Legs 2 and 3 — default Native falls back, and DriverLinq goes straight to the bridge, which refuses.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, CodeOwnerModel);
            var ex = Assert.Throws<NotSupportedException>(() => db.Entities.AsNoTracking()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList());
            Assert.Contains("CodeItem.Code'", ex.Message);
        }

        // Leg 4 — the C# answer, which the old stored-form order ([cA, cB, cC]) contradicted.
        using (var oracleDb = CreateContext(collection, MongoQueryMode.Native, CodeOwnerModel))
        {
            var inMemory = oracleDb.Entities.AsNoTracking().ToList()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList();

            Assert.Equal(["cB", "cC", "cA"], inMemory);
        }
    }

    // ── 22-24. MongoInExpression / MongoUnaryExpression{Not} computed sort keys ─────────────────────
    // Require MongoAggregationExpressionRenderer arms for both, or TryTranslateComputedSortKey's CanRender gate
    // declines.

    [Fact]
    public void Computed_sort_key_using_client_collection_Contains_goes_native()
    {
        var collection = Seed(nameof(Computed_sort_key_using_client_collection_Contains_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var favored = new[] { "pA", "pD" };

        // Ascending by membership (false < true); ThenBy(Label) makes a dropped $sort observable.
        var result = db.Entities.AsNoTracking()
            .OrderBy(x => favored.Contains(x.Label))
            .ThenBy(x => x.Label)
            .ToList();

        // Not-favored (pB, pC) sort first (false), alphabetically; then favored (pA, pD), alphabetically.
        Assert.Equal(["pB", "pC", "pA", "pD"], result.Select(x => x.Label));
    }

    [Fact]
    public void Negated_computed_sort_key_using_Contains_goes_native()
    {
        var collection = Seed(nameof(Negated_computed_sort_key_using_Contains_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var favored = new[] { "pA", "pD" };

        // !list.Contains(...) becomes a negated MongoInExpression, exercising RenderIn's $not:[$in:...] branch.
        var result = db.Entities.AsNoTracking()
            .OrderBy(x => !favored.Contains(x.Label))
            .ThenBy(x => x.Label)
            .ToList();

        // Favored (pA, pD) sort first (false, since negated), alphabetically; then not-favored (pB, pC).
        Assert.Equal(["pA", "pD", "pB", "pC"], result.Select(x => x.Label));
    }

    [Fact]
    public void Computed_sort_key_using_Not_over_a_bool_field_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<SortItem>(
            UniqueCollectionName(nameof(Computed_sort_key_using_Not_over_a_bool_field_goes_native)));
        collection.InsertMany(
        [
            new SortItem { A = 1, B = 1, Label = "pX", Flag = true },
            new SortItem { A = 2, B = 2, Label = "pY", Flag = false },
            new SortItem { A = 3, B = 3, Label = "pZ", Flag = true },
            new SortItem { A = 4, B = 4, Label = "pW", Flag = false }
        ]);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // !x.Flag over a bool field is a genuine MongoUnaryExpression{Not} (not the $in collapse above).
        // Ascending on !Flag puts Flag==true rows first; ThenBy(Label) makes a dropped $sort observable.
        var result = db.Entities.AsNoTracking()
            .OrderBy(x => !x.Flag)
            .ThenBy(x => x.Label)
            .ToList();

        Assert.Equal(["pX", "pZ", "pW", "pY"], result.Select(x => x.Label));
    }

    // ── 25. Not over a value-converted bool must decline, never answer wrong ──────────────────────────
    // A raw-field { $not: [...] } is truthiness-based and both converted values ("Y"/"N") are truthy, so rendering
    // it natively would tie every row on a wrong constant. MongoExpressionTranslator.AllFieldsDefaultSerialized
    // therefore declines a MongoUnaryExpression over a non-default-serialized field. Driver-LINQ would render the same
    // raw { $not: "$Flag" }, so the bridge refuses it too (EF-337: a converted bool used for its truth in a key).
    // In-memory the answer is [p2, p3, p1, p4]; both server paths used to return label order.

    public class ConvertedFlagItem
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public bool Flag { get; set; }
    }

    // Both stored values are non-empty strings (truthy under $not), so a raw-field $not is wrong for every
    // Flag==false row, not just some.
    private static readonly Action<ModelBuilder> ConvertedFlagModel =
        mb => mb.Entity<ConvertedFlagItem>().Property(x => x.Flag)
            .HasConversion(v => v ? "Y" : "N", v => v == "Y");

    [Fact]
    public void Computed_sort_key_using_Not_over_a_value_converted_bool_declines_instead_of_answering_wrong()
    {
        var name = UniqueCollectionName(
            nameof(Computed_sort_key_using_Not_over_a_value_converted_bool_declines_instead_of_answering_wrong));
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p2" }, { "Flag", "Y" } }, // true
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p1" }, { "Flag", "N" } }, // false
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p3" }, { "Flag", "Y" } }, // true
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p4" }, { "Flag", "N" } }  // false
        ]);
        var collection = database.MongoDatabase.GetCollection<ConvertedFlagItem>(name);

        // NativeOnly: a clean decline, never silently-wrong data.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, ConvertedFlagModel))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                nativeOnly.Entities.AsNoTracking()
                    .OrderBy(x => !x.Flag).ThenBy(x => x.Label)
                    .ToList());
        }

        // Native declines to the bridge, which refuses; so does explicit DriverLinq.
        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode, ConvertedFlagModel);
            var ex = Assert.Throws<NotSupportedException>(() => db.Entities.AsNoTracking()
                .OrderBy(x => !x.Flag).ThenBy(x => x.Label)
                .ToList());
            Assert.Contains("ConvertedFlagItem.Flag'", ex.Message);
        }

        // The C# answer the old stored-form order contradicted.
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native, ConvertedFlagModel);
        Assert.Equal(
            ["p2", "p3", "p1", "p4"],
            oracleDb.Entities.AsNoTracking().ToList().OrderBy(x => !x.Flag).ThenBy(x => x.Label).Select(x => x.Label));
    }

    // ── Seeds and helpers ───────────────────────────────────────────────────────────────────────

    private IMongoCollection<SortItem> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<SortItem>(UniqueCollectionName(name));
        collection.InsertMany(MainRows.Select(r => new SortItem { A = r.A, B = r.B, Label = r.Label }));
        return collection;
    }

    private IMongoCollection<SortItem> SeedTies(string name)
    {
        var collection = database.MongoDatabase.GetCollection<SortItem>(UniqueCollectionName(name));
        collection.InsertMany(TieRows.Select(r => new SortItem { A = r.A, B = r.B, Label = r.Label }));
        return collection;
    }

    // Raw-BSON seed carrying the TPH discriminator ("_t"), mirroring
    // NativeTransactionAndCancellationTests.SeedDiscriminatedRange's pattern.
    private IMongoCollection<SortDomItem> SeedDom(string name)
    {
        var collectionName = UniqueCollectionName(name);
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        raw.InsertMany(MainRows.Select(r => new BsonDocument
        {
            {"_id", ObjectId.GenerateNewId()}, {"A", r.A}, {"B", r.B}, {"Label", r.Label},
            {"_t", nameof(SortDomItem)}
        }));
        return database.MongoDatabase.GetCollection<SortDomItem>(collectionName);
    }

    // ── The synthetic $set sort field must not clobber a real mapped element ────────────────────────
    //
    // $set silently overwrites a same-named field (and the trailing $unset removes it), so
    // SyntheticSortFieldAllocator must avoid every reachable element name. Both tests run under NativeOnly;
    // MongoSelectLowererTests has the allocator-level unit tests.

    public class ClashItem
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public int B { get; set; }
        public string Label { get; set; } = "";
    }

    // Special is mapped onto "__sort0", the first synthetic name, and declared only on the derived type, so
    // ClashItem's GetProperties() never returns it.
    public class ClashItemDerived : ClashItem
    {
        public int Special { get; set; }
    }

    [Fact]
    public void Synthetic_sort_field_does_not_clobber_a_TPH_derived_types_own_element()
    {
        // A TPH derived type's own "__sort0" element must be reserved too; otherwise $set/$unset clobbers it
        // (a crash for this required property, silent data loss for a nullable one).
        var name = UniqueCollectionName(nameof(Synthetic_sort_field_does_not_clobber_a_TPH_derived_types_own_element));
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"A", 9}, {"B", 1}, {"Label", "pC"}, {"_t", nameof(ClashItem)}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"A", 1}, {"B", 2}, {"Label", "pD"}, {"_t", nameof(ClashItem)}},
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"A", 2}, {"B", 23}, {"Label", "pA"},
                {"__sort0", 42}, {"_t", nameof(ClashItemDerived)}
            },
        ]);

        using var db = CreateContextWithLogging(
            database.MongoDatabase.GetCollection<ClashItem>(name), MongoQueryMode.NativeOnly, out var spy,
            mb => mb.Entity<ClashItemDerived>().Property(x => x.Special).HasElementName("__sort0"));

        // NativeOnly: a fallback would throw here.
        var rows = db.Entities.AsNoTracking().OrderBy(x => x.A + x.B).ToList();

        // Order, not just count (A+B: 3, 10, 25).
        Assert.Equal(["pD", "pC", "pA"], rows.Select(x => x.Label));

        // The derived row's own element survived.
        var derived = Assert.IsType<ClashItemDerived>(Assert.Single(rows.OfType<ClashItemDerived>()));
        Assert.Equal(42, derived.Special);

        // The allocator skipped "__sort0".
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("__sort1", mql);
    }

    // ── Gap 1: a set-op operand of a DIFFERENT entity type ──────────────────────────────────────────

    public class SetOpMain
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class SetOpOther
    {
        public ObjectId Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }

        // Mapped onto "__sort0": the operand's own namespace, invisible from the outer query's root type.
        public string Clash { get; set; } = "";
    }

    [Fact]
    public void Synthetic_sort_field_does_not_clobber_a_set_op_operands_own_element()
    {
        // A projected-operand set op needn't share an entity type, and the operand's ops lower through the same
        // SyntheticSortFieldAllocator into the nested $unionWith pipeline, so the operand's element names must be
        // reserved too, or $set/$unset destroys its "__sort0" before its $project reads it.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var mains = UniqueCollectionName(nameof(Synthetic_sort_field_does_not_clobber_a_set_op_operands_own_element)) + "M" + suffix;
        var others = UniqueCollectionName(nameof(Synthetic_sort_field_does_not_clobber_a_set_op_operands_own_element)) + "O" + suffix;

        database.MongoDatabase.GetCollection<BsonDocument>(mains).InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Name", "m1"}},
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(others).InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"X", 1}, {"Y", 2}, {"__sort0", "o1"}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"X", 5}, {"Y", 1}, {"__sort0", "o2"}},
        ]);

        using var db = new SetOpElementNameContext(database, mains, others, MongoQueryMode.NativeOnly);

        // NativeOnly: a fallback would throw rather than mask the collision.
        var rows = db.Mains.AsNoTracking().Select(m => new {N = m.Name})
            .Union(db.Others.AsNoTracking().OrderBy(o => o.X + o.Y).Select(o => new {N = o.Clash}))
            .AsEnumerable()
            .Select(x => x.N)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["m1", "o1", "o2"], rows);
    }

    private class SetOpElementNameContext : DbContext
    {
        private readonly string _mains;
        private readonly string _others;

        public SetOpElementNameContext(TemporaryDatabaseFixture db, string mains, string others, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<SetOpElementNameContext>()
                .UseMongoDB(db.Client, db.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _mains = mains;
            _others = others;
        }

        public DbSet<SetOpMain> Mains { get; set; } = null!;
        public DbSet<SetOpOther> Others { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<SetOpMain>().ToCollection(_mains);
            mb.Entity<SetOpOther>().ToCollection(_others);
            mb.Entity<SetOpOther>().Property(x => x.Clash).HasElementName("__sort0");
        }

        private sealed class IgnoreCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => System.Threading.Interlocked.Increment(ref _count);
        }
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // FunctionalTests has no TestMqlLoggerFactory/AssertMql, so MQL is captured through SpyLoggerProvider.
    private static SingleEntityDbContext<T> CreateContextWithLogging<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, out SpyLoggerProvider spyLogger,
        Action<ModelBuilder>? modelBuilderAction = null)
        where T : class
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            modelBuilderAction: modelBuilderAction,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }
}
