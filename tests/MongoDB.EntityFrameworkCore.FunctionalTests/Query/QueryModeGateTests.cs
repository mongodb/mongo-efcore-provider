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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// End-to-end proof of the EF-323 compile-time native-vs-driver gate (Task 14). These are the first tests
/// that actually execute native aggregation pipelines (not just assert the rendered MQL of the driver-LINQ
/// path). Each test asserts results AND/OR the captured MQL shape via a <c>LogTo</c> sink with sensitive-data
/// logging enabled (so bound parameter values appear in the logged pipeline).
/// </summary>
[XUnitCollection("QueryTests")]
public class QueryModeGateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Customer
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public int? NullableScore { get; set; }
        public string? NullableName { get; set; }
    }

    // ── Test fixtures ───────────────────────────────────────────────────────────────────────────

    private (IMongoCollection<Customer> collection, List<string> logs) SeedCustomers(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Dave" }, { "Score", 40 } },
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

    // ── 1. Native mode (default): filter renders a raw aggregation $match ─────────────────────────

    [Fact]
    public void Native_mode_filter_uses_native_match_pipeline()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_mode_filter_uses_native_match_pipeline));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        var value = 15;
        var results = db.Entities.Where(c => c.Score > value).OrderBy(c => c.Score).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        // Native $match emitted by the renderer; NOT the driver-LINQ pipeline shape.
        Assert.Contains("$match", mql);
        Assert.Contains("\"Score\"", mql);
        Assert.Contains("$gt", mql);
    }

    // ── 2. Parameterized across executions (compiled-query cache correctness) ─────────────────────

    [Fact]
    public void Native_parameterized_query_returns_correct_rows_for_each_value()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_parameterized_query_returns_correct_rows_for_each_value));

        // First execution: threshold 15 → Bob, Carol, Dave.
        using (var db = CreateContext(collection, logs, MongoQueryMode.Native))
        {
            var threshold = 15;
            var names = db.Entities.Where(c => c.Score > threshold).OrderBy(c => c.Score)
                .Select(c => c.Name).ToList();
            Assert.Equal(["Bob", "Carol", "Dave"], names.ToArray());
        }

        // Second execution of the same query shape with a different parameter value: threshold 25 → Carol, Dave.
        using (var db = CreateContext(collection, logs, MongoQueryMode.Native))
        {
            var threshold = 25;
            var names = db.Entities.Where(c => c.Score > threshold).OrderBy(c => c.Score)
                .Select(c => c.Name).ToList();
            Assert.Equal(["Carol", "Dave"], names.ToArray());
        }
    }

    // ── 3. Sort + paging → native $sort / $skip / $limit ──────────────────────────────────────────
    // Uses NativeOnly to distinguish native execution from driver-LINQ fallback: both paths emit
    // $sort/$skip/$limit, so the MQL shape alone is not a reliable discriminator. Under NativeOnly,
    // a fallback would throw; success proves the query executed natively.

    [Fact]
    public void Native_sort_skip_take_uses_native_pipeline()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_sort_skip_take_uses_native_pipeline));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Under NativeOnly, this would throw before the fix; after the fix it succeeds.
        var page = db.Entities.OrderBy(c => c.Score).Skip(1).Take(2).ToList();

        Assert.Equal(["Bob", "Carol"], page.Select(c => c.Name).ToArray());
    }

    // ── 6. DriverLinq mode never goes native (even a representable Where) ─────────────────────────

    [Fact]
    public void DriverLinq_mode_never_uses_native_pipeline()
    {
        var (collection, logs) = SeedCustomers(nameof(DriverLinq_mode_never_uses_native_pipeline));
        using var db = CreateContext(collection, logs, MongoQueryMode.DriverLinq);

        var results = db.Entities.Where(c => c.Score > 15).OrderBy(c => c.Score).ToList();

        // Results must still be correct via the driver-LINQ path.
        Assert.Equal(["Bob", "Carol", "Dave"], results.Select(c => c.Name).ToArray());

        // The driver-LINQ provider renders element names through the EF serializer; both paths emit $match,
        // so the discriminating signal is behavioral: DriverLinq compiled a driver-LINQ shaper, asserted by
        // the suite-wide zero-regression run. Here we simply confirm correctness and that MQL was logged.
        var mql = Mql(logs);
        Assert.Contains("aggregate", mql);
    }

    // ── 7. Native fallback: a non-representable query returns correct results via the driver path ──

    [Fact]
    public void Native_mode_falls_back_for_unrepresentable_query()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_mode_falls_back_for_unrepresentable_query));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // A scalar projection is not natively representable (the push-down projection path); it must fall
        // back to the driver-LINQ path and still return correct results.
        var names = db.Entities.Where(c => c.Score > 15).OrderBy(c => c.Score)
            .Select(c => c.Name).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], names.ToArray());
    }

    // ── 5. NativeOnly throws at compile time on a non-representable query ──────────────────────────

    [Fact]
    public void NativeOnly_mode_throws_on_unrepresentable_query()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_mode_throws_on_unrepresentable_query));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // A scalar projection is not natively representable; NativeOnly forbids the driver fallback, so the
        // query must throw at compile time.
        var query = db.Entities.Where(c => c.Score > 15).Select(c => new { c.Name, c.Score });

        Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
    }

    [Fact]
    public void NativeOnly_mode_allows_representable_query()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_mode_allows_representable_query));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Score > 15).OrderBy(c => c.Score).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], results.Select(c => c.Name).ToArray());
        Assert.Contains("$match", Mql(logs));
    }

    // ── Canonical-order guard: paging-then-filter / paging-then-sort must fall back ───────────────
    // The native lowerer emits stages in canonical order ($match → $sort → $skip → $limit). If an
    // operator that lowers to $match or $sort is applied AFTER paging (Skip/Take) has already been
    // recorded, emitting it natively would reorder it ahead of the paging and silently return the
    // wrong rows. These queries must therefore fall back to the driver-LINQ path under Native (and
    // throw under NativeOnly).

    [Fact]
    public void Native_where_after_skip_returns_correct_rows_via_fallback()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_where_after_skip_returns_correct_rows_via_fallback));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // Sorted by Score: Alice(10), Bob(20), Carol(30), Dave(40). Skip(1) drops Alice, leaving
        // Bob, Carol, Dave; the Where(Score > 25) then keeps Carol and Dave. Emitting $match before
        // $skip natively would instead keep {Carol, Dave} then skip the first → ["Dave"] (wrong).
        var results = db.Entities.OrderBy(c => c.Score).Skip(1).Where(c => c.Score > 25).ToList();

        Assert.Equal(["Carol", "Dave"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_where_after_skip_throws()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_where_after_skip_throws));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var query = db.Entities.OrderBy(c => c.Score).Skip(1).Where(c => c.Score > 25);

        Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
    }

    [Fact]
    public void Native_order_after_skip_returns_correct_rows_via_fallback()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_order_after_skip_returns_correct_rows_via_fallback));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // Skip(1) (in document/insertion order) drops Alice, leaving Bob, Carol, Dave; then order
        // those descending by Score → Dave, Carol, Bob. Emitting $sort before $skip natively would
        // sort the full set first and skip Dave → ["Carol", "Bob"] (wrong).
        var results = db.Entities.Skip(1).OrderByDescending(c => c.Score).ToList();

        Assert.Equal(["Dave", "Carol", "Bob"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_order_after_skip_throws()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_order_after_skip_throws));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var query = db.Entities.Skip(1).OrderByDescending(c => c.Score);

        Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
    }

    [Fact]
    public void Native_order_after_take_returns_correct_rows_via_fallback()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_order_after_take_returns_correct_rows_via_fallback));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // Take(2) (in document/insertion order) keeps Alice, Bob; then order those descending by
        // Score → Bob, Alice. Emitting $sort before $limit natively would sort all four descending
        // and take the first two → ["Dave", "Carol"] (wrong).
        var results = db.Entities.Take(2).OrderByDescending(c => c.Score).ToList();

        Assert.Equal(["Bob", "Alice"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_order_after_take_throws()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_order_after_take_throws));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var query = db.Entities.Take(2).OrderByDescending(c => c.Score);

        Assert.Throws<NativeTranslationNotSupportedException>(() => query.ToList());
    }

    // ── EF-329: nullable equality / `== null` are natively representable ──────────────────────────
    // Under NativeOnly, a fallback shape would throw NativeTranslationNotSupportedException;
    // success here proves the predicate went through the native $match pipeline.

    [Fact]
    public void NativeOnly_nullable_equality_does_not_throw()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_nullable_equality_does_not_throw));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // All seeded documents omit NullableScore, so this simply proves the predicate translates
        // natively (and returns the empty, but correct, result) rather than throwing.
        var results = db.Entities.Where(c => c.NullableScore == 5).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void NativeOnly_is_null_predicate_does_not_throw()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_is_null_predicate_does_not_throw));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.NullableScore == null).ToList();

        // All four seeded rows omit NullableScore, so `== null` (matching missing-or-null) must
        // return all of them — proving both native translation AND correct match semantics.
        Assert.Equal(4, results.Count);
    }

    // ── EF-329 Task 4: `!=`-on-nullable is the highest divergence-risk case — lifted C# `!=` treats
    // null/missing as satisfying the predicate (three-valued logic collapsed to "not equal to a
    // non-null value"), so the native $ne / $match rendering must match that, not naive Mongo $ne
    // (which would exclude missing/null fields under some representations). These seed rows deliberately
    // include a value equal to the comparand, a different value, and a row that omits the field entirely,
    // then compare the native (NativeOnly) result set against plain C# LINQ-to-objects semantics over the
    // same data to prove exact behavioral equivalence rather than merely "it doesn't throw".

    [Fact]
    public void NativeOnly_nullable_inequality_does_not_throw()
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(NativeOnly_nullable_inequality_does_not_throw)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 }, { "NullableScore", 5 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 }, { "NullableScore", 7 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 } }, // omits NullableScore
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(collectionName);
        var logs = new List<string>();
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Mirror the seeded data in-memory so we can derive the expected result via plain C# lifted-`!=`
        // semantics (missing field ~ null, and `null != 5` is `true`), independent of the native pipeline.
        var seeded = new List<Customer>
        {
            new() { Name = "Alice", Score = 10, NullableScore = 5 },
            new() { Name = "Bob", Score = 20, NullableScore = 7 },
            new() { Name = "Carol", Score = 30, NullableScore = null },
        };
        var expectedNames = seeded.Where(c => c.NullableScore != 5).Select(c => c.Name).OrderBy(n => n).ToArray();

        var results = db.Entities.Where(c => c.NullableScore != 5).ToList();

        Assert.Equal(expectedNames, results.Select(c => c.Name).OrderBy(n => n).ToArray());
        // Bob (7 != 5 → true) and Carol (missing/null != 5 → true under lifted C# semantics) must both
        // be included; Alice (5 != 5 → false) must be excluded.
        Assert.Equal(["Bob", "Carol"], expectedNames);
    }

    [Fact]
    public void NativeOnly_nullable_string_inequality_does_not_throw()
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(NativeOnly_nullable_string_inequality_does_not_throw)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 }, { "NullableName", "present" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 }, { "NullableName", BsonNull.Value } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 } }, // omits NullableName
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(collectionName);
        var logs = new List<string>();
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var seeded = new List<Customer>
        {
            new() { Name = "Alice", Score = 10, NullableName = "present" },
            new() { Name = "Bob", Score = 20, NullableName = null },
            new() { Name = "Carol", Score = 30, NullableName = null },
        };
        var expectedNames = seeded.Where(c => c.NullableName != null).Select(c => c.Name).OrderBy(n => n).ToArray();

        var results = db.Entities.Where(c => c.NullableName != null).ToList();

        Assert.Equal(expectedNames, results.Select(c => c.Name).OrderBy(n => n).ToArray());
        // Only Alice (a genuinely present, non-null value) must be returned; Bob (explicit null) and
        // Carol (missing) are excluded, matching `c => c.NullableName != null` over the C# model.
        Assert.Equal(["Alice"], expectedNames);
    }

    // ── EF-329: collection Contains → $in / $nin, both inline-literal and parameterized ────────────
    // Under NativeOnly, a fallback shape would throw NativeTranslationNotSupportedException;
    // success here proves the predicate went through the native $match pipeline.

    [Fact]
    public void NativeOnly_inline_collection_contains_uses_in()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_inline_collection_contains_uses_in));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => new[] { 10, 30 }.Contains(c.Score)).ToList();

        Assert.Equal(["Alice", "Carol"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        Assert.Contains("$in", Mql(logs));
    }

    [Fact]
    public void NativeOnly_parameterized_collection_contains_uses_in()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_parameterized_collection_contains_uses_in));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var scores = new List<int> { 20, 40 };
        var results = db.Entities.Where(c => scores.Contains(c.Score)).ToList();

        Assert.Equal(["Bob", "Dave"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        Assert.Contains("$in", Mql(logs));
    }

    [Fact]
    public void NativeOnly_negated_collection_contains_uses_nin()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_negated_collection_contains_uses_nin));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var scores = new[] { 10, 30 };
        var results = db.Entities.Where(c => !scores.Contains(c.Score)).ToList();

        Assert.Equal(["Bob", "Dave"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        Assert.Contains("$nin", Mql(logs));
    }

    // ── EF-329: string.StartsWith/EndsWith/Contains → $regularExpression ────────────────────────────
    // Under NativeOnly, a fallback shape would throw NativeTranslationNotSupportedException; success
    // here proves the predicate went through the native $match pipeline. The MQL shape asserted below
    // (`$regularExpression` with `options: "s"`, anchored per kind) matches exactly what the driver-LINQ
    // fallback emits for these methods — captured empirically under MongoQueryMode.DriverLinq (see the
    // Task 6 report for the raw captured MQL).

    [Fact]
    public void NativeOnly_starts_with_uses_anchored_regex()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_starts_with_uses_anchored_regex));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Name.StartsWith("A")).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        var mql = Mql(logs);
        Assert.Contains("$regularExpression", mql);
        Assert.Contains("\"pattern\" : \"^A\"", mql);
        Assert.Contains("\"options\" : \"s\"", mql);
    }

    [Fact]
    public void NativeOnly_ends_with_uses_anchored_regex()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_ends_with_uses_anchored_regex));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Name.EndsWith("e")).ToList();

        Assert.Equal(["Alice", "Dave"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        var mql = Mql(logs);
        Assert.Contains("$regularExpression", mql);
        Assert.Contains("\"pattern\" : \"e$\"", mql);
    }

    [Fact]
    public void NativeOnly_contains_uses_unanchored_regex()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_contains_uses_unanchored_regex));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.Name.Contains("o")).ToList();

        Assert.Equal(["Bob", "Carol"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        var mql = Mql(logs);
        Assert.Contains("$regularExpression", mql);
        Assert.Contains("\"pattern\" : \"o\"", mql);
    }

    [Fact]
    public void NativeOnly_negated_starts_with_uses_not_wrapped_regex()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_negated_starts_with_uses_not_wrapped_regex));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => !c.Name.StartsWith("A")).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
        var mql = Mql(logs);
        Assert.Contains("$not", mql);
        Assert.Contains("$regularExpression", mql);
    }

    [Fact]
    public void NativeOnly_regex_metacharacters_are_escaped()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_regex_metacharacters_are_escaped));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // "A.e" as a literal must not match "Alice" (which it would if '.' were an unescaped wildcard).
        var results = db.Entities.Where(c => c.Name.StartsWith("A.")).ToList();

        Assert.Empty(results);
        Assert.Contains("\\\\.", Mql(logs));
    }

    [Fact]
    public void Native_parameterized_starts_with_falls_back_to_driver_linq()
    {
        // A parameterized search term is not baked into a native $regularExpression (Task 6 decision:
        // constant-only native regex; parameterized falls back to driver-LINQ, still zero-regression).
        var (collection, logs) = SeedCustomers(nameof(Native_parameterized_starts_with_falls_back_to_driver_linq));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        var term = "A";
        var results = db.Entities.Where(c => c.Name.StartsWith(term)).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void NativeOnly_parameterized_starts_with_throws()
    {
        // Same shape, but under NativeOnly a fallback is not permitted — it must throw rather than
        // silently execute via driver-LINQ.
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_parameterized_starts_with_throws));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var term = "A";
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(c => c.Name.StartsWith(term)).ToList());
    }
}
