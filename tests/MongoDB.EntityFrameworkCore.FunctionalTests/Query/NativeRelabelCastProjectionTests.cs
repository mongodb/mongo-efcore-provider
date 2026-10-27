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
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A projection leaf that only relabels the CLR type of a non-default-stored scalar (<c>(long)x.IntAsString</c>,
/// <c>(int?)x.IntAsString</c>) projects the bare stored field and is read through the property's serializer, then cast
/// (<c>NativeProjectionBinder.IsClientRelabelCast</c>). A narrowing cast would need the cast operator to run over the
/// stored string, so it declines.
/// </summary>
public class NativeRelabelCastProjectionTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public int Id { get; set; }

        [BsonRepresentation(BsonType.String)]
        public int IntAsString { get; set; }

        [BsonRepresentation(BsonType.String)]
        public long LongAsString { get; set; }
    }

    [Fact]
    public void Widening_cast_over_a_string_stored_int_goes_native_and_reads_the_number()
        => NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(Seed(), mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Id).Select(x => (long)x.IntAsString).ToList();
            },
            [7L, 12L]);

    [Fact]
    public void Nullable_lift_over_a_string_stored_int_goes_native_and_reads_the_number()
        => NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(Seed(), mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Id).Select(x => (int?)x.IntAsString).ToList();
            },
            [7, 12]);

    [Fact]
    public void Widening_cast_in_an_anonymous_member_goes_native()
        => NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(Seed(), mode);
                return db.Entities.AsNoTracking().OrderBy(x => x.Id)
                    .Select(x => new { Wide = (long)x.IntAsString, x.Id }).AsEnumerable()
                    .Select(x => $"{x.Id}:{x.Wide}").ToList();
            },
            ["1:7", "2:12"]);

    [Fact]
    public void Narrowing_cast_over_a_string_stored_long_declines_cleanly()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.OrderBy(x => x.Id).Select(x => (int)x.LongAsString).ToList());
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
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("relabel") + Guid.NewGuid().ToString("N")[..8];
        var collection = database.MongoDatabase.GetCollection<Row>(name);
        using var db = Create(collection, MongoQueryMode.DriverLinq);
        db.Entities.AddRange(
            new Row { Id = 1, IntAsString = 7, LongAsString = 70 },
            new Row { Id = 2, IntAsString = 12, LongAsString = 120 });
        db.SaveChanges();
        return collection;
    }
}
