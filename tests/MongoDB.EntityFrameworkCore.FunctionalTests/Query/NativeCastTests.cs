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

using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-403 (slice A1, Task 2) — the silent-wrong-order defect fix. Before this fix, <c>TryTranslateField</c>
/// called the blanket <c>Unwrap</c>, which strips ANY <c>Convert</c>/<c>ConvertChecked</c> unconditionally, so
/// a narrowing or signed/unsigned cast in a sort key was silently discarded and <c>$sort</c> ordered by the RAW
/// STORED VALUE — a genuine order defect, not merely a missed optimization. These tests pin the fix: a
/// narrowing cast now declines at <c>TryTranslateField</c> and falls through to slice B's
/// <c>TryTranslateValue</c> path (which, because Task 3 has not shipped yet, also declines — so under
/// <see cref="MongoQueryMode.NativeOnly"/> the query throws, and under the default <see cref="MongoQueryMode.Native"/>
/// it falls back to driver-LINQ and returns the CORRECT order).
/// </summary>
/// <remarks>
/// Cases 5–8 (Task 3) cover <c>MongoConvertExpression</c> — the <c>$toX</c> node that turns Task 2's decline
/// into a render. Case 5 is the DIRECT continuation of case 1: the same narrowing sort key now goes native
/// instead of declining. Case 6 covers the spike's §3.1 field-to-field/arithmetic-operand shapes. Case 7 pins
/// that a cast to a target MQL cannot express (<c>short</c>/<c>uint</c>/<c>float</c>) still declines — the
/// admissible set is bounded by MQL, not by taste. Case 8 pins the ONE thing this task must never soften:
/// <c>MongoConvertExpression</c> must stay OUT of <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c>,
/// because <c>$expr</c> is a hard server error inside <c>$elemMatch</c>.
/// <para>
/// Cases 9–12 (Task 4) cover the OTHER decline site — <c>TranslateComparison</c>'s query-native branch and its
/// <c>HasNumericConvert</c> guard, which the spike measures as carrying 3.5× the decline volume of
/// <c>TranslateOperand</c>'s <c>Convert</c> branch. Case 9 is the silent-wrong-data pin (a fractional constant
/// truncated to the stored integral type returns an extra row). Case 10 pins the emitted constant's type.
/// Case 11 pins <c>Native == DriverLinq</c>. Case 12 is the fixture the spike did NOT have: it separates
/// <c>HasDefaultKeySerialization</c> from "the property's CLR type is not an enum", which §11 left UNVERIFIED.
/// Case 13 covers the OTHER sub-family that conjunct protects — a value-TRANSFORMING converter, whose failure
/// mode is silently wrong rows rather than case 12's zero rows. Case 14 measures the one admitted pair that is
/// NOT value-preserving (<c>long</c> → <c>double</c> above 2^53) and pins it as an EF-359-family accepted
/// divergence: native equals driver-LINQ, and both differ from in-memory LINQ.
/// </para>
/// <para>
/// Cases 15–16 (Task 5) cover the SECOND arm added to <c>HasNumericConvert</c>'s classification —
/// IDENTITY-LIKE converts (enum ↔ its own underlying type, <c>char</c> → <c>int</c>, boxing to <c>object</c>).
/// Unlike the widening arm (cases 9–14), an identity-like convert must keep the PROPERTY's serializer for the
/// constant, not the comparison's — that is how an enum-as-string constant renders as <c>"Active"</c> instead
/// of a raw number. Case 15 is the enum case, asserting VALUES (not just routing) because it also flips the
/// previously-locked <c>NativeGateRoutingTests.A_enum_as_string_where_equals_routing</c> pin. Case 16 covers
/// <c>char</c> → <c>int</c> and boxing to <c>object</c>.
/// </para>
/// <para>
/// Cases 17–19 are Task 5 fix-round additions (see the branch history for detail): the sub-int-backed enum
/// promotion gap, the enum-to-floating-cast crash-instead-of-decline fix, and a flag-precedence regression
/// pin for boxing over a widening cast.
/// </para>
/// <para>
/// Cases 20–24 (Task 6) cover the PROJECTION-leaf gate — a cast as a <c>Select</c> leaf, both wrapped
/// (<c>new { X = (int)x.D }</c>) and bare (<c>Select(x =&gt; (int)x.D)</c>). Widening the projection-leaf gate
/// exposed the SAME class of A2-lesson defect the <c>EF.Property</c> slice found: the shaper-building visitor
/// dropped the <c>Convert</c> node when registering the leaf, so the read side misread the converted value
/// through the pre-cast member's own serializer; both sides are fixed together (see
/// <c>MongoProjectionBindingExpressionVisitor.Visit</c> and
/// <c>MongoProjectionBindingRemovingExpressionVisitor.VisitExtension</c>). Case 20 is the wrapped leaf going
/// native; case 21 is its mandatory parameterized-<c>Where</c> late-decline leg. Case 22 is the bare leaf's
/// graceful decline (no document path to alias a computed leaf under); case 23 is its parameterized-<c>Where</c>
/// leg, mandatory because the bare-alias mechanism's failure mode elsewhere in this file is silent. Case 24
/// mutation-verifies the node-kind gate: it is UNCONDITIONAL on <c>leafExpression</c>'s own shape (mirroring
/// the count branch, not the arithmetic branch's structural pre-filter), so a BARE constant/parameter leaf —
/// no cast involved at all — reaches it and translates to something other than
/// <c>MongoConvertExpression</c> (a <c>MongoParameterExpression</c>) and must not be admitted.
/// </para>
/// <para>
/// Cases 25–26 (Task 6, fix round 1) close two review findings. Case 25 pins the dependency on Guard B
/// (<c>MongoExpressionTranslator.AllFieldsDefaultSerialized</c>) — a cast over a value-converted property (or
/// one with a non-default <c>BsonRepresentation</c>) never reaches the raw-alias read case 20 installed; it
/// declines at translate time, one layer up, and the fallback correctly applies the converter before casting.
/// Case 26 pins a deliberate BOUNDARY, not a residual: a WIDENING cast (<c>(long)x.I</c>) is unwrapped entirely
/// by <c>TranslateOperand</c> rather than wrapped in a <c>MongoConvertExpression</c>, so it still declines and
/// falls back gracefully — admitting it would need its own measurement of what CLR type to read the raw field
/// back as, deliberately not taken up in this round.
/// </para>
/// <para>
/// Cases 27–29 (Task 7) cover the SITE-B FALL-THROUGH: <c>TranslateComparison</c>'s query-native branch no
/// longer VETOES a comparison whose cast it cannot absorb — it declines only that branch, and control falls
/// through to the general <c>$expr</c> path, where Task 3's <c>MongoConvertExpression</c> renders it.
/// <b>Case 27 is the owner-ruled divergence pin, and the most important test in this file to read before
/// changing anything here:</b> for a narrowing cast against a constant, native now returns the CLR answer and
/// driver-LINQ returns a DIFFERENT one, deliberately — the OPPOSITE of the EF-359 accepted-divergence family
/// that case 14 covers. Case 28 is the mirrored operand order (member on the right), which reaches the same
/// fall-through through the second of the two classification sites, and emits without mirroring the operator.
/// Case 29 pins the DISPOSITION of a narrowing cast over a value-converted property: it must not fall through
/// to <c>$expr</c>. The silent-wrong-rows measurement behind that is real (see the case header), but
/// <b>case 29 does not individually net EITHER guard, and the "load-bearing, not defence-in-depth" wording
/// that used to sit here is withdrawn rather than annotated beside</b> — the fix round below moved the real
/// guard down to <c>MongoExpressionTranslator.TranslateOperand</c>'s convert branch, which SUBSUMES
/// <c>MongoExpressionTranslator.CanFallThroughToExpr</c> (MEASURED: forcing that method to return
/// <c>true</c> turns 0 of 33 functional and 0 of 121 unit tests red). Case 30 is what individually nets the
/// deeper guard. Case 18 also changed disposition in this task (its enum → floating shape now goes native
/// rather than declining, correctly and with no divergence); see its own comment.
/// </para>
/// <para>
/// Cases 30–31 (Task 7, fix round 1) close the SAME hazard one level down, on the FIELD-TO-FIELD shape, which
/// case 29's site-scoped guard did not cover. Review traced it to Task 3 of this slice (<c>94101da5</c>,
/// unreleased) rather than to EF-329, making it a within-slice REGRESSION to close rather than an inherited
/// exposure to defer — MEASURED against the slice base, where the same query threw under default
/// <c>Native</c> and, with Task 3's branch, silently returned a wrong row. The guard now sits on
/// <c>TranslateOperand</c>'s own convert branch, so every caller inherits it; case 30 is the tripwire and
/// case 31 the control that keeps it from being satisfied by declining every cast operand.
/// </para>
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeCastTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double D { get; set; }
        public int I { get; set; }
    }

    // Rows a..e. D is the double that makes truncation observable; I is the int that makes the signed/unsigned
    // and narrowing reinterpretations observable.
    //
    //   a: D =  1.6, I =        1
    //   b: D =  1.4, I =        2
    //   c: D =  2.5, I =        3
    //   d: D = -1.5, I =       -1          // negative: (uint) reinterprets it as huge
    //   e: D =  0.5, I =    50000          // > short.MaxValue AND >= 32768, so (short) wraps NEGATIVE
    //
    // Every order below is COMPUTED from these seeded values (not copied from the task brief's own worked
    // comment) — see the divergence note right after the table.
    //
    // raw-D order (ascending by D):         d(-1.5), e(0.5), b(1.4), a(1.6), c(2.5)  -> d, e, b, a, c
    // (int)D order (ascending, truncating):  d(-1),  e(0),  {a,b}=1 tie, c(2)        -> d, e, a, b, c
    //     (1.6 and 1.4 both truncate to 1 and tie — this is the genuine tie case 1 relies on. In-memory LINQ's
    //     OrderBy is stable, so it resolves the tie to insertion order (a before b) on its own; but MongoDB's
    //     $sort makes NO tie-order guarantee at all, so case 1's server-side legs (Native/DriverLinq/NativeOnly)
    //     add an explicit .ThenBy(x => x.Label) tiebreaker — "a" < "b" alphabetically, so it resolves the SAME
    //     way as insertion order here, but by a rule the server actually honors rather than one it doesn't.)
    // raw-I order (ascending by I):          d(-1), a(1), b(2), c(3), e(50000)       -> d, a, b, c, e
    // (uint)I order (ascending, unsigned reinterpretation):
    //     a=1, b=2, c=3, e=50000, d=4294967295 (unchecked (uint)(-1))                -> a, b, c, e, d
    //     (a genuine REVERSAL versus raw-I order: d moves from FIRST to LAST.)
    // (short)I order (ascending, narrowing truncation to 16 bits):
    //     a=1, b=2, c=3, d=-1, e=-15536 (50000 mod 65536 = 50000 >= 32768, so two's complement is 50000-65536)
    //                                                                                -> e, d, a, b, c
    //     (also a genuine REVERSAL versus raw-I order: e moves from LAST to FIRST.)
    //
    // DIVERGENCE FROM THE TASK BRIEF'S OWN WORKED COMMENT, found by computing rather than copying: the brief
    // seeded e's I as 70000 and asserted "(short) wraps NEGATIVE". That arithmetic is WRONG — 70000 mod 65536 =
    // 4464, which is POSITIVE as a signed 16-bit value (bit 15 is not set), so (short)70000 does NOT go
    // negative and the resulting (short)I order would be IDENTICAL to raw-I order (d, a, b, c, e) — a TIE with
    // the raw order, not the genuine reversal the case is supposed to exercise. e's I is seeded here as 50000
    // instead (50000 mod 65536 = 50000, which IS >= 32768, so it genuinely wraps to -15536), which reproduces
    // exactly the final order the brief's own prose concluded ("e, d, a, b, c") — so the brief's SEED VALUE was
    // wrong but its CONCLUSION was right; this fixture's arithmetic is what actually backs that conclusion.
    private static readonly (string Label, double D, int I)[] Rows =
    [
        ("a", 1.6, 1),
        ("b", 1.4, 2),
        ("c", 2.5, 3),
        ("d", -1.5, -1),
        ("e", 0.5, 50000)
    ];

    private static readonly string[] RawDOrder = ["d", "e", "b", "a", "c"];
    private static readonly string[] IntDOrder = ["d", "e", "a", "b", "c"];
    private static readonly string[] RawIOrder = ["d", "a", "b", "c", "e"];
    private static readonly string[] UIntIOrder = ["a", "b", "c", "e", "d"];
    private static readonly string[] ShortIOrder = ["e", "d", "a", "b", "c"];

    // ── 1. Narrowing (int)double cast sort key — the defect's pin ──────────────────────────────────

    [Fact]
    public void Narrowing_cast_sort_key_no_longer_sorts_by_the_raw_value()
    {
        var collection = Seed(nameof(Narrowing_cast_sort_key_no_longer_sorts_by_the_raw_value));

        // Every server-side leg carries a .ThenBy(x => x.Label) tiebreaker. This is NOT decoration: (int)D
        // genuinely ties a and b (both truncate to 1), and MongoDB's $sort does NOT guarantee any particular
        // order among tied rows (the spike says so explicitly of this exact shape). In-memory LINQ's OrderBy
        // IS stable, so without the tiebreaker the two oracles below could pass by accident of a stability
        // guarantee the SERVER never makes, while native/DriverLinq could legally return the tied rows in
        // either order and still be "correct" per MongoDB's own contract. The tiebreaker makes the expected
        // order the ONLY correct order everywhere, so this test only fails on a genuine defect.
        //
        // The lambda is written out at every call site deliberately, rather than hoisted into a local — a
        // local of type Func<Row,int> would bind IQueryable<Row>.OrderBy to Enumerable.OrderBy (LINQ-to-Objects)
        // instead of Queryable.OrderBy (Expression<Func<Row,int>>, server-translated), silently pulling every
        // row into memory and sorting client-side with NO OrderBy sent to the server at all — exactly the kind
        // of defect this test exists to catch, self-inflicted.

        // Under NativeOnly, (int)x.D declines at TryTranslateField (order-changing) and falls through to slice
        // B's TryTranslateValue — which, as of Task 3, CONVERTS ($toInt) instead of declining, so NativeOnly now
        // SUCCEEDS with the correct order (this used to assert a throw here; see
        // Narrowing_cast_sort_key_now_goes_native for the dedicated routing + MQL-stage-shape pin for the flip).
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(IntDOrder, nativeOnlyLabels);

        // Oracle 1: explicit DriverLinq.
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(IntDOrder, driverLinqLabels);

        // Oracle 2: in-memory LINQ over the same expression, evaluated over the same rows.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(IntDOrder, inMemoryLabels);

        // The defect's pin: default Native mode must agree with both oracles, and must NOT reproduce the
        // raw-D order the pre-fix code silently produced.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();

        Assert.Equal(IntDOrder, nativeLabels);
        Assert.NotEqual(RawDOrder, nativeLabels);
    }

    // ── 2. Unsigned reinterpreting cast sort key — declines, loudly ────────────────────────────────

    [Fact]
    public void Unsigned_reinterpreting_cast_sort_key_declines()
    {
        var collection = Seed(nameof(Unsigned_reinterpreting_cast_sort_key_declines));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().OrderBy(x => (uint)x.I).ToList());

        // Self-check the computed UIntIOrder against an in-memory LINQ oracle over the same expression, so the
        // hand-computed order backing this case's "genuine REVERSAL" claim is verified by execution, not just
        // asserted in a comment.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .OrderBy(x => (uint)x.I).Select(x => x.Label).ToList();
        Assert.Equal(UIntIOrder, inMemoryLabels);
        Assert.NotEqual(RawIOrder, inMemoryLabels);

        // Under the default Native mode this falls back to driver-LINQ, and the driver refuses the shape too
        // (it has no translation for a (uint) reinterpretation of a signed field) — the failure must be LOUD
        // (an exception), never a silently different row order.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        // Loud, not silent: the driver itself has no translation for a (uint) reinterpretation of a signed
        // field, so the fallback throws the driver's own ExpressionNotSupportedException rather than
        // returning a (wrong) row order. Recorded by TYPE so a future change that turns this back into a
        // silent wrong order is caught even if the exact message text drifts.
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => native.Entities.AsNoTracking().OrderBy(x => (uint)x.I).ToList()));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => driverLinq.Entities.AsNoTracking().OrderBy(x => (uint)x.I).ToList()));
    }

    // ── 3. Narrowing integral cast sort key — same shape as case 2 ─────────────────────────────────

    [Fact]
    public void Narrowing_integral_cast_sort_key_declines()
    {
        var collection = Seed(nameof(Narrowing_integral_cast_sort_key_declines));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().OrderBy(x => (short)x.I).ToList());

        // Self-check the computed ShortIOrder against an in-memory LINQ oracle, same reasoning as case 2.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .OrderBy(x => (short)x.I).Select(x => x.Label).ToList();
        Assert.Equal(ShortIOrder, inMemoryLabels);
        Assert.NotEqual(RawIOrder, inMemoryLabels);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        // Same shape as case 2: loud, not silent, and the same exception TYPE (the driver's own decline).
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => native.Entities.AsNoTracking().OrderBy(x => (short)x.I).ToList()));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => driverLinq.Entities.AsNoTracking().OrderBy(x => (short)x.I).ToList()));
    }

    // ── 4. Widening and boxing cast sort keys — the control that stops the fix over-declining ──────

    [Fact]
    public void Widening_cast_sort_keys_are_unchanged_and_native()
    {
        var collection = Seed(nameof(Widening_cast_sort_keys_are_unchanged_and_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Widening numeric conversions are order-preserving, so they still resolve to the raw field and stay
        // native — and the resulting order is the RAW order (raw-I for the two widened-I keys, raw-D for the
        // boxed-D key).
        Assert.Equal(
            RawIOrder,
            db.Entities.AsNoTracking().OrderBy(x => (double)x.I).Select(x => x.Label).ToList());

        Assert.Equal(
            RawIOrder,
            db.Entities.AsNoTracking().OrderBy(x => (long)x.I).Select(x => x.Label).ToList());

        Assert.Equal(
            RawDOrder,
            db.Entities.AsNoTracking().OrderBy(x => (object)x.D).Select(x => x.Label).ToList());
    }

    // ── 5. Narrowing (int)double cast sort key now CONVERTS — Task 3's continuation of case 1 ──────

    [Fact]
    public void Narrowing_cast_sort_key_now_goes_native()
    {
        var logs = new List<string>();
        var collection = Seed(nameof(Narrowing_cast_sort_key_now_goes_native));

        // Case 1 pinned the Task-2 decline (order-changing cast left in the tree by UnwrapOrderPreserving, so
        // TryTranslateField declines). Task 3 does not change that decline — it changes what happens NEXT:
        // NativeSlotPopulator's TryTranslateComputedSortKey fall-through now SUCCEEDS via TryTranslateValue,
        // because TranslateOperand renders the cast as an explicit MongoConvertExpression ($toInt) instead of
        // declining. Under NativeOnly this must now SUCCEED (not throw), with the identical (int)D order case
        // 1 already computed and self-checked.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, logs);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();

        Assert.Equal(IntDOrder, nativeOnlyLabels);

        // Stage-shape pin, NOT a routing proof — filter/sort/paging MQL can look the same whether native or
        // driver-LINQ built it (see Query/AGENTS.md's "MQL shape cannot prove native" pitfall). The NativeOnly
        // SUCCESS above is the actual routing proof; this only pins that a computed sort key lowers to the
        // documented $set → $sort → $unset shape with an explicit $toInt body, not that it proves routing.
        var mql = Mql(logs);
        Assert.Contains("$set", mql);
        Assert.Contains("\"$toInt\"", mql);
        Assert.Contains("\"$D\"", mql);
    }

    // ── 6. Field-to-field comparison with a cast goes native (spike §3.1) ──────────────────────────

    // f1 is a negative control (matches neither predicate below); f2/f3/f4 each discriminate one shape.
    private static readonly (string Label, double D, int I)[] ComparisonRows =
    [
        ("f1", 2.5, 2),   // widening: 2.0 > 2.5 false;  narrowing: (int)2.5=2 > 2 false (equal)
        ("f2", 1.5, 3),   // widening: 3.0 > 1.5 true;   narrowing: (int)1.5=1 > 3 false
        ("f3", 5.9, 2),   // widening: 2.0 > 5.9 false;  narrowing: (int)5.9=5 > 2 true
        ("f4", -2.5, -5)  // widening: -5.0 > -2.5 false; narrowing: (int)-2.5=-2 > -5 true
    ];

    [Fact]
    public void Field_to_field_comparison_with_a_cast_goes_native()
    {
        var collection = SeedComparisonRows(nameof(Field_to_field_comparison_with_a_cast_goes_native));

        // Widening target (double): matches the driver's own rendering of this exact shape (spike P01).
        AssertCastComparisonGoesNative(collection, x => (double)x.I > x.D, ["f2"]);

        // Narrowing target (int): a genuine value-changing cast, still renders explicitly (spike P05).
        AssertCastComparisonGoesNative(collection, x => (int)x.D > x.I, ["f3", "f4"]);
    }

    private void AssertCastComparisonGoesNative(
        IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate, string[] expectedLabels)
    {
        // Routing proof: NativeOnly succeeds rather than throwing.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeOnlyLabels);

        // Native == DriverLinq == CLR, over the SAME Expression object for the in-memory leg.
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, driverLinqLabels);

        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList().AsQueryable()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, inMemoryLabels);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeLabels);
    }

    // ── 7. Cast to an unrenderable target still declines (short/uint/float) ────────────────────────

    [Fact]
    public void Cast_to_an_unrenderable_target_still_declines()
    {
        var collection = SeedComparisonRows(nameof(Cast_to_an_unrenderable_target_still_declines));

        // MQL has no $toShort/$toUInt/$toFloat — MongoConvertExpression.ToOperatorFor returns null for all
        // three, so TranslateOperand declines rather than emitting an operator MQL cannot express. This is
        // the SAME boundary the driver's own LINQ provider has (spike §3.2): the fallback fails LOUDLY too.
        AssertCastComparisonDeclinesLoudly(collection, x => (short)x.I > x.I);
        AssertCastComparisonDeclinesLoudly(collection, x => (uint)x.I > (uint)x.I);
        AssertCastComparisonDeclinesLoudly(collection, x => (float)x.D > x.D);
    }

    private void AssertCastComparisonDeclinesLoudly(
        IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking().Where(predicate).ToList());

        // The driver refuses these shapes too, so the default-mode fallback fails LOUDLY — never a silently
        // different row set (same disposition as cases 2/3's unrenderable sort-key targets).
        using var native = CreateContext(collection, MongoQueryMode.Native);
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => native.Entities.AsNoTracking().Where(predicate).ToList()));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => driverLinq.Entities.AsNoTracking().Where(predicate).ToList()));
    }

    // ── 8. Cast inside a quantifier element predicate declines — the IsQueryDialectRenderable pin ──

    private class QuantBlog
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public List<QuantPost> Posts { get; set; } = [];
    }

    private class QuantPost
    {
        public double Weight { get; set; }
        public int Rank { get; set; }
        public string Heading { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> QuantBlogModel = mb => mb.Entity<QuantBlog>().OwnsMany(b => b.Posts);

    [Fact]
    public void Cast_inside_a_quantifier_element_predicate_declines()
    {
        // The element predicate MUST be field-to-field (Weight vs Rank), not member-vs-constant: a
        // member-vs-constant cast (`(int)p.Weight > 5`) is intercepted earlier by the UNRELATED, unchanged
        // HasNumericConvert guard on the query-native comparison path and never reaches TranslateOperand's
        // Convert branch at all — it would decline for the wrong reason and prove nothing about THIS task's
        // exclusion. Weight vs Rank forces the general field-to-field path, which DOES build a
        // MongoConvertExpression($toInt over Weight) — exactly the node IsQueryDialectRenderable must keep
        // declining, because $expr (what MongoAggregationExpressionRenderer would render this as) is a hard
        // server error inside $elemMatch.
        var collection = database.MongoDatabase.GetCollection<QuantBlog>(
            UniqueCollectionName(nameof(Cast_inside_a_quantifier_element_predicate_declines)));
        collection.InsertMany(
        [
            new QuantBlog
            {
                Title = "match", Posts = [new QuantPost { Weight = 5.9, Rank = 2 }] // (int)5.9=5 > 2 -> true
            },
            new QuantBlog
            {
                Title = "nomatch", Posts = [new QuantPost { Weight = 1.9, Rank = 9 }] // (int)1.9=1 > 9 -> false
            }
        ]);

        using var nativeOnly = CreateQuantContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking()
                .Where(b => b.Posts.Any(p => (int)p.Weight > p.Rank)).ToList());

        // Correct results via the graceful fallback under the default Native mode.
        using var native = CreateQuantContext(collection, MongoQueryMode.Native);
        var titles = native.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => (int)p.Weight > p.Rank)).Select(b => b.Title).ToList();
        Assert.Equal(["match"], titles);
    }

    // ── 9. Cast inside an owned SelectMany inner filter — the MongoFieldPrefixRewriter pin ─────────
    //
    // Step 9 mutation 2 (delete the MongoFieldPrefixRewriter.MongoConvertExpression case) turns NOTHING red
    // anywhere else in the whole solution (unit + functional Query suites both stayed green under that
    // mutation) — no OTHER committed test reaches Rewrite with a converted operand. This is the one that
    // does: an owned SelectMany's inner filter is field-to-field (Weight vs Rank, forcing the same general
    // TranslateOperand path case 8 uses, NOT the member-vs-constant HasNumericConvert intercept), so
    // TryBuildOwnedInnerFilter's non-outer-referencing branch calls
    // MongoFieldPrefixRewriter.Rewrite(expr, "Posts") on a tree containing a MongoConvertExpression. Without
    // the case, Rewrite throws for a node kind it should have prefixed cleanly — at TRANSLATE time, uncaught
    // by the lower/render fallback machinery (that catch only wraps lowering, which runs later).

    [Fact]
    public void Cast_inside_an_owned_SelectMany_inner_filter_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<QuantBlog>(
            UniqueCollectionName(nameof(Cast_inside_an_owned_SelectMany_inner_filter_goes_native)));
        collection.InsertMany(
        [
            new QuantBlog
            {
                Title = "b1",
                Posts =
                [
                    new QuantPost { Weight = 5.9, Rank = 2, Heading = "keep" }, // (int)5.9=5 > 2 -> true
                    new QuantPost { Weight = 1.9, Rank = 9, Heading = "drop" }  // (int)1.9=1 > 9 -> false
                ]
            }
        ]);

        // The anonymous-type result selector is materialized as-is (a SECOND server-side Select chained after
        // the SelectMany's own projection is a different, unrelated shape); the final Heading extraction runs
        // client-side over the already-materialized rows.
        using var nativeOnly = CreateQuantContext(collection, MongoQueryMode.NativeOnly);
        var results = nativeOnly.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => (int)p.Weight > p.Rank), (b, p) => new { p.Heading })
            .ToList();

        Assert.Equal(["keep"], results.Select(x => x.Heading).ToArray());
    }

    // ── 10. Incidental widening: a cast inside a FILTERED Count(pred) element predicate ────────────
    //
    // Found in fix round 1 (branch review), not by design: TranslateOperand's count branch (EF-359) builds a
    // MongoFilteredSizeExpression from the SAME element-scoped TryTranslate this task's cast handling now runs
    // through, so `Posts.Count(p => (int)p.Weight > p.Rank)` — field-to-field, forcing the general path exactly
    // as cases 8/9 do — now translates its element predicate to a MongoConvertExpression too, passes
    // MongoAggregationExpressionRenderer.CanRender (Task 1's arm admits it), and goes native, in BOTH the
    // predicate spelling (`Where(... > 1)`) and the projection spelling (`Select(... N = ...)`). Neither
    // shape previously reached this: pre-Task-3, TranslateOperand's Convert branch always declined a
    // type-changing cast, so the element predicate failed to translate and the whole filtered count declined.

    [Fact]
    public void Cast_inside_a_filtered_Count_element_predicate_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<QuantBlog>(
            UniqueCollectionName(nameof(Cast_inside_a_filtered_Count_element_predicate_goes_native)));
        collection.InsertMany(
        [
            new QuantBlog
            {
                Title = "b1", // (int)5.9=5>2 true; (int)1.9=1>9 false -> count 1
                Posts =
                [
                    new QuantPost { Weight = 5.9, Rank = 2 },
                    new QuantPost { Weight = 1.9, Rank = 9 }
                ]
            },
            new QuantBlog
            {
                Title = "b2", // (int)5.9=5>2 true; (int)6.9=6>1 true -> count 2
                Posts =
                [
                    new QuantPost { Weight = 5.9, Rank = 2 },
                    new QuantPost { Weight = 6.9, Rank = 1 }
                ]
            },
            new QuantBlog
            {
                Title = "b3", // (int)1.9=1>9 false; (int)1.1=1>9 false -> count 0
                Posts =
                [
                    new QuantPost { Weight = 1.9, Rank = 9 },
                    new QuantPost { Weight = 1.1, Rank = 9 }
                ]
            }
        ]);

        // Predicate spelling.
        using var nativeOnlyPredicate = CreateQuantContext(collection, MongoQueryMode.NativeOnly);
        var matchingTitles = nativeOnlyPredicate.Entities.AsNoTracking()
            .Where(b => b.Posts.Count(p => (int)p.Weight > p.Rank) > 1)
            .Select(b => b.Title)
            .OrderBy(t => t)
            .ToList();
        Assert.Equal(["b2"], matchingTitles);

        // Projection spelling — asserts VALUES for every row, not just routing.
        using var nativeOnlyProjection = CreateQuantContext(collection, MongoQueryMode.NativeOnly);
        var counts = nativeOnlyProjection.Entities.AsNoTracking()
            .Select(b => new { b.Title, N = b.Posts.Count(p => (int)p.Weight > p.Rank) })
            .OrderBy(x => x.Title)
            .ToList();
        Assert.Equal(
            new[] { ("b1", 1), ("b2", 2), ("b3", 0) },
            counts.Select(x => (x.Title, x.N)).ToArray());
    }

    // ── 9. Widening cast on the member side of a member-vs-constant comparison ─────────────────────
    //
    // Task 4's headline shape. TranslateComparison's query-native branch used to decline the WHOLE comparison
    // via HasNumericConvert; it now tolerates a widening numeric layer and absorbs it (the emitted field ref is
    // the stored field, exactly as for a bare `x.I >= 2.5` would-be comparison).
    //
    // THIS IS THE SILENT-WRONG-DATA PIN. Absorbing the cast moves the comparison from the STORED type (int) to
    // the CAST's type (double/decimal). TranslateComparison serializes the constant with
    // `forSerialization: leftProperty`, which coerces 2.5 to the property's CLR type — i.e. TRUNCATES it to 2 —
    // emitting {"I": {"$gte": 2}} and returning one row too many, silently, under the DEFAULT Native mode.
    // MEASURED with the spike's prototype (spike §6.2, probes P15/P17). The rows below are what discriminates:
    // the truncated set is a strict superset of the correct one, so a row COUNT alone would not be enough —
    // hence the explicit Assert.NotEqual against the truncated set.
    //
    //   Rows (I): a=1, b=2, c=3, d=-1, e=50000
    //   correct   (I >= 2.5): c, e
    //   truncated (I >= 2  ): b, c, e      <- b is the extra row a truncated constant lets through

    private static readonly string[] FractionalCorrect = ["c", "e"];
    private static readonly string[] FractionalTruncated = ["b", "c", "e"];

    [Fact]
    public void Widening_cast_comparison_with_a_fractional_constant_returns_the_right_rows()
    {
        var collection = Seed(nameof(Widening_cast_comparison_with_a_fractional_constant_returns_the_right_rows));

        AssertFractionalConstantRows(collection, x => (double)x.I >= 2.5);
        AssertFractionalConstantRows(collection, x => (decimal)x.I >= 2.5m);

        // REVERSED OPERAND ORDER — the MIRRORED branch of TranslateComparison, which has its own separate
        // HasNumericConvert call and its own separate TranslateValue call site. Without these two rows, reverting
        // ONLY that branch's constant to the property serializer would reintroduce the exact truncation bug with
        // nothing red anywhere: every other case in this file, and every unit case, puts the member on the LEFT.
        // The shape is reachable — the provider carries `Mirror` precisely because EF does not normalise operand
        // order — and the expected rows are identical, since `2.5 <= x` is the same predicate as `x >= 2.5`.
        AssertFractionalConstantRows(collection, x => 2.5 <= (double)x.I);
        AssertFractionalConstantRows(collection, x => 2.5m <= (decimal)x.I);
    }

    private void AssertFractionalConstantRows(IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        // Routing proof: NativeOnly SUCCEEDS (pre-Task-4 the whole comparison declined here and this threw).
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(FractionalCorrect, nativeOnlyLabels);
        Assert.NotEqual(FractionalTruncated, nativeOnlyLabels);

        // Default Native mode is where the wrong data would be silent — assert it there too.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(FractionalCorrect, nativeLabels);
        Assert.NotEqual(FractionalTruncated, nativeLabels);

        // Oracle: in-memory LINQ over the SAME Expression object.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList().AsQueryable()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(FractionalCorrect, inMemoryLabels);
    }

    // ── 10. The constant is serialized in the COMPARISON's type, not the stored one ────────────────

    [Fact]
    public void Widening_cast_comparison_emits_the_constant_in_the_comparison_type()
    {
        var collection = Seed(nameof(Widening_cast_comparison_emits_the_constant_in_the_comparison_type));

        // Stage-shape pin, NOT a routing proof — case 9's NativeOnly success is the routing proof (see
        // Query/AGENTS.md's "MQL shape cannot prove native" pitfall). What this pins is that the emitted
        // constant is 2.5, not the 2 a stored-property serializer would coerce it to, and that it is
        // BYTE-IDENTICAL to what the driver's own LINQ provider emits for the same shape.
        var nativeLogs = new List<string>();
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, nativeLogs);
        _ = nativeOnly.Entities.AsNoTracking().Where(x => (double)x.I >= 2.5).ToList();
        var nativeMql = MqlPipeline(nativeLogs);

        // The whole $match document, not just the operator fragment — a strictly stronger positive assertion.
        // (There is deliberately NO negative "does not contain the truncated form" assertion beside it: the
        // truncated emission is `{ "$gte" : 2 }`, and every needle spelling one that CANNOT also match `2.5`
        // is fragile against a formatting change. The positive assertion is the discriminator, and it is
        // mutation-verified — forcing the property serializer turns this case red on exactly this line.)
        Assert.Contains("{ \"I\" : { \"$gte\" : 2.5 } }", nativeMql);

        var driverLogs = new List<string>();
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq, driverLogs);
        _ = driverLinq.Entities.AsNoTracking().Where(x => (double)x.I >= 2.5).ToList();

        // Byte-identical to the driver's own rendering of the same shape (the log line's leading timestamp is
        // stripped by MqlPipeline; both legs run against the same collection, so the rest is comparable).
        Assert.Equal(MqlPipeline(driverLogs), nativeMql);
    }

    // ── 11. Native == DriverLinq for the widening-cast comparison shapes ───────────────────────────

    [Fact]
    public void Widening_cast_comparison_matches_driver_linq()
    {
        var collection = Seed(nameof(Widening_cast_comparison_matches_driver_linq));

        AssertNativeMatchesDriverLinq(collection, x => (double)x.I >= 2.5);
        AssertNativeMatchesDriverLinq(collection, x => (decimal)x.I >= 2.5m);
        AssertNativeMatchesDriverLinq(collection, x => (double)x.I > 3.0);   // integral-valued threshold
        AssertNativeMatchesDriverLinq(collection, x => (long)x.I == 2L);     // equality, integral constant
    }

    private void AssertNativeMatchesDriverLinq(IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();

        Assert.Equal(driverLinqLabels, nativeLabels);
    }

    // ── 12. The conjunct that separates "default-serialized" from "not an enum" ────────────────────
    //
    // The spike left it UNVERIFIED (§6.2, §11) whether NativeGroupByBinder.HasDefaultKeySerialization is
    // exactly the right conjunct for the constant rule, versus the cheaper "the property's CLR type is not an
    // enum" — no fixture in the spike separated them. THIS ONE DOES, and it is why the shipped rule uses
    // HasDefaultKeySerialization.
    //
    // `Coded` is a plain `int` (so "not an enum" is TRUE) carried through a VALUE CONVERTER to a string (so
    // HasDefaultKeySerialization is FALSE). Under the shipped rule the constant keeps the PROPERTY serializer
    // and renders as the string "2", matching what the stored field actually holds; under the "not an enum"
    // rule it would render as the raw number 2, which MongoDB type-brackets against a string field so the
    // query returns NO rows at all. The two rules therefore disagree on both the emitted MQL and the ROWS.

    private class ConvRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Coded { get; set; }
    }

    private static readonly Action<ModelBuilder> ConvRowModel =
        mb => mb.Entity<ConvRow>().Property(e => e.Coded).HasConversion<string>();

    [Fact]
    public void Widening_cast_comparison_over_a_value_converted_property_keeps_the_property_serializer()
    {
        var name = UniqueCollectionName(
            nameof(Widening_cast_comparison_over_a_value_converted_property_keeps_the_property_serializer));

        // Seed through a BsonDocument handle, NOT through IMongoCollection<ConvRow>.InsertMany: the latter uses
        // the DRIVER's own POCO serializer, which knows nothing about EF's value converter and would store
        // `Coded` as an Int32 — the very stored shape this test needs to distinguish itself from.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Coded", "1" } },
            new BsonDocument { { "Label", "q" }, { "Coded", "2" } },
            new BsonDocument { { "Label", "r" }, { "Coded", "3" } }
        ]);

        var collection = database.MongoDatabase.GetCollection<ConvRow>(name);

        var logs = new List<string>();
        using var nativeOnly = CreateConvContext(collection, MongoQueryMode.NativeOnly, logs);
        var nativeLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => (long)x.Coded >= 2L).OrderBy(x => x.Label).Select(x => x.Label).ToList();

        // The discriminator, asserted FIRST because it is the thing the two candidate conjuncts disagree on:
        // the constant renders as the STRING "2" (the property serializer, i.e. the shipped rule) and NOT as
        // the raw number 2 the "not an enum" rule would produce.
        var mql = MqlPipeline(logs);
        Assert.Contains("\"$gte\" : \"2\"", mql);

        // And the rows follow from it: a comparison over the STORED strings "1"/"2"/"3" — NOT the empty set a
        // raw number would produce against a string-stored field (MongoDB type-brackets $gte). The seed is
        // deliberately single-digit so the string ordering coincides with the numeric one; this case is about
        // WHICH SERIALIZER renders the constant, not about value-converted comparison semantics in general.
        Assert.Equal(["q", "r"], nativeLabels);
        Assert.NotEmpty(nativeLabels);

        // Control: the UN-cast comparison — which never had a convert layer and so is untouched by this task —
        // emits the identical constant. That is the property the rule preserves: absorbing a widening cast over
        // a NON-default-serialized property must leave the constant exactly where it was.
        var uncastLogs = new List<string>();
        using var uncast = CreateConvContext(collection, MongoQueryMode.NativeOnly, uncastLogs);
        var uncastLabels = uncast.Entities.AsNoTracking()
            .Where(x => x.Coded >= 2).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Contains("\"$gte\" : \"2\"", MqlPipeline(uncastLogs));
        Assert.Equal(nativeLabels, uncastLabels);

        // THERE IS NO DRIVER-LINQ ORACLE FOR THIS SHAPE, measured rather than assumed: the driver's own LINQ
        // provider fails building its numeric-conversion serializer over a ValueConverterSerializer ("Serializer
        // class ValueConverterSerializer`2 does not implement IHasRepresentationSerializer", surfaced through a
        // reflection Invoke), so explicit DriverLinq — and, before this task, the default Native mode's fallback
        // — fails LOUDLY where native now answers. A loud throw becoming correct rows is an improvement, not a
        // divergence. Asserted as "it threw", NOT by exception type: per the versioning rubric the exception
        // type of an unsupported shape is not contract, and CI runs with a DRIVER_VERSION override, so pinning
        // the driver's wrapper type would break on a driver bump for a fact this test does not depend on.
        using var driverLinq = CreateConvContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => (long)x.Coded >= 2L).Select(x => x.Label).ToList()));
    }

    private static SingleEntityDbContext<ConvRow> CreateConvContext(
        IMongoCollection<ConvRow> collection, MongoQueryMode mode, List<string>? logs = null)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: ConvRowModel,
            optionsBuilderAction: b =>
            {
                if (logs is not null)
                    b.LogTo(logs.Add).EnableSensitiveDataLogging();
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 13. The OTHER sub-family the conjunct protects: a value-TRANSFORMING numeric converter ─────
    //
    // Case 12 covers a RE-ENCODING converter (int stored as a string), whose failure mode is ZERO rows —
    // MongoDB type-brackets a number against a string field, so the wrong rule fails loudly-ish. This case
    // covers the sub-family that fails SILENTLY: `v => v * 2` keeps the stored form numeric, so the wrong rule
    // emits a well-typed number that simply selects the WRONG ROWS. It is the clearest wrong-data story for
    // the HasDefaultKeySerialization conjunct, and case 12's seed cannot tell it — that seed is single-digit
    // by construction, so its string ordering coincides with its numeric one.

    private class ScaledRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Scaled { get; set; }
    }

    private static readonly Action<ModelBuilder> ScaledRowModel =
        mb => mb.Entity<ScaledRow>().Property(e => e.Scaled).HasConversion(v => v * 2, v => v / 2);

    [Fact]
    public void Widening_cast_comparison_over_a_value_transforming_converter_returns_the_right_rows()
    {
        var name = UniqueCollectionName(
            nameof(Widening_cast_comparison_over_a_value_transforming_converter_returns_the_right_rows));

        // Stored values are the CONVERTED (provider) form; the model values are half of them: p=1, q=2, r=3.
        // Seeded through a BsonDocument handle for the reason case 12 records — the driver's own POCO
        // serializer knows nothing about EF's converter and would store the model value unconverted.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Scaled", 2 } },
            new BsonDocument { { "Label", "q" }, { "Scaled", 4 } },
            new BsonDocument { { "Label", "r" }, { "Scaled", 6 } }
        ]);

        var collection = database.MongoDatabase.GetCollection<ScaledRow>(name);

        var logs = new List<string>();
        using var nativeOnly = CreateScaledContext(collection, MongoQueryMode.NativeOnly, logs);
        var nativeLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => (long)x.Scaled > 2L).OrderBy(x => x.Label).Select(x => x.Label).ToList();

        // The constant goes through the converter: model 2 -> stored 4. The wrong rule would emit the raw 2.
        Assert.Contains("\"$gt\" : 4", MqlPipeline(logs));

        // And the rows follow: only the row whose MODEL value exceeds 2 (r, model 3 / stored 6). The wrong rule
        // would compare stored values against 2 and return q as well — well-typed, plausible, and WRONG.
        Assert.Equal(["r"], nativeLabels);
        Assert.NotEqual(["q", "r"], nativeLabels);

        // Oracle: materialize whole entities (which applies the converter on read) and filter in memory.
        using var oracle = CreateScaledContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .Where(x => (long)x.Scaled > 2L).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["r"], inMemoryLabels);

        // Same "no driver-LINQ oracle" story as case 12 — asserted as "it threw", not by exception type.
        using var driverLinq = CreateScaledContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => (long)x.Scaled > 2L).Select(x => x.Label).ToList()));
    }

    private static SingleEntityDbContext<ScaledRow> CreateScaledContext(
        IMongoCollection<ScaledRow> collection, MongoQueryMode mode, List<string>? logs = null)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: ScaledRowModel,
            optionsBuilderAction: b =>
            {
                if (logs is not null)
                    b.LogTo(logs.Add).EnableSensitiveDataLogging();
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 14. The accepted divergence: (long)->(double) is admitted but is NOT value-preserving ──────
    //
    // (long, double) and (ulong, double) are in WideningNumericConversions and ToOperatorFor admits double, so
    // this cast is ABSORBED — and above 2^53 IEEE round-to-nearest collapses distinct longs onto one double,
    // so the comparison native performs (raw stored long vs. the double constant) is not the one C# performs
    // (rounded long vs. the double constant). This test exists to MEASURE that, and to pin the property that
    // actually matters for this branch: Native == DriverLinq. It is the EF-359 accepted-divergence family —
    // native and driver-LINQ agree with each other and both differ from the CLR — not wrong data.

    private class LongRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public long L { get; set; }
    }

    [Fact]
    public void Widening_long_to_double_cast_above_2_53_diverges_from_in_memory_linq()
    {
        const long justAbove = 9007199254740993L;  // 2^53 + 1 — not representable as a double
        const double rounded = 9007199254740992.0; // what (double)justAbove rounds to

        var collection = database.MongoDatabase.GetCollection<LongRow>(
            UniqueCollectionName(nameof(Widening_long_to_double_cast_above_2_53_diverges_from_in_memory_linq)));
        collection.InsertMany(
        [
            new LongRow { Label = "big", L = justAbove },
            new LongRow { Label = "small", L = 1L }
        ]);

        // Premise, asserted rather than assumed: the CLR really does round this long onto `rounded`.
        Assert.Equal(rounded, (double)justAbove);

        using var nativeOnly = CreateLongContext(collection, MongoQueryMode.NativeOnly);
        var nativeLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => (double)x.L == rounded).OrderBy(x => x.Label).Select(x => x.Label).ToList();

        // THE PROPERTY THAT MATTERS: native agrees with the driver, which absorbs the cast identically.
        using var driverLinq = CreateLongContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(x => (double)x.L == rounded).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLinqLabels, nativeLabels);

        // The CLR answers differently, because it rounds the OPERAND before comparing. Documented, not fixed.
        using var oracle = CreateLongContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .Where(x => (double)x.L == rounded).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["big"], inMemoryLabels);
        Assert.NotEqual(inMemoryLabels, nativeLabels);

        // A value BELOW 2^53 is exactly representable, so every leg agrees there — this is the control that
        // stops the case above being read as "long casts are broken".
        using var control = CreateLongContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["small"],
            control.Entities.AsNoTracking().Where(x => (double)x.L == 1.0).Select(x => x.Label).ToList());
    }

    private static SingleEntityDbContext<LongRow> CreateLongContext(
        IMongoCollection<LongRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 15. IDENTITY-LIKE arm, part 1: enum ↔ underlying — the constant KEEPS the property serializer ──
    //
    // This is the enum-as-string shape that used to lock NativeGateRoutingTests.A_enum_as_string_where_equals_
    // routing to the fallback. EF emits the comparison as `(int)e.Status == (int)Status.Active` — a Convert of
    // the member to the enum's own underlying type — which HasNumericConvert now recognizes as IDENTITY-LIKE
    // (not widening): the comparison happens on the SAME stored value, so the field ref is the stored field
    // unchanged, but the constant must go through the PROPERTY's own serializer (the ValueConverterSerializer
    // for HasConversion<string>()) to render "Active" rather than the raw underlying int 0. Getting this wrong
    // — treating it like the widening arm and dropping the property serializer — would render the constant as
    // a bare number, which MongoDB type-brackets against the string-stored field: the query would go native
    // and silently match NOTHING. This case therefore asserts VALUES in all three modes, not just routing.

    private enum Status { Active, Suspended, Closed }

    private class EnumRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public Status Status { get; set; }
    }

    private static readonly Action<ModelBuilder> EnumRowModel =
        mb => mb.Entity<EnumRow>().Property(e => e.Status).HasConversion<string>();

    [Fact]
    public void Enum_as_string_comparison_goes_native_and_returns_the_right_values()
    {
        var name = UniqueCollectionName(nameof(Enum_as_string_comparison_goes_native_and_returns_the_right_values));

        // Seeded through a BsonDocument handle (same reason as cases 12/13): the driver's own POCO serializer
        // knows nothing about EF's value converter and would store the enum as an Int32, the very stored shape
        // this test needs to distinguish itself from.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Status", "Active" } },
            new BsonDocument { { "Label", "q" }, { "Status", "Closed" } },
            new BsonDocument { { "Label", "r" }, { "Status", "Active" } },
            new BsonDocument { { "Label", "s" }, { "Status", "Suspended" } }
        ]);

        var collection = database.MongoDatabase.GetCollection<EnumRow>(name);

        // Routing proof: NativeOnly SUCCEEDS — before Task 5 this whole comparison declined at HasNumericConvert
        // and NativeOnly threw NativeTranslationNotSupportedException (see the now-superseded comment on
        // NativeGateRoutingTests.A_enum_as_string_where_equals_routing).
        var logs = new List<string>();
        using var nativeOnly = CreateEnumContext(collection, MongoQueryMode.NativeOnly, logs);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => x.Status == Status.Active).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["p", "r"], nativeOnlyLabels);

        // The discriminator: the constant renders as the mapped STRING "Active", not the raw underlying int —
        // this is exactly what the identity-like arm's "keep the property serializer" rule buys.
        Assert.Contains("\"Status\" : \"Active\"", MqlPipeline(logs));

        // Default Native mode is where a dropped property serializer would silently match nothing.
        using var native = CreateEnumContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(x => x.Status == Status.Active).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["p", "r"], nativeLabels);

        // Native == DriverLinq.
        using var driverLinq = CreateEnumContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(x => x.Status == Status.Active).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLinqLabels, nativeLabels);

        // Oracle: in-memory LINQ over the same expression, evaluated over the same rows.
        using var oracle = CreateEnumContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .Where(x => x.Status == Status.Active).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["p", "r"], inMemoryLabels);
    }

    private static SingleEntityDbContext<EnumRow> CreateEnumContext(
        IMongoCollection<EnumRow> collection, MongoQueryMode mode, List<string>? logs = null)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: EnumRowModel,
            optionsBuilderAction: b =>
            {
                if (logs is not null)
                    b.LogTo(logs.Add).EnableSensitiveDataLogging();
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 16. IDENTITY-LIKE arm, part 2: char -> int, and boxing to object ───────────────────────────
    //
    // Neither of these has a value converter in play — they are plain unconverted properties — so unlike case
    // 15 the constant's serialization context (property vs. comparison type) makes no OBSERVABLE difference to
    // the emitted value here (a plain int/char round-trips identically either way). What these cases pin is
    // that the comparison is ADMITTED at all (routing) rather than declining: neither `char -> int` nor
    // `T -> object` is in WideningNumericConversions (that table holds primitive numeric pairs only — char's
    // own widenings and any boxing conversion are deliberately excluded from it), so before Task 5 both shapes
    // declined at HasNumericConvert.

    private class CharBoxRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public char Grade { get; set; }
        public int I { get; set; }
    }

    private static readonly (string Label, char Grade, int I)[] CharBoxRows =
    [
        ("a", 'A', 1),
        ("b", 'B', 2),
        ("c", 'C', 3)
    ];

    [Fact]
    public void Char_and_boxing_converts_go_native()
    {
        var collection = SeedCharBox(nameof(Char_and_boxing_converts_go_native));

        // char -> int identity-like convert: (int)'A' == 65. A genuine numeric conversion (char to int is a
        // value, not a reference, conversion), so the in-memory CLR oracle agrees with VALUE equality here —
        // the full four-leg helper (including the CLR oracle) applies unchanged.
        AssertCharBoxComparisonGoesNative(collection, x => (int)x.Grade == 65, ["a"]);

        // Boxing convert to object: (object)x.I == (object)2. Deliberately NOT run through the shared helper
        // above — see AssertBoxingComparisonGoesNative for why the in-memory CLR oracle leg does not apply here.
        AssertBoxingComparisonGoesNative(collection, x => (object)x.I == (object)2, ["b"]);
    }

    // Boxing `==` is a REFERENCE-equality comparison in real C# (object.ReferenceEquals semantics for two
    // freshly-boxed value-type operands, per the CLR's built-in `object`-to-`object` equality operator) — so
    // `(object)x.I == (object)2` evaluated by ACTUAL in-memory LINQ compares two DIFFERENT boxed instances and
    // is FALSE for every row, never matching even when the underlying values are equal. This is not a defect
    // in the translator: the translator reinterprets the Convert+Equal shape structurally as a VALUE
    // comparison (identical to what the driver's own LINQ provider does for this shape, and to what every
    // other MEMBER-side identity-like convert in this file does), so native and driver-LINQ agree with EACH
    // OTHER and both correctly differ from the CLR's own reference-equality answer — the SAME "native equals
    // driver-LINQ, both differ from in-memory LINQ" shape as case 14's accepted divergence, just via a
    // different mechanism (reference vs. value equality rather than lossy rounding). There is therefore no
    // useful in-memory oracle leg for this sub-case; only Native == DriverLinq (and the routing proof) apply.
    private void AssertBoxingComparisonGoesNative(
        IMongoCollection<CharBoxRow> collection, Expression<Func<CharBoxRow, bool>> predicate, string[] expectedLabels)
    {
        // Premise, asserted rather than assumed: the CLR really does answer differently here.
        var clrLabels = CharBoxRows.Select(r => new CharBoxRow { Label = r.Label, Grade = r.Grade, I = r.I })
            .AsQueryable().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Empty(clrLabels);

        // Routing proof: NativeOnly succeeds rather than throwing.
        using var nativeOnly = CreateCharBoxContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeOnlyLabels);

        // THE property that matters: native agrees with the driver, which reinterprets the same shape the
        // same (value-equality) way.
        using var driverLinq = CreateCharBoxContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, driverLinqLabels);

        using var native = CreateCharBoxContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeLabels);
    }

    private void AssertCharBoxComparisonGoesNative(
        IMongoCollection<CharBoxRow> collection, Expression<Func<CharBoxRow, bool>> predicate, string[] expectedLabels)
    {
        // Routing proof: NativeOnly succeeds rather than throwing.
        using var nativeOnly = CreateCharBoxContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeOnlyLabels);

        // Native == DriverLinq == CLR, over the SAME Expression object for the in-memory leg.
        using var driverLinq = CreateCharBoxContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, driverLinqLabels);

        using var oracle = CreateCharBoxContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList().AsQueryable()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, inMemoryLabels);

        using var native = CreateCharBoxContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeLabels);
    }

    private IMongoCollection<CharBoxRow> SeedCharBox(string name)
    {
        var collection = database.MongoDatabase.GetCollection<CharBoxRow>(UniqueCollectionName(name));
        collection.InsertMany(CharBoxRows.Select(r => new CharBoxRow { Label = r.Label, Grade = r.Grade, I = r.I }));
        return collection;
    }

    private static SingleEntityDbContext<CharBoxRow> CreateCharBoxContext(
        IMongoCollection<CharBoxRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 17. IDENTITY-LIKE arm, fix round 1: a SUB-int-backed enum's promoted comparison goes native ──
    //
    // Fix round 1 finding: C# promotes a short/byte/ushort/sbyte-backed enum's equality comparison to Int32 —
    // a WIDENING of the enum's own underlying type, not an exact match — so the member-side Convert targets
    // Int32, not the enum's own Int16. Every enum fixture elsewhere in this file (case 15's EnumRow.Status,
    // Int32-backed) could never expose this, because an Int32-backed enum's promoted target IS its own
    // underlying type (an exact match). This is the shape that cost this task 6 BuiltInDataTypesMongoTest
    // specification cases on first delivery. No value converter here — this pins ROUTING, not the constant
    // serializer (case 15 already pins that for the enum-as-string shape).

    private enum ShortStatus : short { Active, Suspended, Closed }

    private class ShortEnumRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public ShortStatus Status { get; set; }
    }

    [Fact]
    public void Short_backed_enum_comparison_goes_native()
    {
        var collection = database.MongoDatabase.GetCollection<ShortEnumRow>(
            UniqueCollectionName(nameof(Short_backed_enum_comparison_goes_native)));
        collection.InsertMany(
        [
            new ShortEnumRow { Label = "p", Status = ShortStatus.Active },
            new ShortEnumRow { Label = "q", Status = ShortStatus.Suspended },
            new ShortEnumRow { Label = "r", Status = ShortStatus.Suspended }
        ]);

        // Routing proof: NativeOnly succeeds — before the fix, the promoted Convert(m, Int32) target (not
        // ShortStatus's own Int16) declined at HasNumericConvert and this threw
        // NativeTranslationNotSupportedException.
        using var nativeOnly = CreateShortEnumContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => x.Status == ShortStatus.Suspended).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["q", "r"], nativeOnlyLabels);

        using var native = CreateShortEnumContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(x => x.Status == ShortStatus.Suspended).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["q", "r"], nativeLabels);

        using var driverLinq = CreateShortEnumContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(x => x.Status == ShortStatus.Suspended).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLinqLabels, nativeLabels);
    }

    private static SingleEntityDbContext<ShortEnumRow> CreateShortEnumContext(
        IMongoCollection<ShortEnumRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 18. IDENTITY-LIKE arm, fix round 2: enum -> floating must never CRASH ────────────────────
    //        (Task 7: it no longer DECLINES either -- it goes native. The guard below is unchanged and
    //         still netted; see the TASK 7 FLIP block after the fix-round-2 finding.)
    //
    // Fix round 2 finding: for an int-backed enum, IsWideningNumericConvert(Int32, Double) is TRUE (that
    // pair is a genuine C# implicit numeric widening for a plain int), so (double)x.Level >= n was wrongly
    // admitted as identity-like by the enum arm's widening disjunct -- toleratedWideningTarget stays null
    // (this is the ENUM arm, not the numeric-widening arm), so the flag-precedence fix from fix round 1 does
    // NOT save it, and the constant kept the ENUM property's own serializer. BsonValueSerializer.Coerce's
    // Enum.ToObject then throws ArgumentException for a non-integral value -- an UNCAUGHT crash under the
    // DEFAULT Native mode, on a shape that declined cleanly (fell back to driver-LINQ, correct rows) before
    // Task 5 ever touched this arm. The identity-like arm now restricts its widening disjunct to an
    // INTEGRAL target (IsIntegerType) -- short/byte/ushort/sbyte -> Int32, the only promotion this arm
    // exists for, are themselves integral, so the restriction costs nothing there and only closes this hole.

    private enum RankTier { Low, Medium, High }

    private class RankRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public RankTier Level { get; set; }
    }

    // TASK 7 FLIP, recorded here rather than in a new case, because it is this case's OWN shape changing
    // disposition. Task 7 turns the HasNumericConvert decline into a FALL-THROUGH to the general $expr path,
    // and `(double)x.Level` is exactly such a decline -- so this shape is no longer a decline at all: it now
    // goes NATIVE, emitting {$expr: {$gte: [{$toDouble: "$Level"}, 1.5]}} over the stored (int-backed) enum,
    // and returns the same rows as in-memory LINQ and as driver-LINQ. The method was renamed from
    // `Enum_to_floating_cast_declines_instead_of_crashing` accordingly.
    //
    // THE FIX-ROUND-2 GUARD THIS CASE EXISTS FOR IS STILL LOAD-BEARING, AND THIS TEST STILL DISCRIMINATES IT.
    // The crash it pins comes from the QUERY-NATIVE branch: if IsIdentityLikeConvert wrongly admitted
    // enum -> double, HasNumericConvert would return FALSE, the query-native branch would be taken (never the
    // $expr fall-through), the constant would keep the ENUM property's own serializer, and
    // BsonValueSerializer.Coerce's Enum.ToObject would throw ArgumentException for a non-integral value --
    // uncaught, under the DEFAULT Native mode. The `Assert.Equal(expectedLabels, nativeLabels)` leg below is
    // what catches that; it does not depend on which of the two paths produced the rows, only on getting rows
    // rather than a crash.
    //
    // The shape is SAFE to admit natively because RankTier is stored in its default (integral) form -- an
    // enum carrying a non-default BsonRepresentation (enum-as-string) or a value converter is held back from
    // the fall-through by Guard B (MongoExpressionTranslator.CanFallThroughToExpr); see case 29.

    [Fact]
    public void Enum_to_floating_cast_now_goes_native_and_still_never_crashes()
    {
        var collection = database.MongoDatabase.GetCollection<RankRow>(
            UniqueCollectionName(nameof(Enum_to_floating_cast_now_goes_native_and_still_never_crashes)));
        collection.InsertMany(
        [
            new RankRow { Label = "a", Level = RankTier.Low },
            new RankRow { Label = "b", Level = RankTier.Medium },
            new RankRow { Label = "c", Level = RankTier.High }
        ]);

        // Fractional constant.
        AssertEnumToFloatingIsNativeAndCorrect(collection, x => (double)x.Level >= 1.5, ["c"]);

        // Whole-number constant crashed IDENTICALLY before the fix-round-2 guard -- a fix that special-cased
        // only a fractional value would still have been wrong here, so both are kept.
        AssertEnumToFloatingIsNativeAndCorrect(collection, x => (double)x.Level == 2.0, ["c"]);
    }

    private void AssertEnumToFloatingIsNativeAndCorrect(
        IMongoCollection<RankRow> collection, Expression<Func<RankRow, bool>> predicate, string[] expectedLabels)
    {
        // Default Native mode: returns the correct rows. The pre-fix-round-2 behavior was an UNCAUGHT
        // ArgumentException here, not merely a routing miss -- this is still the load-bearing assertion for
        // the whole case, and it is unaffected by Task 7 changing WHICH path produces the rows.
        using var native = CreateRankContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeLabels);

        // NativeOnly: since Task 7 this SUCCEEDS (it used to throw NativeTranslationNotSupportedException) --
        // the routing proof that the $expr fall-through, not the driver-LINQ fallback, produced those rows.
        using var nativeOnly = CreateRankContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeOnlyLabels);

        // No divergence here, unlike case 27: for an int-backed enum the driver renders the same comparison
        // and agrees with both native and the CLR.
        using var driverLinq = CreateRankContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, driverLinqLabels);
    }

    private static SingleEntityDbContext<RankRow> CreateRankContext(
        IMongoCollection<RankRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 19. Flag-precedence fix round 1, at ROWS level: boxing must not mask widening's truncation guard ──
    //
    // MongoExpressionTranslatorTests.Boxing_over_a_widening_cast_lets_the_widening_arm_win_precedence (and its
    // mirrored sibling) pin this at the NODE-SHAPE level (constant.ForSerialization is null). This case pins
    // the same fix at the level that actually matters end to end: the RENDERED VALUE. Had the identity-like
    // (boxing) layer wrongly won precedence, the constant would keep I's own (plain int) serializer, and
    // MongoValueRenderer.ToBsonValue -> BsonValueSerializer.Coerce(int, 2.5) truncates it to 2 at RENDER time
    // -- a step the unit test's translation-layer assertions cannot observe at all, since constant.Value stays
    // 2.5 regardless of which arm won (see that test's corrected comment). The truncated constant would match
    // row "b" (I=2); the correct (untruncated) comparison matches nothing, since no I value's widened double
    // form equals 2.5 exactly.

    [Fact]
    public void Boxing_over_a_widening_cast_precedence_returns_the_untruncated_row()
    {
        var collection = Seed(nameof(Boxing_over_a_widening_cast_precedence_returns_the_untruncated_row));

        AssertBoxingOverWideningPrecedenceReturnsNoRows(collection, x => (object)(double)x.I == (object)2.5);

        // Mirrored branch (member on the right) has its own separate HasNumericConvert /
        // ConstantSerializationContext call site.
        AssertBoxingOverWideningPrecedenceReturnsNoRows(collection, x => (object)2.5 == (object)(double)x.I);
    }

    private void AssertBoxingOverWideningPrecedenceReturnsNoRows(
        IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList();

        // The discriminator: if boxing had wrongly won, the constant would render as the TRUNCATED int 2 and
        // match row "b" (I=2). Fixed behavior: widening wins, the constant stays 2.5, and no stored int
        // equals it.
        Assert.Empty(nativeOnlyLabels);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList();
        Assert.Empty(nativeLabels);
    }

    private static SingleEntityDbContext<QuantBlog> CreateQuantContext(
        IMongoCollection<QuantBlog> collection, MongoQueryMode mode, List<string>? logs = null)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: QuantBlogModel,
            optionsBuilderAction: b =>
            {
                if (logs is not null)
                    b.LogTo(logs.Add).EnableSensitiveDataLogging();
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 20. Numeric-cast PROJECTION leaf — wrapped spelling goes native (EF-322 slice A1, Task 6) ──
    //
    // NativeProjectionBinder.TryTranslateLeaf now admits a UnaryExpression{Convert} leaf gated on the
    // RESULTING node kind being MongoConvertExpression, mirroring the count/arithmetic branches' own
    // "renders as a DOCUMENT, so $project cannot misread it as an inclusion/exclusion flag" argument. This is
    // also the A2-lesson audit target: MongoProjectionBindingExpressionVisitor.Visit registers the WHOLE
    // Convert node (not just the operand) so the read side knows a CONVERTED value was projected, and
    // MongoProjectionBindingRemovingExpressionVisitor reads it back RAW by alias instead of through the
    // pre-cast member's own (mismatched) serializer.
    //
    // (int)D truncates toward zero, computed directly from the Rows table: a=1.6->1, b=1.4->1, c=2.5->2,
    // d=-1.5->-1, e=0.5->0.

    [Fact]
    public void Wrapped_cast_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Wrapped_cast_projection_leaf_goes_native));

        // Routing proof: NativeOnly succeeds AND returns the correct (int)D values.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int)x.D }).ToList();
        Assert.Equal(
            [("a", 1), ("b", 1), ("c", 2), ("d", -1), ("e", 0)],
            nativeOnlyResult.Select(r => (r.Label, r.X)));

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int)x.D }).ToList();
        Assert.Equal(
            nativeOnlyResult.Select(r => (r.Label, r.X)), nativeResult.Select(r => (r.Label, r.X)));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int)x.D }).ToList();
        Assert.Equal(
            nativeResult.Select(r => (r.Label, r.X)), driverLinqResult.Select(r => (r.Label, r.X)));
    }

    // ── 21. Wrapped numeric-cast PROJECTION leaf behind a parameterized predicate ──────────────────
    //
    // `prefix` is a captured local: this shape now goes fully native under Native/NativeOnly (a parameterized
    // StartsWith term is natively representable), so this proves Native and DriverLinq agree on the read.
    // Carries BOTH a non-nullable and a NULLABLE cast-target leg (fix round 1, Minor 6) — the non-nullable
    // leaf alone cannot discriminate a silent alias miss (it fails loudly instead), which is exactly why this
    // file's own tests elsewhere mix nullable and non-nullable leaves.

    [Fact]
    public void Wrapped_cast_projection_leaf_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection = Seed(nameof(Wrapped_cast_projection_leaf_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "a";

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var result = native.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int)x.D }).ToList();

        Assert.Equal([("a", 1)], result.Select(r => (r.Label, r.X)));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int)x.D }).ToList();
        Assert.Equal(result.Select(r => (r.Label, r.X)), driverLinqResult.Select(r => (r.Label, r.X)));

        // Fix round 1, Minor 6: a NULLABLE cast-target leg. The non-nullable leaf above fails LOUDLY on an
        // alias miss (a required-property materialization exception), which is exactly why this branch's own
        // recorded rule requires mixing a nullable leaf in alongside a non-nullable one -- an alias miss is
        // SILENT for a nullable/reference leaf (BsonBinding's nullable arm returns null with no exception),
        // and only a nullable leg can discriminate that failure mode on this late-decline route.
        var nullableResult = native.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int?)x.D }).ToList();
        Assert.Equal([("a", (int?)1)], nullableResult.Select(r => (r.Label, r.X)));

        var driverLinqNullableResult = driverLinq.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (int?)x.D }).ToList();
        Assert.Equal(
            nullableResult.Select(r => (r.Label, r.X)), driverLinqNullableResult.Select(r => (r.Label, r.X)));
    }

    // ── 22. Numeric-cast PROJECTION leaf — bare spelling goes NATIVE (EF-322 slice A4, tier 2) ─────
    //
    // FLIPPED BY A4-1, and the previous disposition is recorded rather than deleted because the two are one
    // fact seen from either side. Through step 3a this shape DECLINED: the bare arm derived its alias only via
    // TryDeriveDocumentPathAlias, which admits a non-dotted MongoFieldExpression/MongoElementRefExpression and
    // nothing else, and a MongoConvertExpression is backed by no document element at all. A4-1 adds the SECOND
    // derivation (TryDeriveSyntheticAlias) for exactly the computed leaf kinds that render as an
    // aggregation-operator DOCUMENT rather than a bare value — an arithmetic MongoBinaryExpression and this
    // MongoConvertExpression — under the reserved `_v` alias and the Synthetic tier. The VALUES are unchanged;
    // only the route is, which is what makes this test worth keeping in its flipped form.
    //
    // ONE OF THE SIX A4 DECLINE TRIPWIRES, and the only one A4-1 flipped rather than A4-3.
    //
    // A4-3 LEFT THIS ON SEQUENTIAL ASSERTIONS, on the grounds that it "already asserts" all three legs. THAT
    // DEFENCE DOES NOT ANSWER THE OBJECTION AND THE FINAL REVIEW WAS RIGHT: asserting three legs is not running
    // them. The NativeOnly value assertion ran FIRST, so any routing regression failed there and the
    // explicit-DriverLinq leg below it — the leg the versioning rubric MANDATES, because the native default's
    // carve-out is conditional on UseQueryMode(DriverLinq) restoring the previous path — never executed at all,
    // and the failure report named only the NativeOnly symptom. Converted to the collect-then-assert convention
    // this slice uses everywhere else (see NativeComputedBareProjectionTests.LegOutcome's remarks for why it is
    // not a style preference).

    [Fact]
    public void Bare_cast_projection_leaf_goes_native_and_returns_correct_values()
    {
        var collection = Seed(nameof(Bare_cast_projection_leaf_goes_native_and_returns_correct_values));

        // NativeOnly SUCCEEDING is the routing proof; the values it returns are the same ones the two fallback
        // modes return, which is the actual contract. The DriverLinq leg is the rubric-level escape hatch:
        // populating Projection flips ProjectionAnalyzer.CanPushDown, so under explicit DriverLinq the driver
        // now renders this bare Select itself instead of folding it client-side — under its OWN `_v` alias,
        // which is precisely the alias tier 2 reserved so that one alias-addressed shaper reads either pipeline.
        var expected = "[1,1,2,-1,0]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            legs.Add(($"{mode}", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => (int)x.D).ToList())));
        }

        Assert.Equal(
            [("NativeOnly", expected), ("Native", expected), ("DriverLinq", expected)],
            legs);
    }

    /// <summary>
    /// Runs <paramref name="query"/> and describes what it did as a short string, so a caller can COLLECT every
    /// leg's outcome and assert them together instead of aborting on the first.
    /// </summary>
    /// <remarks>
    /// A local copy of <c>NativeComputedBareProjectionTests.LegOutcome</c> rather than a shared helper: the two
    /// files have no common base and adding one for a six-line diagnostic would touch every case in both. The
    /// duplication is deliberate and the two must stay behaviourally identical — a leg that DECLINES reports
    /// <c>"declined"</c>, any other exception reports its type and first message line, and a sequence reports
    /// its values, so a regression names what EVERY mode did rather than only the first one to fail.
    /// </remarks>
    private static string LegOutcome(Func<object?> query)
    {
        try
        {
            var result = query();
            return result is System.Collections.IEnumerable values and not string
                ? "[" + string.Join(",", values.Cast<object>()) + "]"
                : result?.ToString() ?? "null";
        }
        catch (NativeTranslationNotSupportedException)
        {
            return "declined";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }
    }

    // ── 23. Bare numeric-cast PROJECTION leaf behind a parameterized predicate ─────────────────────
    //
    // The MANDATORY late-decline leg, and A4-1 changes what it is a leg OF. Through step 3a the bare spelling
    // declined at TRANSLATE time, so nothing here was ever late. Now the leaf IS admitted and the captured-local
    // `StartsWith` is what declines — at RENDER time, after the alias-addressed shaper has already been
    // committed. That is the only route in the suite where this slice's failure mode lives, and the failure mode
    // is SILENT (Query/AGENTS.md's "A READ-SIDE ALIAS MISMATCH IS SILENT" note), so this asserts VALUES. It is
    // correct only because the late-fallback strip is TIER-conditional and does NOT fire for a Synthetic
    // override: the driver's own push-down stays in place and writes `_v`, which is what the shaper reads by.

    [Fact]
    public void Bare_cast_projection_leaf_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection = Seed(nameof(Bare_cast_projection_leaf_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "a"; // a captured local, not a constant — a genuine query parameter

        // Now goes fully native under NativeOnly too: a parameterized StartsWith term is natively
        // representable (it defers its escape/anchor to a regex placeholder sentinel resolved at Build time),
        // so the earlier render-time decline this test exercised no longer occurs.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).Select(x => (int)x.D).ToList();
        Assert.Equal([1], nativeOnlyResult);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).Select(x => (int)x.D).ToList();
        Assert.Equal([1], nativeResult);

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking()
            .Where(x => x.Label.StartsWith(prefix)).Select(x => (int)x.D).ToList();
        Assert.Equal(nativeResult, driverLinqResult);
    }

    // ── 24. The node-kind gate, mutation-verified: a bare constant/parameter leaf ───────────────────
    //
    // NativeProjectionBinder.TryTranslateLeaf's cast-leaf branch is UNCONDITIONAL on leafExpression's own
    // top-level shape — it mirrors the owned-collection count branch's style, not the arithmetic branch's
    // structural pre-filter, because MongoConvertExpression has exactly ONE construction site in the whole
    // codebase (TranslateOperand's Convert branch, MongoExpressionTranslator.cs), so gating on the RESULTING
    // node kind is sufficient on its own. Relaxing that node-kind check to plain "TryTranslateValue
    // succeeded" wrongly admits ANY leaf that translates — including a BARE constant/parameter that never
    // went through a cast at all (confirmed live: a captured local reaches this exact branch, translating to
    // a MongoParameterExpression, before ever reaching the arithmetic/count branches' own — differently
    // gated — checks). The emitted $project would carry that bare value under alias "X" alongside the
    // inclusion "Label", and a FALSY value (0) makes $project read it as an EXCLUSION flag, aborting the
    // aggregate with MongoCommandException under the default Native mode — the same hazard the sibling
    // count/arithmetic gates' own mutation-verified tests already pin
    // (NativeOwnedCollectionCountTests.Constant_projection_leaf_is_not_admitted_by_the_count_binder_gate).
    // This test asserts the CORRECT (declined, graceful-fallback) behavior; the mutation itself was verified
    // manually by relaxing the gate, rebuilding, and re-running this test (see the task report), rather than
    // automated in-repo.

    [Fact]
    public void Constant_leaf_is_not_admitted_by_the_projection_cast_gate()
    {
        var collection = Seed(nameof(Constant_leaf_is_not_admitted_by_the_projection_cast_gate));
        var captured = 0;

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => nativeOnly.Entities.AsNoTracking()
                .Select(x => new { x.Label, X = captured }).ToList());

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = captured }).ToList();
        Assert.All(nativeResult, r => Assert.Equal(0, r.X));
        Assert.Equal(Rows.Select(r => r.Label).ToList(), nativeResult.Select(r => r.Label).ToList());

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = captured }).ToList();
        Assert.All(driverLinqResult, r => Assert.Equal(0, r.X));
        Assert.Equal(Rows.Select(r => r.Label).ToList(), driverLinqResult.Select(r => r.Label).ToList());
    }

    // ── 25. Guard B closes a converted-property gap this gate would otherwise reopen (fix round 1) ──
    //
    // IMPORTANT FINDING FROM REVIEW: a cast over a value-converted property (or one with a non-default
    // BsonRepresentation) NEVER reaches the raw-alias read case 20's read-side fix installed. It declines at
    // TRANSLATE time, one layer up: TryTranslateValue applies Guard B (AllFieldsDefaultSerialized), whose
    // MongoConvertExpression arm recurses THROUGH the cast into the field, and HasDefaultKeySerialization
    // rejects a converter (or a non-default BsonRepresentation) exactly as it does for the arithmetic/count/
    // comparison gates elsewhere in this file. $toInt over the RAW STORED (converted) value is never emitted.
    // This is the A5-precedent functional tripwire for that dependency (mirrors
    // NativeNullableMemberTests.Value_converted_nullable_Value_projection_leaf_declines_instead_of_reading_the_raw_stored_value):
    // it asserts an OUTCOME VALUE, not absence-of-throw, so a future edit that routes this leaf onto the raw-
    // alias read is caught by the WRONG VALUE it returns, not merely a missing exception.

    private class ConvertedWeightRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double Weight { get; set; }
    }

    private static readonly Action<ModelBuilder> ConvertedWeightRowModel =
        mb => mb.Entity<ConvertedWeightRow>().Property(e => e.Weight).HasConversion(v => v * 2, v => v / 2);

    [Fact]
    public void Cast_over_a_value_converted_property_declines_instead_of_reading_the_raw_stored_value()
    {
        var name = UniqueCollectionName(
            nameof(Cast_over_a_value_converted_property_declines_instead_of_reading_the_raw_stored_value));

        // Stored value is the CONVERTED (provider) form; the model value is half of it: CLR Weight = 3.5,
        // stored 7.0. (int)3.5 truncates to 3 -- the raw stored value 7.0 would truncate to 7, which is the
        // discriminator a wrongly-admitted raw-alias read would expose.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 7.0 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<ConvertedWeightRow>(name);

        // The discriminating assertion, in the same "outcome string" style the A5 precedent
        // (NativeNullableMemberTests.Value_converted_nullable_Value_projection_leaf_declines_instead_of_reading_the_raw_stored_value)
        // uses: run under NativeOnly (which forces native routing with no fallback to land on) and describe
        // the outcome as a STRING. Today Guard B declines the leaf and this throws. If a future edit relaxes
        // Guard B, or adds a cast-leaf path that bypasses TryTranslateValue, the leaf would be admitted and
        // NativeOnly would SUCCEED, returning the WRONG value (7, the raw stored value truncated) instead of
        // the correct converted-then-cast value (3) -- and the failing assertion below would print exactly
        // that wrong value, not merely report a missing exception.
        using var nativeOnly = CreateConvertedWeightContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking()
                .Select(x => new { x.Label, X = (int)x.Weight }).AsEnumerable()
                .Select(r => (r.Label, r.X)).ToList(),
            r => $"{r.Label}={r.X}");
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        // There is also NO driver-LINQ oracle for this shape (measured, not assumed) -- the SAME
        // "Serializer class ValueConverterSerializer`2 does not implement IHasRepresentationSerializer" limitation
        // cases 12/13 measure for a numeric cast over a value-converted property in COMPARISON position also
        // fires from PROJECTION position, under both Native (the declined leaf's fallback) and explicit
        // DriverLinq. Asserted as "it threw", not by exception type or value, for the same reason cases 12/13
        // are: there is no correct answer available to compare against on this route, only "did it avoid
        // silently returning the wrong one" -- which the NativeOnly assertion above already covers.
        using var native = CreateConvertedWeightContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Select(x => new { x.Label, X = (int)x.Weight }).ToList()));

        using var driverLinq = CreateConvertedWeightContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Select(x => new { x.Label, X = (int)x.Weight }).ToList()));
    }

    // ONE outcome-describer for the whole file (Task 7 fix round 1 collapsed a second, near-identical copy
    // into this). The row projector is an explicit parameter rather than a T.ToString() default so each
    // caller keeps the exact wording its own recorded measurement quotes -- case 25's mutation was recorded
    // as producing "returned p=7", and a formatting change here would silently invalidate that record.
    private static string DescribeOutcome<T>(Func<List<T>> query, Func<T, string> format)
    {
        List<T> result;
        try
        {
            result = query();
        }
        catch (Exception ex)
        {
            return $"threw {ex.GetType().Name}";
        }

        return "returned " + string.Join(",", result.Select(format));
    }

    private static SingleEntityDbContext<ConvertedWeightRow> CreateConvertedWeightContext(
        IMongoCollection<ConvertedWeightRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: ConvertedWeightRowModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 26. EF-410: a WIDENING cast projection leaf now goes native (fix round 2) ─────────────────
    //
    // Fix round 1 left this as a documented boundary: (long)x.I / (double)x.I are the COMMONEST numeric-cast
    // projection shapes, and TranslateOperand's Convert branch UNWRAPS a widening conversion entirely rather
    // than wrapping it in a MongoConvertExpression -- the resulting node kind is a bare MongoFieldExpression,
    // indistinguishable by node kind alone from a leaf that was never cast at all. EF-410 closes this: the
    // gate now also re-derives "was this leaf syntactically a cast" from the ORIGINAL (pre-translation)
    // leafExpression, so this NativeOnly leg now SUCCEEDS instead of throwing. Updated in place, per the
    // EF-335 precedent, rather than orphaned — it is still the correctness pin for this shape, just with the
    // opposite expected disposition.

    [Fact]
    public void Widening_cast_projection_leaf_now_goes_native()
    {
        var collection = Seed(nameof(Widening_cast_projection_leaf_now_goes_native));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (long)x.I }).ToList();
        Assert.Equal(
            Rows.OrderBy(r => r.Label).Select(r => (r.Label, (long)r.I)).ToList(),
            nativeOnlyResult.Select(r => (r.Label, r.X)).ToList());

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeResult = native.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (long)x.I }).ToList();
        Assert.Equal(
            nativeOnlyResult.Select(r => (r.Label, r.X)).ToList(),
            nativeResult.Select(r => (r.Label, r.X)).ToList());

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = (long)x.I }).ToList();
        Assert.Equal(
            nativeResult.Select(r => (r.Label, r.X)).ToList(), driverLinqResult.Select(r => (r.Label, r.X)).ToList());
    }

    // ── 26a. EF-410: a widening cast as a BARE (non-`new{}`) projection leaf now goes native too ────
    //
    // TranslateOperand unwraps (long)x.I to a bare MongoFieldExpression (correct — see its own remarks), so the
    // tier-2 bare-projection gate's post-translation node-kind check alone can't tell "was cast" from "was never
    // cast". The fix re-derives that fact from the ORIGINAL leafExpression's own node kind instead.

    [Fact]
    public void Widening_cast_bare_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Widening_cast_bare_projection_leaf_goes_native));

        var expected = "[1,2,3,-1,50000]";
        var legs = new List<(string Leg, string Outcome)>();

        foreach (var mode in new[] {MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq})
        {
            using var db = CreateContext(collection, mode);
            legs.Add(($"{mode}", LegOutcome(
                () => db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => (long)x.I).ToList())));
        }

        Assert.Equal(
            [("NativeOnly", expected), ("Native", expected), ("DriverLinq", expected)],
            legs);
    }

    // ── 27. THE OWNER RULING (Task 7): a narrowing cast vs. a CONSTANT now returns the CLR answer, and ─
    //        DELIBERATELY DIVERGES FROM DRIVER-LINQ. DO NOT "CORRECT" THIS TOWARD THE DRIVER.
    //
    // Task 7 turns TranslateComparison's query-native cast VETO into a fall-through: a cast the query-native
    // branch cannot absorb no longer declines the whole comparison, it declines only that branch, and the
    // general $expr path renders it as {$expr: {$gt: [{$toInt: "$D"}, 0]}}.
    //
    // MEASURED, and this is the whole point of the case: the driver's own LINQ provider DROPS the narrowing
    // cast on this shape and answers as though the predicate were `x.D > 0`, which returns e (D = 0.5, and
    // (int)0.5 == 0, so C# excludes it). Native returns what C# returns. THE OWNER HAS RULED: take the
    // CLR-correct answer and document the divergence.
    //
    // WHICH FAMILY THIS IS, stated because the two look alike and a future reader must not conflate them.
    // This is the OPPOSITE of the EF-359 accepted-divergence family (e.g. case 14 in this same file, the
    // long -> double widening above 2^53, and the filtered-Count ragged-data rows in
    // NativeOwnedCollectionFilteredCountTests): there, native and driver-LINQ AGREE WITH EACH OTHER and both
    // differ from in-memory LINQ, so the CLR is the odd one out and "accept and document" means accepting a
    // server-semantics answer. HERE it is the reverse — native agrees with the CLR and driver-LINQ is the odd
    // one out, i.e. native is deliberately MORE CORRECT than the path it replaces. So the instinct that fires
    // on seeing "native != DriverLinq" ("restore parity with the driver") would, for THIS shape, be a
    // regression from a correct answer to a wrong one.
    //
    // TWO CONSEQUENCES, recorded here (and in TranslateComparison's own remarks) rather than merely lived
    // with, because they weaken two arguments this branch has leaned on elsewhere:
    //   (1) "query results are unchanged" is NO LONGER a blanket argument for making the native path the
    //       default — this is a result change, under the DEFAULT Native mode, for a shape that until this
    //       task fell back;
    //   (2) UseQueryMode(MongoQueryMode.DriverLinq) NO LONGER restores the same answer for this shape. It
    //       restores the driver's answer, which for a narrowing cast against a constant is the CLR-wrong one.

    [Fact]
    public void Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq()
    {
        var collection = Seed(nameof(Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq));

        // The CLR answer: (int)D > 0 is true for a (1.6 -> 1), b (1.4 -> 1) and c (2.5 -> 2); false for
        // d (-1.5 -> -1) and e (0.5 -> 0).
        string[] clrLabels = ["a", "b", "c"];
        // The driver's answer, with the cast dropped: D > 0 additionally admits e (0.5).
        string[] driverLabels = ["a", "b", "c", "e"];

        // Routing proof: NativeOnly has no fallback to land on, so succeeding at all proves the fall-through
        // reached the $expr path rather than declining the comparison.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            clrLabels,
            nativeOnly.Entities.AsNoTracking().Where(x => (int)x.D > 0)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        // LEG 1 — native, under the DEFAULT mode, returns the CLR rows.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking().Where(x => (int)x.D > 0)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(clrLabels, nativeLabels);

        // LEG 2 — in-memory LINQ over the SAME expression, evaluated over the same rows, agrees.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList().Where(x => (int)x.D > 0)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(clrLabels, inMemoryLabels);

        // LEG 3 — explicit DriverLinq returns its OWN, DIFFERENT rows. Asserted POSITIVELY (the exact row set
        // the driver produces), not merely as "not equal to native": a bare Assert.NotEqual would still pass
        // if the driver started returning some third, unrelated answer, and would not record WHAT the
        // divergence is. The NotEqual below is kept as well, so the test states the divergence itself and not
        // only the two row sets that happen to differ today.
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLabels_ = driverLinq.Entities.AsNoTracking().Where(x => (int)x.D > 0)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLabels, driverLabels_);
        Assert.NotEqual(nativeLabels, driverLabels_);
    }

    // ── 28. The MIRRORED operand order reaches the SAME fall-through ────────────────────────────────
    //
    // TranslateComparison classifies converts in TWO places -- the member-left branch and the mirrored
    // member-right branch -- and Task 7 changes the decline into a fall-through in BOTH. This case is the
    // member-RIGHT half: `0 < (int)x.D` is the same predicate written the other way round. Note the $expr
    // path deliberately does NOT mirror the operator (operand order matters inside $expr), so this is a
    // genuinely different code path to the one case 27 exercises, not a spelling of it.
    //
    // The divergence is the same one, and is deliberate and owner-ruled for the same reason (see case 27).

    [Fact]
    public void Mirrored_narrowing_cast_vs_constant_also_returns_the_CLR_answer()
    {
        var collection = Seed(nameof(Mirrored_narrowing_cast_vs_constant_also_returns_the_CLR_answer));

        string[] clrLabels = ["a", "b", "c"];
        string[] driverLabels = ["a", "b", "c", "e"];

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            clrLabels,
            nativeOnly.Entities.AsNoTracking().Where(x => 0 < (int)x.D)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking().Where(x => 0 < (int)x.D)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(clrLabels, nativeLabels);

        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            clrLabels,
            oracle.Entities.AsNoTracking().ToList().Where(x => 0 < (int)x.D)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking().Where(x => 0 < (int)x.D)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLabels, driverLinqLabels);

        // The same non-vacuity leg case 27 carries: state the DIVERGENCE itself, not only two row sets that
        // happen to differ today. Without it, a future change that made both sides return the driver's answer
        // would only be caught by the clrLabels assertions above -- which is enough, but leaves the test
        // silent about the thing it exists to record.
        Assert.NotEqual(nativeLabels, driverLinqLabels);
    }

    // ── 29. GUARD B holds the fall-through back from a value-converted property (Task 7) ─────────────
    //
    // WHAT THIS CASE PINS IS THE SHAPE'S DISPOSITION, not either individual guard -- the earlier
    // "load-bearing, not defence-in-depth" wording here was about CanFallThroughToExpr and is WITHDRAWN,
    // because the fix round below moved the real guard down to TranslateOperand's own convert branch and
    // MEASURED that CanFallThroughToExpr nets nothing on its own (forcing it to return true turns 0 of 33
    // functional and 0 of 121 unit tests red -- see its XML remarks). Under that mutation the deeper guard
    // catches this case; under the mirror mutation CanFallThroughToExpr does. Case 30 is the test that
    // individually nets the deeper guard.
    //
    // The silent-wrong-rows measurement behind the guard is still real, and is why the decline exists at all:
    // on the tree with Task 7's fall-through in place and NO guard anywhere, the NativeOnly leg below
    // "returned p" -- one row where ZERO is correct -- because the emitted
    // {$expr: {$gt: [{$toInt: "$Weight"}, 3]}} reads the RAW STORED value (7.0 -> 7 > 3 is true) instead of
    // the converted CLR value (3.5 -> 3, and 3 > 3 is false). Under the DEFAULT Native mode too, since the
    // query is natively representable and never reaches the fallback.
    //
    // Structured as an OUTCOME STRING rather than Assert.Throws, following the same A5/Task-6 precedent case
    // 25 uses: a future regression that re-admits the leaf fails with the actual WRONG ROWS printed in the
    // assertion message, not merely "no exception was thrown".

    [Fact]
    public void Narrowing_cast_comparison_over_a_value_converted_property_still_declines()
    {
        var name = UniqueCollectionName(
            nameof(Narrowing_cast_comparison_over_a_value_converted_property_still_declines));
        // CLR Weight = 3.5 (stored 7.0, the converted/provider form). (int)3.5 == 3, so `> 3` is FALSE --
        // ZERO rows is the correct answer. A raw read of the stored 7.0 truncates to 7, so `> 3` would be
        // TRUE -- one row, and that row is the discriminator.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 7.0 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<ConvertedWeightRow>(name);

        using var nativeOnly = CreateConvertedWeightContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight > 3).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        // As cases 12/13 and 25 already measure, there is NO working driver-LINQ oracle for a numeric cast
        // over a value-converted property in comparison position either (the
        // "ValueConverterSerializer`2 does not implement IHasRepresentationSerializer" limitation), so both
        // the fallback route and explicit DriverLinq throw. Asserted as "it threw", not by type or value:
        // there is no correct answer available on this route to compare against, only "did it avoid silently
        // returning the wrong one" -- which the NativeOnly assertion above is what actually covers. This is
        // also exactly the pre-Task-7 behaviour, so the guard RESTORES this shape's disposition rather than
        // giving it a new one.
        using var native = CreateConvertedWeightContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > 3).Select(x => x.Label).ToList()));

        using var driverLinq = CreateConvertedWeightContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > 3).Select(x => x.Label).ToList()));
    }

    // ── 30. The WITHIN-SLICE regression case 29's guard did NOT cover: field-to-field (fix round 1) ──
    //
    // Case 29 guards the member-vs-CONSTANT fall-through this task added. Review found the SAME mechanism
    // open one level down, on the FIELD-TO-FIELD shape, and traced it to Task 3 of this slice (94101da5,
    // unreleased) rather than to EF-329 -- so it was a within-slice REGRESSION to close, not an inherited
    // exposure to defer. MEASURED, `Where(x => (int)x.Weight > x.Other)`, CLR Weight 3.5 (stored 7.0) vs
    // Other 5, correct answer ZERO rows ((int)3.5 == 3, and 3 > 5 is false):
    //
    //   slice base fd6bd8ba   : NativeOnly threw, default Native THREW, in-memory []
    //   HEAD before this fix  : NativeOnly returned [p], default Native RETURNED [p]   <-- WRONG, and SILENT
    //   after this fix        : NativeOnly threw, default Native threw (as at the base)
    //
    // Note the failure mode is strictly worse than case 29's: there is no cast-vs-constant subtlety here, it
    // is simply the $toX reading the provider value where the model value was meant.

    private class ConvertedWeightPairRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double Weight { get; set; }
        public int Other { get; set; }
    }

    private static readonly Action<ModelBuilder> ConvertedWeightPairRowModel =
        mb => mb.Entity<ConvertedWeightPairRow>().Property(e => e.Weight).HasConversion(v => v * 2, v => v / 2);

    [Fact]
    public void Field_to_field_cast_over_a_value_converted_property_still_declines()
    {
        var name = UniqueCollectionName(nameof(Field_to_field_cast_over_a_value_converted_property_still_declines));
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 7.0 }, { "Other", 5 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<ConvertedWeightPairRow>(name);

        // Outcome-string, for the same reason case 29 uses one: a regression that re-admits this shape fails
        // with the actual WRONG ROW printed, not merely "no exception was thrown".
        using var nativeOnly = CreatePairContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight > x.Other).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        // Same "no working driver-LINQ oracle" limitation as cases 12/13/25/29 -- both routes throw, which is
        // exactly the slice base's behaviour, restored.
        using var native = CreatePairContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > x.Other).Select(x => x.Label).ToList()));

        using var driverLinq = CreatePairContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > x.Other).Select(x => x.Label).ToList()));
    }

    // The control that keeps case 30 from being read as "any field-to-field cast declines": the SAME shape
    // over a DEFAULT-serialized field goes native and returns the CLR answer. Without this, tightening the
    // guard to reject every cast operand would look green.
    [Fact]
    public void Field_to_field_cast_over_a_default_serialized_property_still_goes_native()
    {
        var name = UniqueCollectionName(nameof(Field_to_field_cast_over_a_default_serialized_property_still_goes_native));
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 7.0 }, { "Other", 5 } },   // (int)7.0 = 7 > 5 -> yes
            new BsonDocument { { "Label", "q" }, { "Weight", 3.5 }, { "Other", 5 } }    // (int)3.5 = 3 > 5 -> no
        ]);
        var collection = database.MongoDatabase.GetCollection<PlainWeightPairRow>(name);

        using var nativeOnly = CreatePlainPairContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["p"],
            nativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight > x.Other)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        using var native = CreatePlainPairContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            ["p"],
            native.Entities.AsNoTracking().Where(x => (int)x.Weight > x.Other)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    private class PlainWeightPairRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double Weight { get; set; }
        public int Other { get; set; }
    }

    private static SingleEntityDbContext<ConvertedWeightPairRow> CreatePairContext(
        IMongoCollection<ConvertedWeightPairRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: ConvertedWeightPairRowModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static SingleEntityDbContext<PlainWeightPairRow> CreatePlainPairContext(
        IMongoCollection<PlainWeightPairRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 34. C1 (EF-403 fix wave): a RELATIONAL cast comparison over a NULLABLE property must NOT leave ──
    //        the TYPE-BRACKETED query dialect
    //
    // THE DEFECT. Task 7's site-B fall-through moved a `member <op> constant` comparison out of the
    // type-bracketed query dialect and into $expr. MongoDB's query dialect TYPE-BRACKETS a relational operator:
    // {Price: {$lt: 100}} matches neither a stored BSON null nor a MISSING element. The $expr form does not --
    // $toInt/$toDouble map both of those to null, and BSON TOTAL ORDER puts Null BELOW every number, so a
    // null/missing row satisfies $lt and $lte. MEASURED end to end on a live server, over the fixture below:
    //
    //   {Price: {$lt: 100}}                        -> [p1_50]                        <- released packages
    //   {$expr: {$lt: [{$toInt: "$Price"}, 100]}}  -> [p1_50, p3_null, p4_missing]    <- the fall-through
    //
    // This is the invariant MongoExpressionNegator's class remarks already record ("silent wrong data, under
    // default Native, on an extremely ordinary input"), re-opened from the other direction: there it is about
    // NEGATING a type-bracketed comparison, here about a comparison that WAS bracketed and stopped being so.
    //
    // *** THE SPELLING MATTERS, AND THAT IS THE EXPENSIVE FACT TO RE-DERIVE. *** Whether the DRIVER (i.e. the
    // released behaviour, since MongoQueryMode does not exist at v10.0.2/v9.1.2/v8.4.2) type-brackets this
    // comparison depends on HOW THE CAST IS SPELLED, and it is not predictable from the property alone.
    // MEASURED, same fixture, same server, driver-LINQ emission:
    //
    //   LIFTED   (int?)x.Price < 100   ->  {Price: {$lt: 100}}                      cast DROPPED, bracketed
    //   UNLIFTED (int)x.Price  < 100   ->  {$expr: {$lt: [{$toInt: "$Price"}, 100]}} cast RENDERED, NOT bracketed
    //
    // The LIFTED spelling is the one Northwind uses (its pre-slice baseline for Decimal_cast_to_double_works is
    // {"UnitPrice": {"$gt": 100.0}}), and it is the only one on which this guard is OBSERVABLE at all: for the
    // unlifted spelling the fallback the guard routes to emits the very $expr form the guard is avoiding, so
    // rows are unchanged either way. A test written against the unlifted spelling would therefore be VACUOUS --
    // it would pass with the guard deleted. Every legged assertion below is deliberately LIFTED.
    //
    // The provider cannot key the guard on which spelling the driver happens to drop -- that is the driver's own
    // per-shape behaviour, unknowable at translation time -- so it must instead never emit a form that admits
    // null/missing for an operator whose query-dialect form excludes them. That is exactly what the guard does.
    //
    // WHY ALL FOUR RELATIONAL OPERATORS AND NOT JUST < AND <=. Only < and <= measurably differ: because Null
    // sorts below every number, $gt/$gte happen to exclude the ragged rows too, so they happen to agree. That
    // agreement is an ACCIDENT of collation order, not a property of the rendering. The alternative fix --
    // emitting a {Price: {$ne: null}} conjunct -- is NOT the exact complement either (type bracketing also
    // excludes every foreign BSON type; $ne: null does not), and this codebase's recorded rule for exactly this
    // family is MongoExpressionNegator's: EXACT COMPLEMENT OR DECLINE, NEVER AN APPROXIMATION. Declining is the
    // only exact option available, so it is the one taken.
    //
    // WHAT IT COSTS, stated rather than hidden: NorthwindWhereQueryMongoTest.Decimal_cast_to_double_works is
    // exactly this shape over Product.UnitPrice (decimal?), so the slice's ONLY specification conversion from
    // the fall-through reverts to driver-LINQ and its baseline returns to {"UnitPrice": {"$gt": 100.0}}. It was
    // NOT returning wrong rows (it uses $gt); it reverts because the guard is keyed on a property of the
    // RENDERING, not on which operators luck out.
    //
    // TWO CONTROLS keep the guard from being satisfied by over-declining: EQUALITY over the same nullable
    // property still goes native ($eq/$ne partition every BSON value including null and missing, so moving one
    // into $expr changes nothing), and a RELATIONAL comparison over a NON-NULLABLE property still goes native
    // (that is the owner-ruled CLR-correct divergence of case 27, which this guard must not revoke).

    private class NullablePriceRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double? Price { get; set; }
        public decimal? Amount { get; set; }
        public double Weight { get; set; }
    }

    [Fact]
    public void Relational_cast_comparison_over_a_nullable_property_declines_instead_of_untype_bracketing()
    {
        var collection = SeedNullablePrices(
            nameof(Relational_cast_comparison_over_a_nullable_property_declines_instead_of_untype_bracketing));

        // All four relational operators DECLINE (NativeOnly throws) and fall back to the type-bracketed rows
        // under Native -- identical to explicit DriverLinq. The expected sets are asserted PER OPERATOR, not
        // just for parity: parity alone would pass if BOTH paths returned the ragged rows.
        AssertRelationalCastDeclines(collection, x => (int?)x.Price < 100, "returned p1_50");
        AssertRelationalCastDeclines(collection, x => (int?)x.Price <= 100, "returned p1_50");
        AssertRelationalCastDeclines(collection, x => (int?)x.Price > 100, "returned p2_150");
        AssertRelationalCastDeclines(collection, x => (int?)x.Price >= 100, "returned p2_150");

        // The mirrored branch (member on the RIGHT) has its own separate CanFallThroughToExpr call site.
        AssertRelationalCastDeclines(collection, x => 100 > (int?)x.Price, "returned p1_50");

        // decimal? -> double?, the exact Northwind Decimal_cast_to_double_works shape.
        AssertRelationalCastDeclines(collection, x => (double?)x.Amount < 100, "returned p1_50");
        AssertRelationalCastDeclines(collection, x => (double?)x.Amount > 100, "returned p2_150");

        // CONTROL 1 -- equality over the SAME nullable property still falls through and goes native.
        using (var eqNativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal(
                ["p1_50"],
                eqNativeOnly.Entities.AsNoTracking().Where(x => (int?)x.Price == 50)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        }

        // CONTROL 2 -- a relational cast comparison over a NON-NULLABLE property still goes native (case 27's
        // owner-ruled shape). Weight: p1 = 1.6, p2 = 0.5, p3 = 2.5, p4 = MISSING -> (int) 1, 0, 2, null.
        using (var relNativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal(
                ["p1_50", "p3_null"],
                relNativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight > 0)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        }
    }

    // ── 34b. The RESIDUAL this guard deliberately does NOT close, pinned as MEASURED — not as correct ──
    //
    // The same un-type-bracketing reaches a NON-NULLABLE property through a MISSING element: p4_missing has no
    // Weight at all, so $toInt yields null, and null < 2 is true under BSON total order. MEASURED:
    //
    //   Native / NativeOnly : {$expr: {$lt: [{$toInt: "$Weight"}, 2]}} -> p1_50, p2_150, p4_missing
    //   DriverLinq          : {Weight: {$lt: 2}}                       -> p1_50, p2_150
    //
    // NOT CLOSED HERE, deliberately, and the reason is scope rather than taste: gating a relational cast on the
    // OPERATOR alone (dropping the nullability conjunct) would revoke case 27's owner-ruled CLR-correct
    // divergence -- `(int)x.D > 0` is exactly a relational cast comparison -- i.e. it would undo the
    // fall-through for relational comparisons entirely, which is a far larger change than this fix wave's
    // remit. And the document it affects VIOLATES THE MODEL: an absent element for a required non-nullable
    // property is a state the provider's own read path rejects ("Document element 'Weight' is missing for
    // required non-nullable property"), so there is no in-memory CLR oracle for it at all -- materializing the
    // entity throws before any comparison happens.
    //
    // Pinned so it cannot change silently in either direction, and so the next person to touch this guard finds
    // the measurement rather than re-deriving it.

    [Fact]
    public void Missing_element_on_a_NON_nullable_property_still_reaches_the_untype_bracketed_expr_form()
    {
        var collection = SeedNullablePrices(
            nameof(Missing_element_on_a_NON_nullable_property_still_reaches_the_untype_bracketed_expr_form));

        using var nativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["p1_50", "p2_150", "p4_missing"],
            nativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight < 2)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        using var driverLinq = CreateNullablePriceContext(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(
            ["p1_50", "p2_150"],
            driverLinq.Entities.AsNoTracking().Where(x => (int)x.Weight < 2)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        // The premise: there IS no CLR oracle here, because materializing p4_missing throws first.
        using var oracle = CreateNullablePriceContext(collection, MongoQueryMode.Native);
        Assert.Throws<InvalidOperationException>(() => oracle.Entities.AsNoTracking().ToList());
    }

    private void AssertRelationalCastDeclines(
        IMongoCollection<NullablePriceRow> collection,
        Expression<Func<NullablePriceRow, bool>> predicate,
        string expectedOutcome)
    {
        // Every leg -- including the routing one -- goes through DescribeOutcome and projects LABELS rather than
        // whole entities, for two reasons that were both found the hard way. (a) Outcome STRINGS make a
        // regression NAME the ragged rows it wrongly admitted ("returned p1_50,p3_null,p4_missing") instead of
        // only reporting a missing exception, which is what an Assert.Throws routing leg would have said.
        // (b) A WHOLE-ENTITY read of this fixture throws on its own, because p4_missing omits the non-nullable
        // Weight -- so a .Where(predicate).ToList() routing leg fails with InvalidOperationException under the
        // very mutation it is meant to catch, hiding the rows. See case 34b, which asserts that materialization
        // throw as its own premise.
        using var nativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyOutcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label)
                .ToList(),
            l => l);
        Assert.Equal("threw NativeTranslationNotSupportedException", nativeOnlyOutcome);

        using var native = CreateNullablePriceContext(collection, MongoQueryMode.Native);
        var nativeOutcome = DescribeOutcome(
            () => native.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList(),
            l => l);

        using var driverLinq = CreateNullablePriceContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqOutcome = DescribeOutcome(
            () => driverLinq.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label)
                .ToList(),
            l => l);

        Assert.Equal(expectedOutcome, nativeOutcome);
        Assert.Equal(nativeOutcome, driverLinqOutcome);
    }

    // Four rows, three states for the nullable properties: a value (twice, so an assertion is never a one-row
    // accident), an explicit BSON null, and a MISSING element. Weight is NON-nullable and is also absent on the
    // fourth row, which is what case 34b needs. The seed SELF-CHECKS the stored shape, because "missing" and
    // "present but null" are indistinguishable from results alone and an un-self-checked seed could silently
    // degrade to two states -- which is exactly the axis these cases exist to exercise.
    private IMongoCollection<NullablePriceRow> SeedNullablePrices(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        var typed = database.MongoDatabase.GetCollection<NullablePriceRow>(raw.CollectionNamespace.CollectionName);

        // The three well-formed rows go in TYPED so decimal? gets whatever representation the driver's own
        // mapping produces, rather than one hand-picked here; the fourth is raw, since a missing element cannot
        // be expressed through the typed writer at all.
        typed.InsertMany(
        [
            new NullablePriceRow { Label = "p1_50", Price = 50.0, Amount = 50m, Weight = 1.6 },
            new NullablePriceRow { Label = "p2_150", Price = 150.0, Amount = 150m, Weight = 0.5 },
            new NullablePriceRow { Label = "p3_null", Price = null, Amount = null, Weight = 2.5 }
        ]);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "p4_missing" } });

        var stored = raw.Find(FilterDefinition<BsonDocument>.Empty).ToList().ToDictionary(d => d["Label"].AsString);
        Assert.Equal(4, stored.Count);
        Assert.Equal(50.0, stored["p1_50"]["Price"].AsDouble);
        Assert.Equal(150.0, stored["p2_150"]["Price"].AsDouble);
        Assert.True(stored["p3_null"]["Price"].IsBsonNull);
        Assert.True(stored["p3_null"]["Amount"].IsBsonNull);
        Assert.False(stored["p4_missing"].Contains("Price"));
        Assert.False(stored["p4_missing"].Contains("Amount"));
        Assert.False(stored["p4_missing"].Contains("Weight"));

        return typed;
    }

    private static SingleEntityDbContext<NullablePriceRow> CreateNullablePriceContext(
        IMongoCollection<NullablePriceRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 35. I2 (EF-403 fix wave): an owned-collection .Count compared against a NON-INTEGRAL threshold ──
    //
    // MongoQueryLanguageRenderer.TryRenderSizeComparison's remarks used to carry a MEASURED claim that this
    // shape "falls back to driver-LINQ before this method is ever reached", because TranslateOperand's convert
    // guard "rejects that convert outright". THIS SLICE FALSIFIED IT: Convert(count, Double) now matches the
    // new MongoConvertExpression branch, the operand resolves to a MongoSizeExpression,
    // AllFieldsDefaultSerialized admits it on its catch-all (a size node carries no IProperty to check), and
    // the query goes NATIVE. The OUTPUT is correct, so this is an unrecorded INCIDENTAL WIDENING plus a
    // now-false MEASURED claim, not a bug -- recorded deliberately, because this repo's own record says four
    // earlier slices had to retro-fit an unnoticed widening.
    //
    // The MQL leg is what makes this case discriminating rather than decorative: it pins that the comparison
    // takes the $expr TIER and NOT the query-dialect array-index form, which would answer the WRONG question
    // (an array-index $exists test can only express an integral threshold).

    [Fact]
    public void Count_compared_against_a_non_integral_threshold_goes_native_via_expr()
    {
        var collection = database.MongoDatabase.GetCollection<QuantBlog>(
            UniqueCollectionName(nameof(Count_compared_against_a_non_integral_threshold_goes_native_via_expr)));
        collection.InsertMany(
        [
            new QuantBlog { Title = "b0", Posts = [] },
            new QuantBlog { Title = "b2", Posts = [new QuantPost(), new QuantPost()] },
            new QuantBlog { Title = "b3", Posts = [new QuantPost(), new QuantPost(), new QuantPost()] }
        ]);

        // Routing proof plus the answer: Count > 2.5 selects only the 3-post blog.
        using var nativeOnly = CreateQuantContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            ["b3"],
            nativeOnly.Entities.AsNoTracking().Where(b => b.Posts.Count > 2.5)
                .OrderBy(b => b.Title).Select(b => b.Title).ToList());

        var logs = new List<string>();
        using (var native = CreateQuantContext(collection, MongoQueryMode.Native, logs))
        {
            Assert.Equal(
                ["b3"],
                native.Entities.AsNoTracking().Where(b => b.Posts.Count > 2.5)
                    .OrderBy(b => b.Title).Select(b => b.Title).ToList());
        }

        var mql = MqlPipeline(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("$toDouble", mql);
        // NOT the array-index tier: {"Posts.2": {$exists: true}} answers Count > 2, a different question.
        Assert.DoesNotContain("Posts.2", mql);

        // In-memory LINQ over the same expression, and explicit DriverLinq, both agree -- so the widening is
        // value-preserving, which is what makes it benign rather than a second owner ruling.
        using var oracle = CreateQuantContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            ["b3"],
            oracle.Entities.AsNoTracking().ToList().Where(b => b.Posts.Count > 2.5)
                .OrderBy(b => b.Title).Select(b => b.Title).ToList());

        using var driverLinq = CreateQuantContext(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(
            ["b3"],
            driverLinq.Entities.AsNoTracking().Where(b => b.Posts.Count > 2.5)
                .OrderBy(b => b.Title).Select(b => b.Title).ToList());
    }

    // ── 36. M4 (EF-403 fix wave): $toInt's ROUNDING MODE is pinned — it truncates toward zero ─────────
    //
    // Case 27's fixture cannot discriminate this: every one of its values gives the same answer whether MQL
    // truncates, floors or rounds. Both assertions below are chosen so that exactly one rounding rule survives.
    //
    //   d: D = -1.5  ->  truncate-toward-zero -1 | floor -2 | round-half-even -2 | round-half-away -2
    //   a: D =  1.6  ->  truncate 1             | floor 1  | round 2
    //   c: D =  2.5  ->  truncate 2             | floor 2  | round-half-even 2 | round-half-away 3
    //
    // (1) A threshold of -1.5 lies strictly BETWEEN the truncated (-1) and floored (-2) results, so d is in the
    //     result set under truncation and absent under floor/round -- d's presence IS the discriminator.
    // (2) (int)D == 2 selects only c under truncation; under round-half-even it would ALSO select a (1.6 -> 2).
    //
    // The oracle here is IN-MEMORY LINQ, not driver-LINQ: this is case 27's owner-ruled divergence, so explicit
    // DriverLinq drops the cast and answers a different question ({D: {$gt: -1.5}} excludes d, whose D is
    // exactly -1.5; {D: {$eq: 2}} matches nothing). C# truncates toward zero, and native agrees with C#.

    [Fact]
    public void Cast_truncates_toward_zero_rather_than_flooring_or_rounding()
    {
        var collection = Seed(nameof(Cast_truncates_toward_zero_rather_than_flooring_or_rounding));

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            nativeOnly.Entities.AsNoTracking().Where(x => (int)x.D > -1.5)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        Assert.Equal(
            ["c"],
            nativeOnly.Entities.AsNoTracking().Where(x => (int)x.D == 2)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        // The CLR oracle, over the same expressions and the same rows.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var materialized = oracle.Entities.AsNoTracking().ToList();
        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            materialized.Where(x => (int)x.D > -1.5).OrderBy(x => x.Label).Select(x => x.Label).ToList());
        Assert.Equal(
            ["c"],
            materialized.Where(x => (int)x.D == 2).OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    // ── 37. I1 (EF-403 fix wave): an OUT-OF-RANGE value aborts the WHOLE query, not just its own row ──
    //
    // MongoConvertExpression's remarks used to tag this UNVERIFIED. MEASURED here, and the answer is worse than
    // "the offending row errors": $expr is evaluated for every document the stage SCANS, so one unconvertible
    // value aborts the entire aggregate -- including for documents that would never have matched, and even for
    // a predicate that matches NOTHING. The released packages returned rows (they drop the cast).
    //
    // DISPOSITION, re-taken explicitly rather than inherited: KEEP the server error; do NOT add $convert's
    // onError. Three answers are available and all three differ -- released returns rows (from a comparison the
    // query did not ask for), unchecked C# produces an unspecified wrapped value, and onError:null would give a
    // THIRD answer matching neither, because a converted-to-null operand then participates in a BSON-total-order
    // comparison and quietly moves the row into or out of the result depending on the operator (the very
    // silent, operator-dependent behaviour case 34's guard exists to prevent). A loud abort is the only one of
    // the three that cannot be mistaken for an answer. Recorded in BREAKING-CHANGES.md.

    private class BigRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double D { get; set; }
    }

    [Fact]
    public void Out_of_range_narrowing_cast_aborts_the_whole_query()
    {
        var collection = database.MongoDatabase.GetCollection<BigRow>(
            UniqueCollectionName(nameof(Out_of_range_narrowing_cast_aborts_the_whole_query)));
        collection.InsertMany(
        [
            new BigRow { Label = "small", D = 1.6 },
            new BigRow { Label = "big", D = 1e30 }
        ]);

        foreach (var mode in new[] { MongoQueryMode.Native, MongoQueryMode.NativeOnly })
        {
            using var db = CreateBigContext(collection, mode);

            var ex = Assert.Throws<MongoCommandException>(
                () => db.Entities.AsNoTracking().Where(x => (int)x.D > 0)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
            Assert.Contains("overflow", ex.Message);

            // THE BLAST-RADIUS LEG, and the reason this case is not just "an overflow throws": the predicate
            // below matches NO document under any rounding rule ((int)1.6 = 1, and 1e30 overflows), so a
            // per-ROW failure would simply have produced an empty result. It aborts anyway.
            Assert.Throws<MongoCommandException>(
                () => db.Entities.AsNoTracking().Where(x => (int)x.D < 0)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        }

        // The released behaviour, still available through the documented escape hatch: the driver drops the
        // cast, so both queries answer and neither aborts.
        using var driverLinq = CreateBigContext(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(
            ["big", "small"],
            driverLinq.Entities.AsNoTracking().Where(x => (int)x.D > 0)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        Assert.Empty(
            driverLinq.Entities.AsNoTracking().Where(x => (int)x.D < 0)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    private static SingleEntityDbContext<BigRow> CreateBigContext(
        IMongoCollection<BigRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── 38. EF-404: a PLAIN field-to-field comparison (no cast) over a value-converted operand ──
    //        must decline, not read the raw stored value through $expr
    //
    // Case 30 closed this hazard for the CAST subset (`(int)x.Weight > x.Other`); this closes the PLAIN
    // subset (`x.Weight > x.Other`, no cast at all), which routes straight to TranslateComparison's general
    // $expr path and previously had no default-serialization check on either operand.
    //
    // MEASURED, all four combinations of {value-transforming, re-encoding} x {one side converted, both sides
    // converted}: under the RELEASED/pre-fix general $expr path, Native read the RAW stored value on the
    // converted side(s) and disagreed with the CLR/model answer every time, while DriverLinq has NO working
    // oracle for this shape at all -- the driver's own LINQ3 provider throws ExpressionNotSupportedException
    // ("the two arguments are serialized differently") for a field-to-field comparison whenever the two
    // operands' serializers differ, regardless of query mode. So this is squarely the ticket's outcome (2):
    // "Native differs from DriverLinq" (DriverLinq's disagreement takes the form of a decline, not a
    // different wrong answer) -- a genuine native-only defect, fixed by declining to fall back, which lands
    // on the same throw DriverLinq already produces for this shape.

    private class OneSideTransformRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double Weight { get; set; } // converted v => v * 2
        public double Other { get; set; } // plain
    }

    private static readonly Action<ModelBuilder> OneSideTransformRowModel =
        mb => mb.Entity<OneSideTransformRow>().Property(e => e.Weight).HasConversion(v => v * 2, v => v / 2);

    [Fact]
    public void Field_to_field_comparison_over_a_one_side_transforming_converter_declines()
    {
        var name = UniqueCollectionName(nameof(Field_to_field_comparison_over_a_one_side_transforming_converter_declines));
        // CLR Weight=3, Other=4 -> model answer 3>4 FALSE. Stored Weight=6 (converted), Other=4 (plain) ->
        // a raw-value read would compare 6>4 TRUE -- the wrong row, and the discriminator this test pins.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 6.0 }, { "Other", 4.0 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<OneSideTransformRow>(name);

        using var nativeOnly = CreateOneSideTransformContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => x.Weight > x.Other).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        // No driver-LINQ oracle exists for this shape either (measured): both routes decline/throw.
        using var native = CreateOneSideTransformContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));

        using var driverLinq = CreateOneSideTransformContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));
    }

    private static SingleEntityDbContext<OneSideTransformRow> CreateOneSideTransformContext(
        IMongoCollection<OneSideTransformRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: OneSideTransformRowModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private class BothSideTransformRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double Weight { get; set; } // converted v => v * 2
        public double Other { get; set; } // converted v => v * 3
    }

    private static readonly Action<ModelBuilder> BothSideTransformRowModel = mb =>
    {
        mb.Entity<BothSideTransformRow>().Property(e => e.Weight).HasConversion(v => v * 2, v => v / 2);
        mb.Entity<BothSideTransformRow>().Property(e => e.Other).HasConversion(v => v * 3, v => v / 3);
    };

    [Fact]
    public void Field_to_field_comparison_over_a_both_side_transforming_converter_declines()
    {
        var name = UniqueCollectionName(nameof(Field_to_field_comparison_over_a_both_side_transforming_converter_declines));
        // CLR Weight=3, Other=2 -> model answer 3>2 TRUE. Stored Weight=6 (x2), Other=6 (x3) -> a raw-value
        // read would compare 6>6 FALSE, wrongly EXCLUDING a row the model says matches.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 6.0 }, { "Other", 6.0 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<BothSideTransformRow>(name);

        using var nativeOnly = CreateBothSideTransformContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => x.Weight > x.Other).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        using var native = CreateBothSideTransformContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));

        using var driverLinq = CreateBothSideTransformContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));
    }

    private static SingleEntityDbContext<BothSideTransformRow> CreateBothSideTransformContext(
        IMongoCollection<BothSideTransformRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BothSideTransformRowModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private class OneSideReencodeRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Weight { get; set; } // stored as string
        public int Other { get; set; } // plain
    }

    private static readonly Action<ModelBuilder> OneSideReencodeRowModel =
        mb => mb.Entity<OneSideReencodeRow>().Property(e => e.Weight).HasConversion<string>();

    [Fact]
    public void Field_to_field_comparison_over_a_one_side_reencoding_converter_declines()
    {
        var name = UniqueCollectionName(nameof(Field_to_field_comparison_over_a_one_side_reencoding_converter_declines));
        // CLR Weight=9, Other=10 -> model answer 9>10 FALSE. Stored Weight="9" (string), Other=10 (int32) --
        // BSON type-brackets a string above every numeric type by total order, so a raw comparison inside
        // $expr answers "9" > 10 as TRUE regardless of the numeric values, wrongly admitting the row.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", "9" }, { "Other", 10 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<OneSideReencodeRow>(name);

        using var nativeOnly = CreateOneSideReencodeContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => x.Weight > x.Other).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        using var native = CreateOneSideReencodeContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));

        using var driverLinq = CreateOneSideReencodeContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));
    }

    private static SingleEntityDbContext<OneSideReencodeRow> CreateOneSideReencodeContext(
        IMongoCollection<OneSideReencodeRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: OneSideReencodeRowModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private class BothSideReencodeRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Weight { get; set; } // stored as string
        public int Other { get; set; } // stored as string
    }

    private static readonly Action<ModelBuilder> BothSideReencodeRowModel = mb =>
    {
        mb.Entity<BothSideReencodeRow>().Property(e => e.Weight).HasConversion<string>();
        mb.Entity<BothSideReencodeRow>().Property(e => e.Other).HasConversion<string>();
    };

    [Fact]
    public void Field_to_field_comparison_over_a_both_side_reencoding_converter_declines()
    {
        var name = UniqueCollectionName(nameof(Field_to_field_comparison_over_a_both_side_reencoding_converter_declines));
        // CLR Weight=9, Other=10 -> model answer 9>10 FALSE. Stored Weight="9", Other="10", both strings --
        // a raw comparison inside $expr would compare them LEXICOGRAPHICALLY ("9" > "10" is TRUE, since '9' >
        // '1'), wrongly admitting the row under a numeric-looking predicate.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", "9" }, { "Other", "10" } }
        ]);
        var collection = database.MongoDatabase.GetCollection<BothSideReencodeRow>(name);

        using var nativeOnly = CreateBothSideReencodeContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => x.Weight > x.Other).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        using var native = CreateBothSideReencodeContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));

        using var driverLinq = CreateBothSideReencodeContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => x.Weight > x.Other).Select(x => x.Label).ToList()));
    }

    private static SingleEntityDbContext<BothSideReencodeRow> CreateBothSideReencodeContext(
        IMongoCollection<BothSideReencodeRow> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: BothSideReencodeRowModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // The control that keeps case 38 from being read as "any field-to-field comparison declines": the SAME
    // shape over DEFAULT-serialized fields still goes native and returns the CLR answer -- this is exactly
    // case 30's control (`Field_to_field_cast_over_a_default_serialized_property_still_goes_native`), which
    // already covers a bare (non-converted) field-to-field comparison, so it is not repeated here.

    // ── Seed and helpers ────────────────────────────────────────────────────────────────────────────

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(UniqueCollectionName(name));
        collection.InsertMany(Rows.Select(r => new Row { Label = r.Label, D = r.D, I = r.I }));
        return collection;
    }

    private IMongoCollection<Row> SeedComparisonRows(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(UniqueCollectionName(name));
        collection.InsertMany(ComparisonRows.Select(r => new Row { Label = r.Label, D = r.D, I = r.I }));
        return collection;
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static SingleEntityDbContext<Row> CreateContext(
        IMongoCollection<Row> collection, MongoQueryMode mode, List<string>? logs = null)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                if (logs is not null)
                    b.LogTo(logs.Add).EnableSensitiveDataLogging();
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static string Mql(List<string> logs)
        => Assert.Single(logs, l => l.Contains("Executed MQL query"));

    // The captured log line carries a leading timestamp, so it can never be compared across two contexts as-is.
    // Trim to the pipeline itself so two modes' emissions ARE comparable.
    private static string MqlPipeline(List<string> logs)
    {
        var line = Mql(logs);
        return line[line.IndexOf("Executed MQL query", StringComparison.Ordinal)..];
    }
}
