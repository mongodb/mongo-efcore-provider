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

/// <summary>
/// Native <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> over a <i>computed</i> string receiver
/// (<c>(x.A + "").Contains("1")</c>, <c>(x.S ?? "z").StartsWith("a")</c>, <c>x.U.ToString().Contains("7")</c>).
/// A computed receiver has no query-dialect form, so it must always render via <c>$expr</c>/<c>$regexMatch</c>;
/// the query-dialect <c>{ path: /re/ }</c> form would match nothing. Also covers the unsigned/sbyte integral
/// <c>ToString()</c> receivers.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeComputedReceiverRegexTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public string? S { get; set; }
        public string? S2 { get; set; }
        public uint U { get; set; }
        public ushort US { get; set; }
        public sbyte SB { get; set; }
        public ulong UL { get; set; }
    }

    private static Row[] Rows() =>
    [
        new() { A = 1, S = "abc", S2 = null, U = 7, US = 17, SB = -7, UL = 70 },
        new() { A = 21, S = null, S2 = "ab", U = 12, US = 12, SB = 12, UL = 12 },
        new() { A = 3, S = "x1", S2 = "q", U = 2000000007, US = 65507, SB = -128, UL = 17 },
        new() { A = 40, S = null, S2 = null, U = int.MaxValue, US = ushort.MaxValue, SB = sbyte.MaxValue, UL = 0 },
    ];

    [Fact]
    public void Contains_over_int_concat_receiver()
        => AssertWhere(x => (x.A + "").Contains("1"));

    [Fact]
    public void StartsWith_over_coalesce_receiver()
        => AssertWhere(x => (x.S ?? "z").StartsWith("a"));

    // `x.S + "q"` over a null S: C# gives "q", and so does native ($concat coalesces each operand to ""), but
    // driver-LINQ's bare $concat yields null and its EndsWith then fails server-side ("$strLenCP requires a string
    // argument, found: null"). Driver-LINQ is no oracle here, so these compare NativeOnly with the CLR oracle.
    [Fact]
    public void Negated_EndsWith_over_concat_receiver()
        => AssertWhereAgainstClrOracle(x => !(x.S + "q").EndsWith("q1"));

    [Fact]
    public void Negated_EndsWith_over_concat_receiver_that_discriminates()
        => AssertWhereAgainstClrOracle(x => !(x.S + "q").EndsWith("1q"));

    [Fact]
    public void EndsWith_over_concat_receiver()
        => AssertWhereAgainstClrOracle(x => (x.S + "q").EndsWith("1q"));

    [Fact]
    public void Contains_over_uint_ToString_receiver()
        => AssertWhere(x => x.U.ToString().Contains("7"));

    [Fact]
    public void Contains_over_ushort_ToString_receiver()
        => AssertWhere(x => x.US.ToString().Contains("7"));

    [Fact]
    public void StartsWith_over_sbyte_ToString_receiver()
        => AssertWhere(x => x.SB.ToString().StartsWith("-"));

    [Fact]
    public void Contains_over_ulong_ToString_receiver()
        => AssertWhere(x => x.UL.ToString().Contains("7"));

    // The spec shape behind Northwind's Ternary_* tests: a predicate over a projected computed string.
    [Fact]
    public void FirstOrDefault_Contains_over_projected_conditional_concat()
    {
        var collection = Seed(Rows());
        var result = NativeModeAssert.NativeAndParity<string?>(mode =>
        {
            using var db = CreateContext(collection, mode);
            return
            [
                db.Entities.AsNoTracking().OrderBy(x => x.Id)
                    .Select(x => x != null ? x.A + "" : null).FirstOrDefault(s => s!.Contains("1"))
            ];
        });
        Assert.Equal("1", Assert.Single(result));
    }

    // Null receiver: `x.S + x.S2` with both null is "" in C# and in native ($concat coalesces each operand to ""),
    // so it never matches, like the existing field receiver over a null field (x.S.StartsWith("a"), below). Driver-
    // LINQ is no oracle: its bare $concat is null when either operand is null, so it also drops "abc" + null and
    // null + "ab", which C# matches. NativeOnly is compared with the CLR oracle instead.
    [Fact]
    public void StartsWith_over_concat_of_null_fields()
    {
        var rows = Rows();
        var collection = Seed(rows);

        var computed = Query(collection, MongoQueryMode.NativeOnly, x => (x.S + x.S2).StartsWith("a"));
        Assert.Equal(Oracle(rows, x => (x.S + x.S2).StartsWith("a")), computed);
        Assert.Equal([rows[0].Id, rows[1].Id], computed); // "abc" and null + "ab"; both-null rows are excluded

        // The existing field-receiver behavior for a null field: no match, same as the computed receiver.
        var field = NativeModeAssert.NativeAndParity(mode => Query(collection, mode, x => x.S!.StartsWith("a")));
        Assert.Equal([rows[0].Id], field);
    }

    // A receiver that really is null (`x.S ?? x.S2`, both null; C# would throw). $regexMatch over a null input is
    // false, as the query-dialect regex is over a null field, and the negated form is true, as
    // { S: { $not: /^a/ } } is for a null S. Pinned against the field receiver and driver-LINQ.
    [Fact]
    public void Null_valued_computed_receiver_matches_field_receiver_over_a_null_field()
    {
        var rows = Rows();
        var collection = Seed(rows);
        var bothNull = rows[3].Id;

        var computed = NativeModeAssert.NativeAndParity(mode => Query(collection, mode, x => (x.S ?? x.S2)!.StartsWith("a")));
        var field = NativeModeAssert.NativeAndParity(mode => Query(collection, mode, x => x.S!.StartsWith("a")));
        Assert.DoesNotContain(bothNull, computed);
        Assert.DoesNotContain(bothNull, field);

        var negatedComputed = NativeModeAssert.NativeAndParity(
            mode => Query(collection, mode, x => !(x.S ?? x.S2)!.StartsWith("a")));
        var negatedField = NativeModeAssert.NativeAndParity(mode => Query(collection, mode, x => !x.S!.StartsWith("a")));
        Assert.Contains(bothNull, negatedComputed);
        Assert.Contains(bothNull, negatedField);
    }

    // ulong above long.MaxValue: EF's default UInt64Serializer (Int64 representation, no overflow allowed) refuses
    // to store it, so every stored ulong is exactly representable and $toString over it matches .NET.
    [Fact]
    public void Ulong_above_long_MaxValue_is_not_storable_so_ToString_is_exact()
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]));
        using var db = CreateContext(collection, MongoQueryMode.Native);
        db.Entities.Add(new Row { UL = (ulong)long.MaxValue + 7 });
        var ex = Record.Exception(() => db.SaveChanges());
        Assert.IsType<OverflowException>(ex);
        Assert.Empty(database.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToList());
    }

    // Likewise uint above int.MaxValue (UInt32Serializer: Int32 representation, no overflow allowed).
    [Fact]
    public void Uint_above_int_MaxValue_is_not_storable_so_ToString_is_exact()
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]));
        using var db = CreateContext(collection, MongoQueryMode.Native);
        db.Entities.Add(new Row { U = (uint)int.MaxValue + 7 });
        var ex = Record.Exception(() => db.SaveChanges());
        Assert.IsType<OverflowException>(ex);
        Assert.Empty(database.MongoDatabase.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToList());
    }

    // The largest storable unsigned values: $toString renders them exactly.
    [Fact]
    public void Unsigned_ToString_at_the_storable_maximum()
    {
        var collection = Seed([new Row { U = int.MaxValue, UL = long.MaxValue }, new Row { U = 5, UL = 5 }]);
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Where(x => x.U.ToString().EndsWith("647") && x.UL.ToString().EndsWith("807"))
                .Select(x => x.U).ToList();
        });
        Assert.Equal([(uint)int.MaxValue], result);
    }

    private void AssertWhere(Expression<Func<Row, bool>> predicate)
    {
        var rows = Rows();
        var collection = Seed(rows);
        var result = NativeModeAssert.NativeAndParity(mode => Query(collection, mode, predicate));
        Assert.Equal(Oracle(rows, predicate), result);
    }

    private void AssertWhereAgainstClrOracle(Expression<Func<Row, bool>> predicate)
    {
        var rows = Rows();
        var collection = Seed(rows);
        Assert.Equal(Oracle(rows, predicate), Query(collection, MongoQueryMode.NativeOnly, predicate));
    }

    private static List<ObjectId> Oracle(Row[] rows, Expression<Func<Row, bool>> predicate)
        => rows.AsQueryable().Where(predicate).Select(r => r.Id).OrderBy(x => x).ToList();

    private static List<ObjectId> Query(IMongoCollection<Row> collection, MongoQueryMode mode, Expression<Func<Row, bool>> predicate)
    {
        using var db = CreateContext(collection, mode);
        return db.Entities.AsNoTracking().Where(predicate).Select(r => r.Id).ToList().OrderBy(x => x).ToList();
    }

    // Seeded through EF so the provider's own serializers (not the driver's class map) decide the stored shape.
    private IMongoCollection<Row> Seed(Row[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(Guid.NewGuid().ToString("N")[..8]));
        using var db = CreateContext(collection, MongoQueryMode.Native);
        db.Entities.AddRange(rows);
        db.SaveChanges();
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
