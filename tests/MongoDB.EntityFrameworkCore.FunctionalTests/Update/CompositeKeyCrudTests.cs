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

[XUnitCollection("UpdateTests")]
public class CompositeKeyCrudTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public CompositeKeyCrudTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    [Fact]
    public void Should_insert_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        var entity = new Entity {Id1 = "key", Id2 = 2, Data = "some text"};

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id1), nameof(Entity.Id2));
            });
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
        }

        {
            var actual = collection
                .Find(Builders<Entity>.Filter.Empty)
                .Project(Builders<Entity>.Projection.As<BsonDocument>())
                .Single();

            var expected = BsonDocument.Parse("{ _id : { Id1 : 'key', Id2 : 2 }, Data: 'some text' }");
            Assert.Equivalent(expected, actual);
        }
    }

    [Fact]
    public void Should_read_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        var entity = new Entity {Id1 = "key", Id2 = 2, Data = "some text"};

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id1), nameof(Entity.Id2));
            });
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
        }

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id1), nameof(Entity.Id2));
            });

            var actual = dbContext.Entitites.Single();

            Assert.Equal(entity.Id1, actual.Id1);
            Assert.Equal(entity.Id2, actual.Id2);
            Assert.Equal(entity.Data, actual.Data);
        }
    }

    [Fact]
    public void Should_update_composite_key_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<Entity>();
        var entity = new Entity {Id1 = "key", Id2 = 2, Data = "some text"};

        {
            var dbContext = SingleEntityDbContext.Create(collection, builder =>
            {
                builder.Entity<Entity>()
                    .HasKey(nameof(Entity.Id1), nameof(Entity.Id2));
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

            var expected = BsonDocument.Parse("{ _id : { Id1 : 'key', Id2 : 2 }, Data: 'updated text' }");
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
                    .HasKey(nameof(Entity.Id1), nameof(Entity.Id2));
            });
            var entity = new Entity {Id1 = "key", Id2 = 2, Data = "some text"};
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
        public string Id1 { get; set; }

        public int Id2 { get; set; }

        public string Data { get; set; }
    }
}
