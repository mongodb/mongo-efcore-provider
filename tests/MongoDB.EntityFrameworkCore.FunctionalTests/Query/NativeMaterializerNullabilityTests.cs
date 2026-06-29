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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-323 streaming-materializer nullability/missing regression tests. The streaming materializer
/// (<c>MongoStreamingEntityMaterializerRewriter</c>) must match the driver-LINQ entity-materialization
/// semantics — the oracle — for schema-drifted documents:
/// <list type="bullet">
///   <item>a document MISSING a required non-nullable scalar element → throw
///   <see cref="InvalidOperationException"/> (Bug 1, matching <c>BsonBinding</c>);</item>
///   <item>a document with an explicit BSON <c>null</c> on a non-nullable property → materialize
///   <c>default(T)</c> (Bug 2).</item>
/// </list>
/// Queries use <c>.ToList()</c> (<see cref="Microsoft.EntityFrameworkCore.Query.ResultCardinality.Enumerable"/>)
/// so the streaming materializer is genuinely exercised (scalar-cardinality operators such as
/// <c>.Single()</c> are never streaming-eligible and would silently take the DOM path). Each case is run
/// under <see cref="MongoQueryMode.Native"/> and <see cref="MongoQueryMode.DriverLinq"/> and asserted equal
/// (parity), and the flat-entity shape is confirmed to genuinely go native via <see cref="MongoQueryMode.NativeOnly"/>.
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

    // ── Bug 1: missing required non-nullable scalar → throw InvalidOperationException ──────────────

    [Fact]
    public void Missing_required_scalar_throws_under_native_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // Document is MISSING the required "Score" element entirely.
        raw.InsertOne(new BsonDocument { { "_id", ObjectId.GenerateNewId() } });

        // Driver-LINQ (the oracle) throws InvalidOperationException with a specific message.
        InvalidOperationException driverEx;
        using (var driver = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            driverEx = Assert.Throws<InvalidOperationException>(() => driver.Entities.ToList());
        }

        // Native (streaming) must throw the same type AND the same message (some spec tests assert it).
        InvalidOperationException nativeEx;
        using (var native = CreateContext(collection, MongoQueryMode.Native))
        {
            nativeEx = Assert.Throws<InvalidOperationException>(() => native.Entities.ToList());
        }

        Assert.Equal(driverEx.Message, nativeEx.Message);

        // And it must genuinely go native (not silently fall back to DOM).
        using (var nativeOnly = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            Assert.Throws<InvalidOperationException>(() => nativeOnly.Entities.ToList());
        }
    }

    // ── Bug 2: explicit BSON null on a non-nullable scalar → default(T) ───────────────────────────

    [Fact]
    public void Explicit_null_on_non_nullable_scalar_materializes_default_matching_driver()
    {
        var collection = database.CreateCollection<Scored>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // "Score" is present but explicitly BSON null.
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

    // Confirm the flat-entity Enumerable query genuinely goes streaming (NativeOnly succeeds → native path;
    // a fallback would throw NativeTranslationNotSupportedException). Locks in that Bug 2 is exercised on the
    // streaming materializer, not silently on the DOM path.
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

    // ── #3: present-but-null required is distinct from missing (this vs the Bug-1 test) ───────────

    // ── Nullable scalar present-but-null still works (sanity / no regression) ─────────────────────

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

    // ── Happy path: complete document materializes correctly under both modes ─────────────────────

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

    // ── Owned sub-property: missing required scalar on owned reference ────────────────────────────
    //
    // NOTE: at the EF-323 foundation an owned-reference query is NOT yet routed to the native/streaming
    // path (the gate marks it non-representable and falls back to driver-LINQ even under Native mode — see
    // Query/AGENTS.md "Reference Include is deferred"). So today these owned cases assert driver-LINQ↔native
    // PARITY while both run the DOM/driver path. The streaming materializer's recursion still applies the
    // same missing/null handling to owned sub-documents (RequiredPresence is built for every EntityPlan,
    // including owned children, and BuildFillLoop(child) enforces it) — so when owned-reference streaming is
    // enabled in a later sub-project these tests already pin the correct (parity) behavior.

    [Fact]
    public void Missing_required_scalar_on_owned_subdocument_matches_driver()
    {
        var collection = database.CreateCollection<WithOwned>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // Owned "Stats" sub-document is present but MISSING its required "Score".
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

        // Parity: native must match driver-LINQ — same throw-or-not, same type if thrown.
        Assert.Equal(driverEx?.GetType(), nativeEx?.GetType());
    }

    [Fact]
    public void Explicit_null_required_scalar_on_owned_subdocument_matches_driver()
    {
        var collection = database.CreateCollection<WithOwned>();
        var raw = database.GetCollection<BsonDocument>(collection.CollectionNamespace);
        // Owned "Stats.Score" present but explicit BSON null.
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
    }
}
