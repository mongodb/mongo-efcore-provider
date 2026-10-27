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
/// A top-level collection <c>Include</c> whose target is a derived TPH type: the <c>$lookup</c> sub-pipeline narrows to
/// the derived type's discriminator values
/// (<c>LookupPipelineKind.DiscriminatorNarrowed</c>), so the sibling type that shares the foreign key never leaks in.
/// </summary>
public class NativeTphIncludeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Owner
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<PriorityRow> Priority { get; set; } = [];
    }

    public class Row
    {
        public ObjectId Id { get; set; }
        public ObjectId OwnerId { get; set; }
        public string Label { get; set; } = "";
    }

    public class PriorityRow : Row
    {
        public int Level { get; set; }
    }

    private sealed class FreshModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;

        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class TphContext(
        TemporaryDatabaseFixture database, string owners, string rows, MongoQueryMode mode, bool convertedDiscriminator)
        : DbContext(new DbContextOptionsBuilder<TphContext>()
            .UseMongoDB(
                database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, FreshModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options)
    {
        public DbSet<Owner> Owners { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>(b =>
            {
                b.ToCollection(owners);
                b.HasMany(o => o.Priority).WithOne().HasForeignKey(r => r.OwnerId);
            });

            modelBuilder.Entity<Row>(b =>
            {
                b.ToCollection(rows);
                if (convertedDiscriminator)
                {
                    // Stored as 101 / 102: the narrowing $in must carry the stored form, not the model value.
                    b.HasDiscriminator<int>("_t").HasValue<Row>(1).HasValue<PriorityRow>(2);
                    b.Property<int>("_t").HasConversion(v => v + 100, v => v - 100);
                }
                else
                {
                    b.HasDiscriminator<string>("_t").HasValue<Row>("row").HasValue<PriorityRow>("prio");
                }
            });
        }
    }

    private (string Owners, string Rows) Seed(bool converted)
    {
        var owners = TemporaryDatabaseFixtureBase.CreateCollectionName("tphowners") + Guid.NewGuid().ToString("N")[..8];
        var rows = TemporaryDatabaseFixtureBase.CreateCollectionName("tphrows") + Guid.NewGuid().ToString("N")[..8];
        var ownerId = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(owners).InsertOne(new BsonDocument { { "_id", ownerId }, { "Name", "o" } });

        BsonValue Row() => converted ? 101 : "row";
        BsonValue Prio() => converted ? 102 : "prio";
        database.MongoDatabase.GetCollection<BsonDocument>(rows).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", ownerId }, { "Label", "plain" }, { "_t", Row() } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", ownerId }, { "Label", "p1" }, { "_t", Prio() }, { "Level", 1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", ownerId }, { "Label", "p2" }, { "_t", Prio() }, { "Level", 2 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "OwnerId", ownerId }, { "Label", "p3" }, { "_t", Prio() }, { "Level", 3 } }
        ]);
        return (owners, rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Include_of_a_derived_target_excludes_the_sibling_type(bool converted)
    {
        var (owners, rows) = Seed(converted);
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = new TphContext(database, owners, rows, mode, converted);
                var owner = db.Owners.AsNoTracking().Include(o => o.Priority).Single();
                return owner.Priority.Select(p => $"{p.Label}:{p.Level}").OrderBy(x => x).ToList();
            },
            ["p1:1", "p2:2", "p3:3"]);
    }
}
