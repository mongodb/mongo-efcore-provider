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
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>&amp;</c> and <c>|</c> over two non-nullable bools translate natively as logical and/or; a <c>bool?</c>
/// operand (three-valued logic) declines. See <c>MongoExpressionTranslator.IsNonNullableBoolLogical</c>.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeBoolBitwiseTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Item
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public int A { get; set; }
        public int B { get; set; }
        public bool Flag { get; set; }
        public bool Other { get; set; }
        public bool? NullableFlag { get; set; }
    }

    public class ConvertedItem
    {
        public ObjectId Id { get; set; }
        public string Label { get; set; } = "";
        public bool Flag { get; set; }
        public bool Other { get; set; }
    }

    private IMongoCollection<Item> Seed(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        bson.InsertMany([
            Doc("r1", 1, 2, true, true, true),
            Doc("r2", 1, 5, true, false, false),
            Doc("r3", 4, 2, false, true, null),
            Doc("r4", 4, 5, false, false, true),
            Doc("r5", 0, 0, false, false, false),
        ]);
        return database.MongoDatabase.GetCollection<Item>(collectionName);

        static BsonDocument Doc(string label, int a, int b, bool flag, bool other, bool? nullable)
            => new()
            {
                { "_id", ObjectId.GenerateNewId() }, { "Label", label }, { "A", a }, { "B", b },
                { "Flag", flag }, { "Other", other }, { "NullableFlag", nullable is null ? BsonNull.Value : nullable.Value }
            };
    }

    private IMongoCollection<ConvertedItem> SeedConverted(string name)
    {
        var collectionName = TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];
        var bson = database.MongoDatabase.GetCollection<BsonDocument>(collectionName);
        // Flag is stored as "Y"/"N"; both strings are truthy, so a raw $and/$or would be wrong.
        bson.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "c1" }, { "Flag", "Y" }, { "Other", true } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "c2" }, { "Flag", "N" }, { "Other", true } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "c3" }, { "Flag", "N" }, { "Other", false } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Label", "c4" }, { "Flag", "Y" }, { "Other", false } },
        ]);
        return database.MongoDatabase.GetCollection<ConvertedItem>(collectionName);
    }

    private static void Configure(DbContextOptionsBuilder b, MongoQueryMode mode)
    {
        b.EnableSensitiveDataLogging()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
    }

    private static SingleEntityDbContext<Item> Create(IMongoCollection<Item> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(collection, optionsBuilderAction: b => Configure(b, mode));

    private static SingleEntityDbContext<ConvertedItem> CreateConverted(
        IMongoCollection<ConvertedItem> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: mb => mb.Entity<ConvertedItem>().Property(x => x.Flag)
                .HasConversion(v => v ? "Y" : "N", v => v == "Y"),
            optionsBuilderAction: b => Configure(b, mode));

    [Fact]
    public void Where_or_of_two_comparisons()
    {
        var collection = Seed(nameof(Where_or_of_two_comparisons));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.A == 1 | x.B == 2).Select(x => x.Label).OrderBy(l => l).ToList();
        });
        Assert.Equal(["r1", "r2", "r3"], result);
    }

    [Fact]
    public void Where_and_of_bare_bool_and_comparison()
    {
        var collection = Seed(nameof(Where_and_of_bare_bool_and_comparison));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Flag & x.A > 0).Select(x => x.Label).OrderBy(l => l).ToList();
        });
        Assert.Equal(["r1", "r2"], result);
    }

    [Fact]
    public void Select_or_of_two_bare_bools()
    {
        var collection = Seed(nameof(Select_or_of_two_bare_bools));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.Flag | x.Other).ToList();
        });
        Assert.Equal([true, true, true, false, false], result);
    }

    [Fact]
    public void Select_and_of_two_comparisons_in_an_anonymous_type()
    {
        var collection = Seed(nameof(Select_and_of_two_comparisons_in_an_anonymous_type));
        var result = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().OrderBy(x => x.Label)
                .Select(x => new { R = (x.A == 1) & (x.B == 2) }).ToList().Select(x => x.R).ToList();
        });
        Assert.Equal([true, false, false, false, false], result);
    }

    [Fact]
    public void Where_or_with_a_nullable_bool_operand_declines_cleanly()
    {
        var collection = Seed(nameof(Where_or_with_a_nullable_bool_operand_declines_cleanly));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().Where(x => (x.NullableFlag | x.Flag) == true)
                .Select(x => x.Label).OrderBy(l => l).ToList();
        });
        Assert.Equal(["r1", "r2", "r4"], result);
    }

    // Three-valued `|` / `&` over bool? differ from logical or/and (null | true == true, null & false == false), so a
    // projected value must also decline rather than render a two-valued $or/$and. Driver-LINQ has no oracle for
    // these projections (it throws on the bool? result serializer), so only the native decline is asserted.
    [Fact]
    public void Select_or_with_a_nullable_bool_operand_declines()
    {
        var collection = Seed(nameof(Select_or_with_a_nullable_bool_operand_declines));
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.NullableFlag | x.Flag).ToList());
    }

    [Fact]
    public void Select_and_with_a_nullable_bool_operand_declines()
    {
        var collection = Seed(nameof(Select_and_with_a_nullable_bool_operand_declines));
        using var db = Create(collection, MongoQueryMode.NativeOnly);
        Assert.Throws<NativeTranslationNotSupportedException>(() =>
            db.Entities.AsNoTracking().OrderBy(x => x.Label).Select(x => x.NullableFlag & x.Other).ToList());
    }

    // Operands that are non-null in practice but typed bool? via a Convert the translator unwraps: the node's own type
    // is bool?, so it must still decline (this is the shape that discriminates the operand-type check).
    [Fact]
    public void Where_or_of_a_bool_converted_to_nullable_bool_declines_cleanly()
    {
        var collection = Seed(nameof(Where_or_of_a_bool_converted_to_nullable_bool_declines_cleanly));
        var result = NativeModeAssert.DeclinesCleanly(mode =>
        {
            using var db = Create(collection, mode);
            return db.Entities.AsNoTracking().Where(x => (x.Flag | (bool?)x.Other) == true)
                .Select(x => x.Label).OrderBy(l => l).ToList();
        });
        Assert.Equal(["r1", "r2", "r3"], result);
    }

    // A value-converted bool must take the same route and give the same result as the `||` form.
    [Fact]
    public void Value_converted_bool_or_matches_the_logical_or_form()
    {
        var collection = SeedConverted(nameof(Value_converted_bool_or_matches_the_logical_or_form));
        var bitwise = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateConverted(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Flag | x.Other).Select(x => x.Label).OrderBy(l => l).ToList();
        });
        var logical = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateConverted(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Flag || x.Other).Select(x => x.Label).OrderBy(l => l).ToList();
        });
        Assert.Equal(["c1", "c2", "c4"], bitwise);
        Assert.Equal(logical, bitwise);
    }

    [Fact]
    public void Value_converted_bool_and_matches_the_logical_and_form()
    {
        var collection = SeedConverted(nameof(Value_converted_bool_and_matches_the_logical_and_form));
        var bitwise = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateConverted(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Flag & x.Other).Select(x => x.Label).OrderBy(l => l).ToList();
        });
        var logical = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateConverted(collection, mode);
            return db.Entities.AsNoTracking().Where(x => x.Flag && x.Other).Select(x => x.Label).OrderBy(l => l).ToList();
        });
        Assert.Equal(["c1"], bitwise);
        Assert.Equal(logical, bitwise);
    }
}
