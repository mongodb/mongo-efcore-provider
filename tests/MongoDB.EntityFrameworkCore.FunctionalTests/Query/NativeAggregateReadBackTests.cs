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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Native terminal <c>Min</c>/<c>Max</c> read the server's value back through the operand property's own serializer
/// (an enum, <c>DateOnly</c>, <c>char</c> are not the generic mapped BSON value), and a terminal <c>Sum</c> whose server
/// result doesn't fit the CLR result type throws <see cref="OverflowException"/> like LINQ's checked <c>Sum</c>
/// instead of wrapping. A default (string-stored) <c>TimeSpan</c> has no server ordering that matches the CLR one, so
/// <c>Min</c>/<c>Max</c> over it declines.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeAggregateReadBackTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Level { Low = 1, Mid = 2, High = 3 }

    public class AggDoc
    {
        public ObjectId Id { get; set; }
        public Level Level { get; set; }
        public TimeSpan Span { get; set; }
        public DateOnly Day { get; set; }
        public char Letter { get; set; }
        public int Count { get; set; }
        public int? NCount { get; set; }
        public long Big { get; set; }
    }

    private IMongoCollection<AggDoc> Seed(params AggDoc[] docs)
    {
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("agg") + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<AggDoc>(name);
        using var db = Create(collection, MongoQueryMode.DriverLinq);
        db.Entities.AddRange(docs);
        db.SaveChanges();
        return collection;
    }

    private static SingleEntityDbContext<AggDoc> Create(IMongoCollection<AggDoc> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private static AggDoc Doc(Level level, TimeSpan span, DateOnly day, char letter)
        => new() { Id = ObjectId.GenerateNewId(), Level = level, Span = span, Day = day, Letter = letter };

    private IMongoCollection<AggDoc> SeedValues()
        => Seed(
            Doc(Level.Mid, TimeSpan.FromSeconds(30), new DateOnly(2021, 1, 1), 'B'),
            Doc(Level.High, TimeSpan.FromMinutes(1), new DateOnly(2021, 1, 2), 'C'),
            Doc(Level.Low, TimeSpan.FromSeconds(5), new DateOnly(2020, 12, 31), 'A'));

    private static string Run(SingleEntityDbContext<AggDoc> db, string shape)
        => shape switch
        {
            "Max_enum" => db.Entities.Max(e => e.Level).ToString(),
            "Min_enum" => db.Entities.Min(e => e.Level).ToString(),
            "Select_Max_enum" => db.Entities.Select(e => e.Level).Max().ToString(),
            "Max_DateOnly" => db.Entities.Max(e => e.Day).ToString("O"),
            "Min_DateOnly" => db.Entities.Min(e => e.Day).ToString("O"),
            "Max_char" => db.Entities.Max(e => e.Letter).ToString(),
            "Min_char" => db.Entities.Min(e => e.Letter).ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

    [Theory]
    [InlineData("Max_enum", "High")]
    [InlineData("Min_enum", "Low")]
    [InlineData("Select_Max_enum", "High")]
    [InlineData("Max_DateOnly", "2021-01-02")]
    [InlineData("Min_DateOnly", "2020-12-31")]
    [InlineData("Max_char", "C")]
    [InlineData("Min_char", "A")]
    public void MinMax_reads_back_through_property_serializer(string shape, string expected)
    {
        var collection = SeedValues();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return new List<string> { Run(db, shape) };
            },
            [expected]);
    }

    [Fact]
    public void Max_over_default_TimeSpan_declines()
    {
        var collection = SeedValues();
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = Create(collection, mode);
            return new List<string> { db.Entities.Max(e => e.Span).ToString() };
        });
        Assert.Equal(["00:01:00"], result);
    }

    [Fact]
    public void Min_over_default_TimeSpan_declines()
    {
        var collection = SeedValues();
        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = Create(collection, mode);
            return new List<string> { db.Entities.Min(e => e.Span).ToString() };
        });
    }

    [Fact]
    public void Select_Max_over_default_TimeSpan_declines()
    {
        var collection = SeedValues();
        NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = Create(collection, mode);
            return new List<string> { db.Entities.Select(e => e.Span).Max().ToString() };
        });
    }

    private IMongoCollection<AggDoc> SeedOverflow()
        => Seed(
            new AggDoc { Id = ObjectId.GenerateNewId(), Count = int.MaxValue, NCount = int.MaxValue, Big = long.MaxValue },
            new AggDoc { Id = ObjectId.GenerateNewId(), Count = int.MaxValue, NCount = int.MaxValue, Big = long.MaxValue },
            new AggDoc { Id = ObjectId.GenerateNewId(), Count = 1, NCount = null, Big = 1 });

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Sum_int_overflow_throws_OverflowException(MongoQueryMode mode)
    {
        var collection = SeedOverflow();
        using var db = Create(collection, mode);
        Assert.Throws<OverflowException>(() => db.Entities.Sum(e => e.Count));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Sum_nullable_int_overflow_throws_OverflowException(MongoQueryMode mode)
    {
        var collection = SeedOverflow();
        using var db = Create(collection, mode);
        Assert.Throws<OverflowException>(() => db.Entities.Sum(e => e.NCount));
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Select_Sum_int_overflow_throws_OverflowException(MongoQueryMode mode)
    {
        var collection = SeedOverflow();
        using var db = Create(collection, mode);
        Assert.Throws<OverflowException>(() => db.Entities.Select(e => e.Count).Sum());
    }

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Sum_long_overflow_throws_OverflowException(MongoQueryMode mode)
    {
        var collection = SeedOverflow();
        using var db = Create(collection, mode);
        Assert.Throws<OverflowException>(() => db.Entities.Sum(e => e.Big));
    }
}
