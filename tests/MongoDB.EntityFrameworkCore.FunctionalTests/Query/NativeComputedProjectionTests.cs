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
/// EF-347 end-to-end coverage of numeric arithmetic COMPUTED PROJECTION leaves (as opposed to EF-329's
/// comparison-operand coverage in <see cref="NativeExprComparisonTests"/>), which the native translator now
/// binds directly into <c>$project</c> via <see cref="MongoExpressionTranslator.TryTranslateValue"/> /
/// <c>NativeProjectionBinder</c>. Each in-scope shape is proven native via <see cref="MongoQueryMode.NativeOnly"/>
/// (succeeds ⇒ went native; a fallback shape would throw <c>NativeTranslationNotSupportedException</c>), asserted
/// for result-set parity between native and driver-LINQ execution, and asserted for the expected aggregation
/// operator in the captured MQL. Two shapes (integer division, string concatenation) are guarded OFF the native
/// path on purpose and are covered here as "graceful fallback" — they must still produce correct driver-LINQ
/// results and must still throw under <c>NativeOnly</c>. One additional test documents a known, PRE-EXISTING,
/// out-of-scope limitation (a mixed whole-entity + computed-arithmetic projection) that this slice neither
/// introduces nor fixes.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeComputedProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public int Score { get; set; }
        public double Weight { get; set; }
        public int? MaybeAge { get; set; }
    }

    // Alice: Age=7,  Score=2,  Weight=70.0, MaybeAge=7   → 7*2=14, 7-2=5, 7+2=9, 7%2=1, 70.0/2=35.0
    // Bob:   Age=20, Score=20, Weight=200.0, MaybeAge=null → 20*20=400, 20-20=0, 20+20=40, 20%20=0, 200.0/20=10.0
    // Carol: Age=-7, Score=2,  Weight=35.0, MaybeAge=-7  → -7*2=-14, -7-2=-9, -7+2=-5, -7%2=-1 (negative), 35.0/2=17.5
    private (IMongoCollection<Customer> collection, List<string> logs) SeedCustomers(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 7 }, { "Score", 2 },
                { "Weight", 70.0 }, { "MaybeAge", 7 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 20 }, { "Score", 20 },
                { "Weight", 200.0 }, { "MaybeAge", BsonNull.Value }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", -7 }, { "Score", 2 },
                { "Weight", 35.0 }, { "MaybeAge", -7 }
            },
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

    // ── In-scope: each proven NativeOnly + Native==DriverLinq parity + expected MQL operator ─────────

    [Fact]
    public void Multiply_projection_goes_native_and_matches_driver()
    {
        var (collection, logs) = SeedCustomers(nameof(Multiply_projection_goes_native_and_matches_driver));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, P = c.Age * c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([14, 400, -14], results.Select(r => r.P).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$multiply", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, P = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, P = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Subtract_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Subtract_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, D = c.Age - c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([5, 0, -9], results.Select(r => r.D).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$subtract", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, D = c.Age - c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, D = c.Age - c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Add_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Add_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, S = c.Age + c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([9, 40, -5], results.Select(r => r.S).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$add", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, S = c.Age + c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, S = c.Age + c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Modulo_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Modulo_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, M = c.Age % c.Score })
                .OrderBy(r => r.Name).ToList();
            // Alice: 7%2=1, Bob: 20%20=0, Carol: -7%2=-1 (negative dividend exercised)
            Assert.Equal([1, 0, -1], results.Select(r => r.M).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$mod", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, M = c.Age % c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, M = c.Age % c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Floating_division_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Floating_division_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            // c.Weight / c.Score: the compiler inserts an implicit int->double widening Convert on Score,
            // which TryTranslateValue must unwrap (allowNumericWidening: true) to go native.
            var results = nativeOnly.Entities.Select(c => new { c.Name, R = c.Weight / c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal([35.0, 10.0, 17.5], results.Select(r => r.R).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$divide", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, R = c.Weight / c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, R = c.Weight / c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Mixed_member_and_arithmetic_projection_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(Mixed_member_and_arithmetic_projection_goes_native));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, T = c.Age * c.Score })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Name).ToArray());
            Assert.Equal([14, 400, -14], results.Select(r => r.T).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$multiply", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);
        var nativeResults = native.Entities.Select(c => new { c.Name, T = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, T = c.Age * c.Score })
            .OrderBy(r => r.Name).ToList();
        Assert.Equal(driverResults, nativeResults);
    }

    [Fact]
    public void Nullable_operand_arithmetic_matches_driver()
    {
        var (collection, logs) = SeedCustomers(nameof(Nullable_operand_arithmetic_matches_driver));

        // Prove native: under NativeOnly a driver-LINQ fallback throws, so success ⇒ the $project went native.
        // Parity alone (Native == DriverLinq below) is NOT proof — a silent fallback would produce identical
        // results and still pass — so this block plus the $multiply MQL assertion is the actual native proof.
        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var results = nativeOnly.Entities.Select(c => new { c.Name, X = c.MaybeAge * 2 })
                .OrderBy(r => r.Name).ToList();
            Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.Name).ToArray());

            var mql = Mql(logs);
            Assert.Contains("$multiply", mql);
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeResults = native.Entities.Select(c => new { c.Name, X = c.MaybeAge * 2 })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, X = c.MaybeAge * 2 })
            .OrderBy(r => r.Name).ToList();

        Assert.Equal(driverResults, nativeResults);
        // Bob's MaybeAge is null → the product must be null, not 0 or thrown.
        Assert.Null(nativeResults.Single(r => r.Name == "Bob").X);
        Assert.Equal(14, nativeResults.Single(r => r.Name == "Alice").X);
        Assert.Equal(-14, nativeResults.Single(r => r.Name == "Carol").X);
    }

    // ── Guard fallbacks: graceful — there IS a driver-LINQ oracle, and results must agree ────────────

    [Fact]
    public void Integer_division_projection_falls_back_gracefully_except_under_NativeOnly()
    {
        // Uses a dedicated, evenly-divisible seed (rather than the shared Age/Score of 7/2, 20/20, -7/2) so the
        // assertion isolates the guard's fallback behavior. MongoDB's $divide is non-truncating and always
        // yields a BSON double; the C# driver's own Int32 deserializer additionally throws TruncationException
        // for a non-integral double (e.g. 7/2 = 3.5) when read back into an int-typed property — an unrelated,
        // pre-existing driver-deserialization quirk that fires identically whether the raw $divide comes from
        // native's fallback or from driver-LINQ itself. Evenly-divisible values sidestep that quirk so this test
        // isolates just the guard/fallback behavior this slice is responsible for.
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Integer_division_projection_falls_back_gracefully_except_under_NativeOnly)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Age", 8 }, { "Score", 2 }, { "Weight", 1.0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Age", 21 }, { "Score", 7 }, { "Weight", 1.0 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Age", -9 }, { "Score", 3 }, { "Weight", 1.0 } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(collectionName);
        var logs = new List<string>();

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var query = nativeOnly.Entities.Select(c => new { c.Name, X = c.Age / c.Score });
            Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeResults = native.Entities.Select(c => new { c.Name, X = c.Age / c.Score })
            .OrderBy(r => r.Name).ToList();
        var driverResults = driver.Entities.Select(c => new { c.Name, X = c.Age / c.Score })
            .OrderBy(r => r.Name).ToList();

        Assert.Equal(driverResults, nativeResults);
        // Evenly-divisible, so truncating-toward-zero C# division and MongoDB's non-truncating $divide agree:
        // 8/2=4, 21/7=3, -9/3=-3.
        Assert.Equal([4, 3, -3], nativeResults.Select(r => r.X).ToArray());
    }

    [Fact]
    public void String_concat_projection_falls_back_gracefully_except_under_NativeOnly()
    {
        var (collection, logs) = SeedCustomers(nameof(String_concat_projection_falls_back_gracefully_except_under_NativeOnly));

        using (var nativeOnly = CreateContext(collection, logs, MongoQueryMode.NativeOnly))
        {
            var query = nativeOnly.Entities.Select(c => new { X = c.Name + "!" });
            Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
        }

        using var native = CreateContext(collection, [], MongoQueryMode.Native);
        using var driver = CreateContext(collection, [], MongoQueryMode.DriverLinq);

        var nativeResults = native.Entities.Select(c => new { X = c.Name + "!" }).OrderBy(r => r.X).ToList();
        var driverResults = driver.Entities.Select(c => new { X = c.Name + "!" }).OrderBy(r => r.X).ToList();

        Assert.Equal(driverResults, nativeResults);
        Assert.Equal(["Alice!", "Bob!", "Carol!"], nativeResults.Select(r => r.X).ToArray());
    }

    // ── Known, pre-existing, OUT-OF-SCOPE limitation: mixed whole-entity + computed-arithmetic ───────
    //
    // Select(c => new { c, Total = c.Age * c.Score }) mixes a whole-entity leaf with a computed-arithmetic
    // leaf. The native projection binder's Route stays Fallback (an entity leaf isn't natively representable),
    // so this shape routes to the pre-existing MIXED shaper (MongoMixedProjectionBindingRemovingExpressionVisitor)
    // via the default (non-native) projection-binding walk — NOT through the new Route == Projection-gated
    // BinaryExpression case added in this slice (see MongoProjectionBindingExpressionVisitor), which is exactly
    // what confines this slice's change to the native path. Under the default walk, the BinaryExpression's two
    // operands (c.Age, c.Score) are each visited as ordinary MemberExpressions against the SAME current
    // projection member (there is no per-operand member push for a bare arithmetic node), so the second operand's
    // binding silently overwrites the first's in the projection mapping. The net observed effect: EVERY row's
    // "Total" comes out as Score*Score instead of Age*Score (e.g. Alice Age=7,Score=2 → Total=4, not 14; Carol
    // Age=-7,Score=2 → Total=4, not -14; Bob Age=20,Score=20 → Total=400, which happens to be correct only because
    // Age==Score for that row). No exception is thrown — this is a SILENT wrong-data bug that predates this slice
    // and is unrelated to it (the mixed shaper has no BinaryExpression handling at all). This test pins down that
    // exact, reproducible, wrong value so any future change to this area shows up as a test diff; it is NOT
    // asserting correct behavior. Tracked as a follow-up ticket (see task-4-report.md).
    [Fact]
    public void Mixed_whole_entity_and_computed_leaf_is_a_known_preexisting_limitation()
    {
        var (collection, _) = SeedCustomers(nameof(Mixed_whole_entity_and_computed_leaf_is_a_known_preexisting_limitation));
        using var db = CreateContext(collection, [], MongoQueryMode.Native);

        var results = db.Entities.Select(c => new { c, Total = c.Age * c.Score }).OrderBy(r => r.c.Name).ToList();

        // Whole-entity fields materialize correctly...
        Assert.Equal(["Alice", "Bob", "Carol"], results.Select(r => r.c.Name).ToArray());
        Assert.Equal([7, 20, -7], results.Select(r => r.c.Age).ToArray());
        Assert.Equal([2, 20, 2], results.Select(r => r.c.Score).ToArray());

        // ...but "Total" is silently WRONG: it comes out as Score*Score, not Age*Score. Correct values would be
        // [14, 400, -14]; Bob's happens to match by coincidence because Age == Score for that row.
        Assert.Equal([4, 400, 4], results.Select(r => r.Total).ToArray());
        Assert.NotEqual(14, results.Single(r => r.c.Name == "Alice").Total); // documents the divergence explicitly
    }
}
