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

using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>A keyless entity (a view or query type) has nothing to track or to key by, and is read straight off the cursor like any other.</summary>
[XUnitCollection("QueryTests")]
public class NativeKeylessStreamingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Reading
    {
        public string Sensor { get; set; } = "";
        public double Value { get; set; }
        public int? Count { get; set; }
    }

    private sealed class ReadingContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Reading> Readings => Set<Reading>();

        protected override void OnModelCreating(ModelBuilder mb)
            => mb.Entity<Reading>(b =>
            {
                b.HasNoKey();
                b.ToCollection("Readings" + suffix);
            });
    }

    private ReadingContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<ReadingContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>("Readings" + suffix).InsertMany(
        [
            new BsonDocument { { "Sensor", "a" }, { "Value", 1.5 }, { "Count", 7 } },
            new BsonDocument { { "Sensor", "b" }, { "Value", 2.5 }, { "Count", BsonNull.Value } },
            new BsonDocument { { "Sensor", "c" }, { "Value", 3.5 } },
        ]);
        return suffix;
    }

    [Fact]
    public void A_keyless_entity_streams_and_matches_driver_linq()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return db.Readings.OrderBy(r => r.Sensor).ToList().Select(r => $"{r.Sensor}:{r.Value}:{r.Count?.ToString() ?? "-"}").ToList();
        });

        Assert.Equal(["a:1.5:7", "b:2.5:-", "c:3.5:-"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        var enumerator = ((IEnumerable)streaming.Readings.Where(r => r.Value > 1)).GetEnumerator();
        try
        {
            Assert.NotEqual(typeof(BsonDocument), enumerator.GetType().GetGenericArguments()[0]);
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }
}
