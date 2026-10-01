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
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>Nullable&lt;T&gt;.Value</c> (peeled to the underlying field) and <c>Nullable&lt;T&gt;.HasValue</c> (same as
/// <c>!= null</c>) in predicate, sort-key and projection position. Routing is proven by
/// <see cref="MongoQueryMode.NativeOnly"/>, not MQL shape.
/// </summary>
/// <remarks>
/// <para>
/// The oracle is <c>Native == DriverLinq</c>, not in-memory LINQ: in-memory LINQ throws for <c>x.Score.Value</c>
/// when <c>Score</c> is null, but a server-side <c>$match</c>/<c>$sort</c> doesn't (a documented divergence; see
/// <c>Query/AGENTS.md</c>). <see cref="Parity_with_driver_linq_over_the_ragged_fixture"/> is the main gate.
/// </para>
/// <para>
/// Every nullable property has three states — a value, explicit BSON <c>null</c>, and a missing element — and
/// <c>!HasValue</c> must select both absent states.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeNullableMemberTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public int? Score { get; set; }
        public bool? Flag { get; set; }
    }

    /// <summary>
    /// Separate from <see cref="Item"/> so its value-transforming converter doesn't change the model the other
    /// tests use.
    /// </summary>
    public class ConvertedItem
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int? Converted { get; set; }
    }

    // ── 1. Predicate position ──────────────────────────────────────────────────────

    [Fact]
    public void Value_in_a_predicate_goes_native()
    {
        var collection = SeedRagged(nameof(Value_in_a_predicate_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Relational comparisons are type-bracketed server-side: null and missing never match $gt, so the ragged
        // rows are absent (as with driver-LINQ), rather than the in-memory InvalidOperationException.
        Assert.Equal(
            ["r1_ten"],
            db.Entities.AsNoTracking().Where(x => x.Score!.Value > 5).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        // Also bracketed, so { $gt: 5 } and { $lte: 5 } don't partition; hence MongoExpressionNegator wraps a
        // relational comparison in $not instead of inverting it.
        Assert.Equal(
            ["r4_three"],
            db.Entities.AsNoTracking().Where(x => x.Score!.Value < 5).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        // The $not wrap is the exact complement, so the ragged rows reappear here.
        Assert.Equal(
            ["r2_null", "r3_missing", "r4_three"],
            db.Entities.AsNoTracking().Where(x => !(x.Score!.Value > 5)).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        Assert.Equal(
            ["r1_ten"],
            db.Entities.AsNoTracking().Where(x => x.Score!.Value == 10).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        // The idiomatic guarded spelling, and its disjunctive mirror.
        Assert.Equal(
            ["r1_ten"],
            db.Entities.AsNoTracking().Where(x => x.Score.HasValue && x.Score.Value > 5).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        Assert.Equal(
            ["r1_ten", "r2_null", "r3_missing"],
            db.Entities.AsNoTracking().Where(x => !x.Score.HasValue || x.Score!.Value > 5).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        // Whole-entity result, so routing isn't confounded by the bare-projection path.
        Assert.Equal(
            ["r1_ten"],
            db.Entities.AsNoTracking().Where(x => x.Score!.Value > 5).OrderBy(x => x.Title)
                .ToList().Select(x => x.Title).ToList());
    }

    // ── 2. Sort-key position ───────────────────────────────────────────────────────

    [Fact]
    public void Value_in_a_sort_key_goes_native()
    {
        var collection = SeedRagged(nameof(Value_in_a_sort_key_goes_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // BSON sorts null (and missing) before numbers, so the ragged rows lead and tie; ThenBy breaks the tie.
        Assert.Equal(
            ["r2_null", "r3_missing", "r4_three", "r1_ten"],
            db.Entities.AsNoTracking().OrderBy(x => x.Score!.Value).ThenBy(x => x.Title)
                .Select(x => x.Title).ToList());
    }

    // ── 3. Projection position ─────────────────────────────────────────────────────

    [Fact]
    public void Value_in_a_projection_goes_native()
    {
        var collection = SeedRagged(nameof(Value_in_a_projection_goes_native));

        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            // Well-formed rows only.
            Assert.Equal(
                ["r1_ten=10", "r4_three=3"],
                db.Entities.AsNoTracking().Where(x => x.Rank <= 2).OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Score!.Value})
                    .ToList().Select(a => $"{a.Title}={a.V}").ToList());

            // Bare spelling; its $project alias is the leaf's document path, "Score".
            Assert.Equal(
                [3, 10],
                db.Entities.AsNoTracking().Where(x => x.Rank <= 2).OrderBy(x => x.Score!.Value)
                    .Select(x => x.Score!.Value).ToList());
        }

        // Over the ragged rows a non-nullable target can't hold null, so both paths must throw rather than
        // silently return 0. Exception types are asserted per path (not a catch-all, which would also pass on a
        // connection error) and legitimately differ: native reads through the `Score` property's binding and a
        // narrowing Convert (.NET's "Nullable object must have a value"), while the driver fails in its
        // deserializer. The type isn't contract for erroneous input.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Score!.Value}).ToList());
            Assert.Contains("Nullable object must have a value", ex.Message);
        }

        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var ex = Assert.Throws<FormatException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Score!.Value}).ToList());
            Assert.Contains("Cannot deserialize a 'Int32' from BsonType 'Null'", ex.ToString());
        }
    }

    // ── 4. HasValue / !HasValue ────────────────────────────────────────────────────

    [Fact]
    public void HasValue_and_negated_HasValue_go_native()
    {
        var collection = SeedRagged(nameof(HasValue_and_negated_HasValue_go_native));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var withValue = db.Entities.AsNoTracking().Where(x => x.Score.HasValue)
            .OrderBy(x => x.Title).Select(x => x.Title).ToList();
        var withoutValue = db.Entities.AsNoTracking().Where(x => !x.Score.HasValue)
            .OrderBy(x => x.Title).Select(x => x.Title).ToList();

        Assert.Equal(["r1_ten", "r4_three"], withValue);

        // !HasValue must select both explicit null and missing; $eq/$ne partition every BSON value including
        // missing, so the negation renders as $not over $ne.
        Assert.Equal(["r2_null", "r3_missing"], withoutValue);

        // Every row must be in exactly one result set, which only holds if both absent states are handled.
        var all = db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => x.Title).ToList();
        Assert.Equal(4, all.Count);
        Assert.Empty(withValue.Intersect(withoutValue));
        Assert.Equal(all, withValue.Concat(withoutValue).OrderBy(t => t, StringComparer.Ordinal).ToList());

        // Whole-entity spelling, so routing isn't confounded by the bare projection.
        Assert.Equal(
            ["r2_null", "r3_missing"],
            db.Entities.AsNoTracking().Where(x => !x.Score.HasValue).OrderBy(x => x.Title)
                .ToList().Select(x => x.Title).ToList());
    }

    [Fact]
    public void Negated_HasValue_emits_not_over_ne_null()
    {
        var collection = SeedRagged(nameof(Negated_HasValue_emits_not_over_ne_null));

        using var db = CreateContextWithLogging(collection, MongoQueryMode.NativeOnly, out var spy);

        var titles = db.Entities.AsNoTracking().Where(x => !x.Score.HasValue)
            .OrderBy(x => x.Title).Select(x => x.Title).ToList();
        Assert.Equal(["r2_null", "r3_missing"], titles);

        // Stage-shape pin: the filter must be the complement form that selects missing as well as null.
        var mql = spy.GetLogMessageByEventId(MongoEventId.ExecutedMqlQuery)!;
        Assert.Contains("\"$not\"", mql);
        Assert.Contains("\"$ne\" : null", mql);
    }

    // ── 5. The real gate: parity with driver-LINQ over every ragged state ──────────

    [Fact]
    public void Parity_with_driver_linq_over_the_ragged_fixture()
    {
        var collection = SeedRagged(nameof(Parity_with_driver_linq_over_the_ragged_fixture));

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Score!.Value > 5).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Score!.Value < 5).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => !(x.Score!.Value > 5)).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Score!.Value == 10).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Score.HasValue).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => !x.Score.HasValue).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Score.HasValue && x.Score.Value > 5).OrderBy(x => x.Title).Select(x => x.Title).ToList());

        AssertParity(collection, db => db.Entities.AsNoTracking()
            .OrderBy(x => x.Score!.Value).ThenBy(x => x.Title).Select(x => x.Title).ToList());

        // Nullable bool .Value: the bare-boolean-member arm declines it (it could diverge from the driver's
        // rendering), so this pins parity over a shape that still falls back.
        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Flag!.Value).OrderBy(x => x.Title).Select(x => x.Title).ToList());
    }

    // ── 6. The mandatory late-decline leg ──────────────────────────────────────────

    [Fact]
    public void Parameterized_where_leg()
    {
        var collection = SeedRagged(nameof(Parameterized_where_leg));

        // A late decline in TryBuildNativeFactory, after the emit side committed a pushed-down projection, only
        // happens under Native (NativeOnly throws; DriverLinq never builds a native factory), so it needs its
        // own case.
        var prefix = "r";

        using var db = CreateContext(collection, MongoQueryMode.Native);

        // Nullable leaves first: an alias miss there is silent (null), while a non-nullable leaf throws and would
        // abort the test before the silent case is observed.
        Assert.Equal(
            [10, null, null, 3],
            db.Entities.AsNoTracking().Where(x => x.Title.StartsWith(prefix))
                .OrderBy(x => x.Title).Select(x => x.Score).ToList());

        // Bare `.Value` behind the same late decline: its $project alias is the document path rather than a member
        // name, so an alias miss would corrupt it silently. HasValue excludes the ragged rows.
        Assert.Equal(
            [10, 3],
            db.Entities.AsNoTracking().Where(x => x.Title.StartsWith(prefix) && x.Score.HasValue)
                .OrderBy(x => x.Title).Select(x => x.Score!.Value).ToList());

        Assert.Equal(
            ["r1_ten", "r4_three"],
            db.Entities.AsNoTracking().Where(x => x.Title.StartsWith(prefix) && x.Score.HasValue)
                .OrderBy(x => x.Title).Select(x => x.Title).ToList());

        Assert.Equal(
            ["r2_null", "r3_missing"],
            db.Entities.AsNoTracking().Where(x => x.Title.StartsWith(prefix) && !x.Score.HasValue)
                .OrderBy(x => x.Title).Select(x => x.Title).ToList());

        Assert.Equal(
            ["r1_ten"],
            db.Entities.AsNoTracking().Where(x => x.Title.StartsWith(prefix) && x.Score!.Value > 5)
                .OrderBy(x => x.Title).Select(x => x.Title).ToList());
    }

    // ── 7. Tripwires ───────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_wrapped_nullable_target_projection_leaf_now_goes_native_too()
    {
        var collection = SeedRagged(nameof(Convert_wrapped_nullable_target_projection_leaf_now_goes_native_too));

        // `(int?)x.Score.Value` is a Convert that TranslateOperand unwraps to a bare MongoFieldExpression, which
        // NativeProjectionBinder's cast gate admits when the original leaf was a Convert. It's the same field
        // access as the un-cast leaf, and Score has default serialization, so there's no read-side hazard.
        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(
                ["r1_ten=10", "r2_null=<null>", "r3_missing=<null>", "r4_three=3"],
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = (int?)x.Score!.Value})
                    .ToList().Select(a => $"{a.Title}={(a.V.HasValue ? a.V.Value.ToString() : "<null>")}").ToList());
        }
    }

    [Fact]
    public void HasValue_as_a_projection_leaf_still_declines_and_is_unchanged_by_this_slice()
    {
        var collection = SeedRagged(nameof(HasValue_as_a_projection_leaf_still_declines_and_is_unchanged_by_this_slice));

        // HasValue is only handled in predicate position (TranslateNode), so as a projection leaf it declines.
        // Deliberate: for a missing element DriverLinq answers True while CLR semantics (and, by $expr semantics,
        // a native rendering) answer False, so going native would diverge from the DriverLinq oracle — though it
        // would move toward CLR semantics. Pinned so a widening flips this tripwire.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, H = x.Score.HasValue}).ToList());
        }

        // Both take the fallback, so they agree.
        AssertParity(collection, db => db.Entities.AsNoTracking().OrderBy(x => x.Title)
            .Select(x => new {x.Title, H = x.Score.HasValue})
            .ToList().Select(a => $"{a.Title}={a.H}").ToList());
    }

    // ── 8. A value-converted `.Value` projection leaf reads through the converter ──

    /// <summary>
    /// Both the read side (<c>MongoProjectionBindingRemovingExpressionVisitor.TryResolveFieldAccess</c>) and the
    /// emit side (<c>MongoExpressionTranslator.TryResolveMember</c>) peel <c>Nullable&lt;T&gt;.Value</c>, so
    /// <c>x.Converted.Value</c> resolves to the same <see cref="IProperty"/> and reads through its converter. If
    /// the sides disagree, the raw stored value (14 instead of 7) is returned silently.
    /// </summary>
    [Fact]
    public void Value_converted_nullable_Value_projection_leaf_goes_native_and_reads_through_the_converter()
    {
        var collection = SeedConverted(
            nameof(Value_converted_nullable_Value_projection_leaf_goes_native_and_reads_through_the_converter));

        using var db = CreateConvertedContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(
            [7, 2],
            db.Entities.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => new {x.Title, V = x.Converted!.Value}).ToList().Select(a => a.V).ToList());

        Assert.Equal(
            [7, 2],
            db.Entities.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => x.Converted!.Value).ToList());
    }

    // ── 9. A non-nullable computed leaf over a null/missing nullable operand throws ──

    public static TheoryData<string> NullableOperandShapes => new() { "add", "mul", "div", "abs", "cond" };

    /// <summary>
    /// The server propagates a null <c>Score</c> through <c>$add</c>/<c>$multiply</c>/<c>$divide</c>/<c>$abs</c>/
    /// <c>$cond</c>, and a plain non-nullable alias read would take that null as <c>0</c>. EF (and driver-LINQ, which
    /// fails in its deserializer) throws instead, so the leaf is flagged <c>MongoProjection.ThrowsOnNull</c> and read
    /// as <c>int?</c> + <c>.Value</c> (<c>MongoAggregationExpressionRenderer.ClassifyNonNullableValueRead</c>).
    /// r2 has an explicit null Score, r3 omits it.
    /// </summary>
    [Theory]
    [MemberData(nameof(NullableOperandShapes))]
    public void Non_nullable_computed_leaf_over_null_or_missing_operand_throws(string shape)
    {
        var collection = SeedRagged(nameof(Non_nullable_computed_leaf_over_null_or_missing_operand_throws) + shape);

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() => Run(db, shape));
            Assert.Contains("Nullable object must have a value", ex.Message);
        }

        // The oracle throws too (its deserializer can't read BSON null as Int32); exception types legitimately differ.
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var ex = Assert.Throws<FormatException>(() => Run(db, shape));
            Assert.Contains("Cannot deserialize a 'Int32' from BsonType 'Null'", ex.ToString());
        }

        static List<int> Run(SingleEntityDbContext<Item> db, string shape)
        {
            var q = db.Entities.AsNoTracking().OrderBy(x => x.Title);
            return (shape switch
            {
                "add" => q.Select(x => x.Score!.Value + 1),
                "mul" => q.Select(x => (int)x.Score! * 2),
                "div" => q.Select(x => x.Score!.Value / 2),
                "abs" => q.Select(x => Math.Abs(x.Score!.Value)),
                _ => q.Select(x => x.Rank > 1 ? x.Score!.Value : 0),
            }).ToList();
        }
    }

    [Fact]
    public void Non_nullable_computed_leaf_inside_a_construction_over_null_operand_throws()
    {
        var collection = SeedRagged(nameof(Non_nullable_computed_leaf_inside_a_construction_over_null_operand_throws));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Score!.Value + 1}).ToList());
            Assert.Contains("Nullable object must have a value", ex.Message);
        }
    }

    [Fact]
    public void Non_nullable_computed_leaf_over_a_proven_or_coalesced_operand_is_unchanged()
    {
        var collection = SeedRagged(nameof(Non_nullable_computed_leaf_over_a_proven_or_coalesced_operand_is_unchanged));

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            var q = db.Entities.AsNoTracking().OrderBy(x => x.Title);
            Assert.Equal([11, 1, 1, 4], q.Select(x => (x.Score ?? 0) + 1).ToList());
            // Well-formed rows only: the flag changes nothing for a value that is present.
            Assert.Equal([11, 4], q.Where(x => x.Rank <= 2).Select(x => x.Score!.Value + 1).ToList());
        }

        // The test proves Score non-null in the true branch, so the leaf stays a plain read.
        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native})
        {
            using var db = CreateContext(collection, mode);
            Assert.Equal(
                [11, 0, 0, 4],
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => x.Score != null ? x.Score.Value + 1 : 0).ToList());
        }

        // Driver-LINQ renders the test as a bare aggregation `$ne: ["$Score", null]`, which is true for a MISSING
        // Score (missing != null in the aggregation dialect), so r3 takes the true branch, `$add` answers null, and its
        // Int32 deserializer throws. Not this slice's concern; pinned so a driver fix is noticed.
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            var ex = Assert.Throws<FormatException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => x.Score != null ? x.Score.Value + 1 : 0).ToList());
            Assert.Contains("Cannot deserialize a 'Int32' from BsonType 'Null'", ex.ToString());
        }
    }

    // Distinct carries the flag onto its key part and flattened alias (NativeGroupByBinder), so it stays native and
    // the deduped null still throws rather than reading 0.
    [Fact]
    public void Distinct_over_a_flagged_computed_leaf_stays_native_and_throws_on_null()
    {
        var collection = SeedRagged(nameof(Distinct_over_a_flagged_computed_leaf_stays_native_and_throws_on_null));

        // Well-formed rows only.
        Assert.Equal(
            [4, 11],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return db.Entities.AsNoTracking().Where(x => x.Rank <= 2)
                    .Select(x => x.Score!.Value + 1).Distinct().AsEnumerable().Order().ToList();
            }));

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                db.Entities.AsNoTracking().Select(x => x.Score!.Value + 1).Distinct().ToList());
            Assert.Contains("Nullable object must have a value", ex.Message);
        }
    }

    // A bare `.Value` leaf is read through the property's binding, which handles the null itself; it is not flagged, so
    // an aggregate over it stays native. Pins ClassifyNonNullableValueRead's bare-field arm.
    [Fact]
    public void Max_over_a_bare_Value_leaf_stays_native()
    {
        var collection = SeedRagged(nameof(Max_over_a_bare_Value_leaf_stays_native));

        // Well-formed rows only.
        Assert.Equal(
            [10],
            NativeModeAssert.NativeAndParity(mode =>
            {
                using var db = CreateContext(collection, mode);
                return new List<int> {db.Entities.AsNoTracking().Where(x => x.Rank <= 2).Select(x => x.Score!.Value).Max()};
            }));
    }

    // Max over a flagged bare leaf would reduce an all-null input to null and read it as 0, so it declines (through the
    // bare Select's flagged `_v` reference); the fallback agrees with driver-LINQ.
    [Fact]
    public void Max_over_a_flagged_computed_leaf_declines_cleanly()
    {
        var collection = SeedRagged(nameof(Max_over_a_flagged_computed_leaf_declines_cleanly));

        // Well-formed rows only, so the fallback returns a value to compare.
        Assert.Equal(
            [11],
            NativeModeAssert.DeclinesCleanly(mode =>
            {
                using var db = CreateContext(collection, mode);
                return new List<int> {db.Entities.AsNoTracking().Where(x => x.Rank <= 2).Select(x => x.Score!.Value + 1).Max()};
            }));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    // Runs the query under Native and DriverLinq and asserts equal results. Both legs must return: comparing
    // only the fact of throwing would pass for two unrelated failures, and exception types can legitimately
    // differ (see Value_in_a_projection_goes_native).
    private void AssertParity<T>(IMongoCollection<Item> collection, Func<SingleEntityDbContext<Item>, List<T>> query)
    {
        var (nativeOk, nativeResult, nativeError) = Attempt(collection, MongoQueryMode.Native, query);
        var (driverOk, driverResult, driverError) = Attempt(collection, MongoQueryMode.DriverLinq, query);

        if (!nativeOk || !driverOk)
        {
            Assert.Fail(
                "AssertParity requires BOTH legs to return a result; it does not compare failures. "
                + $"Native: {Describe(nativeOk, nativeError)}. DriverLinq: {Describe(driverOk, driverError)}. "
                + "If this shape is meant to throw, assert its exception type per path (see "
                + "Value_in_a_projection_goes_native) instead of routing it through AssertParity.");
        }

        Assert.Equal(driverResult, nativeResult);
    }

    private static string Describe(bool ok, Exception? error)
        => ok ? "returned" : $"threw {error!.GetType().Name}: {error.Message}";

    private (bool Ok, List<T>? Result, Exception? Error) Attempt<T>(
        IMongoCollection<Item> collection, MongoQueryMode mode, Func<SingleEntityDbContext<Item>, List<T>> query)
    {
        using var db = CreateContext(collection, mode);
        try
        {
            return (true, query(db), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex);
        }
    }

    // r1/r4 have values, r2 is explicitly null, r3 omits the elements. The seed self-checks the stored shape,
    // since missing vs. null is invisible in results and a degraded seed would make the !HasValue tests vacuous.
    private IMongoCollection<Item> SeedRagged(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "r1_ten"}, {"Rank", 1}, {"Score", 10}, {"Flag", true}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "r2_null"}, {"Rank", 3}, {"Score", BsonNull.Value}, {"Flag", BsonNull.Value}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "r3_missing"}, {"Rank", 4}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "r4_three"}, {"Rank", 2}, {"Score", 3}, {"Flag", false}}
        ]);

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList().ToDictionary(d => d["Title"].AsString);
        Assert.Equal(4, stored.Count);
        Assert.Equal(10, stored["r1_ten"]["Score"].AsInt32);
        Assert.True(stored["r2_null"]["Score"].IsBsonNull);
        Assert.True(stored["r2_null"]["Flag"].IsBsonNull);
        Assert.False(stored["r3_missing"].Contains("Score"));
        Assert.False(stored["r3_missing"].Contains("Flag"));
        Assert.Equal(3, stored["r4_three"]["Score"].AsInt32);

        return database.MongoDatabase.GetCollection<Item>(raw.CollectionNamespace.CollectionName);
    }

    // Converter is v * 2 to store / v / 2 to read, so stored 14 is CLR 7. A value-transforming converter is
    // needed: re-encoding converters survive a raw read only because the driver's deserializers are lenient.
    // Stored values all differ from their CLR values, so a raw read is never mistaken for a correct one.
    private IMongoCollection<ConvertedItem> SeedConverted(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "c1"}, {"Converted", 14}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "c2"}, {"Converted", 4}}
        ]);

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList().ToDictionary(d => d["Title"].AsString);
        Assert.Equal(14, stored["c1"]["Converted"].AsInt32);
        Assert.Equal(4, stored["c2"]["Converted"].AsInt32);

        return database.MongoDatabase.GetCollection<ConvertedItem>(raw.CollectionNamespace.CollectionName);
    }

    private static SingleEntityDbContext<ConvertedItem> CreateConvertedContext(
        IMongoCollection<ConvertedItem> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<ConvertedItem>()
                .Property(x => x.Converted)
                .HasConversion(new ValueConverter<int, int>(v => v * 2, v => v / 2)),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // FunctionalTests has no TestMqlLoggerFactory/AssertMql, so MQL is captured through SpyLoggerProvider.
    private static SingleEntityDbContext<Item> CreateContextWithLogging(
        IMongoCollection<Item> collection, MongoQueryMode mode, out SpyLoggerProvider spyLogger)
    {
        var (loggerFactory, provider) = SpyLoggerProvider.Create();
        spyLogger = provider;

        return SingleEntityDbContext.Create(
            collection,
            loggerFactory,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                b.EnableSensitiveDataLogging();
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
}
