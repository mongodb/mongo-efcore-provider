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
/// Field-to-field and arithmetic-operand comparisons, rendered natively via <c>{ $expr: … }</c>
/// (<see cref="MongoExpressionTranslator"/> / <see cref="MongoAggregationExpressionRenderer"/>). Each shape is
/// proven native under <see cref="MongoQueryMode.NativeOnly"/> and checked for MQL shape and result parity with
/// driver-LINQ.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeExprComparisonTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // Public: a [Theory]/[MemberData] method takes an Expression<Func<Customer, bool>> parameter, and xUnit
    // requires parameter types at least as accessible as the test method.
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
    // Modulo: driver-LINQ and native both emit raw $mod (no C# dividend-sign emulation), so they agree even where
    // both diverge from in-memory C#.
    //
    // Division: MongoDB has no integer division ($divide always yields a double), so an integral-result division
    // renders as $trunc-of-$divide (MongoBinaryOperator.IntegerDivide) to match C#, deliberately diverging from
    // driver-LINQ. Otherwise an int projection fails to deserialize and an int comparison sees a fraction.

    [Fact]
    public void NativeOnly_divide_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_divide_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Alice: 7/2 → 3 (3.5 raw); both > 1, so rows don't discriminate here — the MQL does. The row-level check
        // is Integer_division_operand_truncates_toward_zero_like_csharp_EF434.
        var results = db.Entities.Where(c => c.Age / c.Score > 1).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("\"$trunc\" : { \"$divide\" : [\"$Age\", \"$Score\"] }", mql);
    }

    // Rows are the discriminator: each expected value is one only C#'s truncate-toward-zero division produces.
    //   Alice   7/2:  C# 3   | raw $divide 3.5   | floor 3    -> "== 3" excludes raw division
    //   Carol  -7/2:  C# -3  | raw $divide -3.5  | floor -4   -> "== -3" excludes raw division AND flooring
    // NativeOnly, so a fallback (raw $divide) throws rather than silently supplying the answer.
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

    // A non-integral division must still render a bare $divide: Alice's 7/2 = 3.5 satisfies `> 3.4` only if not
    // truncated. The cast is inside a projection because a widening cast is rejected on a bare comparison operand.
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

    // Integral division read back into an int member (as in
    // NorthwindSelectQueryMongoTest.Projection_when_arithmetic_expression_precedence). A raw double result makes
    // the driver's Int32 deserializer throw "Truncation resulted in data loss".
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

    // Result parity with driver-LINQ over the negative dividend (Carol, Age=-7, Score=2), limited to double
    // division and modulo, where both paths agree. Integral division is excluded (native truncates, driver-LINQ
    // doesn't).
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

    // ── 4. Numeric-cast operand ───────────────────────────────────────────────────────────────────
    //
    // A type-changing cast to int/long/double/decimal renders as an explicit MongoConvertExpression ($toX) in both
    // the bare field-to-field and arithmetic positions. Driver-LINQ renders $toDouble for the former and drops the
    // cast in the latter; values agree either way since the arithmetic operators work on the raw BSON number. See
    // NativeCastTests for cast-breadth coverage.

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

        // Parity alone passes when both paths return the same wrong rows, so assert absolute values too
        // (Alice: 7.0 > 2 true; Bob: 20 > 20 false; Carol: -7 > 2 false).
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
        // Pin $toDouble, not just $add/$expr.
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

    // ── 5. Negated comparisons: !(comparison) ──────────────────────────────────────────────────────
    //
    // EF doesn't normalize away !(a>b)/!(a==b)/etc., so all six operators are reachable from user code; each
    // renders via RenderUnary's $not-wrapped-comparison arm.
    //
    // Threshold 7 against Alice=7, Bob=20, Carol=-7 gives every operator a genuine 1-2 or 2-1 split, so a
    // mis-wired $not is detectable.

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
        // MongoQueryLanguageRenderer.RenderUnary renders Not over a query-native comparison as
        // { field: { $not: { <op>: value } } }.
        var (collection, logs) = SeedCustomers(nameof(Negated_comparison_predicate_goes_native) + name);
        using var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly);
        // Whole-entity Where + ToList(): a bare-scalar Select falls back for unrelated reasons and would mask
        // this.
        var nativeNames = nativeOnly.Entities.Where(predicate).ToList().Select(c => c.Name).OrderBy(n => n).ToList(); // succeeds => went native

        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var driverNames = driver.Entities.Where(predicate).ToList().Select(c => c.Name).OrderBy(n => n).ToList();

        Assert.Equal(driverNames, nativeNames);
    }

    // NegatedComparisonCases uses inline constants, so it never exercises RenderUnary's BsonDocument branch of the
    // '$'-prefix check. A captured local renders as PlaceholderTable's { __mongoef_param__: N } document; this pins
    // that !(x.Age == capturedLocal) still goes native and substitutes correctly.
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

    // The equality forms matter most: RenderUnary wraps a bare Equal in { $eq: … } to avoid
    // {field: {$not: <bareValue>}}, which the server rejects ("$not argument must be a regex or an object").

    // A Not over a conjunction of query-native comparisons De Morgan's exactly (MongoExpressionNegator): each
    // comparison negates on its own (a relational operator $not-wraps, $eq/$ne inverts), so the whole thing stays
    // in the query dialect as $or of the two negated comparisons — no $expr needed at all.
    [Fact]
    public void Negated_conjunction_predicate_goes_native_via_query_dialect_de_morgan()
    {
        // Only Alice satisfies (Age > 5 && Name == "Alice"), so the negation gives a genuine two-to-one split.
        var (collection, logs) = SeedCustomers(nameof(Negated_conjunction_predicate_goes_native_via_query_dialect_de_morgan));
        using var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var nativeNames = nativeOnly.Entities.Where(c => !(c.Age > 5 && c.Name == "Alice"))
            .ToList().Select(c => c.Name).OrderBy(n => n).ToList(); // succeeds => went native

        var mql = Mql(logs);
        Assert.Contains("\"$or\"", mql);
        Assert.Contains("\"$not\"", mql);
        Assert.Contains("\"$ne\"", mql);
        Assert.DoesNotContain("$expr", mql);

        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var driverNames = driver.Entities.Where(c => !(c.Age > 5 && c.Name == "Alice")).Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(driverNames, nativeNames);
        Assert.Equal(["Bob", "Carol"], nativeNames); // Alice is the only row excluded
    }
}
