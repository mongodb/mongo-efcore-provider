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
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// F6: <c>Math.Sign/Max/Min</c> over a nullable operand in a predicate. Sign's <c>$switch</c> orders null below 0 and
/// <c>$max/$min</c> skip null, so native matched null rows where driver-LINQ (the released path) threw
/// <c>ExpressionNotSupportedException</c>. Native now declines such shapes (NativeOnly throws
/// <see cref="NativeTranslationNotSupportedException"/>; Native falls back and so matches main). Null-propagating
/// functions, non-nullable operands, and null-guarded (<c>??</c>) operands stay native.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeNullAbsorbingMathPredicateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int A { get; set; }
        public double? D { get; set; }
    }

    // Rows: a (D = null), b (D = -1), c (D = 5), d (D missing).
    private IMongoCollection<Row> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        raw.InsertMany(
        [
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "a"}, {"A", -3}, {"D", BsonNull.Value}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "b"}, {"A", 0}, {"D", -1.0}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "c"}, {"A", 7}, {"D", 5.0}},
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "d"}, {"A", 2}}
        ]);

        return database.MongoDatabase.GetCollection<Row>(collectionName);
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private void CheckDeclines(string name, Func<IQueryable<Row>, IQueryable<Row>> where)
    {
        var collection = Seed(name);
        List<string> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            return where(db.Entities.AsNoTracking()).OrderBy(x => x.Title).Select(x => x.Title).ToList();
        }

        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));

        // Main parity: the fallback (and driver-LINQ itself) throws the driver's ExpressionNotSupportedException.
        Assert.Equal("ExpressionNotSupportedException", Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.Native)).GetType().Name);
        Assert.Equal("ExpressionNotSupportedException", Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.DriverLinq)).GetType().Name);
    }

    // driverThrows: driver-LINQ has no Math.Sign/Max/Min translation, so the native result is a new capability (no
    // oracle); the expected rows are hand-computed.
    private void CheckNative(
        string name, Func<IQueryable<Row>, IQueryable<Row>> where, bool driverThrows, params string[] expected)
    {
        var collection = Seed(name);
        List<string> Run(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            return where(db.Entities.AsNoTracking()).OrderBy(x => x.Title).Select(x => x.Title).ToList();
        }

        if (!driverThrows)
        {
            NativeModeAssert.NativeAndExpected(Run, [.. expected]);
            return;
        }

        Assert.Equal(expected, Run(MongoQueryMode.NativeOnly));
        Assert.Equal(expected, Run(MongoQueryMode.Native));
        Assert.Equal("ExpressionNotSupportedException", Assert.ThrowsAny<Exception>(() => Run(MongoQueryMode.DriverLinq)).GetType().Name);
    }

    [Fact]
    public void Math_Sign_over_nullable_value_declines()
        => CheckDeclines(nameof(Math_Sign_over_nullable_value_declines), q => q.Where(d => Math.Sign(d.D!.Value) < 0));

    [Fact]
    public void Math_Max_over_nullable_value_declines()
        => CheckDeclines(nameof(Math_Max_over_nullable_value_declines), q => q.Where(d => Math.Max(d.D!.Value, 1.0) > 0));

    [Fact]
    public void Math_Min_over_nullable_value_declines()
        => CheckDeclines(nameof(Math_Min_over_nullable_value_declines), q => q.Where(d => Math.Min(d.D!.Value, 1.0) < 2));

    [Fact]
    public void Math_Max_over_coalesced_nullable_stays_native()
        => CheckNative(nameof(Math_Max_over_coalesced_nullable_stays_native), q => q.Where(d => Math.Max(d.D ?? 0, 1.0) > 2), true, "c");

    [Fact]
    public void Math_Sign_over_non_nullable_stays_native()
        => CheckNative(nameof(Math_Sign_over_non_nullable_stays_native), q => q.Where(d => Math.Sign(d.A) < 0), true, "a");

    [Fact]
    public void Math_Max_over_non_nullable_and_captured_non_nullable_int_stays_native()
    {
        var captured = 3;
        CheckNative(
            nameof(Math_Max_over_non_nullable_and_captured_non_nullable_int_stays_native),
            q => q.Where(d => Math.Max(d.A, captured) == 3), true, "a", "b", "d");
    }

    [Fact]
    public void Math_Abs_over_nullable_value_stays_native()
        => CheckNative(nameof(Math_Abs_over_nullable_value_stays_native), q => q.Where(d => Math.Abs(d.D!.Value) > 2), false, "c");
}
