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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native translation of string concatenation (<c>a + b</c> → <c>$concat</c>), checked against real server
/// behavior for operand types whose <c>$toString</c> may not match .NET's <c>ToString()</c>.
/// </summary>
/// <remarks>
/// int/long/double/decimal/Guid match the in-memory oracle. bool and DateTime don't (<c>$toString</c> gives
/// <c>"true"</c> and ISO-8601), but the driver-LINQ fallback renders them identically, so the bar is agreement
/// with driver-LINQ (<see cref="AssertConcatMatchesDriverLinqAcceptedDivergence"/>). See
/// <c>MongoExpressionTranslator.TranslateConcatOperand</c>.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeStringConcatTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public string Tag { get; set; } = "";
        public int I { get; set; } = 42;
        public long L { get; set; } = 9_000_000_000L;
        public double D { get; set; } = 1.5;
        public decimal M { get; set; } = 1.50m;
        public bool B { get; set; } = true;
        public DateTime Dt { get; set; } = new(2024, 3, 5, 1, 2, 3, DateTimeKind.Utc);
        public Guid G { get; set; } = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    }

    [Fact]
    public void Concat_int_operand_matches_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_int_operand_matches_the_in_memory_oracle));
        AssertConcatMatchesOracle(collection, x => x.Tag + x.I);
    }

    [Fact]
    public void Concat_long_operand_matches_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_long_operand_matches_the_in_memory_oracle));
        AssertConcatMatchesOracle(collection, x => x.Tag + x.L);
    }

    [Fact]
    public void Concat_double_operand_matches_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_double_operand_matches_the_in_memory_oracle));
        AssertConcatMatchesOracle(collection, x => x.Tag + x.D);
    }

    [Fact]
    public void Concat_decimal_operand_matches_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_decimal_operand_matches_the_in_memory_oracle));
        AssertConcatMatchesOracle(collection, x => x.Tag + x.M);
    }

    [Fact]
    public void Concat_Guid_operand_matches_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_Guid_operand_matches_the_in_memory_oracle));
        AssertConcatMatchesOracle(collection, x => x.Tag + x.G);
    }

    [Fact]
    public void Concat_DateTime_operand_matches_driver_linq_but_not_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_DateTime_operand_matches_driver_linq_but_not_the_in_memory_oracle));
        AssertConcatMatchesDriverLinqAcceptedDivergence(collection, x => x.Tag + x.Dt);
    }

    [Fact]
    public void Concat_bool_operand_matches_driver_linq_but_not_the_in_memory_oracle()
    {
        var collection = Seed(nameof(Concat_bool_operand_matches_driver_linq_but_not_the_in_memory_oracle));
        AssertConcatMatchesDriverLinqAcceptedDivergence(collection, x => x.Tag + x.B);
    }

    // `selector` must be Expression<Func<...>>, not Func: a Func binds Select to Enumerable.Select, silently
    // evaluating client-side and never exercising the native $concat path.
    private static void AssertConcatMatchesOracle(IMongoCollection<Row> collection, Expression<Func<Row, string>> selector)
    {
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().Select(selector.Compile()).ToList();

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Equal(oracle, nativeOnly.Entities.AsNoTracking().Select(selector).ToList());

        using var native = CreateContext(collection, MongoQueryMode.Native);
        Assert.Equal(oracle, native.Entities.AsNoTracking().Select(selector).ToList());

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        Assert.Equal(oracle, driverLinq.Entities.AsNoTracking().Select(selector).ToList());
    }

    // bool/DateTime: must go native under NativeOnly, equal the driver-LINQ answer, and differ from the CLR
    // oracle (so the check isn't vacuous).
    private static void AssertConcatMatchesDriverLinqAcceptedDivergence(
        IMongoCollection<Row> collection, Expression<Func<Row, string>> selector)
    {
        using var oracleDb = CreateContext(collection, MongoQueryMode.Native);
        var oracle = oracleDb.Entities.AsNoTracking().ToList().Select(selector.Compile()).ToList();

        using var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly);
        var nativeOnlyResult = nativeOnly.Entities.AsNoTracking().Select(selector).ToList();

        using var driverLinq = CreateContext(collection, MongoQueryMode.DriverLinq);
        var driverLinqResult = driverLinq.Entities.AsNoTracking().Select(selector).ToList();

        Assert.Equal(driverLinqResult, nativeOnlyResult);
        Assert.NotEqual(oracle, nativeOnlyResult);
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(UniqueCollectionName(name));
        collection.InsertOne(new Row());
        return collection;
    }

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });
}
