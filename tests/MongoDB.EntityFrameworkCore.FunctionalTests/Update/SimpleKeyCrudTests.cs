﻿/* Copyright 2023-present MongoDB Inc.
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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class SimpleKeyCrudTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Should_insert_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        var entity = new Entity {Id = "key", Data = "some text"};

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id)));
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<Entity>.Filter.Empty)
                .Project(Builders<Entity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 'key', Data: 'some text' }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_read_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        var entity = new Entity {Id = "key", Data = "some text"};

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id)));
            db.Entities.Add(entity);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id)));

            var actual = db.Entities.Single();

            Assert.Equal(entity.Id, actual.Id);
            Assert.Equal(entity.Data, actual.Data);
        }
    }

    [Fact]
    public void Should_update_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        var entity = new Entity {Id = "key", Data = "some text"};

        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id)));
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

            var expected = BsonDocument.Parse("{ _id : 'key', Data: 'updated text' }");
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Should_delete_composite_key_entity()
    {
        var collection = database.CreateCollection<Entity>();
        {
            using var db = SingleEntityDbContext.Create(collection, builder =>
                builder.Entity<Entity>().HasKey(nameof(Entity.Id)));
            var entity = new Entity {Id = "key", Data = "some text"};
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

    private class Entity
    {
        public string Id { get; set; }
        public string Data { get; set; }
    }
}
