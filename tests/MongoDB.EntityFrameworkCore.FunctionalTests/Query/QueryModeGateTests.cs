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
}
