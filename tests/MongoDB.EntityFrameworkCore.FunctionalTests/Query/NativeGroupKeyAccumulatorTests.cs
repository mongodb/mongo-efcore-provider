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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An accumulator whose operand is the grouping key itself (<c>g.Max(e =&gt; g.Key)</c>), and ordering/paging over a keyed
/// <c>GroupBy(...).Select(...)</c> result (<c>.OrderBy(t =&gt; t.Key).Skip(1).Take(2)</c>), which resolve against the Select's
/// output aliases. The seed has a MISSING key (groups with the null key) and ties on the aggregate.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeGroupKeyAccumulatorTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public int Id { get; set; }
        public string? K { get; set; }
        public int B { get; set; }
    }

    [Fact]
    public void Max_over_a_string_key_yields_the_key_and_null_for_the_null_group()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.GroupBy(r => r.K)
                    .Select(g => new { g.Key, Max = g.Max(e => g.Key) })
                    .OrderBy(t => t.Key)
                    .AsEnumerable().Select(t => $"{t.Key ?? "-"}:{t.Max ?? "-"}").ToList();
            },
            ["-:-", "x:x", "y:y", "z:z"]);
    }

    [Fact]
    public void Sum_and_Min_over_an_int_key_use_the_key_per_row()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.GroupBy(r => r.B)
                    .Select(g => new { g.Key, Sum = g.Sum(e => g.Key), Min = g.Min(e => g.Key) })
                    .OrderBy(t => t.Key)
                    .AsEnumerable().Select(t => $"{t.Key}:{t.Sum}:{t.Min}").ToList();
            },
            ["1:2:1", "2:2:2", "3:12:3"]);
    }

    [Fact]
    public void A_composite_key_member_accumulates_through_its_part()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.GroupBy(r => new { r.K, r.B })
                    .Select(g => new { g.Key.B, Max = g.Max(e => g.Key.B) })
                    .OrderBy(t => t.B)
                    .AsEnumerable().Select(t => $"{t.B}:{t.Max}").ToList();
            },
            ["1:1", "2:2", "3:3", "3:3", "3:3"]);
    }

    [Fact]
    public void Ordering_and_paging_over_the_grouped_result_sort_by_the_output_alias()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                // Counts: y 3, x 2, null 1, z 1: descending by count, ties by key (null first), then skip 1 take 2.
                return db.Entities.GroupBy(r => r.K)
                    .Select(g => new { g.Key, N = g.Count() })
                    .OrderByDescending(t => t.N).ThenBy(t => t.Key)
                    .Skip(1).Take(2)
                    .AsEnumerable().Select(t => $"{t.Key ?? "-"}:{t.N}").ToList();
            },
            ["x:2", "-:1"]);
    }

    [Fact]
    public void Ordering_by_a_name_that_is_not_an_output_alias_declines()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);

        // `t.N * 2` is a computed key over the output, which has no alias: refused rather than resolved against the entity.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.GroupBy(r => r.K).Select(g => new { g.Key, N = g.Count() })
                .OrderBy(t => t.N * 2).ToList());
    }

    private static SingleEntityDbContext<Row> Create(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Row> Seed()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("grpkey") + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "K", "x" }, { "B", 1 } },
            new BsonDocument { { "_id", 2 }, { "K", "x" }, { "B", 1 } },
            new BsonDocument { { "_id", 3 }, { "K", "y" }, { "B", 2 } },
            new BsonDocument { { "_id", 4 }, { "K", "y" }, { "B", 3 } },
            new BsonDocument { { "_id", 5 }, { "K", "y" }, { "B", 3 } },
            // K is MISSING: a key part that groups as null.
            new BsonDocument { { "_id", 6 }, { "B", 3 } },
            new BsonDocument { { "_id", 7 }, { "K", "z" }, { "B", 3 } }
        ]);
        return database.MongoDatabase.GetCollection<Row>(name);
    }
}
