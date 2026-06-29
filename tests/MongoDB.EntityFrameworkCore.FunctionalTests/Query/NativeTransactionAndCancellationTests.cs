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
/// EF-323 coverage for the native query path in two runtime contexts that the unit/MQL-shape tests
/// cannot exercise:
/// <list type="bullet">
///   <item><b>Inside an explicit transaction.</b> A native query run while a <see cref="IDbContextTransaction"/>
///   is open must bind the ambient driver session, so it sees the transaction's view and returns the correct
///   rows. Asserted under <see cref="MongoQueryMode.Native"/>, with a parity check against
///   <see cref="MongoQueryMode.DriverLinq"/> inside a transaction.</item>
///   <item><b>Async cancellation mid-stream.</b> The native enumerator must observe a cancelled
///   <see cref="CancellationToken"/> per <c>MoveNext</c>, so a token cancelled partway through async
///   enumeration stops the stream with an <see cref="OperationCanceledException"/>.</item>
/// </list>
/// Both require a replica set; the <c>mongodb/mongodb-atlas-local</c> testcontainer provides a single-node
/// replica set (transactions enabled).
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
