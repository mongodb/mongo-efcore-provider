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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native field-to-field <c>StartsWith</c>/<c>Contains</c>/<c>EndsWith</c> (<c>c.A.StartsWith(c.B)</c>), the shape
/// behind Northwind's <c>All_top_level_column</c>. Renders via <c>$indexOfCP</c>/<c>$strLenCP</c>, matching
/// driver-LINQ for the un-negated form; negation renders as <c>$expr:{$not:[...]}</c> rather than the fallback's
/// <c>$nor:[{$expr:...}]</c> (logically identical).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeFieldToFieldRegexTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string A { get; set; } = "";
        public string B { get; set; } = "";
    }

    [Fact]
    public void StartsWith_with_matching_prefix_column()
        => AssertMatchesOracleAndDriverLinq(
            [new Row { A = "Hello world", B = "Hello" }, new Row { A = "Hello world", B = "world" }],
            r => r.A.StartsWith(r.B));

    [Fact]
    public void StartsWith_with_same_column_on_both_sides()
        => AssertMatchesOracleAndDriverLinq(
            [new Row { A = "Hello" }, new Row { A = "" }],
            r => r.A.StartsWith(r.A));

    [Fact]
    public void Contains_with_column_term()
        => AssertMatchesOracleAndDriverLinq(
            [new Row { A = "Hello world", B = "lo wo" }, new Row { A = "Hello world", B = "xyz" }],
            r => r.A.Contains(r.B));

    [Fact]
    public void EndsWith_with_column_term()
        => AssertMatchesOracleAndDriverLinq(
            [new Row { A = "Hello world", B = "world" }, new Row { A = "Hello world", B = "Hello" }],
            r => r.A.EndsWith(r.B));

    [Fact]
    public void Negated_starts_with_column_term()
        => AssertMatchesOracleAndDriverLinq(
            [new Row { A = "Hello world", B = "Hello" }, new Row { A = "Hello world", B = "xyz" }],
            r => !r.A.StartsWith(r.B));

    // Pins current behavior for a missing/null term column (the term is deliberately not $ifNull-guarded, for driver
    // parity; see RenderRegexAsExpr). The CLR oracle can't referee (StartsWith(null) throws), so compare against
    // driver-LINQ only: both throw the same server exception, neither returns wrong rows.
    [Fact]
    public void StartsWith_with_null_term_column_matches_driver_linq()
    {
        var collection = Seed([new Row { A = "Hello world", B = null! }]);
        Expression<Func<Row, bool>> predicate = r => r.A.StartsWith(r.B);

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeEx = Record.Exception(
            () => nativeOnly.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList());

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqEx = Record.Exception(
            () => driverLinq.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList());

        Assert.NotNull(nativeEx);
        Assert.NotNull(driverLinqEx);
        Assert.IsType<MongoCommandException>(nativeEx);
        Assert.IsType<MongoCommandException>(driverLinqEx);
        Assert.Equal(driverLinqEx.Message, nativeEx.Message);
    }

    // All(pred): the consumer of MongoExpressionNegator.TryNegate's field-to-field-regex exemption. Lowers to a
    // negated top-level complement, so it's asserted separately from the Where helper.
    [Fact]
    public void All_with_field_to_field_starts_with()
    {
        Row[] rows =
        [
            new Row { A = "Hello world", B = "Hello" },
            new Row { A = "Goodbye", B = "Hello" }
        ];
        var collection = Seed(rows);
        Expression<Func<Row, bool>> predicate = r => r.A.StartsWith(r.B);

        var oracle = rows.AsQueryable().All(predicate);

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().All(predicate);
        Assert.Equal(oracle, nativeOnlyResult);

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().All(predicate);
        Assert.Equal(oracle, driverLinqResult);
    }

    // `predicate` must be an Expression, not a Func, or it binds to Enumerable.Where and never runs natively.
    private void AssertMatchesOracleAndDriverLinq(Row[] rows, Expression<Func<Row, bool>> predicate)
    {
        var collection = Seed(rows);

        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().AsQueryable().Where(predicate).Select(r => r.Id).ToList();

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList();
        Assert.Equal(oracle.OrderBy(x => x), nativeOnlyResult.OrderBy(x => x));

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList();
        Assert.Equal(oracle.OrderBy(x => x), driverLinqResult.OrderBy(x => x));
    }

    private IMongoCollection<Row> Seed(Row[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]));
        collection.InsertMany(rows);
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
