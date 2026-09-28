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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// The native query path inside an explicit transaction (it must bind the ambient session) and under async
/// cancellation mid-stream (the enumerator checks the token per <c>MoveNext</c>). Both need a replica set, which the
/// <c>mongodb/mongodb-atlas-local</c> container provides.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeTransactionAndCancellationTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Item
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Value { get; set; }
    }

    private IMongoCollection<Item> SeedRange(string name, int count)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        var rows = Enumerable.Range(0, count).Select(i => new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Label", $"L{i:D5}" }, { "Value", i }
        }).ToList();
        bson.InsertMany(rows);
        return database.MongoDatabase.GetCollection<Item>(collectionName);
    }

    private SingleEntityDbContext<Item> CreateContext(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    // ── (a) Native query executes correctly inside an explicit transaction ────────────────────────

    [Fact]
    public void Native_query_inside_transaction_returns_correct_rows_and_parity()
    {
        var collection = SeedRange(nameof(Native_query_inside_transaction_returns_correct_rows_and_parity), 6);

        // Values 0..5; Where(Value >= 2) ordered ascending => L00002, L00003, L00004, L00005.
        var expected = new[] { "L00002", "L00003", "L00004", "L00005" };

        List<string> RunInTransaction(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            using var tx = db.Database.BeginTransaction();
            var result = db.Entities
                .Where(x => x.Value >= 2)
                .OrderBy(x => x.Value)
                .ToList()
                .Select(x => x.Label)
                .ToList();
            tx.Commit();
            return result;
        }

        var native = RunInTransaction(MongoQueryMode.Native);
        Assert.Equal(expected, native);

        var driver = RunInTransaction(MongoQueryMode.DriverLinq);
        Assert.Equal(expected, driver);
        Assert.Equal(driver, native);
    }

    [Fact]
    public void NativeOnly_query_inside_transaction_goes_native_and_returns_correct_rows()
    {
        var collection = SeedRange(nameof(NativeOnly_query_inside_transaction_goes_native_and_returns_correct_rows), 6);
        var expected = new[] { "L00002", "L00003", "L00004", "L00005" };

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        using var tx = db.Database.BeginTransaction();

        // Under NativeOnly a fallback would throw; success proves the native Aggregate ran against the
        // ambient session inside the transaction.
        var result = db.Entities
            .Where(x => x.Value >= 2)
            .OrderBy(x => x.Value)
            .ToList()
            .Select(x => x.Label)
            .ToList();
        tx.Commit();

        Assert.Equal(expected, result);
    }

    // ── (a1) Native query sees its own transaction's uncommitted writes ────────────────────────────

    [Fact]
    public void NativeOnly_query_inside_transaction_sees_own_uncommitted_write()
    {
        // Seeds inside the transaction, so the native query only sees the row if it runs on the ambient session
        // (read-your-own-writes). Pre-transaction seeding wouldn't catch a dropped Session assignment.
        var collection = SeedRange(nameof(NativeOnly_query_inside_transaction_sees_own_uncommitted_write), 3);

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        using var tx = db.Database.BeginTransaction();

        db.Entities.Add(new Item { Label = "L99999", Value = 99 });
        db.SaveChanges();

        // Under NativeOnly a driver-LINQ fallback would throw; success proves this went native.
        var result = db.Entities
            .Where(x => x.Value >= 2)
            .OrderBy(x => x.Value)
            .ToList()
            .Select(x => x.Label)
            .ToList();

        tx.Commit();

        Assert.Equal(["L00002", "L99999"], result);
    }

    // ── (a2) Native query over a non-streaming-eligible entity, inside a transaction ──────────────

    private class DiscriminatedItem
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int Value { get; set; }
    }

    // TPH makes the base type non-streaming-eligible, so this covers the DOM `collection.Aggregate(session,
    // pipeline)` branch of MongoClientWrapper.Execute rather than the RawBsonDocument streaming branch.
    private class SpecialDiscriminatedItem : DiscriminatedItem
    {
        public string Note { get; set; } = "";
    }

    private IMongoCollection<DiscriminatedItem> SeedDiscriminatedRange(string name, int count)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        var rows = Enumerable.Range(0, count).Select(i => new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() }, { "Label", $"L{i:D5}" }, { "Value", i }, { "_t", nameof(DiscriminatedItem) }
        }).ToList();
        bson.InsertMany(rows);
        return database.MongoDatabase.GetCollection<DiscriminatedItem>(collectionName);
    }

    private SingleEntityDbContext<DiscriminatedItem> CreateDiscriminatedContext(
        IMongoCollection<DiscriminatedItem> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: b => b.Entity<SpecialDiscriminatedItem>(),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void NativeOnly_query_over_non_streaming_eligible_entity_inside_transaction_returns_correct_rows()
    {
        var collection = SeedDiscriminatedRange(
            nameof(NativeOnly_query_over_non_streaming_eligible_entity_inside_transaction_returns_correct_rows), 6);
        var expected = new[] { "L00002", "L00003", "L00004", "L00005" };

        using var db = CreateDiscriminatedContext(collection, MongoQueryMode.NativeOnly);

        // Premise: a derived type makes StreamingEligibility.IsEligible false, so the DOM shaper is used.
        var entityType = db.Model.FindEntityType(typeof(DiscriminatedItem))!;
        Assert.True(entityType.GetDirectlyDerivedTypes().Any());

        using var tx = db.Database.BeginTransaction();

        // Under NativeOnly a fallback would throw; success proves the non-streaming native branch ran in the session.
        var result = db.Entities
            .Where(x => x.Value >= 2)
            .OrderBy(x => x.Value)
            .ToList()
            .Select(x => x.Label)
            .ToList();
        tx.Commit();

        Assert.Equal(expected, result);
    }

    // ── (b) Async cancellation mid-stream stops the native enumerator ─────────────────────────────

    [Fact]
    public async Task Native_async_enumeration_observes_cancellation_mid_stream()
    {
        // Seed enough rows that streaming yields incrementally and there is room to cancel partway.
        const int rowCount = 5000;
        var collection = SeedRange(nameof(Native_async_enumeration_observes_cancellation_mid_stream), rowCount);

        await using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        using var cts = new CancellationTokenSource();

        var seen = 0;

        async Task Enumerate()
        {
            await foreach (var item in db.Entities
                               .Where(x => x.Value >= 0)
                               .OrderBy(x => x.Value)
                               .AsAsyncEnumerable()
                               .WithCancellation(cts.Token))
            {
                seen++;
                // Cancel partway through the stream. The native enumerator checks the token per MoveNext,
                // so subsequent iterations must observe the cancellation and stop the stream.
                if (seen == 10)
                {
                    cts.Cancel();
                }
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(Enumerate);

        // We cancelled at row 10; the stream must not have run to completion.
        Assert.True(seen < rowCount, $"Stream ran to completion ({seen} rows) despite cancellation.");
    }

    [Fact]
    public async Task Native_ToListAsync_honors_already_cancelled_token()
    {
        var collection = SeedRange(nameof(Native_ToListAsync_honors_already_cancelled_token), 100);

        await using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.Entities.Where(x => x.Value >= 0).OrderBy(x => x.Value).ToListAsync(cts.Token));
    }
}
