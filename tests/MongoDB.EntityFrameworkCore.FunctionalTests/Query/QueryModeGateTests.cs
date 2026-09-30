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
/// End-to-end tests of the compile-time native-vs-driver gate across <c>MongoQueryMode</c>s. Asserts results
/// and/or the logged MQL (sensitive-data logging on, so bound parameter values appear).
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
        public bool IsPremium { get; set; }
    }

    // ── Test fixtures ───────────────────────────────────────────────────────────────────────────

    private (IMongoCollection<Customer> collection, List<string> logs) SeedCustomers(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Dave" }, { "Score", 40 }, { "IsPremium", false } },
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

    // ── Native mode (default): filter renders a native $match ───────────────────────────────────

    [Fact]
    public void Native_mode_filter_uses_native_match_pipeline()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_mode_filter_uses_native_match_pipeline));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        var value = 15;
        var results = db.Entities.Where(c => c.Score > value).OrderBy(c => c.Score).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], results.Select(c => c.Name).ToArray());

        var mql = Mql(logs);
        Assert.Contains("$match", mql);
        Assert.Contains("\"Score\"", mql);
        Assert.Contains("$gt", mql);
    }

    // ── Parameterized across executions (compiled-query cache correctness) ───────────────────────

    [Fact]
    public void Native_parameterized_query_returns_correct_rows_for_each_value()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_parameterized_query_returns_correct_rows_for_each_value));

        using (var db = CreateContext(collection, logs, MongoQueryMode.Native))
        {
            var threshold = 15;
            var names = db.Entities.Where(c => c.Score > threshold).OrderBy(c => c.Score)
                .Select(c => c.Name).ToList();
            Assert.Equal(["Bob", "Carol", "Dave"], names.ToArray());
        }

        using (var db = CreateContext(collection, logs, MongoQueryMode.Native))
        {
            var threshold = 25;
            var names = db.Entities.Where(c => c.Score > threshold).OrderBy(c => c.Score)
                .Select(c => c.Name).ToList();
            Assert.Equal(["Carol", "Dave"], names.ToArray());
        }
    }

    // ── Sort + paging → native $sort / $skip / $limit ─────────────────────────────────────────────
    // Both paths emit the same stages, so NativeOnly (a fallback would throw) is the proof of native.

    [Fact]
    public void Native_sort_skip_take_uses_native_pipeline()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_sort_skip_take_uses_native_pipeline));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var page = db.Entities.OrderBy(c => c.Score).Skip(1).Take(2).ToList();

        Assert.Equal(["Bob", "Carol"], page.Select(c => c.Name).ToArray());
    }

    // ── DriverLinq mode never goes native (even a representable Where) ────────────────────────────

    [Fact]
    public void DriverLinq_mode_never_uses_native_pipeline()
    {
        var (collection, logs) = SeedCustomers(nameof(DriverLinq_mode_never_uses_native_pipeline));
        using var db = CreateContext(collection, logs, MongoQueryMode.DriverLinq);

        var results = db.Entities.Where(c => c.Score > 15).OrderBy(c => c.Score).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], results.Select(c => c.Name).ToArray());

        // Both paths emit $match, so MQL can't discriminate; this only confirms correctness and logging.
        var mql = Mql(logs);
        Assert.Contains("aggregate", mql);
    }

    // ── Native fallback: a non-representable query returns correct results via the driver path ────

    [Fact]
    public void Native_mode_falls_back_for_unrepresentable_query()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_mode_falls_back_for_unrepresentable_query));

        // A Split(...)[0] computed projection has no native translation (driver-LINQ has one). There is no fallback log event, so prove the
        // fallback is real by asserting NativeOnly rejects the same query; otherwise, once the native translator
        // learns the shape, this test would silently stop exercising the fallback.
        using (var nativeOnly = CreateContext(collection, [], MongoQueryMode.NativeOnly))
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() => nativeOnly.Entities
                .Where(c => c.Score > 15).OrderBy(c => c.Score).Select(c => c.Name.Split(' ')[0]).ToList());
        }

        using var db = CreateContext(collection, logs, MongoQueryMode.Native);
        var names = db.Entities.Where(c => c.Score > 15).OrderBy(c => c.Score)
            .Select(c => c.Name.Split(' ')[0]).ToList();

        Assert.Equal(["Bob", "Carol", "Dave"], names.ToArray());
    }

    // ── NativeOnly: representable shapes succeed, non-representable ones throw at compile time ────

    [Fact]
    public void NativeOnly_mode_allows_arithmetic_computed_projection()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_mode_allows_arithmetic_computed_projection));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // An arithmetic computed projection leaf renders as a computed $project, so NativeOnly succeeds.
        var results = db.Entities.Where(c => c.Score > 15).OrderBy(c => c.Score)
            .Select(c => new { c.Name, Doubled = c.Score * 2 }).ToList();

        Assert.Equal(
            [("Bob", 40), ("Carol", 60), ("Dave", 80)],
            results.Select(r => (r.Name, r.Doubled)).ToArray());
        Assert.Contains("$project", Mql(logs));
    }

    [Fact]
    public void NativeOnly_mode_throws_on_unrepresentable_query()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_mode_throws_on_unrepresentable_query));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // A PadLeft computed projection has no native translation, so NativeOnly must throw at compile time.
        var query = db.Entities.Where(c => c.Score > 15).Select(c => new { Greeting = c.Name.PadLeft(10) });

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

    // ── Non-canonical order: paging-then-filter / paging-then-sort go native ──────────────────────
    // PipelineOps are emitted in arrival order, so a $match/$sort after paging runs after it, matching
    // LINQ's sequential semantics.

    [Fact]
    public void Native_where_after_skip_returns_correct_rows()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_where_after_skip_returns_correct_rows));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // Skip(1) drops Alice; the Where then keeps Carol and Dave.
        var results = db.Entities.OrderBy(c => c.Score).Skip(1).Where(c => c.Score > 25).ToList();

        Assert.Equal(["Carol", "Dave"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_where_after_skip_succeeds()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_where_after_skip_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.OrderBy(c => c.Score).Skip(1).Where(c => c.Score > 25).ToList();

        Assert.Equal(["Carol", "Dave"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Native_order_after_skip_returns_correct_rows()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_order_after_skip_returns_correct_rows));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // Skip(1) in insertion order drops Alice; the $sort then orders the remaining rows.
        var results = db.Entities.Skip(1).OrderByDescending(c => c.Score).ToList();

        Assert.Equal(["Dave", "Carol", "Bob"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_order_after_skip_succeeds()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_order_after_skip_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Skip(1).OrderByDescending(c => c.Score).ToList();

        Assert.Equal(["Dave", "Carol", "Bob"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Native_order_after_take_returns_correct_rows()
    {
        var (collection, logs) = SeedCustomers(nameof(Native_order_after_take_returns_correct_rows));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        // Take(2) in insertion order keeps Alice and Bob; the $sort then orders just those.
        var results = db.Entities.Take(2).OrderByDescending(c => c.Score).ToList();

        Assert.Equal(["Bob", "Alice"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_order_after_take_succeeds()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_order_after_take_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Take(2).OrderByDescending(c => c.Score).ToList();

        Assert.Equal(["Bob", "Alice"], results.Select(c => c.Name).ToArray());
    }

    // ── Take-before-Skip and repeated paging go native (same arrival-order mechanism) ─────────────

    [Fact]
    public void NativeOnly_take_before_skip_succeeds()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_take_before_skip_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.OrderBy(c => c.Score).Take(3).Skip(1).ToList();

        Assert.Equal(["Bob", "Carol"], results.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void NativeOnly_repeated_paging_succeeds()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_repeated_paging_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.OrderBy(c => c.Score).Skip(1).Take(2).Skip(1).ToList();

        Assert.Equal(["Carol"], results.Select(c => c.Name).ToArray());
    }

    // ── A predicate-injecting aggregate after paging goes native ──────────────────────────────────
    // The injected $match (All's negated predicate, a Count/Any predicate) is appended after any recorded
    // $skip/$limit, so it evaluates over only the paged rows.

    [Fact]
    public void NativeOnly_take_then_all_with_predicate_succeeds()
    {
        // A bare-bool predicate keeps this test about paging order, independent of how All negates a
        // comparison predicate.
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(NativeOnly_take_then_all_with_predicate_succeeds)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 }, { "IsPremium", true } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 }, { "IsPremium", true } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Dave" }, { "Score", 40 }, { "IsPremium", true } },
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(collectionName);
        var logs = new List<string>();
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Take(2) keeps Alice and Bob; Bob isn't premium, so All must be false.
        var result = db.Entities.OrderBy(c => c.Score).Take(2).All(c => c.IsPremium);

        Assert.False(result);
    }

    [Fact]
    public void NativeOnly_take_then_count_with_predicate_succeeds()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_take_then_count_with_predicate_succeeds));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // Take(2) keeps Alice and Bob; only Bob has Score > 15.
        var result = db.Entities.OrderBy(c => c.Score).Take(2).Count(c => c.Score > 15);

        Assert.Equal(1, result);
    }

    // ── Nullable equality / `== null` are natively representable ─────────────────────────────────

    [Fact]
    public void NativeOnly_nullable_equality_does_not_throw()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_nullable_equality_does_not_throw));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // All seeded documents omit NullableScore, so the correct result is empty.
        var results = db.Entities.Where(c => c.NullableScore == 5).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void NativeOnly_is_null_predicate_does_not_throw()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_is_null_predicate_does_not_throw));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var results = db.Entities.Where(c => c.NullableScore == null).ToList();

        // All four seeded rows omit NullableScore; `== null` must match missing as well as null.
        Assert.Equal(4, results.Count);
    }

    // ── `!=` on nullable: lifted C# `!=` treats null/missing as satisfying `x != 5`, so the native rendering
    // must include those rows. Seeds an equal value, a different value, and a missing field, and compares
    // against LINQ-to-objects over the same data.

    [Fact]
    public void NativeOnly_nullable_inequality_does_not_throw()
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(NativeOnly_nullable_inequality_does_not_throw)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 }, { "NullableScore", 5 }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 }, { "NullableScore", 7 }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 }, { "IsPremium", false } }, // omits NullableScore
        ]);
        var collection = database.MongoDatabase.GetCollection<Customer>(collectionName);
        var logs = new List<string>();
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        // In-memory oracle (missing field ~ null).
        var seeded = new List<Customer>
        {
            new() { Name = "Alice", Score = 10, NullableScore = 5 },
            new() { Name = "Bob", Score = 20, NullableScore = 7 },
            new() { Name = "Carol", Score = 30, NullableScore = null },
        };
        var expectedNames = seeded.Where(c => c.NullableScore != 5).Select(c => c.Name).OrderBy(n => n).ToArray();

        var results = db.Entities.Where(c => c.NullableScore != 5).ToList();

        Assert.Equal(expectedNames, results.Select(c => c.Name).OrderBy(n => n).ToArray());
        Assert.Equal(["Bob", "Carol"], expectedNames);
    }

    [Fact]
    public void NativeOnly_nullable_string_inequality_does_not_throw()
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(NativeOnly_nullable_string_inequality_does_not_throw)) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Alice" }, { "Score", 10 }, { "NullableName", "present" }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Bob" }, { "Score", 20 }, { "NullableName", BsonNull.Value }, { "IsPremium", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", "Carol" }, { "Score", 30 }, { "IsPremium", false } }, // omits NullableName
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
        // Both explicit null (Bob) and missing (Carol) are excluded.
        Assert.Equal(["Alice"], expectedNames);
    }

    // ── Collection Contains → $in / $nin, both inline-literal and parameterized ───────────────────

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

    // ── string.StartsWith/EndsWith/Contains → $regularExpression ────────────────────────────────────
    // The asserted shape (`options: "s"`, anchored per kind) matches what the driver-LINQ path emits.

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
    public void Native_parameterized_starts_with_goes_native()
    {
        // A parameterized term goes native: escaping/anchoring is deferred to a regex placeholder resolved
        // per execution.
        var (collection, logs) = SeedCustomers(nameof(Native_parameterized_starts_with_goes_native));
        using var db = CreateContext(collection, logs, MongoQueryMode.Native);

        var term = "A";
        var results = db.Entities.Where(c => c.Name.StartsWith(term)).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void NativeOnly_parameterized_starts_with_goes_native()
    {
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_parameterized_starts_with_goes_native));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var term = "A";
        var results = db.Entities.Where(c => c.Name.StartsWith(term)).ToList();

        Assert.Equal(["Alice"], results.Select(c => c.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void NativeOnly_parameterized_starts_with_escapes_regex_metacharacters()
    {
        // Parameterized terms must be escaped like constants: "A." must not match "Alice".
        var (collection, logs) = SeedCustomers(nameof(NativeOnly_parameterized_starts_with_escapes_regex_metacharacters));
        using var db = CreateContext(collection, logs, MongoQueryMode.NativeOnly);

        var term = "A.";
        var results = db.Entities.Where(c => c.Name.StartsWith(term)).ToList();

        Assert.Empty(results);
        Assert.Contains("\\\\.", Mql(logs));
    }
}
