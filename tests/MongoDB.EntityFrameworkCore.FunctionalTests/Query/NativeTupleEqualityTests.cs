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
/// F9: tuple equality with a <see cref="Guid"/> (or other non-<c>BsonValue.Create</c>-safe) element used to throw
/// <see cref="ArgumentException"/> natively, because the constant/parameter element had no property to serialize
/// through. The element is now rebound to the property at the same position on the other side. Expected values
/// are main's (driver-LINQ) results.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeTupleEqualityTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Level { Low = 1, High = 10 }

    private static readonly Guid G1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid G2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public class Doc
    {
        public ObjectId Id { get; set; }
        public string Code { get; set; } = "";
        public Guid Gid { get; set; }
        public int N { get; set; }
        public Level Lvl { get; set; }
        public DateTime When { get; set; }
    }

    private IMongoCollection<Doc> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        BsonDocument D(string code, Guid g, int n, int lvl, DateTime w) => new()
        {
            {"_id", ObjectId.GenerateNewId()}, {"Code", code}, {"Gid", new BsonBinaryData(g, GuidRepresentation.Standard)},
            {"N", n}, {"Lvl", lvl}, {"When", w}
        };

        raw.InsertMany([D("abc", G1, 1, 10, when), D("def", G2, 1, 1, when), D("ghi", G1, 2, 10, when.AddDays(1))]);
        return database.MongoDatabase.GetCollection<Doc>(collectionName);
    }

    private static SingleEntityDbContext<Doc> CreateContext(IMongoCollection<Doc> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private void Check(string name, Func<IQueryable<Doc>, IQueryable<Doc>> where, params string[] expected)
    {
        var collection = Seed(name);
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return where(db.Entities.AsNoTracking()).OrderBy(x => x.Code).Select(x => x.Code).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void Tuple_with_guid_equals_captured_tuple_elements()
    {
        var g = G1;
        Check(nameof(Tuple_with_guid_equals_captured_tuple_elements),
            q => q.Where(d => new Tuple<Guid, int>(d.Gid, d.N) == new Tuple<Guid, int>(g, 1)), "abc");
    }

    [Fact]
    public void Tuple_with_guid_not_equals()
    {
        var g = G1;
        Check(nameof(Tuple_with_guid_not_equals),
            q => q.Where(d => new Tuple<Guid, int>(d.Gid, d.N) != new Tuple<Guid, int>(g, 1)), "def", "ghi");
    }

    [Fact]
    public void Tuple_with_guid_equals_constant_element()
        => Check(nameof(Tuple_with_guid_equals_constant_element),
            q => q.Where(d => new Tuple<Guid, int>(d.Gid, d.N) == new Tuple<Guid, int>(Guid.Parse("22222222-2222-2222-2222-222222222222"), 1)),
            "def");

    [Fact]
    public void Tuple_Create_with_guid_equals()
    {
        var g = G1;
        Check(nameof(Tuple_Create_with_guid_equals),
            q => q.Where(d => Tuple.Create(d.Gid, d.N) == Tuple.Create(g, 1)), "abc");
    }

    [Fact]
    public void Tuple_with_enum_element()
        => Check(nameof(Tuple_with_enum_element),
            q => q.Where(d => new Tuple<Level, int>(d.Lvl, d.N) == Tuple.Create(Level.High, 1)), "abc");

    [Fact]
    public void Tuple_with_datetime_element()
    {
        var w = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Check(nameof(Tuple_with_datetime_element),
            q => q.Where(d => new Tuple<DateTime, int>(d.When, d.N) == new Tuple<DateTime, int>(w, 1)), "abc", "def");
    }

    [Fact]
    public void Whole_tuple_parameter_with_guid_element()
    {
        var t = Tuple.Create(G1, 1);
        Check(nameof(Whole_tuple_parameter_with_guid_element),
            q => q.Where(d => new Tuple<Guid, int>(d.Gid, d.N) == t), "abc");
    }

    [Fact]
    public void Guid_element_against_computed_counterpart_declines_or_matches_driver()
    {
        var g = G1;
        var collection = Seed(nameof(Guid_element_against_computed_counterpart_declines_or_matches_driver));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = CreateContext(collection, mode);
            return db.Entities.AsNoTracking()
                .Where(d => new Tuple<Guid, int>(g, d.N + 0) == new Tuple<Guid, int>(g, 1))
                .OrderBy(d => d.Code).Select(d => d.Code).ToList();
        });
        Assert.Equal(["abc", "def"], result);
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Tuple_with_guid_equals_twice_with_different_values(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Tuple_with_guid_equals_twice_with_different_values) + mode);
        using var db = CreateContext(collection, mode);

        NativeModeAssert.TwiceWithDifferentValues(
            p =>
            {
                var g = (Guid)p!;
                return db.Entities.AsNoTracking().Where(d => new Tuple<Guid, int>(d.Gid, d.N) == new Tuple<Guid, int>(g, 1))
                    .OrderBy(d => d.Code).Select(d => d.Code).ToList();
            },
            G1, ["abc"],
            G2, ["def"]);
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Whole_tuple_parameter_twice_with_different_values(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Whole_tuple_parameter_twice_with_different_values) + mode);
        using var db = CreateContext(collection, mode);

        NativeModeAssert.TwiceWithDifferentValues(
            p =>
            {
                var t = (Tuple<Guid, int>)p!;
                return db.Entities.AsNoTracking().Where(d => new Tuple<Guid, int>(d.Gid, d.N) == t)
                    .OrderBy(d => d.Code).Select(d => d.Code).ToList();
            },
            Tuple.Create(G1, 1), ["abc"],
            Tuple.Create(G2, 1), ["def"]);
    }
}
