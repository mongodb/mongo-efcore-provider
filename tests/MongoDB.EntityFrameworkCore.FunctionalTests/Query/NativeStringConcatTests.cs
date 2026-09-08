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
/// Native translation of compiler-generated string concatenation (<c>a + b</c>, <c>MongoConcatExpression</c> →
/// <c>$concat</c>). The int/string shape (<see cref="Concat_int_operand_matches_the_in_memory_oracle"/>) is the
/// one the EF spec suite's <c>Concat_int_string</c>/<c>Concat_string_int</c> exercise; every other case here is
/// a DIFFERENTIAL check against real server behavior for operand types whose MQL <c>$toString</c> rendering is
/// not guaranteed to match .NET's <c>object.ToString()</c> byte-for-byte.
/// </summary>
/// <remarks>
/// int/long/double/decimal/Guid all measure identically to the in-memory LINQ oracle
/// (<see cref="AssertConcatMatchesOracle"/>). bool and DateTime do NOT — MQL's <c>$toString</c> renders a bool
/// lowercase (<c>"true"</c>, .NET capitalizes: <c>"True"</c>) and a date as ISO-8601 (.NET's default format is
/// culture-dependent and does not match). This is NOT a defect this feature introduces: the driver's OWN LINQ
/// v3 bridge — the pre-existing, already-released fallback path, unmodified by this feature — ALSO renders
/// string-concat via <c>$concat</c>/<c>$toString</c> and so carries the IDENTICAL divergence from .NET already
/// (confirmed here by <see cref="AssertConcatMatchesDriverLinqAcceptedDivergence"/>, which asserts native ==
/// driver-LINQ while both differ from the in-memory oracle). Per this area's "measure or decline" discipline
/// (<c>MongoConvertExpression</c>'s remarks; <c>NativeCastTests</c>' own accepted-divergence case), the
/// correctness bar for admitting a native shape is agreement with the pre-existing driver-LINQ answer, not
/// agreement with CLR — declining bool/DateTime would buy nothing, since the fallback answers exactly as
/// "wrong" today. See <c>MongoExpressionTranslator.TranslateConcatOperand</c>'s remarks for the reasoning
/// pinned in the production code.
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

    // `selector` MUST be Expression<Func<...>>, never a plain Func delegate — a Func parameter here would bind
    // `IQueryable<Row>.Select(selector)` to Enumerable.Select (LINQ-to-Objects) instead of Queryable.Select
    // (server-translated), silently pulling every row into memory and computing client-side — exactly the
    // self-inflicted defect NativeCastTests' own remarks warn about, and the one this helper originally had
    // (it reported all-green while never exercising the native $concat path at all).
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

    // For bool/DateTime: native must go native under NativeOnly (proving it is admitted, not silently falling
    // back), must equal the driver-LINQ answer (the accepted-divergence bar), and that shared answer must
    // genuinely differ from the in-memory CLR oracle — confirming this is a real, measured, pre-existing
    // divergence and not a vacuous check.
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
