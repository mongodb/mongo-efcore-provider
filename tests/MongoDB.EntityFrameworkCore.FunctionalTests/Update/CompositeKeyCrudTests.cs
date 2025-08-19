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

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class CompositeKeyCrudTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Should_insert_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        var entity = new Entity { Id1 = "key", Id2 = 2, Data = "some text" };

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id1), nameof(Entity.Id2)));
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<Entity>.Filter.Empty)
                .Project(Builders<Entity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : { Id1 : 'key', Id2 : 2 }, Data: 'some text' }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_read_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        var entity = new Entity { Id1 = "key", Id2 = 2, Data = "some text" };

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id1), nameof(Entity.Id2)));
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            using var dbContext = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id1), nameof(Entity.Id2)));
            var actual = dbContext.Entities.Single();

            Assert.Equal(entity.Id1, actual.Id1);
            Assert.Equal(entity.Id2, actual.Id2);
            Assert.Equal(entity.Data, actual.Data);
        }
    }

    [Fact]
    public void Should_update_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        var entity = new Entity { Id1 = "key", Id2 = 2, Data = "some text" };

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id1), nameof(Entity.Id2)));
            db.Entities.Add(entity);
            db.SaveChanges();

            entity.Data = "updated text";
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<Entity>.Filter.Empty)
                .Project(Builders<Entity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : { Id1 : 'key', Id2 : 2 }, Data: 'updated text' }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_delete_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id1), nameof(Entity.Id2)));
            var entity = new Entity { Id1 = "key", Id2 = 2, Data = "some text" };
            db.Entities.Add(entity);
            db.SaveChanges();

            db.Remove(entity);
            db.SaveChanges();
        }

        {
            var actualCount = collection.CountDocuments(Builders<Entity>.Filter.Empty);
            Assert.Equal(0, actualCount);
        }
    }

    [Fact]
    public void Should_order_by_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();

        using var db = SingleEntityDbContext.Create(collection, builder =>
            builder.Entity<Entity>().HasKey(nameof(Entity.Id1), nameof(Entity.Id2)));
        db.Entities.AddRange(
            new Entity { Id1 = "key", Id2 = 2 },
            new Entity { Id1 = "key", Id2 = 1 },
            new Entity { Id1 = "key", Id2 = 4 },
            new Entity { Id1 = "key", Id2 = 0 },
            new Entity { Id1 = "key2", Id2 = 2 },
            new Entity { Id1 = "key2", Id2 = 4 },
            new Entity { Id1 = "key2", Id2 = 3 });
        db.SaveChanges();

        var actualOrdered = db.Entities.OrderBy(e => e).ToList();
        var expectedOrdered = actualOrdered.OrderBy(e => e.Id1).ThenBy(e => e.Id2).ToList();

        Assert.Equal(expectedOrdered, actualOrdered);
    }

    private class Entity
    {
        public string Id1 { get; set; }
        public int Id2 { get; set; }
        public string Data { get; set; }
    }
}
