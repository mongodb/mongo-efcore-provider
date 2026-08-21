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
/// EF-322 stream 1, slice A5 (EF-400) — <c>Nullable&lt;T&gt;.Value</c> peels to the underlying field and
/// <c>Nullable&lt;T&gt;.HasValue</c> becomes the node an explicit <c>!= null</c> already produced, in predicate,
/// sort-key and projection position. Routing is proven by <see cref="MongoQueryMode.NativeOnly"/>, never by MQL
/// shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unlike slice A2, this slice CAN change results, so the governing oracle is <c>Native == DriverLinq</c>,
/// not in-memory LINQ.</b> In-memory LINQ throws <c>InvalidOperationException("Nullable object must have a
/// value")</c> for <c>x.Score.Value</c> the moment <c>Score</c> is null; a server-side <c>$match</c>/<c>$sort</c>
/// over a null or missing element does not, and never did — the driver-LINQ path this slice replaces answered
/// the same way. That divergence is DOCUMENTED, not fixed (precedent: the EF-359 owner ruling in
/// <c>Query/AGENTS.md</c>). <see cref="Parity_with_driver_linq_over_the_ragged_fixture"/> is the slice's real
/// gate.
/// </para>
/// <para>
/// <b>The fixture is ragged and un-masked</b>: every nullable property carries three states — a value, an
/// explicit BSON <c>null</c>, and a MISSING element (raw-inserted). "Missing" and "present but null" are
/// otherwise indistinguishable from results alone, and <c>!HasValue</c> has to select BOTH.
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
    /// A SEPARATE entity, deliberately not folded into <see cref="Item"/>: the whole point of this fixture is a
    /// property carrying a VALUE-TRANSFORMING converter, and adding one to <see cref="Item"/> would change the
    /// model every other test in this class runs against.
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

        // A relational comparison is TYPE-BRACKETED server-side: neither a stored null nor a missing element
        // matches $gt, so the ragged rows are simply absent — the same answer driver-LINQ gives (Step 1 probe),
        // and NOT the InvalidOperationException in-memory LINQ raises for the same lambda.
        Assert.Equal(
            ["r1_ten"],
            db.Entities.AsNoTracking().Where(x => x.Score!.Value > 5).OrderBy(x => x.Title)
                .Select(x => x.Title).ToList());

        // The opposite direction is bracketed too, so the ragged rows are absent from BOTH — which is exactly
        // why { $gt: 5 } and { $lte: 5 } do not partition, and why MongoExpressionNegator $not-WRAPS a
        // relational comparison instead of inverting it.
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

        // A WHOLE-ENTITY result, so routing is proven without the bare-projection leg confounding it.
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

        // BSON sort order is Null < Numbers, and a MISSING element sorts as null — so the two ragged rows lead,
        // tie with each other, and the ThenBy makes that tie deterministic. Measured identical under DriverLinq.
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
            // Over the WELL-FORMED rows the projection goes native and returns the values.
            Assert.Equal(
                ["r1_ten=10", "r4_three=3"],
                db.Entities.AsNoTracking().Where(x => x.Rank <= 2).OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Score!.Value})
                    .ToList().Select(a => $"{a.Title}={a.V}").ToList());

            // The BARE spelling too (EF-322 step 3a made a bare body native; its $project alias is the leaf's
            // own document path, "Score").
            Assert.Equal(
                [3, 10],
                db.Entities.AsNoTracking().Where(x => x.Rank <= 2).OrderBy(x => x.Score!.Value)
                    .Select(x => x.Score!.Value).ToList());
        }

        // Over the RAGGED rows the non-nullable target has nowhere to put a null, and BOTH paths throw — which
        // is the disposition Step 1's probe measured for DriverLinq and which the decision rule requires native
        // to match (throw or decline, never a silently different answer).
        //
        // The EXACT exception TYPE is asserted PER PATH rather than through a catch-all, because a catch-all
        // here would also pass on a connection error or an unrelated NullReferenceException — an absence-shaped
        // assertion over the one behaviour this slice most needed to get right (native must NOT silently return
        // 0). The two types differ by design and both were measured: the driver fails inside its own
        // deserializer (FormatException: "Cannot deserialize a 'Int32' from BsonType 'Null'"), native fails in
        // the DOM shaper's required-element check (InvalidOperationException: "Document element 'V' is missing
        // but required"). Per the versioning rubric the exception type of an erroneous input is not contract,
        // and the released 8.4.2/9.1.2/10.0.2 packages throw here too (see the break check in the AGENTS.md
        // note), so this pins the measurement rather than promising an API.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.NativeOnly})
        {
            using var db = CreateContext(collection, mode);
            var ex = Assert.Throws<InvalidOperationException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Score!.Value}).ToList());
            Assert.Contains("missing but required", ex.Message);
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

        // The load-bearing half: !HasValue must select the explicit BSON null AND the MISSING element. It does
        // because $ne/$eq partition every BSON value including missing, which is why the negation renders as
        // $not over $ne rather than as an inverted relational operator.
        Assert.Equal(["r2_null", "r3_missing"], withoutValue);

        // Stated as a PARTITION rather than as two lists, because that is the property that is only true when
        // both absent states are handled: every row is in exactly one of the two result sets.
        var all = db.Entities.AsNoTracking().OrderBy(x => x.Title).Select(x => x.Title).ToList();
        Assert.Equal(4, all.Count);
        Assert.Empty(withValue.Intersect(withoutValue));
        Assert.Equal(all, withValue.Concat(withoutValue).OrderBy(t => t, StringComparer.Ordinal).ToList());

        // And the whole-entity spelling, so routing is not confounded by the bare projection.
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

        // A STAGE-SHAPE pin, not a routing proof (the NativeOnly mode above is the routing proof): the emitted
        // filter must be the complement form that selects missing as well as null.
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

        // A nullable BOOL .Value: the peel resolves the receiver, and the bare-boolean-member arm then declines
        // it because a nullable bool bare access could diverge from the driver's rendering. The peel must not
        // open that hole — so this is a parity assertion over a shape that still FALLS BACK.
        AssertParity(collection, db => db.Entities.AsNoTracking()
            .Where(x => x.Flag!.Value).OrderBy(x => x.Title).Select(x => x.Title).ToList());
    }

    // ── 6. The mandatory late-decline leg ──────────────────────────────────────────

    [Fact]
    public void Parameterized_where_leg()
    {
        var collection = SeedRagged(nameof(Parameterized_where_leg));

        // A captured local inside string.StartsWith has no native regex rendering, so TryBuildNativeFactory
        // declines LATE — after the emit side has already committed a pushed-down projection — under the DEFAULT
        // Native mode. That route exists in neither NativeOnly (which throws on the decline) nor DriverLinq
        // (which never builds a native factory), so it needs its own case.
        var prefix = "r";

        using var db = CreateContext(collection, MongoQueryMode.Native);

        // NULLABLE leaves first, deliberately: an alias miss on a nullable leaf is SILENT (null, no exception),
        // while a non-nullable leaf throws — and ToList() materializes eagerly, so a loud query run first would
        // abort the test before either silent row was observed.
        Assert.Equal(
            [10, null, null, 3],
            db.Entities.AsNoTracking().Where(x => x.Title.StartsWith(prefix))
                .OrderBy(x => x.Title).Select(x => x.Score).ToList());

        // A BARE `.Value` projection behind the same late decline — the leaf whose $project alias is chosen by
        // the provider (the leaf's document path) rather than by a member name, i.e. the one an alias miss would
        // corrupt silently. Guarded by HasValue so the ragged rows are excluded and the result is well-defined.
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

    // ── 7. Tripwires: the two sub-shapes this slice deliberately leaves on fallback ─

    [Fact]
    public void Convert_wrapped_nullable_target_projection_leaf_still_declines_and_still_returns_correct_values()
    {
        var collection = SeedRagged(nameof(Convert_wrapped_nullable_target_projection_leaf_still_declines_and_still_returns_correct_values));

        // `(int?)x.Score.Value` arrives as a Convert around the member access, and NativeProjectionBinder's
        // plain-field gate admits a MemberExpression (or an EF.Property call), not a UnaryExpression — so the
        // projection declines as a WHOLE and falls back. Deliberately not widened here: the fallback returns the
        // correct values, and widening the gate to unwrap a Convert would change which leaf kinds reach the
        // shaper for every projection, not just this one.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = (int?)x.Score!.Value}).ToList());
        }

        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
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

        // The HasValue arm lives in TranslateNode, the PREDICATE entry point — TryTranslateField (the projection
        // and sort-key entry point) reaches TryResolveMember, which has no HasValue arm — so a HasValue
        // PROJECTION leaf still declines and still falls back.
        //
        // Keeping it that way is deliberate, and the DIRECTION of the reasoning matters. MEASURED (Step 1 probe
        // row 8): for a MISSING element, DriverLinq answers True here and in-memory LINQ answers False. Going
        // native would not "inherit" that divergence — it would CREATE a new one: an aggregation-expression
        // rendering evaluates a missing path as null, so native would answer False, AGREEING with CLR semantics
        // and DISAGREEING with DriverLinq. (The measured halves are the two answers above; that native would
        // render it that way is reasoned from $expr semantics, not measured — nothing was built.) Under this
        // slice's declared oracle, Native == DriverLinq, that is a divergence and declining is correct — but a
        // future ticket that changes the oracle for this shape should know it would be moving TOWARD CLR
        // semantics, not away from them. Pinned so such a widening flips a tripwire.
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, H = x.Score.HasValue}).ToList());
        }

        // Native and DriverLinq agree with each other (both take the fallback), which is the oracle that governs.
        AssertParity(collection, db => db.Entities.AsNoTracking().OrderBy(x => x.Title)
            .Select(x => new {x.Title, H = x.Score.HasValue})
            .ToList().Select(a => $"{a.Title}={a.H}").ToList());
    }

    // ── 8. EF-400 fix wave: a VALUE-CONVERTED `.Value` projection leaf must not read the RAW stored value ──

    /// <summary>
    /// The defect this slice's final fix wave closed, and the tripwire for the follow-up that will reopen the
    /// shape properly (**EF-402**).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Mechanism.</b> The EMIT side peels <c>.Value</c> (this slice's own change to
    /// <c>MongoExpressionTranslator.TryResolveMember</c>), so <c>x.Converted.Value</c> addresses the same field
    /// as <c>x.Converted</c>. The READ side does not:
    /// <c>MongoProjectionBindingRemovingExpressionVisitor.TryResolveFieldAccessSource</c> recognises a
    /// <c>StructuralTypeShaperExpression</c> but not a <c>MemberExpression</c> wrapping one, so <c>Property</c>
    /// comes back null and the read falls to <c>BsonBinding.GetElementValue&lt;T&gt;</c>, which builds a DEFAULT
    /// type serializer and discards the converter. <c>NativeProjectionBinder</c>'s pre-existing converter guard
    /// keyed on a DOTTED element path, and a top-level <c>.Value</c> leaf has no dot, so it did not fire.
    /// </para>
    /// <para>
    /// <b>Measured before the fix</b> (stored 14, correct CLR 7): <c>new { V = x.Converted.Value }</c> and the
    /// bare <c>x.Converted.Value</c> both returned <b>14</b> under <c>Native</c> AND <c>NativeOnly</c> —
    /// silently, under the default mode — while <c>new { V = x.Converted }</c> and a whole-entity read both
    /// returned the correct 7.
    /// </para>
    /// <para>
    /// <b>The fix is a DECLINE, not a repair</b> — the projection falls back to driver-LINQ, which for this
    /// mapping throws, exactly as it does under explicit <c>DriverLinq</c> and exactly as the released packages
    /// do. Teaching the read side to peel <c>.Value</c> so emit and read agree by construction is <b>EF-402</b>;
    /// when it lands, the guard's <c>.Value</c> disjunct goes away and this test is replaced by one asserting
    /// the shape goes native and returns 7.
    /// </para>
    /// <para>
    /// Assertions are written as an outcome STRING rather than <c>Assert.Throws</c> so a regression's failure
    /// message shows the values that came back ("returned 14,4") instead of only naming a missing exception.
    /// The scope is a value-TRANSFORMING converter: <c>HasBsonRepresentation</c> and re-encoding converters
    /// survive the raw read because the driver's default scalar deserializers are lenient about encoding, which
    /// is luck rather than design — so the guard is deliberately keyed on
    /// <c>HasDefaultKeySerialization</c>, not on a narrower "transforming converter" test.
    /// </para>
    /// </remarks>
    [Fact]
    public void Value_converted_nullable_Value_projection_leaf_declines_instead_of_reading_the_raw_stored_value()
    {
        var collection = SeedConverted(
            nameof(Value_converted_nullable_Value_projection_leaf_declines_instead_of_reading_the_raw_stored_value));

        // CONTROLS FIRST, and they are load-bearing: they prove the converter is actually live on this fixture,
        // so the two decline assertions below cannot pass vacuously against a model where nothing is converted.
        // Stored 14/4 → CLR 7/2 on both the plain-member projection leaf and a whole-entity read.
        using (var db = CreateConvertedContext(collection, MongoQueryMode.Native))
        {
            Assert.Equal("returned 7,2", Outcome(() => db.Entities.AsNoTracking().OrderBy(x => x.Title)
                .Select(x => new {x.Title, V = x.Converted}).ToList().Select(a => a.V).ToList()));

            Assert.Equal("returned 7,2", Outcome(() => db.Entities.AsNoTracking().OrderBy(x => x.Title)
                .ToList().Select(x => x.Converted).ToList()));
        }

        // NativeOnly: the guard DECLINES the projection, so the gate refuses the driver-LINQ fallback and
        // throws. Pre-fix this returned 14,4.
        using (var db = CreateConvertedContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal("threw NativeTranslationNotSupportedException",
                Outcome(() => db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Converted!.Value}).ToList().Select(a => a.V).ToList()));

            Assert.Equal("threw NativeTranslationNotSupportedException",
                Outcome(() => db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => x.Converted!.Value).ToList()));
        }

        // Native and DriverLinq must now AGREE, which is this slice's declared oracle. Both throw
        // NullReferenceException: the decline falls back to driver-LINQ, whose own LINQ v3 translation of
        // `.Value` over a value-converted nullable serializer fails. The exception TYPE is not the point and is
        // not contract (the rubric excludes the exception type of an unsupported shape) — the point is that
        // Native no longer answers 14 where DriverLinq refuses to answer at all. Pre-fix, Native returned 14,4
        // here while DriverLinq threw exactly as it does now.
        foreach (var mode in new[] {MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateConvertedContext(collection, mode);

            Assert.Equal("threw NullReferenceException",
                Outcome(() => db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => new {x.Title, V = x.Converted!.Value}).ToList().Select(a => a.V).ToList()));

            Assert.Equal("threw NullReferenceException",
                Outcome(() => db.Entities.AsNoTracking().OrderBy(x => x.Title)
                    .Select(x => x.Converted!.Value).ToList()));
        }
    }

    // Describes an outcome as a string so a regression reports the VALUES it returned rather than only the
    // absence of an expected throw — the difference between "Assert.Throws failed" and "returned 14,4".
    private static string Outcome<T>(Func<List<T>> query)
    {
        try
        {
            return "returned " + string.Join(",", query());
        }
        catch (Exception ex)
        {
            return "threw " + ex.GetType().Name;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    // Runs the same query under Native and DriverLinq and asserts they agree — the oracle for this slice.
    //
    // BOTH legs must RETURN; a throw on either side fails the helper. An earlier version compared only the
    // fact-of-throwing, which made the throw-vs-throw case VACUOUS: two legs failing for entirely unrelated
    // reasons (a connection error on one, a translation bug on the other) would have been reported as
    // "parity". Every shape routed through here today returns values on both legs, so nothing is lost by
    // requiring it — and the next person to add a THROWING shape is forced to decide explicitly what parity
    // means for it rather than inheriting a silent pass. Deliberately NOT "assert the exception types match":
    // this file already contains a measured shape where they legitimately DIFFER (the non-nullable projection
    // target in Value_in_a_projection_goes_native — FormatException from the driver's deserializer vs
    // InvalidOperationException from the DOM shaper), so a matching-types rule would be wrong, not stricter.
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

    // Four rows, three states for every nullable property: a value, an explicit BSON null, and a MISSING
    // element. r1/r4 carry values (two, so HasValue is not a one-row assertion), r2 is explicitly null and r3
    // omits the elements entirely. The seed self-checks the stored shape, because "missing" and "present but
    // null" are indistinguishable from results alone and an un-self-checked seed could silently degrade to two
    // states — which would make every !HasValue assertion here vacuous.
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

    // Three rows for the value-converted fixture. The converter is v => v * 2 (to store) / v => v / 2 (to read),
    // so a STORED 14 is a CLR 7 — a value-TRANSFORMING converter, which is the class this guard is about
    // (HasBsonRepresentation and re-encoding converters happen to survive the raw read because the driver's
    // default scalar deserializers are lenient about encoding; that is luck, not design). The stored numbers
    // are all even and all differ from their CLR values, so a raw read is never mistakable for a correct one.
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

    // MQL-capture idiom mirrored from NativeBareProjectionTests: FunctionalTests has no TestMqlLoggerFactory /
    // AssertMql (those live in the SpecificationTests project), so MQL is captured through SpyLoggerProvider.
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
