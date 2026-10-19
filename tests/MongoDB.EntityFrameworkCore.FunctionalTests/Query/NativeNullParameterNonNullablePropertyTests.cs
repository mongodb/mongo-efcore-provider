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
/// F7: a null parameter / list element compared with a non-nullable value-type property used to reach the property's
/// serializer as null and throw <see cref="NullReferenceException"/> natively. Native now serializes it as BSON null,
/// matching driver-LINQ (<c>{field: null}</c> matches a missing/null field). Expected values are main's (driver-LINQ)
/// results, not C# semantics (see main-issues M8). The seed's row "c" is deliberately ill-formed for the non-nullable
/// Rank/Gid/When/Flag properties (they are missing).
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeNullParameterNonNullablePropertyTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public enum Level { Low = 1, High = 10 }

    public class Row
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public int Rank { get; set; }
        public Level Level { get; set; }
        public Guid Gid { get; set; }
        public DateTime When { get; set; }
        public bool Flag { get; set; }
        public long Big { get; set; }
    }

    private IMongoCollection<Row> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var raw = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        var when = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        raw.InsertMany(
        [
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Title", "a"}, {"Rank", 1}, {"Level", 2},
                {"Gid", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard)}, {"When", when},
                {"Flag", true}, {"Big", 1L}
            },
            new BsonDocument
            {
                {"_id", ObjectId.GenerateNewId()}, {"Title", "b"}, {"Rank", 2}, {"Level", 10},
                {"Gid", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard)}, {"When", when.AddDays(1)},
                {"Flag", false}, {"Big", 2L}
            },
            new BsonDocument {{"_id", ObjectId.GenerateNewId()}, {"Title", "c"}, {"Level", 1}, {"Big", 3L}}
        ]);

        return database.MongoDatabase.GetCollection<Row>(collectionName);
    }

    private static SingleEntityDbContext<Row> CreateContext(IMongoCollection<Row> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private void Check(string name, Func<IQueryable<Row>, IQueryable<Row>> where, params string[] expected)
    {
        var collection = Seed(name);
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = CreateContext(collection, mode);
                return where(db.Entities.AsNoTracking()).OrderBy(x => x.Title).Select(x => x.Title).ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void Rank_equals_null_int_parameter()
    {
        int? p = null;
        Check(nameof(Rank_equals_null_int_parameter), q => q.Where(x => x.Rank == p), "c");
    }

    [Fact]
    public void Rank_not_equals_null_int_parameter()
    {
        int? p = null;
        Check(nameof(Rank_not_equals_null_int_parameter), q => q.Where(x => x.Rank != p), "a", "b");
    }

    // Level is present in every document, so {Level: null} matches nothing; != matches everything.
    [Fact]
    public void Level_equals_null_enum_parameter()
    {
        Level? p = null;
        Check(nameof(Level_equals_null_enum_parameter), q => q.Where(x => x.Level == p));
    }

    [Fact]
    public void Level_not_equals_null_enum_parameter()
    {
        Level? p = null;
        Check(nameof(Level_not_equals_null_enum_parameter), q => q.Where(x => x.Level != p), "a", "b", "c");
    }

    [Fact]
    public void Guid_equals_null_guid_parameter()
    {
        Guid? p = null;
        Check(nameof(Guid_equals_null_guid_parameter), q => q.Where(x => x.Gid == p), "c");
    }

    [Fact]
    public void Guid_not_equals_null_guid_parameter()
    {
        Guid? p = null;
        Check(nameof(Guid_not_equals_null_guid_parameter), q => q.Where(x => x.Gid != p), "a", "b");
    }

    [Fact]
    public void DateTime_equals_null_parameter()
    {
        DateTime? p = null;
        Check(nameof(DateTime_equals_null_parameter), q => q.Where(x => x.When == p), "c");
    }

    [Fact]
    public void Bool_equals_null_parameter()
    {
        bool? p = null;
        Check(nameof(Bool_equals_null_parameter), q => q.Where(x => x.Flag == p), "c");
    }

    [Fact]
    public void Long_equals_null_parameter()
    {
        long? p = null;
        Check(nameof(Long_equals_null_parameter), q => q.Where(x => x.Big == p));
    }

    [Fact]
    public void Non_null_value_still_matches()
    {
        int? p = 2;
        Check(nameof(Non_null_value_still_matches), q => q.Where(x => x.Rank == p), "b");
    }

    [Fact]
    public void Null_parameter_in_disjunction_with_range()
    {
        int? min = null;
        Check(nameof(Null_parameter_in_disjunction_with_range), q => q.Where(x => min == null || x.Rank >= min),
            "a", "b", "c");
    }

    [Fact]
    public void Int_list_with_null_parameter()
    {
        var ids = new List<int?> {1, null};
        Check(nameof(Int_list_with_null_parameter), q => q.Where(x => ids.Contains(x.Rank)), "a", "c");
    }

    [Fact]
    public void Not_int_list_with_null_parameter()
    {
        var ids = new List<int?> {1, null};
        Check(nameof(Not_int_list_with_null_parameter), q => q.Where(x => !ids.Contains(x.Rank)), "b");
    }

    [Fact]
    public void Int_array_with_null_constant()
        => Check(nameof(Int_array_with_null_constant), q => q.Where(x => new int?[] {1, null}.Contains(x.Rank)), "a", "c");

    [Fact]
    public void Not_int_array_with_null_constant()
        => Check(nameof(Not_int_array_with_null_constant), q => q.Where(x => !new int?[] {1, null}.Contains(x.Rank)), "b");

    [Fact]
    public void Guid_list_with_null_parameter()
    {
        var ids = new List<Guid?> {Guid.Empty, null};
        Check(nameof(Guid_list_with_null_parameter), q => q.Where(x => ids.Contains(x.Gid)), "c");
    }

    // c has Level 1 (Low) and a missing-nothing; a/b are 2/10, so only c matches.
    [Fact]
    public void Enum_list_with_null_parameter()
    {
        var ls = new List<Level?> {Level.Low, null};
        Check(nameof(Enum_list_with_null_parameter), q => q.Where(x => ls.Contains(x.Level)), "c");
    }

    [Fact]
    public void DateTime_list_with_null_constant()
        => Check(nameof(DateTime_list_with_null_constant),
            q => q.Where(x => new DateTime?[] {null}.Contains(x.When)), "c");

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Rank_equals_parameter_twice_with_different_values(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Rank_equals_parameter_twice_with_different_values) + mode);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

        NativeModeAssert.TwiceWithDifferentValues(
            p =>
            {
                var value = (int?)p;
                return db.Entities.AsNoTracking().Where(x => x.Rank == value).OrderBy(x => x.Title)
                    .Select(x => x.Title).ToList();
            },
            null, ["c"],
            2, ["b"]);
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Rank_in_list_parameter_twice_with_different_values(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Rank_in_list_parameter_twice_with_different_values) + mode);
        using var db = SingleEntityDbContext.Create(
            collection,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

        NativeModeAssert.TwiceWithDifferentValues(
            p =>
            {
                var ids = (List<int?>)p!;
                return db.Entities.AsNoTracking().Where(x => ids.Contains(x.Rank)).OrderBy(x => x.Title)
                    .Select(x => x.Title).ToList();
            },
            new List<int?> {1, null}, ["a", "c"],
            new List<int?> {2}, ["b"]);
    }
}
