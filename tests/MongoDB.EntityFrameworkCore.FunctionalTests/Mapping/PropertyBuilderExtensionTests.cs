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

using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Mapping;

[XUnitCollection("MappingTests")]
public class PropertyBuilderExtensionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class VectorEntity
    {
        public ObjectId _id { get; set; }
        public byte[] embedding { get; set; }
    }

    [Fact]
    public void HasBinaryVectorDataType_sets_annotation_and_round_trips()
    {
        var collection = database.CreateCollection<VectorEntity>();
        var vectorData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<VectorEntity>()
                .Property(e => e.embedding)
                .HasBinaryVectorDataType(BinaryVectorDataType.Int8);
        }))
        {
            db.Entities.Add(new VectorEntity { embedding = vectorData });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<VectorEntity>()
                .Property(e => e.embedding)
                .HasBinaryVectorDataType(BinaryVectorDataType.Int8);
        }))
        {
            var result = db.Entities.First();
            Assert.Equal(vectorData, result.embedding);
        }
    }

    [Fact]
    public void HasBinaryVectorDataType_generic_sets_annotation_and_round_trips()
    {
        var collection = database.CreateCollection<VectorEntity>();
        var vectorData = new byte[] { 10, 20, 30, 40 };

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<VectorEntity>()
                .Property<byte[]>(e => e.embedding)
                .HasBinaryVectorDataType(BinaryVectorDataType.PackedBit);
        }))
        {
            db.Entities.Add(new VectorEntity { embedding = vectorData });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<VectorEntity>()
                .Property<byte[]>(e => e.embedding)
                .HasBinaryVectorDataType(BinaryVectorDataType.PackedBit);
        }))
        {
            var result = db.Entities.First();
            Assert.Equal(vectorData, result.embedding);
        }
    }

    class DateEntity
    {
        public ObjectId _id { get; set; }
        public DateTime created { get; set; }
    }

    [Fact]
    public void HasDateTimeKind_utc_round_trips()
    {
        var collection = database.CreateCollection<DateEntity>();
        var expected = DateTime.UtcNow;

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<DateEntity>()
                .Property(e => e.created)
                .HasDateTimeKind(DateTimeKind.Utc);
        }))
        {
            db.Entities.Add(new DateEntity { created = expected });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<DateEntity>()
                .Property(e => e.created)
                .HasDateTimeKind(DateTimeKind.Utc);
        }))
        {
            var result = db.Entities.First();
            Assert.Equal(DateTimeKind.Utc, result.created.Kind);
        }
    }

    [Fact]
    public void HasDateTimeKind_local_round_trips()
    {
        var collection = database.CreateCollection<DateEntity>();
        var expected = DateTime.Now;

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<DateEntity>()
                .Property(e => e.created)
                .HasDateTimeKind(DateTimeKind.Local);
        }))
        {
            db.Entities.Add(new DateEntity { created = expected });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection, mb =>
        {
            mb.Entity<DateEntity>()
                .Property(e => e.created)
                .HasDateTimeKind(DateTimeKind.Local);
        }))
        {
            var result = db.Entities.First();
            Assert.Equal(DateTimeKind.Local, result.created.Kind);
        }
    }
}
