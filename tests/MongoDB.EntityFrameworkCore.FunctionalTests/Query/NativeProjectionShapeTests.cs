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
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// Projection leaves the native binder now admits (a nested construction flattened into the enclosing projection,
/// <c>Mql.Field</c>, <c>Tuple.Create</c>) and the cases that must still decline: two members that share a last name at
/// different levels, and an <c>Mql.Field</c> that names no mapped element or reads a converted property.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeProjectionShapeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Row
    {
        public int Id { get; set; }
        public int A { get; set; }
        public int B { get; set; }
        public string Name { get; set; } = "";
        public int Coded { get; set; }
    }

    [Fact]
    public void A_nested_construction_with_a_computed_member_is_flattened_and_reads_correctly()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.AsNoTracking().OrderBy(r => r.Id)
                    .Select(r => new { r.Name, Inner = new { Sum = r.A + r.B, Label = r.Name } })
                    .AsEnumerable()
                    .Select(x => $"{x.Name}:{x.Inner.Sum}:{x.Inner.Label}")
                    .ToList();
            },
            ["one:3:one", "two:7:two"]);
    }

    [Fact]
    public void Two_members_sharing_a_last_name_at_different_levels_decline_the_projection()
    {
        var collection = Seed();
        List<string> Run(MongoQueryMode mode)
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => new { r.A, Inner = new { A = r.B + 1 } })
                .AsEnumerable()
                .Select(x => $"{x.A}:{x.Inner.A}")
                .ToList();
        }

        // Both leaves would be aliased "A"; the read side would rename one, so native declines and the fallback answers.
        Assert.Throws<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        Assert.Equal(["1:3", "3:5"], Run(MongoQueryMode.Native));
        Assert.Equal(["1:3", "3:5"], Run(MongoQueryMode.DriverLinq));
    }

    [Fact]
    public void Tuple_Create_goes_native_and_reads_each_item()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.AsNoTracking().OrderBy(r => r.Id)
                    .Select(r => Tuple.Create(r.Name, r.A, r.B))
                    .AsEnumerable()
                    .Select(t => $"{t.Item1}:{t.Item2}:{t.Item3}")
                    .ToList();
            },
            ["one:1:2", "two:3:4"]);
    }

    [Fact]
    public void Mql_Field_resolves_by_element_name_in_a_projection_and_a_predicate()
    {
        var collection = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(collection, mode);
                return db.Entities.AsNoTracking()
                    .Where(r => Mql.Field(r, "A", Int32Serializer.Instance) > 1)
                    .Select(r => new { r.Name, Doubled = Mql.Field(r, "B", Int32Serializer.Instance) * 2 })
                    .AsEnumerable()
                    .Select(x => $"{x.Name}:{x.Doubled}")
                    .ToList();
            },
            ["two:8"]);
    }

    [Fact]
    public void Mql_Field_naming_no_mapped_element_declines()
    {
        var collection = Seed();
        using var db = Create(collection, MongoQueryMode.NativeOnly);

        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(r => new { r.Name, V = Mql.Field(r, "NoSuchElement", Int32Serializer.Instance) }).ToList());
    }

    [Fact]
    public void Mql_Field_of_a_converted_property_declines()
    {
        var collection = Seed();
        using var db = SingleEntityDbContext.Create(
            collection,
            mb => mb.Entity<Row>().Property(r => r.Coded).HasConversion<string>(),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(MongoQueryMode.NativeOnly);
            });

        // The serializer argument is ignored, so a property whose stored form differs must not resolve through it.
        Assert.Throws<NativeTranslationNotSupportedException>(
            () => db.Entities.Select(r => new { r.Name, V = Mql.Field(r, "Coded", StringSerializer.Instance) }).ToList());
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
        var name = TemporaryDatabaseFixtureBase.CreateCollectionName("projshape") + Guid.NewGuid().ToString("N")[..8];
        database.MongoDatabase.GetCollection<BsonDocument>(name).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "A", 1 }, { "B", 2 }, { "Name", "one" }, { "Coded", 5 } },
            new BsonDocument { { "_id", 2 }, { "A", 3 }, { "B", 4 }, { "Name", "two" }, { "Coded", 6 } }
        ]);
        return database.MongoDatabase.GetCollection<Row>(name);
    }
}
