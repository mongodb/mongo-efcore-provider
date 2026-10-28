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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Scalar aggregates (Count/Sum/Any/Max) honour the cancellation token on every query path: they run lazily at the first
/// MoveNext, so a pre-cancelled token throws instead of completing.
/// </summary>
public class NativeAggregateCancellationTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    private class ValueEntity
    {
        public ObjectId Id { get; set; }
        public int Value { get; set; }
    }

    private SingleEntityDbContext<ValueEntity> CreateContext(MongoQueryMode mode, string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<ValueEntity>(collectionName);
        collection.InsertMany(
            new[] { 1, 2, 3 }.Select(v => new ValueEntity { Id = ObjectId.GenerateNewId(), Value = v }));

        return SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
    }

    private static CancellationToken Cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public async Task CountAsync_with_cancelled_token_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(CountAsync_with_cancelled_token_throws) + mode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Entities.CountAsync(Cancelled()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Entities.Where(e => e.Value > 1).CountAsync(Cancelled()));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public async Task SumAsync_with_cancelled_token_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(SumAsync_with_cancelled_token_throws) + mode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Entities.SumAsync(e => e.Value, Cancelled()));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public async Task AnyAsync_with_cancelled_token_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(AnyAsync_with_cancelled_token_throws) + mode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Entities.AnyAsync(Cancelled()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Entities.AnyAsync(e => e.Value > 1, Cancelled()));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public async Task MaxAsync_with_cancelled_token_throws(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(MaxAsync_with_cancelled_token_throws) + mode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Entities.MaxAsync(e => e.Value, Cancelled()));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public async Task Aggregates_with_live_token_return_values(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(Aggregates_with_live_token_return_values) + mode);
        var token = CancellationToken.None;

        Assert.Equal(3, await db.Entities.CountAsync(token));
        Assert.Equal(6, await db.Entities.SumAsync(e => e.Value, token));
        Assert.True(await db.Entities.AnyAsync(e => e.Value > 2, token));
        Assert.Equal(3, await db.Entities.MaxAsync(e => e.Value, token));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Sync_aggregates_return_values(MongoQueryMode mode)
    {
        using var db = CreateContext(mode, nameof(Sync_aggregates_return_values) + mode);

        Assert.Equal(3, db.Entities.Count());
        Assert.Equal(6, db.Entities.Sum(e => e.Value));
        Assert.True(db.Entities.Any(e => e.Value > 2));
        Assert.False(db.Entities.Any(e => e.Value > 5));
        Assert.Equal(3, db.Entities.Max(e => e.Value));
    }
}
