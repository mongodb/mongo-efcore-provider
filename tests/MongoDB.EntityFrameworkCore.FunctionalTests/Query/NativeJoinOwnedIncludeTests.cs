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
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A whole-entity leaf of a join result whose entity owns a reference and a collection (<c>Join(...)</c> into
/// <c>new { owner, tag }</c>): the owned data rides in the staged whole document, so the owner's Include wrapper must not
/// decline the projection. Rows with a missing owned reference and missing/empty owned collections are included.
/// </summary>
public class NativeJoinOwnedIncludeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Car
    {
        public string Reg { get; set; } = "";
    }

    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Car? Main { get; set; }
        public List<Car> Spares { get; set; } = [];
    }

    public class Tag
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public string Label { get; set; } = "";
    }

    [Fact]
    public void A_joined_owner_materializes_its_owned_reference_and_collection_in_every_mode()
    {
        var (owners, tags) = Seed();
        var results = NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(owners, tags, mode);
                return db.Set<Owner>().Join(db.Set<Tag>(), o => o.Id, t => t.OwnerId, (o, t) => new { o, t })
                    .AsEnumerable()
                    .OrderBy(r => r.t.Label)
                    .Select(r => $"{r.t.Label}:{r.o.Name}:{r.o.Main?.Reg ?? "-"}:{string.Join('+', r.o.Spares.Select(s => s.Reg))}")
                    .ToList();
            },
            ["t1:A:TAXI:BUS+VAN", "t2:A:TAXI:BUS+VAN", "t3:B:-:", "t4:C:SOLO:"]);
        Assert.Equal(4, results.Count);
    }

    private SingleContext Create(string owners, string tags, MongoQueryMode mode)
    {
        var builder = new DbContextOptionsBuilder<SingleContext>()
            .UseMongoDB(
                database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new SingleContext(builder.Options, owners, tags);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class SingleContext(DbContextOptions<SingleContext> options, string owners, string tags) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Owner>(b =>
            {
                b.ToCollection(owners);
                b.OwnsOne(o => o.Main);
                b.OwnsMany(o => o.Spares);
            });
            mb.Entity<Tag>().ToCollection(tags);
        }
    }

    private (string Owners, string Tags) Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owners = TemporaryDatabaseFixtureBase.CreateCollectionName("joOwners") + suffix;
        var tags = TemporaryDatabaseFixtureBase.CreateCollectionName("joTags") + suffix;
        var a = ObjectId.GenerateNewId();
        var b = ObjectId.GenerateNewId();
        var c = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(owners).InsertMany(
        [
            new BsonDocument
            {
                { "_id", a }, { "Name", "A" }, { "Main", new BsonDocument("Reg", "TAXI") },
                { "Spares", new BsonArray { new BsonDocument("Reg", "BUS"), new BsonDocument("Reg", "VAN") } }
            },
            // Main and Spares missing.
            new BsonDocument { { "_id", b }, { "Name", "B" } },
            new BsonDocument
            {
                { "_id", c }, { "Name", "C" }, { "Main", new BsonDocument("Reg", "SOLO") }, { "Spares", new BsonArray() }
            }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(tags).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", a }, { "Label", "t1" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", a }, { "Label", "t2" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", b }, { "Label", "t3" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", c }, { "Label", "t4" } }
        ]);
        return (owners, tags);
    }
}
