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
/// Numeric/enum casts in sort keys, predicates and projections. A narrowing or signed/unsigned cast must never be
/// silently stripped (that sorts/filters by the raw stored value); it either renders as an explicit
/// <c>MongoConvertExpression</c> (<c>$toX</c>) or declines.
/// </summary>
/// <remarks>
/// <para>
/// Sort keys (cases 1–5): narrowing casts render via <c>$toInt</c>; casts MQL can't express (<c>short</c>,
/// <c>uint</c>, <c>float</c>) decline, and the fallback fails loudly too. <c>MongoConvertExpression</c> must stay
/// out of <c>MongoQueryLanguageRenderer.IsQueryDialectRenderable</c> (case 8), because <c>$expr</c> is a server
/// error inside <c>$elemMatch</c>.
/// </para>
/// <para>
/// Comparisons (cases 9–19): <c>TranslateComparison</c>'s query-native branch and its <c>HasNumericConvert</c>
/// guard. Widening converts serialize the constant with the comparison's type; identity-like converts (enum ↔
/// underlying type, <c>char</c> → <c>int</c>, boxing) keep the property's serializer, so an enum-as-string
/// constant renders as <c>"Active"</c>. <c>HasDefaultKeySerialization</c> guards value converters (case 12
/// zero rows, case 13 wrong rows). Case 14 (<c>long</c> → <c>double</c> above 2^53) is an accepted divergence:
/// native equals driver-LINQ, both differ from in-memory LINQ.
/// </para>
/// <para>
/// Projection leaves (cases 20–26): wrapped and bare cast leaves go native (bare ones under the synthetic
/// <c>_v</c> alias). The shaper must keep the <c>Convert</c> node when registering the leaf, or the value is read
/// back through the pre-cast member's serializer (see <c>MongoProjectionBindingExpressionVisitor.Visit</c> and
/// <c>MongoProjectionBindingRemovingExpressionVisitor.VisitExtension</c>). Casts over value-converted properties
/// decline via <c>MongoExpressionTranslator.AllFieldsDefaultSerialized</c>.
/// </para>
/// <para>
/// <c>$expr</c> fall-through (cases 27–31): a comparison whose cast the query-native branch can't absorb falls
/// through to the <c>$expr</c> path. <b>Case 27 is a deliberate divergence:</b> for a narrowing cast against a
/// constant, native returns the CLR answer and driver-LINQ a different one. A narrowing cast over a value-converted
/// property must not fall through; the guard is on <c>TranslateOperand</c>'s convert branch (case 30 is the
/// tripwire, case 31 the control).
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

    // Rows a..e. D makes truncation observable; I makes signed/unsigned and narrowing reinterpretation observable.
    //
    //   a: D =  1.6, I =        1
    //   b: D =  1.4, I =        2
    //   c: D =  2.5, I =        3
    //   d: D = -1.5, I =       -1          // negative: (uint) reinterprets it as huge
    //   e: D =  0.5, I =    50000          // > short.MaxValue AND >= 32768, so (short) wraps NEGATIVE
    //
    // raw-D order (ascending by D):         d(-1.5), e(0.5), b(1.4), a(1.6), c(2.5)  -> d, e, b, a, c
    // (int)D order (ascending, truncating):  d(-1),  e(0),  {a,b}=1 tie, c(2)        -> d, e, a, b, c
    //     (a/b tie; $sort guarantees no tie order, so server-side legs add .ThenBy(x => x.Label).)
    // raw-I order (ascending by I):          d(-1), a(1), b(2), c(3), e(50000)       -> d, a, b, c, e
    // (uint)I order (ascending, unsigned reinterpretation):
    //     a=1, b=2, c=3, e=50000, d=4294967295 (unchecked (uint)(-1))                -> a, b, c, e, d
    // (short)I order (ascending, narrowing truncation to 16 bits):
    //     a=1, b=2, c=3, d=-1, e=-15536 (50000 - 65536)                              -> e, d, a, b, c
    //
    // Both (uint) and (short) genuinely reorder versus raw-I. (A value like 70000 would not: (short)70000 = 4464.)
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

        // .ThenBy(x => x.Label) breaks the a/b tie: $sort makes no tie-order guarantee, while in-memory OrderBy is
        // stable, so without it the oracles could pass by accident.
        //
        // The lambda is written inline at every call site: a Func<Row,int> local would bind Enumerable.OrderBy and
        // sort client-side, never sending the sort to the server.

        // NativeOnly: TryTranslateField declines the order-changing cast and TryTranslateValue renders $toInt.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(IntDOrder, nativeOnlyLabels);

        // Oracle 1: explicit DriverLinq.
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(IntDOrder, driverLinqLabels);

        // Oracle 2: in-memory LINQ over the same rows.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(IntDOrder, inMemoryLabels);

        // Default Native must agree with both oracles and not produce the raw-D order.
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

        // Verify the hand-computed UIntIOrder by execution.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .OrderBy(x => (uint)x.I).Select(x => x.Label).ToList();
        Assert.Equal(UIntIOrder, inMemoryLabels);
        Assert.NotEqual(RawIOrder, inMemoryLabels);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        // The driver can't translate a (uint) reinterpretation of a signed field either, so the fallback throws
        // ExpressionNotSupportedException rather than returning a wrong order. Asserted by type so a regression to
        // a silent wrong order is caught.
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

        // Verify the hand-computed ShortIOrder by execution.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .OrderBy(x => (short)x.I).Select(x => x.Label).ToList();
        Assert.Equal(ShortIOrder, inMemoryLabels);
        Assert.NotEqual(RawIOrder, inMemoryLabels);

        using var native = CreateContext(collection, MongoQueryMode.Native);
        // As case 2: loud, with the driver's own exception type.
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => native.Entities.AsNoTracking().OrderBy(x => (short)x.I).ToList()));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.IsType<ExpressionNotSupportedException>(
            Record.Exception(() => driverLinq.Entities.AsNoTracking().OrderBy(x => (short)x.I).ToList()));
    }

    // ── 4. Widening and boxing cast sort keys — the control that stops over-declining ──────────────

    [Fact]
    public void Widening_cast_sort_keys_are_unchanged_and_native()
    {
        var collection = Seed(nameof(Widening_cast_sort_keys_are_unchanged_and_native));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        // Widening/boxing conversions are order-preserving, so they resolve to the raw field and stay native.
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

    // ── 5. Narrowing (int)double cast sort key renders $toInt ─────────────────────────────────────

    [Fact]
    public void Narrowing_cast_sort_key_now_goes_native()
    {
        var logs = new List<string>();
        var collection = Seed(nameof(Narrowing_cast_sort_key_now_goes_native));

        // TryTranslateField declines the cast (UnwrapOrderPreserving leaves it), then TryTranslateComputedSortKey
        // succeeds via TryTranslateValue, rendering an explicit MongoConvertExpression ($toInt).
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, logs);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .OrderBy(x => (int)x.D).ThenBy(x => x.Label).Select(x => x.Label).ToList();

        Assert.Equal(IntDOrder, nativeOnlyLabels);

        // Stage-shape pin only (MQL shape can't prove routing; NativeOnly success does): $set → $sort → $unset with
        // an explicit $toInt body.
        var mql = Mql(logs);
        Assert.Contains("$set", mql);
        Assert.Contains("\"$toInt\"", mql);
        Assert.Contains("\"$D\"", mql);
    }

    // ── 6. Field-to-field comparison with a cast goes native ─────────────────────────────────────

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

        // Widening target (double): matches the driver's own rendering of this shape.
        AssertCastComparisonGoesNative(collection, x => (double)x.I > x.D, ["f2"]);

        // Narrowing target (int): a genuine value-changing cast, rendered explicitly.
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

        // MQL has no $toShort/$toUInt/$toFloat (MongoConvertExpression.ToOperatorFor returns null), so
        // TranslateOperand declines. The driver has the same boundary, so the fallback fails loudly too.
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

        // The driver refuses these too, so the default-mode fallback fails loudly, never a different row set.
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
        // Field-to-field (Weight vs Rank), not member-vs-constant: `(int)p.Weight > 5` would be declined earlier by
        // HasNumericConvert and prove nothing. This shape builds a MongoConvertExpression, which
        // IsQueryDialectRenderable must decline because $expr is a server error inside $elemMatch.
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

        // Default Native falls back and returns correct results.
        using var native = CreateQuantContext(collection, MongoQueryMode.Native);
        var titles = native.Entities.AsNoTracking()
            .Where(b => b.Posts.Any(p => (int)p.Weight > p.Rank)).Select(b => b.Title).ToList();
        Assert.Equal(["match"], titles);
    }

    // ── 9. Cast inside an owned SelectMany inner filter — the MongoFieldPrefixRewriter pin ─────────
    //
    // The only test reaching MongoFieldPrefixRewriter.Rewrite with a MongoConvertExpression: an owned SelectMany's
    // field-to-field inner filter goes through TryBuildOwnedInnerFilter, which rewrites it with the "Posts"
    // prefix. Without that case Rewrite throws at translate time, outside the lowering fallback's catch.

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

        // A second server-side Select after the SelectMany is a different shape; Heading is extracted client-side.
        using var nativeOnly = CreateQuantContext(collection, MongoQueryMode.NativeOnly);
        var results = nativeOnly.Entities.AsNoTracking()
            .SelectMany(b => b.Posts.Where(p => (int)p.Weight > p.Rank), (b, p) => new { p.Heading })
            .ToList();

        Assert.Equal(["keep"], results.Select(x => x.Heading).ToArray());
    }

    // ── 10. Incidental widening: a cast inside a FILTERED Count(pred) element predicate ────────────
    //
    // TranslateOperand's count branch builds a MongoFilteredSizeExpression from the same element-scoped
    // TryTranslate, so a field-to-field cast in the element predicate becomes a MongoConvertExpression and goes
    // native in both predicate and projection position.

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

        // Projection spelling — asserts values for every row.
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
    // TranslateComparison's query-native branch absorbs a widening numeric layer (the field ref is the stored
    // field). The comparison then happens in the cast's type, so the constant must be serialized in that type:
    // serializing with the property's serializer would truncate 2.5 to 2, emit {"I": {"$gte": 2}} and silently
    // return an extra row. The truncated set is a superset of the correct one, so assert NotEqual explicitly.
    //
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

        // Reversed operand order: the mirrored branch of TranslateComparison has its own HasNumericConvert and
        // TranslateValue call sites, and nothing else puts the member on the right. `2.5 <= x` ≡ `x >= 2.5`.
        AssertFractionalConstantRows(collection, x => 2.5 <= (double)x.I);
        AssertFractionalConstantRows(collection, x => 2.5m <= (decimal)x.I);
    }

    private void AssertFractionalConstantRows(IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        // Routing proof: NativeOnly succeeds.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(FractionalCorrect, nativeOnlyLabels);
        Assert.NotEqual(FractionalTruncated, nativeOnlyLabels);

        // Default Native is where wrong data would be silent.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(FractionalCorrect, nativeLabels);
        Assert.NotEqual(FractionalTruncated, nativeLabels);

        // Oracle: in-memory LINQ over the same Expression object.
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

        // Stage-shape pin (case 9 is the routing proof): the constant is 2.5, not the property-coerced 2, and the
        // MQL is byte-identical to driver-LINQ's.
        var nativeLogs = new List<string>();
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, nativeLogs);
        _ = nativeOnly.Entities.AsNoTracking().Where(x => (double)x.I >= 2.5).ToList();
        var nativeMql = MqlPipeline(nativeLogs);

        // The whole $match document. No negative "truncated form" assertion: `{ "$gte" : 2 }` can't be matched
        // robustly without also matching 2.5, and this positive assertion is sufficient.
        Assert.Contains("{ \"I\" : { \"$gte\" : 2.5 } }", nativeMql);

        var driverLogs = new List<string>();
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq, driverLogs);
        _ = driverLinq.Entities.AsNoTracking().Where(x => (double)x.I >= 2.5).ToList();

        // Byte-identical to driver-LINQ (MqlPipeline strips the log timestamp).
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
    // Why the rule uses NativeGroupByBinder.HasDefaultKeySerialization rather than "the property's CLR type is not
    // an enum". `Coded` is an int (not an enum) value-converted to a string (not default-serialized). The shipped
    // rule keeps the property serializer and renders "2"; the "not an enum" rule would render 2, which MongoDB
    // type-brackets against a string field and so returns no rows.

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

        // Seed as BsonDocument: the driver's POCO serializer ignores EF's converter and would store an Int32.
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

        // Discriminator: the constant renders as the string "2" (property serializer), not the number 2.
        var mql = MqlPipeline(logs);
        Assert.Contains("\"$gte\" : \"2\"", mql);

        // Rows follow: a comparison over the stored strings, not the empty set a number would give. Single-digit
        // seed so string and numeric order coincide; this case is about which serializer renders the constant.
        Assert.Equal(["q", "r"], nativeLabels);
        Assert.NotEmpty(nativeLabels);

        // Control: the un-cast comparison emits the identical constant; absorbing a widening cast over a
        // non-default-serialized property must not change it.
        var uncastLogs = new List<string>();
        using var uncast = CreateConvContext(collection, MongoQueryMode.NativeOnly, uncastLogs);
        var uncastLabels = uncast.Entities.AsNoTracking()
            .Where(x => x.Coded >= 2).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Contains("\"$gte\" : \"2\"", MqlPipeline(uncastLogs));
        Assert.Equal(nativeLabels, uncastLabels);

        // No driver-LINQ oracle: the driver fails building its numeric-conversion serializer over a
        // ValueConverterSerializer ("does not implement IHasRepresentationSerializer"), so DriverLinq throws where
        // native answers. Asserted as "threw", not by type: unsupported-shape exception types aren't contract and
        // CI overrides DRIVER_VERSION.
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
    // Case 12's re-encoding converter fails with zero rows. A value-transforming converter (`v => v * 2`) keeps a
    // numeric stored form, so the wrong rule emits a well-typed number that silently selects the wrong rows.

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

        // Stored values are the converted form (model p=1, q=2, r=3). Seeded as BsonDocument, as in case 12.
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

        // The constant goes through the converter (model 2 -> stored 4); the wrong rule would emit 2.
        Assert.Contains("\"$gt\" : 4", MqlPipeline(logs));

        // Only r (model 3) exceeds 2; the wrong rule would also return q.
        Assert.Equal(["r"], nativeLabels);
        Assert.NotEqual(["q", "r"], nativeLabels);

        // Oracle: materialize (applying the converter) and filter in memory.
        using var oracle = CreateScaledContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .Where(x => (long)x.Scaled > 2L).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["r"], inMemoryLabels);

        // No driver-LINQ oracle, as in case 12.
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

    // ── 14. The accepted divergence: (long)->(double) is admitted but not value-preserving ──────────
    //
    // (long, double) is in WideningNumericConversions, so the cast is absorbed; above 2^53 the server compares the
    // raw long while C# compares the rounded double. Native == DriverLinq is what matters (accepted divergence:
    // both differ from the CLR).

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

        // Premise: the CLR rounds this long onto `rounded`.
        Assert.Equal(rounded, (double)justAbove);

        using var nativeOnly = CreateLongContext(collection, MongoQueryMode.NativeOnly);
        var nativeLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => (double)x.L == rounded).OrderBy(x => x.Label).Select(x => x.Label).ToList();

        // Native agrees with the driver, which absorbs the cast identically.
        using var driverLinq = CreateLongContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqLabels = driverLinq.Entities.AsNoTracking()
            .Where(x => (double)x.L == rounded).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLinqLabels, nativeLabels);

        // The CLR differs because it rounds the operand before comparing.
        using var oracle = CreateLongContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList()
            .Where(x => (double)x.L == rounded).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["big"], inMemoryLabels);
        Assert.NotEqual(inMemoryLabels, nativeLabels);

        // Control: below 2^53 every leg agrees.
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

    // ── 15. Identity-like arm: enum ↔ underlying — the constant keeps the property serializer ─────
    //
    // EF emits `(int)e.Status == (int)Status.Active`. HasNumericConvert treats this as identity-like: the field ref
    // is the stored field, but the constant must go through the property's serializer (ValueConverterSerializer
    // for HasConversion<string>()) to render "Active". Treating it as widening would render 0, which MongoDB
    // type-brackets against the string field, silently matching nothing. Asserts values in all modes.

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

        // Seeded as BsonDocument (as cases 12/13) so the enum is stored as a string.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Status", "Active" } },
            new BsonDocument { { "Label", "q" }, { "Status", "Closed" } },
            new BsonDocument { { "Label", "r" }, { "Status", "Active" } },
            new BsonDocument { { "Label", "s" }, { "Status", "Suspended" } }
        ]);

        var collection = database.MongoDatabase.GetCollection<EnumRow>(name);

        // Routing proof: NativeOnly succeeds (see NativeGateRoutingTests.A_enum_as_string_where_equals_routing).
        var logs = new List<string>();
        using var nativeOnly = CreateEnumContext(collection, MongoQueryMode.NativeOnly, logs);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(x => x.Status == Status.Active).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(["p", "r"], nativeOnlyLabels);

        // Discriminator: the constant renders as the string "Active", not the underlying int.
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

        // Oracle: in-memory LINQ over the same rows.
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

    // ── 16. Identity-like arm: char -> int, and boxing to object ──────────────────────────────────
    //
    // No value converter, so the serializer choice isn't observable; these pin that the comparison is admitted at
    // all. Neither `char -> int` nor `T -> object` is in WideningNumericConversions.

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

        // char -> int: (int)'A' == 65, a value conversion, so the CLR oracle applies.
        AssertCharBoxComparisonGoesNative(collection, x => (int)x.Grade == 65, ["a"]);

        // Boxing to object: no CLR oracle (see AssertBoxingComparisonGoesNative).
        AssertBoxingComparisonGoesNative(collection, x => (object)x.I == (object)2, ["b"]);
    }

    // Boxed `==` is reference equality in C#, so in-memory LINQ is false for every row. The translator (like the
    // driver) treats Convert+Equal as value comparison, so native == driver-LINQ and both differ from the CLR — an
    // accepted divergence like case 14. Only routing and Native == DriverLinq are asserted.
    private void AssertBoxingComparisonGoesNative(
        IMongoCollection<CharBoxRow> collection, Expression<Func<CharBoxRow, bool>> predicate, string[] expectedLabels)
    {
        // Premise: the CLR really answers differently.
        var clrLabels = CharBoxRows.Select(r => new CharBoxRow { Label = r.Label, Grade = r.Grade, I = r.I })
            .AsQueryable().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Empty(clrLabels);

        // Routing proof: NativeOnly succeeds rather than throwing.
        using var nativeOnly = CreateCharBoxContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeOnlyLabels);

        // Native agrees with the driver's value-equality reading.
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

        // Native == DriverLinq == CLR, over the same Expression object for the in-memory leg.
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

    // ── 17. Identity-like arm: a sub-int-backed enum's promoted comparison goes native ──────────────
    //
    // C# promotes a short/byte/ushort/sbyte-backed enum comparison to Int32, so the member-side Convert targets
    // Int32, not the enum's Int16 — a widening, which Int32-backed enums (case 15) never expose. Pins routing
    // (exercised by BuiltInDataTypesMongoTest too).

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

        // Routing proof: NativeOnly succeeds with the promoted Convert(m, Int32) target.
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

    // ── 18. Identity-like arm: enum -> floating must never crash ────────────────────────────────
    //
    // IsWideningNumericConvert(Int32, Double) is true, so without restriction (double)x.Level >= n would be admitted
    // as identity-like and the constant would keep the enum property's serializer; BsonValueSerializer.Coerce's
    // Enum.ToObject then throws ArgumentException for 1.5, uncaught under default Native. The identity-like arm
    // therefore only admits an integral widening target (IsIntegerType).

    private enum RankTier { Low, Medium, High }

    private class RankRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public RankTier Level { get; set; }
    }

    // With that guard, HasNumericConvert declines the query-native branch and the comparison falls through to
    // $expr, going native as {$expr: {$gte: [{$toDouble: "$Level"}, 1.5]}} with the same rows as in-memory LINQ and
    // driver-LINQ. If the guard regressed, the query-native branch would crash; the Native leg below catches that.
    //
    // Safe because RankTier is default-serialized; an enum with a non-default BsonRepresentation or a converter is
    // held back from the fall-through (see case 29).

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

        // A whole-number constant crashed the same way, so both are kept.
        AssertEnumToFloatingIsNativeAndCorrect(collection, x => (double)x.Level == 2.0, ["c"]);
    }

    private void AssertEnumToFloatingIsNativeAndCorrect(
        IMongoCollection<RankRow> collection, Expression<Func<RankRow, bool>> predicate, string[] expectedLabels)
    {
        // Default Native: correct rows, never an uncaught ArgumentException.
        using var native = CreateRankContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking()
            .Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(expectedLabels, nativeLabels);

        // NativeOnly succeeds: the $expr fall-through, not the fallback, produced the rows.
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

    // ── 19. Flag precedence at the rows level: boxing must not mask widening's truncation guard ──────
    //
    // MongoExpressionTranslatorTests.Boxing_over_a_widening_cast_lets_the_widening_arm_win_precedence pins the node
    // shape; this pins the rendered value. If the boxing (identity-like) layer won, the constant would keep I's int
    // serializer and BsonValueSerializer.Coerce(int, 2.5) would truncate it to 2 at render time (invisible at the
    // translation layer), matching row "b". The correct comparison matches nothing.

    [Fact]
    public void Boxing_over_a_widening_cast_precedence_returns_the_untruncated_row()
    {
        var collection = Seed(nameof(Boxing_over_a_widening_cast_precedence_returns_the_untruncated_row));

        AssertBoxingOverWideningPrecedenceReturnsNoRows(collection, x => (object)(double)x.I == (object)2.5);

        // Mirrored branch (member on the right) has its own HasNumericConvert / ConstantSerializationContext site.
        AssertBoxingOverWideningPrecedenceReturnsNoRows(collection, x => (object)2.5 == (object)(double)x.I);
    }

    private void AssertBoxingOverWideningPrecedenceReturnsNoRows(
        IMongoCollection<Row> collection, Expression<Func<Row, bool>> predicate)
    {
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyLabels = nativeOnly.Entities.AsNoTracking().Where(predicate).Select(x => x.Label).ToList();

        // If boxing won, the constant would render as 2 and match "b"; widening wins, so it stays 2.5.
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

    // ── 20. Numeric-cast projection leaf — wrapped spelling goes native ──────────────────────────
    //
    // NativeProjectionBinder.TryTranslateLeaf admits a Convert leaf that translates to MongoConvertExpression (it
    // renders as a document, so $project can't misread it as an inclusion/exclusion flag).
    // MongoProjectionBindingExpressionVisitor.Visit registers the whole Convert node so
    // MongoProjectionBindingRemovingExpressionVisitor reads the value raw by alias, not through the pre-cast
    // member's serializer.
    //
    // (int)D: a=1.6->1, b=1.4->1, c=2.5->2, d=-1.5->-1, e=0.5->0.

    [Fact]
    public void Wrapped_cast_projection_leaf_goes_native()
    {
        var collection = Seed(nameof(Wrapped_cast_projection_leaf_goes_native));

        // Routing proof: NativeOnly succeeds and returns the correct (int)D values.
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
    // `prefix` is a captured local (natively representable), so Native and DriverLinq must agree on the read.
    // Includes a nullable cast-target leg: a non-nullable leaf fails loudly on an alias miss, a nullable one
    // silently.

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

        // Nullable leg: an alias miss is silent for a nullable leaf (BsonBinding returns null), so only this leg
        // can detect it.
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

    // ── 22. Numeric-cast projection leaf — bare spelling goes native ──────────────────────────────
    //
    // A MongoConvertExpression has no document element, so TryDeriveDocumentPathAlias can't alias it;
    // TryDeriveSyntheticAlias gives computed leaves that render as an operator document the reserved `_v` alias
    // (Synthetic tier). Legs are collected and asserted together so a regression reports every mode, including the
    // DriverLinq escape hatch (see NativeComputedBareProjectionTests.LegOutcome).

    [Fact]
    public void Bare_cast_projection_leaf_goes_native_and_returns_correct_values()
    {
        var collection = Seed(nameof(Bare_cast_projection_leaf_goes_native_and_returns_correct_values));

        // NativeOnly success is the routing proof; all modes must agree. Populating Projection flips
        // ProjectionAnalyzer.CanPushDown, so under DriverLinq the driver renders this Select itself under its own
        // `_v` alias, which is why that alias is reserved: one shaper reads either pipeline.
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
    /// Runs <paramref name="query"/> and describes the outcome as a short string, so callers can collect every leg
    /// and assert them together.
    /// </summary>
    /// <remarks>
    /// Local copy of <c>NativeComputedBareProjectionTests.LegOutcome</c> (no shared base); keep them behaviourally
    /// identical.
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
    // The leaf is admitted and the shaper reads the `_v` alias, so any late (render-time) decline must leave the
    // driver's own push-down in place: the late-fallback strip is tier-conditional and skips Synthetic overrides.
    // An alias mismatch is silent, so this asserts values.

    [Fact]
    public void Bare_cast_projection_leaf_behind_a_parameterized_predicate_returns_correct_values()
    {
        var collection = Seed(nameof(Bare_cast_projection_leaf_behind_a_parameterized_predicate_returns_correct_values));
        var prefix = "a"; // a captured local, not a constant — a genuine query parameter

        // Goes fully native: a parameterized StartsWith is natively representable (placeholder sentinel).
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

    // ── 24. The node-kind gate now admits a bare constant/parameter leaf too ────────────────────────
    //
    // TryTranslateLeaf's cast-leaf branch gates on the resulting node kind, so a captured local reaches it as a
    // MongoParameterExpression. A bare falsy value (0) would make $project read it as an exclusion flag and abort
    // the aggregate; MongoPipelineFactory.RenderProject $literal-wraps bare constant/parameter values (as
    // RenderAddFields does for $set), so both node kinds are admitted. See
    // NativeOwnedCollectionCountTests.Constant_projection_leaf_is_safely_admitted_via_the_project_literal_wrap.

    [Fact]
    public void Constant_leaf_now_goes_native_via_the_project_literal_wrap()
    {
        var collection = Seed(nameof(Constant_leaf_now_goes_native_via_the_project_literal_wrap));
        var captured = 0;

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().OrderBy(x => x.Label)
            .Select(x => new { x.Label, X = captured }).ToList();
        Assert.All(nativeOnlyResult, r => Assert.Equal(0, r.X));
        Assert.Equal(Rows.Select(r => r.Label).ToList(), nativeOnlyResult.Select(r => r.Label).ToList());

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

    // ── 25. A cast over a value-converted property declines ──────────────────────────────────────
    //
    // TryTranslateValue's AllFieldsDefaultSerialized recurses through the MongoConvertExpression and
    // HasDefaultKeySerialization rejects the converter, so $toInt over the raw stored value is never emitted. Asserts
    // the outcome value (as NativeNullableMemberTests.Value_converted_nullable_Value_projection_leaf_declines_instead_of_reading_the_raw_stored_value
    // does), so a regression shows the wrong value it returns.

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

        // Model Weight = 3.5, stored 7.0: (int)3.5 = 3, while a raw-stored read would give 7.
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "Label", "p" }, { "Weight", 7.0 } }
        ]);
        var collection = database.MongoDatabase.GetCollection<ConvertedWeightRow>(name);

        // NativeOnly must decline. If the guard were bypassed it would succeed with the wrong value (7), and the
        // assertion would print it.
        using var nativeOnly = CreateConvertedWeightContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking()
                .Select(x => new { x.Label, X = (int)x.Weight }).AsEnumerable()
                .Select(r => (r.Label, r.X)).ToList(),
            r => $"{r.Label}={r.X}");
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        // No driver-LINQ oracle: the ValueConverterSerializer limitation from cases 12/13 fires here too, under
        // Native's fallback and DriverLinq. Asserted as "threw", not by type.
        using var native = CreateConvertedWeightContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Select(x => new { x.Label, X = (int)x.Weight }).ToList()));

        using var driverLinq = CreateConvertedWeightContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Select(x => new { x.Label, X = (int)x.Weight }).ToList()));
    }

    // The row projector is explicit so each caller controls the exact wording its assertion quotes.
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

    // ── 26. A widening cast projection leaf goes native ────────────────────────────────────────────
    //
    // TranslateOperand unwraps a widening conversion entirely, leaving a bare MongoFieldExpression that is
    // indistinguishable by node kind from an uncast leaf, so the gate re-derives "was this a cast" from the original
    // leafExpression.

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

    // ── 26a. A widening cast as a bare (non-`new{}`) projection leaf goes native too ────────────────
    //
    // Same "was this a cast" re-derivation from the original leafExpression, for the bare-projection gate.

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

    // ── 27. A narrowing cast vs. a constant returns the CLR answer and deliberately diverges from ────
    //        driver-LINQ. Do not "correct" this toward the driver.
    //
    // The query-native branch can't absorb the cast, so the comparison falls through to $expr:
    // {$expr: {$gt: [{$toInt: "$D"}, 0]}}. Driver-LINQ drops the cast and answers `x.D > 0`, also returning e
    // (D = 0.5, (int)0.5 == 0). Native returns what C# returns, by design.
    //
    // This is the opposite of the accepted-divergence family (case 14, NativeOwnedCollectionFilteredCountTests),
    // where native and driver-LINQ agree and the CLR is the odd one out. Here driver-LINQ is the odd one out, so
    // restoring parity would be a regression. Consequences: this is a result change under default Native, and
    // UseQueryMode(MongoQueryMode.DriverLinq) restores the driver's (CLR-wrong) answer, not the same one.

    [Fact]
    public void Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq()
    {
        var collection = Seed(nameof(Narrowing_cast_vs_constant_returns_the_CLR_answer_and_diverges_from_driver_linq));

        // The CLR answer: (int)D > 0 is true for a (1.6 -> 1), b (1.4 -> 1) and c (2.5 -> 2); false for
        // d (-1.5 -> -1) and e (0.5 -> 0).
        string[] clrLabels = ["a", "b", "c"];
        // The driver's answer, with the cast dropped: D > 0 additionally admits e (0.5).
        string[] driverLabels = ["a", "b", "c", "e"];

        // Routing proof: NativeOnly succeeding means the fall-through reached $expr.
        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            clrLabels,
            nativeOnly.Entities.AsNoTracking().Where(x => (int)x.D > 0)
                .OrderBy(x => x.Label).Select(x => x.Label).ToList());

        // Leg 1 — default Native returns the CLR rows.
        using var native = CreateContext(collection, MongoQueryMode.Native);
        var nativeLabels = native.Entities.AsNoTracking().Where(x => (int)x.D > 0)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(clrLabels, nativeLabels);

        // Leg 2 — in-memory LINQ over the same expression agrees.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var inMemoryLabels = oracle.Entities.AsNoTracking().ToList().Where(x => (int)x.D > 0)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(clrLabels, inMemoryLabels);

        // Leg 3 — DriverLinq's own rows, asserted positively (a bare NotEqual would accept any third answer) and
        // with NotEqual to state the divergence.
        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLabels_ = driverLinq.Entities.AsNoTracking().Where(x => (int)x.D > 0)
            .OrderBy(x => x.Label).Select(x => x.Label).ToList();
        Assert.Equal(driverLabels, driverLabels_);
        Assert.NotEqual(nativeLabels, driverLabels_);
    }

    // ── 28. The MIRRORED operand order reaches the SAME fall-through ────────────────────────────────
    //
    // `0 < (int)x.D`: the member-right classification site. The $expr path doesn't mirror the operator (operand
    // order matters there), so this is a different code path from case 27. Same deliberate divergence.

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

        // States the divergence itself, as in case 27.
        Assert.NotEqual(nativeLabels, driverLinqLabels);
    }

    // ── 29. A narrowing cast over a value-converted property must not fall through to $expr ─────────
    //
    // Pins the disposition, not an individual guard: the effective guard is TranslateOperand's convert branch
    // (case 30 nets it alone); MongoExpressionTranslator.CanFallThroughToExpr is redundant with it. Without any
    // guard, {$expr: {$gt: [{$toInt: "$Weight"}, 3]}} reads the raw stored 7.0 instead of the model 3.5 and
    // silently returns a row where zero is correct, under default Native too. Uses an outcome string so a
    // regression prints the wrong rows.

    [Fact]
    public void Narrowing_cast_comparison_over_a_value_converted_property_still_declines()
    {
        var name = UniqueCollectionName(
            nameof(Narrowing_cast_comparison_over_a_value_converted_property_still_declines));
        // Model Weight = 3.5 (stored 7.0): (int)3.5 > 3 is false, so zero rows is correct; a raw read gives one.
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

        // No working driver-LINQ oracle (the ValueConverterSerializer limitation of cases 12/13/25), so both the
        // fallback and DriverLinq throw. Asserted as "threw"; the NativeOnly assertion is the real check.
        using var native = CreateConvertedWeightContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > 3).Select(x => x.Label).ToList()));

        using var driverLinq = CreateConvertedWeightContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > 3).Select(x => x.Label).ToList()));
    }

    // ── 30. The same hazard on the field-to-field shape ────────────────────────────────────────────
    //
    // `Where(x => (int)x.Weight > x.Other)`, model Weight 3.5 (stored 7.0) vs Other 5: correct answer is zero rows.
    // Without the guard on TranslateOperand's convert branch, $toInt reads the stored 7.0 and silently returns p
    // under NativeOnly and default Native.

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

        // Outcome string, as in case 29.
        using var nativeOnly = CreatePairContext(collection, MongoQueryMode.NativeOnly);
        var outcome = DescribeOutcome(
            () => nativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight > x.Other).Select(x => x.Label).ToList(),
            label => label);
        Assert.Equal("threw NativeTranslationNotSupportedException", outcome);

        // No working driver-LINQ oracle (as cases 12/13/25/29); both routes throw.
        using var native = CreatePairContext(collection, MongoQueryMode.Native);
        Assert.NotNull(Record.Exception(() => native.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > x.Other).Select(x => x.Label).ToList()));

        using var driverLinq = CreatePairContext(collection, MongoQueryMode.DriverLinq);
        Assert.NotNull(Record.Exception(() => driverLinq.Entities.AsNoTracking()
            .Where(x => (int)x.Weight > x.Other).Select(x => x.Label).ToList()));
    }

    // Control for case 30: the same shape over a default-serialized field goes native with the CLR answer, so
    // the guard can't be satisfied by declining every cast operand.
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

    // ── 34. A relational cast comparison over a nullable property must stay type-bracketed ──────────
    //
    // A fall-through to $expr loses the query dialect's type bracketing: {Price: {$lt: 100}} matches neither null
    // nor missing, but $toInt/$toDouble map both to null, which BSON order puts below every number:
    //
    //   {Price: {$lt: 100}}                        -> [p1_50]                        <- query dialect (bracketed)
    //   {$expr: {$lt: [{$toInt: "$Price"}, 100]}}  -> [p1_50, p3_null, p4_missing]    <- bare $expr (NOT bracketed)
    //
    // MongoExpressionTranslator.NeedsNumericTypeBracket conjoins a MongoNumericTypeBracketExpression:
    //
    //   {$and: [{Price: {$type: "number"}}, {$expr: {$lt: [{$toInt: "$Price"}, 100]}}]}  -> [p1_50]
    //
    // {$type: "number"} rather than {$ne: null}, because the query dialect also brackets away other foreign BSON
    // types; this keeps it an exact equivalent (see MongoExpressionNegator's "exact complement or decline" rule).
    // All four relational operators get the bracket; $gt/$gte only happen to be safe without it.
    // NorthwindWhereQueryMongoTest.Decimal_cast_to_double_works is this shape.
    //
    // Controls: equality over the same nullable property needs no bracket ($eq/$ne partition every BSON value), and
    // a relational comparison over a non-nullable property is case 27's shape, left unbracketed (see 34b).

    private class NullablePriceRow
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public double? Price { get; set; }
        public decimal? Amount { get; set; }
        public double Weight { get; set; }
    }

    [Fact]
    public void Relational_cast_comparison_over_a_nullable_property_goes_native_with_a_numeric_type_bracket()
    {
        var collection = SeedNullablePrices(
            nameof(Relational_cast_comparison_over_a_nullable_property_goes_native_with_a_numeric_type_bracket));

        // All four operators go native with the same type-bracketed rows in every mode. Expected sets are asserted
        // per operator, since parity alone would pass if all paths returned the ragged rows.
        AssertRelationalCastGoesNative(collection, x => (int?)x.Price < 100, ["p1_50"]);
        AssertRelationalCastGoesNative(collection, x => (int?)x.Price <= 100, ["p1_50"]);
        AssertRelationalCastGoesNative(collection, x => (int?)x.Price > 100, ["p2_150"]);
        AssertRelationalCastGoesNative(collection, x => (int?)x.Price >= 100, ["p2_150"]);

        // The mirrored branch (member on the right) has its own NeedsNumericTypeBracket call site.
        AssertRelationalCastGoesNative(collection, x => 100 > (int?)x.Price, ["p1_50"]);

        // decimal? -> double?, the Northwind Decimal_cast_to_double_works shape.
        AssertRelationalCastGoesNative(collection, x => (double?)x.Amount < 100, ["p1_50"]);
        AssertRelationalCastGoesNative(collection, x => (double?)x.Amount > 100, ["p2_150"]);

        // Control 1 — equality over the same nullable property goes native without a bracket.
        using (var eqNativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal(
                ["p1_50"],
                eqNativeOnly.Entities.AsNoTracking().Where(x => (int?)x.Price == 50)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        }

        // Control 2 — relational over a non-nullable property goes native without a bracket (case 27's shape).
        // Weight: p1 = 1.6, p2 = 0.5, p3 = 2.5, p4 = missing -> (int) 1, 0, 2, null.
        using (var relNativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Equal(
                ["p1_50", "p3_null"],
                relNativeOnly.Entities.AsNoTracking().Where(x => (int)x.Weight > 0)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        }
    }

    // ── 34b. Known residual, pinned as measured (not as correct) ─────────────────────────────────────
    //
    // A missing element on a non-nullable property also escapes type bracketing: p4_missing has no Weight, $toInt
    // yields null, and null < 2 is true:
    //
    //   Native / NativeOnly : {$expr: {$lt: [{$toInt: "$Weight"}, 2]}} -> p1_50, p2_150, p4_missing
    //   DriverLinq          : {Weight: {$lt: 2}}                       -> p1_50, p2_150
    //
    // Not closed: bracketing every relational cast would revoke case 27's CLR-correct fall-through, and the
    // document violates the model (the read path rejects a missing required element), so there is no CLR oracle.

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

        // Premise: no CLR oracle, because materializing p4_missing throws.
        using var oracle = CreateNullablePriceContext(collection, MongoQueryMode.Native);
        Assert.Throws<InvalidOperationException>(() => oracle.Entities.AsNoTracking().ToList());
    }

    private void AssertRelationalCastGoesNative(
        IMongoCollection<NullablePriceRow> collection,
        Expression<Func<NullablePriceRow, bool>> predicate,
        string[] expectedLabels)
    {
        // Legs project labels, not entities: materializing p4_missing (no required Weight) throws and would hide the
        // rows (see case 34b).
        using var nativeOnly = CreateNullablePriceContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(
            expectedLabels,
            nativeOnly.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList());

        using var native = CreateNullablePriceContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            expectedLabels,
            native.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList());

        using var driverLinq = CreateNullablePriceContext(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(
            expectedLabels,
            driverLinq.Entities.AsNoTracking().Where(predicate).OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    // Three states for the nullable properties: a value (twice), an explicit BSON null, and a missing element.
    // Weight (non-nullable) is also missing on the fourth row, for case 34b. The seed self-checks the stored shape,
    // since missing and null are indistinguishable from results.
    private IMongoCollection<NullablePriceRow> SeedNullablePrices(string name)
    {
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        var typed = database.MongoDatabase.GetCollection<NullablePriceRow>(raw.CollectionNamespace.CollectionName);

        // Well-formed rows go in typed so decimal? gets the driver's own representation; a missing element needs
        // the raw writer.
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

    // ── 35. An owned-collection .Count compared against a non-integral threshold ───────────────────
    //
    // Convert(count, Double) becomes a MongoConvertExpression over a MongoSizeExpression (which
    // AllFieldsDefaultSerialized admits, having no IProperty), so this goes native via $expr. The MQL leg pins that
    // it doesn't take the query-dialect array-index form, which can only express an integral threshold.

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
        // Not the array-index tier: {"Posts.2": {$exists: true}} answers Count > 2.
        Assert.DoesNotContain("Posts.2", mql);

        // In-memory LINQ and DriverLinq agree, so this is value-preserving.
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

    // ── 36. $toInt truncates toward zero ──────────────────────────────────────────────────────────
    //
    // Each assertion leaves exactly one rounding rule standing:
    //
    //   d: D = -1.5  ->  truncate-toward-zero -1 | floor -2 | round-half-even -2 | round-half-away -2
    //   a: D =  1.6  ->  truncate 1             | floor 1  | round 2
    //   c: D =  2.5  ->  truncate 2             | floor 2  | round-half-even 2 | round-half-away 3
    //
    // (1) Threshold -1.5 lies between -1 and -2, so d is present only under truncation.
    // (2) (int)D == 2 selects only c under truncation; round-half-even would also select a.
    //
    // The oracle is in-memory LINQ, not driver-LINQ, which drops the cast (case 27).

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

        // The CLR oracle, over the same expressions and rows.
        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        var materialized = oracle.Entities.AsNoTracking().ToList();
        Assert.Equal(
            ["a", "b", "c", "d", "e"],
            materialized.Where(x => (int)x.D > -1.5).OrderBy(x => x.Label).Select(x => x.Label).ToList());
        Assert.Equal(
            ["c"],
            materialized.Where(x => (int)x.D == 2).OrderBy(x => x.Label).Select(x => x.Label).ToList());
    }

    // ── 37. An out-of-range value aborts the whole query, not just its own row ────────────────────
    //
    // $expr is evaluated for every scanned document, so one unconvertible value aborts the aggregate, even for a
    // predicate that matches nothing. Deliberately no $convert onError: a null operand would then take part in a
    // BSON-total-order comparison and silently move rows in or out depending on the operator (see case 34). A loud
    // abort can't be mistaken for an answer. Previously released versions (driver-LINQ, cast dropped) returned
    // rows; recorded in BREAKING-CHANGES.md.

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

            // Blast radius: this predicate matches no document, so a per-row failure would give an empty result.
            // It aborts anyway.
            Assert.Throws<MongoCommandException>(
                () => db.Entities.AsNoTracking().Where(x => (int)x.D < 0)
                    .OrderBy(x => x.Label).Select(x => x.Label).ToList());
        }

        // DriverLinq (the escape hatch) drops the cast, so both queries answer.
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

    // ── 38. A plain field-to-field comparison (no cast) over a value-converted operand must decline ──
    //
    // The cast-free counterpart of case 30: `x.Weight > x.Other` goes to TranslateComparison's general $expr path,
    // which must check default serialization on both operands. Otherwise native reads the raw stored value(s) and
    // disagrees with the model in all four {transforming, re-encoding} x {one side, both sides} combinations.
    // Driver-LINQ throws for this shape ("the two arguments are serialized differently"), so declining lands on
    // the same throw.

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
        // Model 3 > 4 is false; stored 6 > 4 would wrongly match.
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

        // No driver-LINQ oracle: both routes throw.
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
        // Model 3 > 2 is true; stored 6 > 6 would wrongly exclude the row.
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
        // Model 9 > 10 is false; stored "9" (string) sorts above every number, so a raw comparison matches.
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
        // Model 9 > 10 is false; stored "9" > "10" lexicographically, so a raw comparison matches.
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

    // Case 38's control (default-serialized fields go native) is
    // Field_to_field_cast_over_a_default_serialized_property_still_goes_native.

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

    // Strips the log line's leading timestamp so emissions from two contexts are comparable.
    private static string MqlPipeline(List<string> logs)
    {
        var line = Mql(logs);
        return line[line.IndexOf("Executed MQL query", StringComparison.Ordinal)..];
    }
}
