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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>string.Length</c> renders as <c>$strLenCP</c>, which counts Unicode code points; .NET counts UTF-16 code
/// units. A surrogate pair (an emoji) is therefore length 1 on the server and 2 in C#. This is an accepted
/// divergence from the CLR, shared with driver-LINQ (which also renders <c>$strLenCP</c>), so native and
/// driver-LINQ agree with each other and not with in-memory LINQ for such strings. BMP-only strings (including
/// non-ASCII ones) agree everywhere.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeStringLengthTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public string S { get; set; } = "";
    }

    // "a😀" is 2 code points but 3 UTF-16 code units (😀 is U+1F600, a surrogate pair). "é中" is 2 of each.
    private static readonly (string Label, string S)[] Rows =
    [
        ("ascii", "abc"),
        ("bmp", "é中"),
        ("emoji", "a\U0001F600")
    ];

    [Fact]
    public void Length_counts_code_points_so_a_surrogate_pair_is_one_like_driver_linq_not_two_like_the_CLR()
    {
        var collection = Seed(nameof(Length_counts_code_points_so_a_surrogate_pair_is_one_like_driver_linq_not_two_like_the_CLR));

        // Premise: the CLR counts UTF-16 code units.
        Assert.Equal(3, "a\U0001F600".Length);

        using var oracle = CreateContext(collection, MongoQueryMode.Native);
        Assert.Equal(
            ["ascii", "emoji"],
            oracle.Entities.AsNoTracking().ToList().Where(x => x.S.Length == 3).Select(x => x.Label).Order().ToList());

        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = CreateContext(collection, mode);

            // Filter: the emoji row is length 2 on the server, so it matches == 2 and not == 3.
            Assert.Equal(
                ["ascii"],
                db.Entities.AsNoTracking().Where(x => x.S.Length == 3).Select(x => x.Label).ToList().Order().ToList());
            Assert.Equal(
                ["bmp", "emoji"],
                db.Entities.AsNoTracking().Where(x => x.S.Length == 2).Select(x => x.Label).ToList().Order().ToList());

            // Projection: same count. A projected Length isn't native yet (it takes the driver-LINQ path under
            // Native), so NativeOnly is skipped here; the filter legs above are the native proof.
            if (mode == MongoQueryMode.NativeOnly)
                continue;

            Assert.Equal(
                new Dictionary<string, int> { ["ascii"] = 3, ["bmp"] = 2, ["emoji"] = 2 },
                db.Entities.AsNoTracking().Select(x => new { x.Label, x.S.Length }).ToList()
                    .ToDictionary(x => x.Label, x => x.Length));
        }
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        collection.InsertMany(Rows.Select(r => new Row { Label = r.Label, S = r.S }));
        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
