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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-329 end-to-end coverage of field-to-field and arithmetic-operand comparisons, which the native
/// translator now accepts and routes through <c>{ $expr: … }</c> (<see cref="MongoExpressionTranslator"/> /
/// <see cref="MongoAggregationExpressionRenderer"/>). Each shape is proven native via
/// <see cref="MongoQueryMode.NativeOnly"/> (succeeds ⇒ went native; a fallback shape would throw
/// <c>NativeTranslationNotSupportedException</c>), and asserted for MQL shape and result-set parity between
/// native and driver-LINQ execution — see task-7-report.md for the empirically-captured driver MQL this
/// mirrors.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeExprComparisonTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // Public (not private): a [Theory]/[MemberData] test method below takes an
    // Expression<Func<Customer, bool>> parameter, and xUnit requires public test methods to have
    // at-least-as-accessible parameter types.
    public class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public int Score { get; set; }
    }

    // Alice: Age=7, Score=2  (7/2 truncates to 3 in C#, but $divide returns 3.5; 7%2=1 in both C# and $mod)
    // Bob:   Age=20, Score=20 (field-to-field equality match)
    // Carol: Age=-7, Score=2 (negative dividend: C# -7/2 = -3 truncated, -7%2 = -1; MongoDB is non-truncating)
    private (IMongoCollection<Customer> collection, List<string> logs) SeedCustomers(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 7 }, { "Score", 2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 20 }, { "Score", 20 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", -7 }, { "Score", 2 } },
        ]);
        return (database.MongoDatabase.GetCollection<Customer>(collectionName), []);
    }

    private SingleEntityDbContext<Customer> CreateContext(
        IMongoCollection<Customer> collection, List<string> logs, MongoQueryMode mode)
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

    // ── 1. Field-to-field equality (c.Age == c.Score) ──────────────────────────────────────────────

    [Fact]
    public void NativeOnly_field_to_field_equality_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_field_to_field_equality_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Age == c.Score).ToList();

        Assert.Equal(["Bob"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("\"$eq\" : [\"$Age\", \"$Score\"]", mql);
    }

    [Fact]
    public void Field_to_field_equality_matches_driver_linq_results()
    {
        var (collection, logs) = SeedCustomers(nameof(Field_to_field_equality_matches_driver_linq_results));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeNames = native.Entities.Where(c => c.Age == c.Score).Select(c => c.Name).OrderBy(n => n).ToList();
        var driverNames = driver.Entities.Where(c => c.Age == c.Score).Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
    }

    // ── 2. Arithmetic operands: +, -, * (c.Age OP c.Score > threshold) ────────────────────────────

    [Fact]
    public void NativeOnly_add_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_add_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Age + c.Score > 5).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice", "Bob"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("\"$add\" : [\"$Age\", \"$Score\"]", mql);
    }

    [Fact]
    public void NativeOnly_subtract_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_subtract_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Age - c.Score > 5).ToList();

        Assert.Empty(results); // 7-2=5 (not >5), 20-20=0, -7-2=-9

        var mql = Mql(logs);
        Assert.Contains("\"$subtract\" : [\"$Age\", \"$Score\"]", mql);
    }

    [Fact]
    public void NativeOnly_multiply_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_multiply_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Age * c.Score > 5).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice", "Bob"], results.Select(c => c.Name).ToArray()); // 14, 400, -14

        var mql = Mql(logs);
        Assert.Contains("\"$multiply\" : [\"$Age\", \"$Score\"]", mql);
    }

    [Fact]
    public void Arithmetic_operands_match_driver_linq_results()
    {
        var (collection, logs) = SeedCustomers(nameof(Arithmetic_operands_match_driver_linq_results));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeAdd = native.Entities.Where(c => c.Age + c.Score > 5).Select(c => c.Name).OrderBy(n => n).ToList();
        var driverAdd = driver.Entities.Where(c => c.Age + c.Score > 5).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(driverAdd, nativeAdd);

        var nativeSub = native.Entities.Where(c => c.Age - c.Score > -20).Select(c => c.Name).OrderBy(n => n).ToList();
        var driverSub = driver.Entities.Where(c => c.Age - c.Score > -20).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(driverSub, nativeSub);

        var nativeMul = native.Entities.Where(c => c.Age * c.Score > -100).Select(c => c.Name).OrderBy(n => n).ToList();
        var driverMul = driver.Entities.Where(c => c.Age * c.Score > -100).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(driverMul, nativeMul);
    }

    // ── 3. Integer division/modulo: danger zone for truncation/sign divergence ────────────────────
    //
    // MODULO: the driver's own LINQ translator emits a RAW $mod for int operands — it does NOT emulate C#'s
    // dividend-sign modulo semantics — and so does the native renderer, so the two execute byte-identical
    // $expr documents and necessarily agree, even though both diverge from in-memory C#.
    //
    // DIVISION: no longer so, as of EF-434. MongoDB has no integer-division operator ($divide over two
    // integers always yields a double), which made the shared raw-$divide shape wrong in two distinct ways —
    // an integral projection member failed to DESERIALIZE (FormatException, "Truncation resulted in data
    // loss"), and an integral comparison answered against a fractional quotient. Native now renders an
    // integral-result division as $trunc-of-$divide (MongoBinaryOperator.IntegerDivide) and therefore matches
    // C#, deliberately diverging from driver-LINQ's MQL. Non-integral division is untouched.

    [Fact]
    public void NativeOnly_divide_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_divide_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Alice: 7/2 → 3 truncated (3.5 raw); both are > 1, so the ROWS do not discriminate here — the MQL
        // assertion below does, and Integer_division_operand_truncates_toward_zero_like_csharp_EF434 carries
        // the row-level discriminator.
        var results = db.Entities.Where(c => c.Age / c.Score > 1).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("\"$trunc\" : { \"$divide\" : [\"$Age\", \"$Score\"] }", mql);
    }

    // EF-434. The rows, not the MQL, are the discriminator here: each expected value is one that ONLY C#'s
    // truncate-toward-zero division produces.
    //   Alice   7/2:  C# 3   | raw $divide 3.5   | floor 3    -> "== 3" excludes raw division
    //   Carol  -7/2:  C# -3  | raw $divide -3.5  | floor -4   -> "== -3" excludes raw division AND flooring
    // Run under NativeOnly, so a fallback to driver-LINQ (which emits the raw $divide) cannot silently supply
    // the answer — it would throw instead.
    [Fact]
    public void Integer_division_operand_truncates_toward_zero_like_csharp_EF434()
    {
        var (collection, logs) = SeedCustomers(nameof(Integer_division_operand_truncates_toward_zero_like_csharp_EF434));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var three = db.Entities.Where(c => c.Age / c.Score == 3).ToList();
        Assert.Equal(["Alice"], three.Select(c => c.Name).ToArray());

        var minusThree = db.Entities.Where(c => c.Age / c.Score == -3).ToList();
        Assert.Equal(["Carol"], minusThree.Select(c => c.Name).ToArray());
    }

    // EF-434, the negative half: a NON-integral division must still render a bare $divide. Alice's 7/2 = 3.5
    // is strictly between the truncated 3 and 4, so `> 3.4` includes her only if no truncation happened —
    // rows discriminate, not just MQL. The cast lives inside a projection because a widening cast is rejected
    // on a bare comparison operand (a separate, pre-existing boundary).
    [Fact]
    public void Double_division_is_not_truncated_EF434()
    {
        var (collection, logs) = SeedCustomers(nameof(Double_division_is_not_truncated_EF434));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var rows = db.Entities.Select(c => new { c.Name, Div = (double)c.Age / c.Score })
            .ToList().OrderBy(r => r.Name).ToList();

        Assert.Equal([("Alice", 3.5), ("Bob", 1.0), ("Carol", -3.5)],
            rows.Select(r => (r.Name, r.Div)).ToArray());

        var mql = Mql(logs);
        Assert.Contains("$divide", mql);
        Assert.DoesNotContain("$trunc", mql);
    }

    // EF-434's projection shape (the one the Northwind spec test
    // NorthwindSelectQueryMongoTest.Projection_when_arithmetic_expression_precedence exercises): an integral
    // division read back into an int member. Before the fix the double result made the driver's Int32
    // deserializer throw FormatException / "Truncation resulted in data loss".
    [Fact]
    public void Integer_division_projection_into_an_int_member_truncates_EF434()
    {
        var (collection, logs) = SeedCustomers(nameof(Integer_division_projection_into_an_int_member_truncates_EF434));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var rows = db.Entities.Select(c => new { c.Name, Div = c.Age / c.Score })
            .ToList().OrderBy(r => r.Name).ToList();

        Assert.Equal([("Alice", 3), ("Bob", 1), ("Carol", -3)], rows.Select(r => (r.Name, r.Div)).ToArray());

        var mql = Mql(logs);
        Assert.Contains("\"$trunc\" : { \"$divide\" : [\"$Age\", \"$Score\"] }", mql);
    }

    [Fact]
    public void NativeOnly_modulo_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_modulo_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Age % c.Score == 1).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).ToArray()); // 7 % 2 == 1

        var mql = Mql(logs);
        Assert.Contains("\"$mod\" : [\"$Age\", \"$Score\"]", mql);
    }

    // Result-parity test with driver-LINQ over the negative dividend (Carol, Age=-7, Score=2). Scoped, since
    // EF-434, to the two operations where native and driver-LINQ still agree: DOUBLE division (never
    // truncated on either path) and modulo (raw $mod on both, so both give -7 % 2 == -1 rather than C#'s -1
    // ... which happens to coincide here, hence the explicit Carol assertion below). Integral division is
    // deliberately excluded — native truncates and driver-LINQ does not; see
    // Integer_division_operand_truncates_toward_zero_like_csharp_EF434.
    [Fact]
    public void Divide_and_modulo_match_driver_linq_results_including_negative_dividend()
    {
        var (collection, logs) = SeedCustomers(nameof(Divide_and_modulo_match_driver_linq_results_including_negative_dividend));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeDiv = native.Entities.Select(c => new { c.Name, Div = (double)c.Age / c.Score }).OrderBy(r => r.Name).ToList();
        var driverDiv = driver.Entities.Select(c => new { c.Name, Div = (double)c.Age / c.Score }).OrderBy(r => r.Name).ToList();
        Assert.Equal(driverDiv, nativeDiv);

        var nativeMod = native.Entities.Where(c => c.Age % c.Score == -1).Select(c => c.Name).ToList();
        var driverMod = driver.Entities.Where(c => c.Age % c.Score == -1).Select(c => c.Name).ToList();
        Assert.Equal(driverMod, nativeMod);
        Assert.Equal(["Carol"], nativeMod); // confirms $mod's non-C#-matching sign for -7 % 2 is exercised
    }

    // ── 4. Numeric-cast operand: EF-403 Task 3 (EF-322 slice A1) made this go NATIVE ───────────────
    //
    // This section documents a FLIP, not a still-standing limitation — the two "NativeOnly_..._throws"
    // pins below used to assert exactly that: a decline. Before Task 3, MongoExpressionTranslator's
    // TranslateOperand rejected ANY type-changing convert on the comparison-operand path unconditionally
    // (the driver's own LINQ translator renders the SAME cast inconsistently depending on shape — explicit
    // $toDouble on a bare field-to-field comparison, silently dropped inside arithmetic — and reproducing
    // that exactly would have meant re-deriving driver-internal numeric-promotion rules). Task 3 instead
    // renders a type-changing cast to a renderable target (int/long/double/decimal) as an explicit
    // MongoConvertExpression ($toX) in BOTH positions, which matches the driver's rendering for the
    // field-to-field shape and merely differs cosmetically (cast rendered vs. dropped) for the arithmetic
    // shape — the arithmetic OPERATORS work on the raw BSON numeric value regardless, so values agree
    // either way (see NativeCastTests for the dedicated cast-breadth coverage this generalizes into).

    [Fact]
    public void NativeOnly_cast_in_field_to_field_comparison_now_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_cast_in_field_to_field_comparison_now_goes_native));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => (double)c.Age > c.Score).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).ToArray()); // 7.0 > 2 true; 20 > 20 false; -7 > 2 false

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("$toDouble", mql);
    }

    [Fact]
    public void Cast_in_field_to_field_comparison_matches_driver_linq_results()
    {
        var (collection, logs) = SeedCustomers(nameof(Cast_in_field_to_field_comparison_matches_driver_linq_results));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeNames = native.Entities.Where(c => (double)c.Age > c.Score).Select(c => c.Name).OrderBy(n => n).ToList();
        var driverNames = driver.Entities.Where(c => (double)c.Age > c.Score).Select(c => c.Name).OrderBy(n => n).ToList();

        // Parity ALONE passes when both paths agree on the same WRONG rows — this is the direct descendant of
        // the pre-Task-3 Native_mode_..._falls_back_and_returns_correct_results test, whose absolute-value
        // assertion (Alice: 7.0 > 2 true; Bob: 20 > 20 false; Carol: -7 > 2 false) is restored alongside parity.
        Assert.Equal(["Alice"], nativeNames);
        Assert.Equal(driverNames, nativeNames);
    }

    [Fact]
    public void NativeOnly_cast_in_arithmetic_operand_now_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_cast_in_arithmetic_operand_now_goes_native));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => (double)c.Age + c.Score > 5).OrderBy(c => c.Name).ToList();

        // Alice: 7+2=9>5 true; Bob: 20+20=40>5 true; Carol: -7+2=-5, not >5.
        Assert.Equal(["Alice", "Bob"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("$add", mql);
        // $toDouble is the operator this task exists to add — pin it, not just the pre-existing $add/$expr.
        Assert.Contains("$toDouble", mql);
    }

    [Fact]
    public void Cast_in_arithmetic_operand_matches_driver_linq_results()
    {
        var (collection, logs) = SeedCustomers(nameof(Cast_in_arithmetic_operand_matches_driver_linq_results));
        using var native = CreateContext(collection, logs, MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeNames = native.Entities.Where(c => (double)c.Age + c.Score > 5).Select(c => c.Name).OrderBy(n => n).ToList();
        var driverNames = driver.Entities.Where(c => (double)c.Age + c.Score > 5).Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
    }

    // ── 5. The !(comparison) widening (EF-335 / EF-322 Task 1) ─────────────────────────────────────
    //
    // MongoExpressionTranslator's Not arm builds MongoUnaryExpression(Not, <comparison>) for ANY of the six
    // comparison operators — EF does not normalize any of !(a>b)/!(a==b)/etc. away (spike-confirmed), so all
    // six are reachable from ordinary user code, not just from the All() aggregate this task's main slice
    // targets. RenderUnary's new $not-wrapped-comparison arm (Task 1) is what makes them all render.
    //
    // Threshold 7 (Alice's own Age) against the fixed SeedCustomers fixture (Alice=7, Bob=20, Carol=-7) is
    // chosen so EVERY operator below discriminates — i.e. the negated predicate is neither trivially true
    // nor trivially false over the three seeded rows (each yields a genuine 1-2 or 2-1 split); a threshold of
    // e.g. 100 (all fail) or -100 (all pass) would prove nothing about $not being wired correctly.

    public static IEnumerable<object[]> NegatedComparisonCases()
    {
        Expression<Func<Customer, bool>> gt = c => !(c.Age > 7);
        Expression<Func<Customer, bool>> gte = c => !(c.Age >= 7);
        Expression<Func<Customer, bool>> lt = c => !(c.Age < 7);
        Expression<Func<Customer, bool>> lte = c => !(c.Age <= 7);
        Expression<Func<Customer, bool>> eq = c => !(c.Age == 7);
        Expression<Func<Customer, bool>> neq = c => !(c.Age != 7);

        yield return ["GreaterThan", gt];
        yield return ["GreaterThanOrEqual", gte];
        yield return ["LessThan", lt];
        yield return ["LessThanOrEqual", lte];
        yield return ["Equal", eq];
        yield return ["NotEqual", neq];
    }

    [Theory]
    [MemberData(nameof(NegatedComparisonCases))]
    public void Negated_comparison_predicate_goes_native(string name, Expression<Func<Customer, bool>> predicate)
    {
        // MongoQueryLanguageRenderer.RenderUnary now renders Not over a query-native comparison as
        // { field: { $not: { <op>: value } } }; previously it threw NativeTranslationNotSupportedException and
        // the gate fell back to driver-LINQ. That renderer arm was added for MongoExpressionNegator (which
        // $not-wraps relational comparisons when complementing an All() predicate), but it also widens plain
        // Where(!(comparison)) to native as a side effect — EF does not normalize any of these six forms away
        // (spike-confirmed), so all six are reachable from ordinary user code.
        var (collection, logs) = SeedCustomers(nameof(Negated_comparison_predicate_goes_native) + name);
        using var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly);
        // Whole-entity Where + ToList(), NOT a projected Select — a bare-scalar Select is its own,
        // unrelated fallback (non-entity NativeOnly result), which would mask what this test is about.
        var nativeNames = nativeOnly.Entities.Where(predicate).ToList().Select(c => c.Name).OrderBy(n => n).ToList(); // succeeds => went native

        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var driverNames = driver.Entities.Where(predicate).ToList().Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
    }

    // I-1 (final whole-branch review of EF-322-owned-collection-all-native): NegatedComparisonCases above uses
    // inline constants only, so element.Value in RenderUnary is always a bare BSON scalar there — it never
    // exercises the BsonDocument branch of the '$'-prefix check at all. The one thing that DOES make
    // element.Value a BsonDocument for an Equal comparison is a captured local / EF query parameter, which
    // renders through PlaceholderTable's sentinel { __mongoef_param__: N } instead of a bare constant. This
    // pins that !(x.Age == capturedLocal) still goes native and substitutes correctly — the live case the
    // rationale in RenderUnary's comment and Query/AGENTS.md now names explicitly.
    [Fact]
    public void Negated_equality_against_a_captured_local_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Negated_equality_against_a_captured_local_goes_native));
        var threshold = 7;

        using var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly);
        var nativeNames = nativeOnly.Entities.Where(c => !(c.Age == threshold)).ToList()
            .Select(c => c.Name).OrderBy(n => n).ToList(); // succeeds => went native, sentinel substituted correctly

        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var driverNames = driver.Entities.Where(c => !(c.Age == threshold)).ToList()
            .Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
        Assert.Equal(["Bob", "Carol"], nativeNames); // Alice (Age=7) is the only row excluded
    }

    // The two equality forms (Equal/NotEqual above) are the important ones — they are what exercise illegal
    // form 1 ({field: {$not: <bareValue>}}, a hard server error: "$not argument must be a regex or an
    // object"). RenderUnary wraps a bare Equal rendering in { $eq: … } to avoid emitting that form; the
    // teeth-check for this (temporarily removing the '$'-prefix guard and confirming the server rejects the
    // resulting bare form) is recorded in task-3-report.md, not as a permanent test — reverting the guard
    // removal would defeat its own purpose.

    // EF-396: a Not over a conjunction is not itself a comparison, so IsQueryDialectRenderable still refuses
    // it at the QUERY-dialect level — that boundary (only All()'s own negator does De Morgan at the query
    // level; a bare Where(!(a && b)) does not) is unchanged. But RenderUnary's new fallback branch now asks
    // MongoAggregationExpressionRenderer.CanRender, which DOES admit a conjunction of renderable comparisons
    // (IsRenderableOperator includes AndAlso/OrElse) — so this shape now goes native via
    // { $expr: { $not: [ { $and: [...] } ] } } instead of declining. This test used to pin the decline; it
    // now pins the (correct, intended) native widening instead.
    [Fact]
    public void Negated_conjunction_predicate_now_goes_native_via_expr()
    {
        // Values: Alice (Age=7) is the only row for which (Age > 5 && Name == "Alice") is true, so negating
        // it yields a genuine two-row/one-row split, not a vacuous all-true/all-false predicate.
        var (collection, logs) = SeedCustomers(nameof(Negated_conjunction_predicate_now_goes_native_via_expr));
        using var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var nativeNames = nativeOnly.Entities.Where(c => !(c.Age > 5 && c.Name == "Alice"))
            .ToList().Select(c => c.Name).OrderBy(n => n).ToList(); // succeeds => went native

        var mql = Mql(logs);
        Assert.Contains("$expr", mql);
        Assert.Contains("\"$not\"", mql);
        Assert.Contains("\"$and\"", mql);

        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var driverNames = driver.Entities.Where(c => !(c.Age > 5 && c.Name == "Alice")).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(driverNames, nativeNames);
        Assert.Equal(["Bob", "Carol"], nativeNames); // Alice is the only row excluded
    }
}
