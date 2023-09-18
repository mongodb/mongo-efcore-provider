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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

public class SimpleKeyCrudTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public SimpleKeyCrudTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    [Fact]
    public void Should_insert_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        var entity = new Entity { Id = "key", Data = "some text" };

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id));
            });
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<Entity>.Filter.Empty)
                .Project(Builders<Entity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 'key', Data: 'some text' }");
            Assert.Equivalent(expected, actual);
        }
    }

    [Fact]
    public void Should_read_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        var entity = new Entity { Id = "key", Data = "some text" };

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id));
            });
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
        }

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id));
            });

            var actual = dbContext.Entitites.Single();

            Assert.Equal(entity.Id, actual.Id);
            Assert.Equal(entity.Data, actual.Data);
        }
    }

    [Fact]
    public void Should_update_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        var entity = new Entity { Id = "key", Data = "some text" };

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id));
            });
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();

            entity.Data = "updated text";
            dbContext.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<Entity>.Filter.Empty)
                .Project(Builders<Entity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : 'key', Data: 'updated text' }");
            Assert.Equivalent(expected, actual);
        }
    }

    [Fact]
    public void Should_delete_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id));
            });
            var entity = new Entity { Id = "key", Data = "some text" };
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();

            dbContext.Remove(entity);
            dbContext.SaveChanges();
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
