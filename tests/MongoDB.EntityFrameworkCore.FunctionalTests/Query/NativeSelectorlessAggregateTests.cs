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
/// A selector-less Sum()/Min()/Max()/Average() reducing the value a preceding bare scalar Select projected
/// (<c>Select(e =&gt; e.Value).Sum()</c>) — EF's Northwind <c>*_with_no_arg</c> family. The aggregate reduces the
/// Select's own <c>$project</c> output alias.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeSelectorlessAggregateTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public ObjectId Id { get; set; }
        public int Value { get; set; }
        public int? NullableValue { get; set; }
    }

    private IMongoCollection<Row> Seed(string name, params (int Value, int? NullableValue)[] rows)
    {
        var collection = database.MongoDatabase.GetCollection<Row>(
            TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8]);
        if (rows.Length > 0)
        {
            collection.InsertMany(rows.Select(r => new Row
            {
                Id = ObjectId.GenerateNewId(), Value = r.Value, NullableValue = r.NullableValue
            }));
        }

        return collection;
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b =>
        {
            b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
            new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
        });

    private static readonly (int, int?)[] OneTwoThree = [(1, 10), (2, null), (3, 30)];

    private List<T> Run<T>(string name, (int, int?)[] rows, Func<IQueryable<Row>, T> query)
    {
        var collection = Seed(name, rows);
        return NativeModeAssert.NativeAndParity<T>(mode =>
        {
            using var db = CreateContext(collection, mode);
            return [query(db.Entities)];
        });
    }

    [Fact]
    public void Sum_over_bare_member_select_goes_native()
        => Assert.Equal([6], Run(nameof(Sum_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Sum()));

    [Fact]
    public void Min_over_bare_member_select_goes_native()
        => Assert.Equal([1], Run(nameof(Min_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Min()));

    [Fact]
    public void Max_over_bare_member_select_goes_native()
        => Assert.Equal([3], Run(nameof(Max_over_bare_member_select_goes_native), OneTwoThree, q => q.Select(e => e.Value).Max()));

    [Fact]
    public void Average_over_bare_member_select_goes_native()
        => Assert.Equal([2.0], Run(nameof(Average_over_bare_member_select_goes_native), OneTwoThree,
            q => q.Select(e => e.Value).Average()));

    [Fact]
    public void Sum_over_computed_select_goes_native()
        => Assert.Equal([12], Run(nameof(Sum_over_computed_select_goes_native), OneTwoThree, q => q.Select(e => e.Value * 2).Sum()));

    [Fact]
    public void Average_over_widening_cast_select_goes_native()
        => Assert.Equal([2.0], Run(nameof(Average_over_widening_cast_select_goes_native), OneTwoThree,
            q => q.Where(e => e.Value > 0).OrderBy(e => e.Value).Select(e => (long)e.Value).Average()));

    [Fact]
    public void Max_over_nullable_select_ignores_nulls()
        => Assert.Equal([30], Run(nameof(Max_over_nullable_select_ignores_nulls), OneTwoThree,
            q => q.Select(e => e.NullableValue).Max()));

    // Empty-input contract: Sum is 0, a nullable Min/Max/Average is null, a non-nullable one throws.
    [Fact]
    public void Sum_over_empty_nullable_cast_select_returns_zero()
        => Assert.Equal([0], Run(nameof(Sum_over_empty_nullable_cast_select_returns_zero), [],
            q => q.Select(e => (int?)e.Value).Sum()));

    // NativeOnly-only, no parity leg: the driver-LINQ fallback itself throws "Sequence contains no elements" for this
    // shape (its SingleFinalizer over an empty $group), so it cannot serve as the oracle. LINQ's contract is null.
    [Fact]
    public void Max_over_empty_nullable_select_returns_null()
    {
        var collection = Seed(nameof(Max_over_empty_nullable_select_returns_null));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Null(db.Entities.Select(e => e.NullableValue).Max());
    }

    [Fact]
    public void Min_over_empty_non_nullable_select_throws_under_NativeOnly()
    {
        var collection = Seed(nameof(Min_over_empty_non_nullable_select_throws_under_NativeOnly));
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<InvalidOperationException>(() => db.Entities.Select(e => e.Value).Min());
    }

    // The shaper does client work on top of the pushed-down value, so the aggregate must NOT reduce the raw field.
    private static int TimesTen(Row row) => row.Value * 10;

    [Fact]
    public void Aggregate_over_client_method_projection_declines()
    {
        var collection = Seed(nameof(Aggregate_over_client_method_projection_declines), OneTwoThree);
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() => db.Entities.Select(e => TimesTen(e)).Sum());
    }

    // A value-converted (string-stored) int must not be reduced on its stored form: $sum over strings is 0 and
    // $max over strings is lexicographic ("9" > "10").
    [Fact]
    public void Aggregate_over_value_converted_projection_is_not_computed_on_stored_values()
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Aggregate_over_value_converted_projection_is_not_computed_on_stored_values))
                   + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Value", "9" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Value", "10" } },
        ]);
        using var db = SingleEntityDbContext.Create(
            database.MongoDatabase.GetCollection<Row>(name),
            modelBuilderAction: m => m.Entity<Row>().Property(r => r.Value).HasConversion<string>(),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        AssertDeclinesOrEquals(19, () => db.Entities.Select(e => e.Value).Sum());
        AssertDeclinesOrEquals(10, () => db.Entities.Select(e => e.Value).Max());
    }

    private static void AssertDeclinesOrEquals(int expected, Func<int> query)
    {
        int? actual = null;
        var ex = Record.Exception(() => actual = query());
        if (ex is null)
            Assert.Equal(expected, actual);
        else
            Assert.IsType<NativeTranslationNotSupportedException>(ex);
    }
}
