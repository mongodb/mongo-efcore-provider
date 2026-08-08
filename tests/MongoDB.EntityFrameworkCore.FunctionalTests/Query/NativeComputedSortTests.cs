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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-401, stream 1 slice B — makes a COMPUTED (non-field) <c>OrderBy</c>/<c>ThenBy</c> key go native. MQL
/// <c>$sort</c> only accepts field paths, so <see cref="MongoDB.EntityFrameworkCore.Query.NativeTranslation.MongoSelectLowerer"/>
/// brackets the sort in a synthetic <c>$set</c> ... <c>$unset</c> pair (Task 2); this class proves the populator
/// fall-through (Task 3) actually reaches that machinery, end to end, against a real server.
/// </summary>
/// <remarks>
/// <b>Every case asserts ORDER, never a row count</b> — a dropped <c>$sort</c> (mutation 2 in the task brief)
/// still returns the right rows, just in insertion order, so a count-only assertion cannot discriminate it.
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
    }

    // The TPH pair for case 2 (DOM shaper) — a base with a derived sibling makes StreamingEligibility.IsEligible
    // false for the base type (GetDirectlyDerivedTypes().Any()), exactly the NativeTransactionAndCancellationTests
    // precedent.
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

    // Case 17's owned-collection fixture (Minor 3, fix round 1) — an unfiltered owned-collection Count as a
    // computed sort key. A separate, minimally-scoped entity: the main SortItem fixture has no collection nav.
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

    // Case 20's owned-collection fixture (the final fix wave) — the element's int Code is STORED AS A STRING
    // (HasBsonRepresentation(BsonType.String)), which is what makes a FILTERED count's element predicate compare
    // the raw stored representation LEXICOGRAPHICALLY rather than numerically. Separate from PostOwner above so
    // case 17's unfiltered-count fixture keeps default serialization and cannot be confused with this one.
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

    // Case 15's probe type (Important 1, fix round 1) — a struct with no BSON representation at all
    // (MongoDB.Bson.BsonValue.Create rejects it; an enum round-trips fine, MEASURED separately). No C#
    // literal syntax can embed a non-enum, non-primitive struct as a genuine ConstantExpression (a captured
    // LOCAL of this type would instead take EF's PARAMETER path via closure-field extraction), so the query
    // is built by hand below via Expression.Constant.
    private struct UnrenderableSortKey
    {
        public int X { get; set; }
    }

    // ── The main fixture ────────────────────────────────────────────────────────────────────────
    //
    // Four rows, seeded in this (insertion) order, chosen so the five orders below are ALL MUTUALLY DISTINCT
    // sequences — a fixture where two of them coincided would silently weaken every case that relies on the
    // difference (a dropped $sort, or a sort on the wrong key, would then still "pass"):
    //
    //   insertion order (as inserted)     : R1, R2, R3, R4   -> Label: pC, pA, pD, pB
    //   A     order (ascending A)         : R3, R2, R1, R4   -> Label: pD, pA, pC, pB
    //   B     order (ascending B)         : R1, R3, R4, R2   -> Label: pC, pD, pB, pA
    //   sum   order (ascending A+B)       : R3, R1, R4, R2   -> Label: pD, pC, pB, pA
    //   label order (ascending, alpha)    : R2, R4, R1, R3   -> Label: pA, pB, pC, pD
    //
    // All five are distinct permutations of {R1,R2,R3,R4} (verified by hand when this fixture was designed).
    // Every Label shares the prefix "p" so a parameterized Where(x => x.Label.StartsWith(prefix)) leg (case 13)
    // selects all four rows and can be compared against the same sum-ordered expectation the unfiltered cases use.
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

    // A-ascending expectation for MainRows, by Label: R3, R2, R1, R4 (case 14, fix round 1).
    private static readonly string[] MainAOrderLabels = ["pD", "pA", "pC", "pB"];

    // insertion-order expectation for MainRows.
    private static readonly string[] MainInsertionOrderLabels = ["pC", "pA", "pD", "pB"];

    // label-ascending (alphabetical) expectation for MainRows — also what Label.ToUpper() ascending produces,
    // since ToUpper() is a monotonic, case-uniform transform over this fixture's labels.
    private static readonly string[] MainLabelOrderLabels = ["pA", "pB", "pC", "pD"];

    // ── The tie fixture (cases 6, 7, 8) ─────────────────────────────────────────────────────────
    //
    // Four rows with TWO deliberate ties: T2/T4 tie on A (=3, for case 6's OrderBy(A).ThenBy(A+B)), and T1/T3
    // tie on A+B (=10, for cases 7/8's OrderBy(A+B).ThenBy(...)). Without a genuine tie the secondary key in
    // each of those cases would be inert — able to translate correctly while silently never being EXERCISED.
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

        // Assert the streaming premise directly — this is the shape the spike's central question was about,
        // and the one-pass materializer's forward name-dispatch must SkipValue() the synthetic sort field.
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

        // Assert both premises: a derived sibling exists, and that alone makes the base type ineligible for
        // the one-pass streaming materializer — this genuinely exercises the DOM shaper, not the streaming one.
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

        // No synthetic field leaks into the result: the anonymous type only ever has Label/A (a structural
        // guarantee), and the MQL itself shows the $unset removing the synthetic field BEFORE the final
        // $project — which only emits Label/A (never a "__sort*" key).
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

        // Sum order is [pD, pC, pB, pA]; Skip(1).Take(2) => [pC, pB] (rows R1, R4).
        // Insertion order is [pC, pA, pD, pB]; the same Skip(1).Take(2) there would be [pA, pD] (rows R2, R3) —
        // a COMPLETELY DIFFERENT pair of rows, not merely a reordering of the same two — asserted explicitly
        // below so a dropped $sort (which would silently fall back to insertion order) cannot pass by accident.
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

        // Re-running returns the SAME instances (identity resolution) — reference equality, position by position.
        var second = db.Entities.OrderBy(x => x.A + x.B).ToList();
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Same(first[i], second[i]);

        // Mutate + SaveChanges round-trips without ever writing a synthetic sort element back to the document.
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

        // Explicitly different from case 6's order over the SAME four rows.
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

    // ── 9. Constant sort key goes native — the A3 bare-constant shape ──────────────────────────────

    [Fact]
    public void Constant_sort_key_goes_native()
    {
        var collection = Seed(nameof(Constant_sort_key_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // A bare .ThenBy(Label) is deliberate, not decoration. Every row ties on the constant PRIMARY key, so
        // an assertion pinned only to that primary key cannot tell "the $sort ran and left every row tied" from
        // "the $sort was silently dropped" — both leave the rows in whatever order the (unsorted) cursor
        // produces, which for a freshly-seeded collection happens to BE insertion order. Chaining a real field
        // as the secondary key forces $sort to do observable work: with the $set/$sort/$unset bracket intact,
        // the whole row set collapses to the SECONDARY key's order (label-ascending); with $sort dropped
        // (mutation 2), it silently reverts to insertion order instead — which this fixture made deliberately
        // different from label order, so the two are distinguishable. (Verified as a genuine tripwire: an
        // earlier version of this test asserted only insertion order over a bare `OrderBy(x => 1)` and did NOT
        // go red under the "drop the $sort" mutation, for exactly the reason above.)
        var result = db.Entities.AsNoTracking().OrderBy(x => 1).ThenBy(x => x.Label).ToList();

        Assert.Equal(MainLabelOrderLabels, result.Select(x => x.Label));
    }

    // ── 10. Parameterized sort key goes native — the placeholder table is threaded through RenderAddFields ──

    [Fact]
    public void Parameterized_sort_key_goes_native()
    {
        var collection = Seed(nameof(Parameterized_sort_key_goes_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Same reasoning as case 9's comment: the .ThenBy(Label) is what makes a dropped $sort observable
        // rather than accidentally matching insertion order.
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

    // ── 13. Parameterized Where leg alongside a computed sort — the mandatory late-decline case ────

    [Fact]
    public void Parameterized_where_leg()
    {
        var collection = Seed(nameof(Parameterized_where_leg));

        // A captured local inside string.StartsWith has no native regex rendering (the renderer refuses a
        // parameterized regex term), so the native factory declines LATE and the WHOLE query — including the
        // computed OrderBy — falls back to driver-LINQ. Run under the DEFAULT Native mode, deliberately: this
        // route does not exist under NativeOnly (which throws on the decline).
        var prefix = "p";
        using var db = CreateContext(collection, MongoQueryMode.Native);

        var result = db.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix))
            .OrderBy(x => x.A + x.B)
            .ToList();

        // Every label shares the "p" prefix, so the filter selects all four rows — the same expectation as the
        // unfiltered sum-ordered case.
        Assert.Equal(MainSumOrderLabels, result.Select(x => x.Label));
        Assert.Equal(MainSumOrderA, result.Select(x => x.A));
    }

    // ── 14. A "$"-prefixed string constant sort key ties, it does not sort by the named field ──────
    // (fix round 1, Important 1 — the $literal-wrap fix in MongoPipelineFactory.RenderAddFields)

    [Fact]
    public void Dollar_prefixed_string_sort_key_does_not_get_interpreted_as_a_field_path()
    {
        var collection = Seed(nameof(Dollar_prefixed_string_sort_key_does_not_get_interpreted_as_a_field_path));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // "$Label" is a LITERAL string, not a field reference — every row ties on it, so the secondary key
        // (A ascending) is what actually determines the order. If RenderAddFields ever again emitted this
        // bare (unwrapped), MongoDB would read "$Label" as a FIELD PATH and sort by the REAL Label field
        // instead — which, over this fixture, produces the alphabetical Label order, a DIFFERENT sequence
        // from the A-order asserted below (see the fixture comment's "label order" vs "A order" rows) — so
        // the two dispositions are distinguishable, not coincidentally equal.
        var result = db.Entities.AsNoTracking().OrderBy(x => "$Label").ThenBy(x => x.A).ToList();

        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(MainAOrderLabels, labels);
        Assert.NotEqual(MainLabelOrderLabels, labels);
    }

    // ── 15. A bare constant whose CLR type has no BSON representation declines instead of throwing ──
    // (fix round 1, Important 1 — the adjacent risk the reviewer flagged: MongoDB.Bson.BsonValue.Create
    // rejects a custom struct with an uncaught ArgumentException; MEASURED separately that an enum is fine)

    [Fact]
    public void Unrenderable_constant_type_sort_key_declines_instead_of_throwing()
    {
        var collection = Seed(nameof(Unrenderable_constant_type_sort_key_declines_instead_of_throwing));

        // X = 5 (never 0/default) — a falsy 0 int field independently trips the DRIVER's own OrderBy-constant
        // translation into the unrelated "$project ... exclusion on field X in inclusion projection" ambiguity
        // this codebase's AGENTS.md notes document for a bare 0/false projection leaf (MEASURED: the same
        // MongoCommandException fires on the graceful FALLBACK with X = 0, unrelated to this fix). X = 5 avoids
        // that so the fallback leg genuinely exercises "declines cleanly, then falls back to correct rows".
        var param = Expression.Parameter(typeof(SortItem), "x");
        var keySelector = Expression.Lambda<Func<SortItem, UnrenderableSortKey>>(
            Expression.Constant(new UnrenderableSortKey { X = 5 }), param);

        // Native: declines cleanly and falls back to driver-LINQ — correct rows (every row ties on the
        // constant key, so order is unconstrained), never an uncaught ArgumentException escaping from
        // BsonValue.Create at pipeline-build time.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = Queryable.OrderBy(native.Entities.AsNoTracking(), keySelector).ToList();
        Assert.Equal(4, nativeResult.Count);

        // NativeOnly: the coverage instrument — a clean decline, never the raw ArgumentException.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => Queryable.OrderBy(nativeOnly.Entities.AsNoTracking(), keySelector).ToList());
    }

    // ── 16. A value-sensitive parameterized computed key — pins substitution AND the rendered value ──
    // (fix round 1, Important 2 — cases 9/10's ThenBy(Label) makes (parameter, Label) observationally
    // identical to (Label) alone, so neither pins that the parameter sentinel is actually substituted)

    [Fact]
    public void Parameterized_computed_sort_key_value_is_correctly_substituted()
    {
        var collection = Seed(nameof(Parameterized_computed_sort_key_value_is_correctly_substituted));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // factor = -1 inverts the A-ascending order into A-descending. If the $literal-wrapped sentinel were
        // never substituted (the $set body staying the raw sentinel document), every row would tie on it and
        // this would silently degrade to INSERTION order instead — a different sequence from both A-ascending
        // and A-descending over this fixture — so a stale sentinel is caught, not just "no ThenBy to mask it".
        var factor = -1;
        var result = db.Entities.AsNoTracking().OrderBy(x => x.A * factor).ToList();

        // A-descending is the exact reverse of MainAOrderLabels (A-ascending).
        var expected = MainAOrderLabels.Reverse().ToList();
        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(expected, labels);
        Assert.NotEqual(MainInsertionOrderLabels, labels);
    }

    // ── 17. An unfiltered owned-collection Count is a computed sort key too (Minor 3, fix round 1) ──

    [Fact]
    public void Unfiltered_owned_collection_count_sort_key_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<PostOwner>(
            UniqueCollectionName(nameof(Unfiltered_owned_collection_count_sort_key_goes_native)));

        // Seeded through the EF context itself (SaveChanges), not a raw driver InsertMany of the POCO — the
        // owned collection's shadow owner-key element is written by the PROVIDER's own entity serializer,
        // which a direct driver-level InsertMany of the CLR type bypasses entirely.
        using (var seedDb = CreateContext(collection, MongoQueryMode.Native, PostOwnerModel))
        {
            seedDb.Entities.AddRange(
                new PostOwner { Label = "pB", Posts = [new PostItem { PostId = 1 }, new PostItem { PostId = 2 }] },
                new PostOwner { Label = "pA", Posts = [] },
                new PostOwner { Label = "pC", Posts = [new PostItem { PostId = 3 }] });
            seedDb.SaveChanges();
        }

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, PostOwnerModel);

        // Insertion order is [pB, pA, pC]; Count-ascending (0, 1, 2) is [pA, pC, pB] — a different sequence,
        // so this cannot pass by a dropped $sort silently matching insertion order.
        var result = db.Entities.AsNoTracking().OrderBy(x => x.Posts.Count).ToList();

        Assert.Equal(["pA", "pC", "pB"], result.Select(x => x.Label));
    }

    // ── 18. A parameterized Where leg over the PROJECTION shape (Minor 5, fix round 1) ───────────────
    // Case 13 only exercises the late-decline route for the whole-entity shape; a projection is this
    // codebase's recorded silent-failure mode for an alias miss (see NativeBareProjectionTests), so the
    // computed-sort-then-projection shape needs its own late-decline leg too.

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

    // ── 19. A REFERENCE-TYPE parameter sort key declines instead of throwing at EXECUTION time ──────
    // (EF-401 Task 4, carried item (a).) The probe guard cannot see a parameter's runtime value, and a
    // default reference-type proxy is always null, so a Uri/Version/custom-class parameter reached
    // MongoPipelineFactory.SerializeParameter -> BsonValue.Create and threw an uncaught ArgumentException
    // at EXECUTION time, outside any compile-time fallback. MEASURED at this slice's base commit: Native
    // returned correct rows there. That made it a regression under the DEFAULT mode, so the guard now
    // declines a bare reference-type parameter unless it is a string (or a BsonValue).

    [Fact]
    public void Reference_type_parameter_sort_key_declines_instead_of_throwing_at_execution_time()
    {
        var collection = Seed(nameof(Reference_type_parameter_sort_key_declines_instead_of_throwing_at_execution_time));

        var uri = new Uri("http://example.com/");

        // Native: declines cleanly and falls back to driver-LINQ. Every row ties on the constant-per-
        // execution key, so ThenBy(Label) fixes the order and the assertion is not a row COUNT (which
        // cannot discriminate a dropped sort — see this class's header).
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            var labels = native.Entities.AsNoTracking()
                .OrderBy(x => uri).ThenBy(x => x.Label)
                .Select(x => x.Label).ToList();
            Assert.Equal(MainLabelOrderLabels, labels);
        }

        // NativeOnly: a clean decline, never the raw ArgumentException from BsonValue.Create.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => nativeOnly.Entities.AsNoTracking()
                    .OrderBy(x => uri).ThenBy(x => x.Label)
                    .Select(x => x.Label).ToList());
        }

        // The control that makes the decline narrow rather than a blanket reference-type rejection: a
        // STRING parameter is on the allowlist and still goes native. Without this leg the guard could be
        // widened to "decline every reference type" and nothing here would notice.
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
    // Cases 1–19 only ever reach OrderByDescending through case 8's ThenByDescending. Same code path
    // (NativeSlotPopulator's OrderBy/OrderByDescending arm, ascending: false) and the direction is unit-pinned,
    // so this is breadth rather than a new mechanism — but the primary-key spelling had no functional case.

    [Fact]
    public void Computed_sort_descending_primary_key()
    {
        var collection = Seed(nameof(Computed_sort_descending_primary_key));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var result = db.Entities.AsNoTracking().OrderByDescending(x => x.A + x.B).ToList();

        // A+B DESCENDING is the exact reverse of the ascending expectation: R2(25), R4(17), R1(10), R3(3).
        var labels = result.Select(x => x.Label).ToList();
        Assert.Equal(MainSumOrderLabels.Reverse(), labels);

        // Distinct from BOTH the ascending order (a lost/ignored direction flag) and insertion order (a dropped
        // $sort) over this fixture, so neither degradation can pass as this expectation.
        Assert.NotEqual(MainSumOrderLabels, labels);
        Assert.NotEqual(MainInsertionOrderLabels, labels);
    }

    // ── 21. A FILTERED owned-collection Count goes native as a sort key ──────────────────────────
    // A DECLINE FOR THIS SHAPE WAS SHIPPED (commit e09fee45) AND THEN REVERTED. Read this before proposing
    // another one. The decline's premise was that a filtered count's element predicate escapes
    // MongoExpressionTranslator.AllFieldsDefaultSerialized, so an operand stored under a non-default
    // BsonRepresentation compares in its RAW stored form and "silently reorders under Native where the
    // pre-slice driver-LINQ fallback was correct". The first half is true; the CLAIM IS MEASURED FALSE, and
    // legs 2-4 below are what pin the refutation:
    //
    //   native (no decline)  -> [cA, cB, cC]      explicit DriverLinq -> [cA, cB, cC]      in-memory -> [cB, cC, cA]
    //
    // The two SERVER-SIDE paths agree — the driver's own LINQ provider serializes the comparison constant
    // through the very same property serializer — so this is the EF-359 accepted-divergence family, not wrong
    // data, and this branch's oracle is Native == DriverLinq. EF-359 itself shipped this very node kind
    // (MongoFilteredSizeExpression) NATIVE in predicate and projection position under an explicit owner ruling
    // to "accept and document" the CLR divergence, so declining it in SORT position created an inconsistency
    // rather than removing one, and cost the common case (a filtered count over default-serialized operands)
    // for no measured correctness benefit. Case 17 is the unfiltered-count sibling; it was never in scope.
    //
    // KEEP LEGS 3 AND 4. They are the record of the refutation. Leg 1 is the routing pin: with a decline
    // re-added it goes red there and NOWHERE ELSE, because a decline changes routing only and no value.
    [Fact]
    public void Filtered_owned_collection_count_sort_key_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<CodeOwner>(
            UniqueCollectionName(nameof(Filtered_owned_collection_count_sort_key_goes_native)));

        // Code is stored as a STRING, and the seeded values are chosen so lexical and numeric order DISAGREE
        // for the predicate `Code > 5`:  "6" > "5" lexically AND 6 > 5 numerically (agree), but "10" < "5"
        // lexically while 10 > 5 numerically (disagree). So each owner's count differs between the two
        // semantics, and the three counts are all distinct under each — no ties to mask a wrong answer:
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
        // All three sequences are distinct. MEASURED: BOTH server-side paths — native and the driver's own LINQ
        // provider — return the server-side sequence, so the legs below assert THAT, and the in-memory sequence
        // is asserted only as the divergence it is.
        //
        // Seeded through the EF context (SaveChanges), like case 17: the owned collection's shadow owner-key
        // element and the string BSON representation are both written by the PROVIDER's own serializer, which a
        // raw driver-level InsertMany of the CLR type bypasses.
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

        // The premise, asserted rather than assumed: Code really is stored as a BSON string. Without this the
        // whole fixture could silently degrade into an ordinary numeric one and the test would pass vacuously.
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        var storedCodes = raw.Find(Builders<BsonDocument>.Filter.Empty).ToList()
            .SelectMany(d => d["Posts"].AsBsonArray.Select(p => p["Code"])).ToList();
        Assert.All(storedCodes, c => Assert.Equal(BsonType.String, c.BsonType));

        // LEG 1 — NativeOnly: the routing pin, and the ONLY leg that discriminates a decline. MEASURED: with a
        // decline re-added this query throws NativeTranslationNotSupportedException here, and every other leg
        // stays green — a decline changes routing only, never a value.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, CodeOwnerModel))
        {
            var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList();

            Assert.Equal(["cA", "cB", "cC"], nativeOnlyLabels);
        }

        // LEG 2 — default Native: the same server-side order.
        List<string> nativeLabels;
        using (var native = CreateContext(collection, MongoQueryMode.Native, CodeOwnerModel))
        {
            nativeLabels = native.Entities.AsNoTracking()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList();
        }

        Assert.Equal(["cA", "cB", "cC"], nativeLabels);

        // LEG 3 — explicit DriverLinq, THE LOAD-BEARING LEG. It is what refutes the "native reorders where the
        // fallback was correct" reading: the driver's own LINQ provider serializes the comparison constant
        // through the SAME property serializer, so it answers identically. This is the branch's oracle
        // (Native == DriverLinq). Delete this leg and the shipped-then-reverted decline looks like a wrong-data
        // fix that was undone, which it is not.
        using (var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq, CodeOwnerModel))
        {
            var driverLabels = driverLinq.Entities.AsNoTracking()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList();

            Assert.Equal(nativeLabels, driverLabels);
        }

        // LEG 4 — the divergence, asserted rather than left implicit: BOTH server-side paths disagree with
        // in-memory LINQ over the very same expression, which is the EF-359 accepted-divergence family (native
        // and DriverLinq agree with each other; both differ from the CLR). Not something this slice introduced
        // — a filtered count's element predicate is equally unguarded in predicate and projection position —
        // and not something a sort-position decline would have fixed.
        using (var oracleDb = CreateContext(collection, MongoQueryMode.Native, CodeOwnerModel))
        {
            var inMemory = oracleDb.Entities.AsNoTracking().ToList()
                .OrderBy(x => x.Posts.Count(p => p.Code > 5))
                .Select(x => x.Label).ToList();

            Assert.Equal(["cB", "cC", "cA"], inMemory);
            Assert.NotEqual(inMemory, nativeLabels);
        }
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

    // MQL-capture idiom mirrored from NativeBareProjectionTests: FunctionalTests has no TestMqlLoggerFactory /
    // AssertMql (those live in the SpecificationTests project), so MQL is captured through SpyLoggerProvider.
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
