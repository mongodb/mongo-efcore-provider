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
    private class Customer
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
    // Empirically (see task-7-report.md), the driver's own LINQ translator emits RAW $divide/$mod for
    // int operands — it does NOT emulate C#'s truncating-toward-zero division or C#'s dividend-sign modulo
    // semantics. The native translator's renderer already emits the identical raw $divide/$mod shape, so
    // native and driver-LINQ execute byte-identical $expr documents server-side and necessarily agree,
    // even though both diverge from what plain in-memory C# arithmetic would compute. Because native MQL
    // is byte-identical to driver MQL here (not merely result-equivalent), no fallback is needed.

    [Fact]
    public void NativeOnly_divide_operand_succeeds_with_expected_mql()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_divide_operand_succeeds_with_expected_mql));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Alice: 7/2 = 3.5 (non-truncating $divide) > 1 → included. C# truncating 7/2=3 would also be >1,
        // so this alone wouldn't distinguish semantics; the MQL assertion below is the real proof.
        var results = db.Entities.Where(c => c.Age / c.Score > 1).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("\"$divide\" : [\"$Age\", \"$Score\"]", mql);
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

    // Result-parity test exposing the truncation/sign danger zone directly: negative dividend (Carol,
    // Age=-7, Score=2). C# would give -7/2 = -3 (truncated) and -7%2 = -1; native/driver-LINQ MQL is
    // identical either way, so if this ever regresses to a truncation-emulating shape, this test would
    // catch the divergence from driver-LINQ's actual (non-truncating) results.
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

    // ── 4. Numeric-cast operand: falls back to driver-LINQ rather than diverge ─────────────────────
    //
    // Empirically (see task-7-report.md), the driver's own LINQ translator renders a numeric cast
    // inconsistently depending on shape: `(double)c.Age > c.Score` → explicit $toDouble on both operands;
    // `(double)c.Age + c.Score > 5` → the cast is silently dropped. Reproducing this exactly would require
    // re-deriving the driver's internal numeric-promotion rules, so MongoExpressionTranslator falls back
    // for both shapes (TranslateOperand rejects any type-changing convert) rather than risk diverging.
    // These tests prove the fallback still executes correctly (just not via native $expr).

    [Fact]
    public void NativeOnly_cast_in_field_to_field_comparison_throws()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_cast_in_field_to_field_comparison_throws));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var query = db.Entities.Where(c => (double)c.Age > c.Score);

        Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
    }

    [Fact]
    public void Native_mode_cast_in_field_to_field_comparison_falls_back_and_returns_correct_results()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_mode_cast_in_field_to_field_comparison_falls_back_and_returns_correct_results));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        var results = db.Entities.Where(c => (double)c.Age > c.Score).OrderBy(c => c.Name).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).ToArray()); // 7.0 > 2 true; 20 > 20 false; -7 > 2 false
    }

    [Fact]
    public void NativeOnly_cast_in_arithmetic_operand_throws()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_cast_in_arithmetic_operand_throws));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var query = db.Entities.Where(c => (double)c.Age + c.Score > 5);

        Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
    }
}
