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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>CompareTo</c>/<c>T.Compare</c> over ordered non-string primitives folds to a relational comparison, and
/// <c>AddTicks</c> with a whole-millisecond constant renders <c>$dateAdd</c> (<c>MongoExpressionTranslator</c>).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeScalarCompareAndDateAddTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public int Id { get; set; }
        public long Big { get; set; }
        public double Dbl { get; set; }
        public decimal Dec { get; set; }
        public DateTime When { get; set; }
    }

    private static readonly DateTime Base = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("long_gt", new[] { 3 })]
    [InlineData("long_eq", new[] { 2 })]
    [InlineData("long_le", new[] { 1, 2 })]
    [InlineData("double_ge", new[] { 2, 3 })]
    [InlineData("decimal_lt", new[] { 1 })]
    [InlineData("date_static_gt", new[] { 3 })]
    [InlineData("date_instance_le", new[] { 1, 2 })]
    [InlineData("reversed_zero", new[] { 1 })]
    [InlineData("folds_to_all", new[] { 1, 2, 3 })]
    public void Compare_over_ordered_primitives_goes_native_and_matches(string shape, int[] expected)
    {
        var collection = Seed(nameof(Compare_over_ordered_primitives_goes_native_and_matches));
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                long l = 20;
                double d = 2.0;
                decimal m = 20m;
                var when = Base.AddDays(1);
                var q = db.Entities.AsNoTracking();
                q = shape switch
                {
                    "long_gt" => q.Where(x => x.Big.CompareTo(l) > 0),
                    "long_eq" => q.Where(x => x.Big.CompareTo(l) == 0),
                    "long_le" => q.Where(x => x.Big.CompareTo(l) <= 0),
                    "double_ge" => q.Where(x => x.Dbl.CompareTo(d) >= 0),
                    "decimal_lt" => q.Where(x => x.Dec.CompareTo(m) < 0),
                    "date_static_gt" => q.Where(x => DateTime.Compare(x.When, when) > 0),
                    "date_instance_le" => q.Where(x => x.When.CompareTo(when) <= 0),
                    "reversed_zero" => q.Where(x => 0 > x.Big.CompareTo(l)),
                    _ => q.Where(x => x.Big.CompareTo(l) >= -1)
                };
                return q.OrderBy(x => x.Id).Select(x => x.Id).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void AddTicks_whole_milliseconds_goes_native()
    {
        var collection = Seed(nameof(AddTicks_whole_milliseconds_goes_native));
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                var target = Base.AddDays(1).AddMilliseconds(2);
                return db.Entities.AsNoTracking()
                    .Where(x => x.When.AddTicks(20000L) == target)
                    .Select(x => x.Id).ToList();
            },
            [2]);
    }

    [Fact]
    public void AddTicks_sub_millisecond_or_parameter_declines()
    {
        var collection = Seed(nameof(AddTicks_sub_millisecond_or_parameter_declines));
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        long ticks = 10000;
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(x => x.When.AddTicks(5L) > Base).Select(x => x.Id).ToList());
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Where(x => x.When.AddTicks(ticks) > Base).Select(x => x.Id).ToList());
    }

    [Fact]
    public void Date_part_over_AddYears_and_AddMonths_goes_native()
    {
        var collection = Seed(nameof(Date_part_over_AddYears_and_AddMonths_goes_native));
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                // Row 3 is 2020-02-10: AddMonths(1).Month is 3, so only the January rows match.
                return db.Entities.AsNoTracking()
                    .Where(x => x.When.AddYears(-1).Year == 2019 && x.When.AddMonths(1).Month == 2)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToList();
            },
            [1, 2]);
    }

    private static SingleEntityDbContext<Row> Create(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private IMongoCollection<Row> Seed(string testName)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        using var db = Create(collection, MongoQueryMode.DriverLinq);
        db.Entities.AddRange(
            new Row { Id = 1, Big = 10, Dbl = 1.0, Dec = 10m, When = Base },
            new Row { Id = 2, Big = 20, Dbl = 2.0, Dec = 20m, When = Base.AddDays(1) },
            new Row { Id = 3, Big = 30, Dbl = 3.0, Dec = 30m, When = Base.AddDays(40) });
        db.SaveChanges();
        return collection;
    }
}
