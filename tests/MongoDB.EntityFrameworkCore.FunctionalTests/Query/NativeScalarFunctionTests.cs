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
/// Scalar functions the native translator took over from driver-LINQ: integer bitwise operators (<c>$bitAnd</c> et al.,
/// null-propagating for <c>int?</c>), <c>CurrentCulture[IgnoreCase]</c> string matching (ordinal, as driver-LINQ does:
/// D-CULTURE) and <c>DateTime.ToString</c> (client-evaluated over the projected receiver: D-TOSTRING).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeScalarFunctionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public int Id { get; set; }
        public int? N { get; set; }
        public long L { get; set; }
        public string Name { get; set; } = "";
        public DateTime When { get; set; }
    }

    [Theory]
    [InlineData("and", new[] { 2, 3, -1, 0 })]
    [InlineData("or", new[] { 7, -1, -1, 13 })]
    [InlineData("xor", new[] { 3, -6, -1, 9 })]
    [InlineData("not", new[] { -7, 0, -1, -13 })]
    public void Bitwise_operators_over_a_nullable_int_propagate_null(string op, int[] expectedWithNullAsMinusOne)
    {
        if (!database.TestServer.SupportsBitwiseOperators)
        {
            return;
        }

        // A bitwise Select LEAF is deliberately not admitted natively (fail closed): NativeOnly refuses, Native falls back, and
        // both answers equal driver-LINQ. The predicate forms are native (next test).
        var collection = Seed();
        var results = NativeModeAssert.DeclinesCleanly(
            mode =>
            {
                using var db = Create(collection, mode);
                var q = db.Entities.AsNoTracking().OrderBy(r => r.Id);
                var values = op switch
                {
                    "and" => q.Select(r => r.N & 3).ToList(),
                    "or" => q.Select(r => r.N | 1).ToList(),
                    "xor" => q.Select(r => r.N ^ 5).ToList(),
                    _ => q.Select(r => ~r.N).ToList()
                };
                // Row 3 stores no N: the lifted operator answers null, shown here as the sentinel -1 after the null check.
                return values.Select((v, i) => i == 2 ? (v is null ? -1 : -99) : v!.Value).ToList();
            });

        Assert.Equal(expectedWithNullAsMinusOne, results);
    }

    [Fact]
    public void Bitwise_predicates_over_int_and_long_filter_the_stored_bits()
    {
        if (!database.TestServer.SupportsBitwiseOperators)
        {
            return;
        }

        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.AsNoTracking()
                    .Where(r => (r.Id & 2) == 2 || (r.L & 4L) == 4L && (r.Id ^ 1) == 5 || ~r.Id == -4)
                    .Select(r => r.Id).OrderBy(i => i).ToList();
            },
            // (Id & 2) == 2: ids 2,3; (L & 4) == 4 && (Id ^ 1) == 5: none (Id would be 4 -> L 12 & 4 == 4, 4 ^ 1 == 5: id 4); ~Id == -4: id 3.
            [2, 3, 4]);
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture, new[] { 1, 4 })]
    [InlineData(StringComparison.CurrentCultureIgnoreCase, new[] { 1, 2, 4 })]
    public void CurrentCulture_string_matching_is_ordinal(StringComparison comparison, int[] expected)
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var q = db.Entities.AsNoTracking();
                q = comparison == StringComparison.CurrentCulture
                    ? q.Where(r => r.Name.StartsWith("ab", StringComparison.CurrentCulture))
                    : q.Where(r => r.Name.StartsWith("ab", StringComparison.CurrentCultureIgnoreCase));
                return q
                    .Select(r => r.Id).OrderBy(i => i).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void Invariant_culture_string_matching_is_still_refused_natively()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(r => r.Name.StartsWith("ab", StringComparison.InvariantCulture)).ToList());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("format")]
    public void DateTime_ToString_is_evaluated_on_the_client_over_the_projected_receiver(string shape)
    {
        var collection = Seed();
        var expectedTimes = new[] { 12, 12, 12, 12 }.Select((h, i) => new DateTime(2024, 5, 6 + i, h, 8, 9, DateTimeKind.Utc)).ToList();
        string[] expected = shape == "default"
            ? [.. expectedTimes.Select(t => t.ToString())]
            : [.. expectedTimes.Select(t => t.ToString("yyyy-MM-dd HH:mm"))];

        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var q = db.Entities.AsNoTracking().OrderBy(r => r.Id);
                return shape == "default"
                    ? q.Select(r => r.When.ToString()).ToList()
                    : q.Select(r => r.When.ToString("yyyy-MM-dd HH:mm")).ToList();
            },
            [.. expected],
            // Driver-LINQ pushes down an ISO $toString and ignores the format (wrong on main).
            driverKnownWrong: true);
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
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("scalarfn") + Guid.NewGuid().ToString("N")[..8];
        var utc = (int day) => new DateTime(2024, 5, day, 12, 8, 9, DateTimeKind.Utc);
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "N", 6 }, { "L", 6L }, { "Name", "abc" }, { "When", utc(6) } },
            new BsonDocument { { "_id", 2 }, { "N", -1 }, { "L", 5L }, { "Name", "ABd" }, { "When", utc(7) } },
            // N is MISSING: the lifted bitwise operators answer null.
            new BsonDocument { { "_id", 3 }, { "L", 0L }, { "Name", "xab" }, { "When", utc(8) } },
            new BsonDocument { { "_id", 4 }, { "N", 12 }, { "L", 12L }, { "Name", "abz" }, { "When", utc(9) } }
        ]);
        return database.MongoDatabase.GetCollection<Row>(name);
    }
}
