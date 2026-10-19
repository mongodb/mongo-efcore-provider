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
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// The streaming materializer (<c>MongoStreamingEntityMaterializerRewriter</c>) must match driver-LINQ for
/// schema-drifted documents: a missing required non-nullable scalar throws <see cref="InvalidOperationException"/>
/// (as <c>BsonBinding</c> does); an explicit BSON <c>null</c> on a non-nullable value property materializes
/// <c>default(T)</c>. Queries use <c>.ToList()</c> because scalar-cardinality operators such as <c>.Single()</c>
/// are never streaming-eligible and would silently take the DOM path.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeMaterializerNullabilityTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Scored
    {
        public ObjectId Id { get; set; }
        public int Score { get; set; }       // required non-nullable scalar
        public int? Bonus { get; set; }       // nullable scalar
    }

    private class WithOwned
    {
        public ObjectId Id { get; set; }
        public Stats Stats { get; set; } = null!;
    }

    private class Stats
    {
        public int Score { get; set; }        // required non-nullable scalar on owned sub-document
    }

    private class Named
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";      // required non-nullable REFERENCE scalar
        public List<int> Tags { get; set; } = [];   // required non-nullable REFERENCE scalar (collection)
    }

    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? model = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    [Fact]
    public void Missing_required_scalar_throws_under_native_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() } });

        InvalidOperationException driverEx;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => driver.Entities.ToList());
        }

        InvalidOperationException nativeEx;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => native.Entities.ToList());
        }

        Assert.Equal(driverEx.Message, nativeEx.Message);

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
        }
    }

    [Fact]
    public void Explicit_null_on_non_nullable_scalar_materializes_default_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Score", BsonNull.Value } });

        int driverScore;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverScore = driver.Entities.ToList().Single().Score;
        }

        int nativeScore;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeScore = native.Entities.ToList().Single().Score;
        }

        Assert.Equal(default, driverScore);
        Assert.Equal(driverScore, nativeScore);
    }

    [Fact]
    public void Explicit_null_on_non_nullable_scalar_goes_native()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Score", BsonNull.Value } });

        using var native = CreateContext(collection, MongoQueryMode.NativeOnly);
        var score = native.Entities.ToList().Single().Score;
        Assert.Equal(default, score);
    }

    [Fact]
    public void Explicit_null_on_nullable_scalar_materializes_null_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Score", 7 }, { "Bonus", BsonNull.Value } });

        int? driverBonus;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverBonus = driver.Entities.ToList().Single().Bonus;
        }

        int? nativeBonus;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeBonus = native.Entities.ToList().Single().Bonus;
        }

        Assert.Null(driverBonus);
        Assert.Equal(driverBonus, nativeBonus);
    }

    // Explicit BSON null on a non-nullable reference-typed scalar must throw, not leave a required member null.

    [Theory]
    [InlineData("Name")]
    [InlineData("Tags")]
    public void Explicit_null_on_non_nullable_reference_scalar_throws_under_native_matching_driver(string element)
    {
        var collection = database.CreateCollection<Named>(values: [element]);
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // Every other required property is valid, so the materializer reaches `element` rather than throwing
        // "missing" on a sibling first.
        var doc = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Name", "a name" },
            { "Tags", new BsonArray([1, 2, 3]) }
        };
        doc[element] = BsonNull.Value;
        raw.InsertOne(doc);

        InvalidOperationException driverEx;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => driver.Entities.ToList());
        }

        InvalidOperationException nativeEx;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => native.Entities.ToList());
        }

        Assert.Equal(driverEx.Message, nativeEx.Message);

        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            var nativeOnlyEx = Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
            Assert.Equal(driverEx.Message, nativeOnlyEx.Message);
        }
    }

    [Fact]
    public void Complete_document_materializes_correctly_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        collection.InsertOne(new Scored { Id = ObjectId.GenerateNewId(), Score = 42, Bonus = 5 });

        Scored driverEntity;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEntity = driver.Entities.ToList().Single();
        }

        Scored nativeEntity;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEntity = native.Entities.ToList().Single();
        }

        Assert.Equal(42, driverEntity.Score);
        Assert.Equal(driverEntity.Score, nativeEntity.Score);
        Assert.Equal(driverEntity.Bonus, nativeEntity.Bonus);
    }

    // Owned single-reference whole-entity queries go native, and the streaming materializer enforces RequiredPresence
    // for owned children too (BuildFillLoop(child)). Parity with driver-LINQ holds whichever path each side takes.

    [Fact]
    public void Missing_required_scalar_on_owned_subdocument_matches_driver()
    {
        var collection = database.CreateCollection<WithOwned>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Stats", new BsonDocument() }
        });

        Action<ModelBuilder> model = mb => mb.Entity<WithOwned>().OwnsOne(e => e.Stats);

        Exception? driverEx = Record.Exception(() =>
        {
            using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, model);
            _ = driver.Entities.ToList();
        });

        Exception? nativeEx = Record.Exception(() =>
        {
            using var native = CreateContext(collection, MongoQueryMode.Native, model);
            _ = native.Entities.ToList();
        });

        Assert.Equal(driverEx?.GetType(), nativeEx?.GetType());

        // Under NativeOnly this must throw InvalidOperationException from the streaming materializer, not NativeTranslationNotSupportedException.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
        }
    }

    [Fact]
    public void Explicit_null_required_scalar_on_owned_subdocument_matches_driver()
    {
        var collection = database.CreateCollection<WithOwned>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        raw.InsertOne(new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Stats", new BsonDocument { { "Score", BsonNull.Value } } }
        });

        Action<ModelBuilder> model = mb => mb.Entity<WithOwned>().OwnsOne(e => e.Stats);

        int? driverScore = null;
        Exception? driverEx = Record.Exception(() =>
        {
            using var driver = CreateContext(collection, MongoQueryMode.DriverLinq, model);
            driverScore = driver.Entities.ToList().Single().Stats.Score;
        });

        int? nativeScore = null;
        Exception? nativeEx = Record.Exception(() =>
        {
            using var native = CreateContext(collection, MongoQueryMode.Native, model);
            nativeScore = native.Entities.ToList().Single().Stats.Score;
        });

        Assert.Equal(driverEx?.GetType(), nativeEx?.GetType());
        Assert.Equal(driverScore, nativeScore);

        // Under NativeOnly a present-but-null required owned scalar materializes to default(T), matching the driver-LINQ oracle.
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly, model))
        {
            var nativeOnlyScore = nativeOnly.Entities.ToList().Single().Stats.Score;
            Assert.Equal(driverScore, nativeOnlyScore);
        }
    }
}
