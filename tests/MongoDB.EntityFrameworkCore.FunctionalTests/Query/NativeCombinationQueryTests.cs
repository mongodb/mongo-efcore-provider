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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-323 end-to-end coverage of the canonical multi-key + paging native shape:
/// <c>Where(predicate).OrderBy(A).ThenByDescending(B).Skip(skip).Take(take)</c> (and the inverse
/// direction). Most existing native-gate coverage probes a single operator at a time; this exercises
/// the full filter → multi-key sort → skip → limit pipeline in one query.
/// <para>
/// The data is seeded with deliberately-tied primary sort keys so the secondary key is load-bearing:
/// if the native pipeline dropped or reordered the <c>ThenBy</c> stage the asserted order would change.
/// <c>skip</c>/<c>take</c> are captured variables, so they bind as query parameters (compiled-query path).
/// </para>
/// Each shape is asserted three ways: (1) under <see cref="MongoQueryMode.NativeOnly"/> it must succeed —
/// proving it went native rather than falling back — AND return the exact expected order; (2) under
/// <see cref="MongoQueryMode.Native"/> and (3) <see cref="MongoQueryMode.DriverLinq"/> it returns the
/// identical ordered sequence (parity).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCombinationQueryTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Item
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int A { get; set; }
        public int B { get; set; }
        public bool Active { get; set; }
    }

    // Seed rows where the primary key A is heavily tied, so ThenBy(B) decides the order within a group.
    // Labels are unique and used as the distinguishing field for ordered comparison.
    //
    // Active rows (Active == true), grouped by A then by B:
    //   A=1: B=30 "a1b30", B=10 "a1b10", B=20 "a1b20"
    //   A=2: B=15 "a2b15", B=5  "a2b5"
    //   A=3: B=7  "a3b7"
    // Plus inactive rows that the predicate must exclude.
    private IMongoCollection<Item> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);

        BsonDocument Row(string label, int a, int b, bool active) => new()
        {
            { "_id", ObjectId.GenerateNewId() }, { "Label", label }, { "A", a }, { "B", b }, { "Active", active }
        };

        // Insert in an order that is NOT the target sort order, so a missing sort stage would be detected.
        bson.InsertMany([
            Row("a1b10", 1, 10, true),
            Row("a2b5", 2, 5, true),
            Row("a1b30", 1, 30, true),
            Row("dead1", 1, 99, false), // excluded by predicate
            Row("a3b7", 3, 7, true),
            Row("a1b20", 1, 20, true),
            Row("a2b15", 2, 15, true),
            Row("dead2", 2, 99, false), // excluded by predicate
        ]);
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

    // ── Ascending-then-descending: OrderBy(A).ThenByDescending(B) ─────────────────────────────────

    [Fact]
    public void Asc_then_desc_with_paging_goes_native_with_correct_order_and_parity()
    {
        var collection = Seed(nameof(Asc_then_desc_with_paging_goes_native_with_correct_order_and_parity));

        // Full active set ordered by A asc, then B desc:
        //   A=1: B30,B20,B10 → a1b30, a1b20, a1b10
        //   A=2: B15,B5       → a2b15, a2b5
        //   A=3: B7           → a3b7
        // => [a1b30, a1b20, a1b10, a2b15, a2b5, a3b7]
        // Skip(1).Take(3) => [a1b20, a1b10, a2b15]
        var skip = 1;
        var take = 3;
        var expected = new[] { "a1b20", "a1b10", "a2b15" };

        List<string> RunAscDesc(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            return db.Entities
                .Where(x => x.Active)
                .OrderBy(x => x.A)
                .ThenByDescending(x => x.B)
                .Skip(skip)
                .Take(take)
                .ToList()
                .Select(x => x.Label)
                .ToList();
        }

        // (1) NativeOnly must succeed (goes native, no fallback) AND return the exact order.
        var nativeOnly = RunAscDesc(MongoQueryMode.NativeOnly);
        Assert.Equal(expected, nativeOnly);

        // (2)/(3) Parity: Native and DriverLinq return the same ordered results.
        var native = RunAscDesc(MongoQueryMode.Native);
        var driver = RunAscDesc(MongoQueryMode.DriverLinq);
        Assert.Equal(expected, native);
        Assert.Equal(expected, driver);
        Assert.Equal(driver, native);
        Assert.Equal(driver, nativeOnly);
    }

    // ── Descending-then-ascending: OrderByDescending(A).ThenBy(B) ─────────────────────────────────

    [Fact]
    public void Desc_then_asc_with_paging_goes_native_with_correct_order_and_parity()
    {
        var collection = Seed(nameof(Desc_then_asc_with_paging_goes_native_with_correct_order_and_parity));

        // Full active set ordered by A desc, then B asc:
        //   A=3: B7            → a3b7
        //   A=2: B5,B15        → a2b5, a2b15
        //   A=1: B10,B20,B30   → a1b10, a1b20, a1b30
        // => [a3b7, a2b5, a2b15, a1b10, a1b20, a1b30]
        // Skip(2).Take(3) => [a2b15, a1b10, a1b20]
        var skip = 2;
        var take = 3;
        var expected = new[] { "a2b15", "a1b10", "a1b20" };

        List<string> RunDescAsc(MongoQueryMode mode)
        {
            using var db = CreateContext(collection, mode);
            return db.Entities
                .Where(x => x.Active)
                .OrderByDescending(x => x.A)
                .ThenBy(x => x.B)
                .Skip(skip)
                .Take(take)
                .ToList()
                .Select(x => x.Label)
                .ToList();
        }

        var nativeOnly = RunDescAsc(MongoQueryMode.NativeOnly);
        Assert.Equal(expected, nativeOnly);

        var native = RunDescAsc(MongoQueryMode.Native);
        var driver = RunDescAsc(MongoQueryMode.DriverLinq);
        Assert.Equal(expected, native);
        Assert.Equal(expected, driver);
        Assert.Equal(driver, native);
        Assert.Equal(driver, nativeOnly);
    }
}
