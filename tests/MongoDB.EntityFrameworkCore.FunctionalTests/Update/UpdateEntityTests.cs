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

using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class UpdateEntityTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class Entity<TValue>
    {
        public ObjectId _id { get; set; }
        public TValue Value { get; set; }
    }

    class SimpleEntity
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    class RealisticEntity
    {
        public ObjectId _id { get; set; }
        public Guid session { get; set; }
        public int lastCount { get; set; }
        public DateTime lastModified { get; set; }
    }

    class DictionaryEntity : SimpleEntity
    {
        public Dictionary<string, string> dictionary { get; set; } = new();
    }

    class DictionaryOfDictionaryEntity : SimpleEntity
    {
        public Dictionary<string, Dictionary<string, string>> dictionary { get; set; } = new();
    }

    [Fact]
    public void Update_simple_entity()
    {
        var collection = tempDatabase.CreateCollection<SimpleEntity>();
        var entity = new SimpleEntity {_id = ObjectId.GenerateNewId(), name = "Before"};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(entity);
            db.SaveChanges();
            entity.name = "After";
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(entity._id, foundEntity._id);
            Assert.Equal("After", foundEntity.name);
        }
    }

    [Fact]
    public void Update_dictionary_entity()
    {
        var collection = tempDatabase.CreateCollection<DictionaryEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new DictionaryEntity
            {
                _id = ObjectId.GenerateNewId(),
                name = "My Post",
                dictionary = new Dictionary<string, string> {{"author", "Damien"}, {"category", "technology"}}
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var changeDictionaryValue = db.Entities.First();
            changeDictionaryValue.dictionary["author"] = "Bob";
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            Assert.Equal("Bob", Assert.Contains("author", db.Entities.Single().dictionary));
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary.Remove("category");
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            Assert.DoesNotContain("category", db.Entities.Single().dictionary);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary.Add("rating", "5");
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            Assert.Equal("5", Assert.Contains("rating", db.Entities.Single().dictionary));
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary.Clear();
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            Assert.Empty(db.Entities.Single().dictionary);
        }

        Action<ModelBuilder> allowNullModel =
            mb => mb.Entity<DictionaryEntity>().Property(e => e.dictionary).IsRequired(false);

        {
            using var db = SingleEntityDbContext.Create(collection, allowNullModel);
            db.Entities.First().dictionary = null!;
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, allowNullModel);
            Assert.Null(db.Entities.Single().dictionary);
        }
    }

   [Fact]
    public void Update_dictionary_of_dictionary_entity()
    {
        var collection = tempDatabase.CreateCollection<DictionaryOfDictionaryEntity>();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new DictionaryOfDictionaryEntity
            {
                _id = ObjectId.GenerateNewId(),
                name = "My Post",
                dictionary = new Dictionary<string, Dictionary<string, string>>
                {
                    {"A", new Dictionary<string, string> {{"name", "Damien"}, { "category", "technology" }}},
                    {"B", new Dictionary<string, string> {{"name", "Bob" }, { "category", "social"}}}
                }
            });
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary["A"] = new Dictionary<string, string> {{"name1", "Damien1"}};
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.Single();
            Assert.Equal(2, entity.dictionary.Count);
            var a = Assert.Single(Assert.Contains("A", entity.dictionary));
            Assert.Equal("name1", a.Key);
            Assert.Equal("Damien1", a.Value);
            var b = Assert.Contains("B", entity.dictionary);
            Assert.Equal(2, b.Count);
            Assert.Single(b, x => x is {Key: "name", Value: "Bob"});
            Assert.Single(b, x => x is {Key: "category", Value: "social"});
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary["A"] = new Dictionary<string, string>();
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.Single();
            Assert.Equal(2, entity.dictionary.Count);
            Assert.Empty(Assert.Contains("A", entity.dictionary));
            var b = Assert.Contains("B", entity.dictionary);
            Assert.Equal(2, b.Count);
            Assert.Single(b, x => x is {Key: "name", Value: "Bob"});
            Assert.Single(b, x => x is {Key: "category", Value: "social"});
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary.Remove("B");
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.Single();
            Assert.Empty(Assert.Single(entity.dictionary).Value);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary.Add("Z", new Dictionary<string, string> { { "name", "Art" }});
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.Single();
            Assert.Equal(2, entity.dictionary.Count);
            Assert.Empty(Assert.Single(entity.dictionary, x => x.Key == "A").Value);
            var z = Assert.Contains("Z", entity.dictionary);
            Assert.Single(z, x => x is {Key: "name", Value: "Art"});
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.First().dictionary["Z"] = null!;
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.Single();
            Assert.Equal(2, entity.dictionary.Count);
            var z = Assert.Contains("Z", entity.dictionary);
            Assert.Null(z);
        }

        Action<ModelBuilder> allowNullModel =
            mb => mb.Entity<DictionaryOfDictionaryEntity>().Property(e => e.dictionary).IsRequired(false);

        {
            using var db = SingleEntityDbContext.Create(collection, allowNullModel);
            db.Entities.First().dictionary = null!;
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection, allowNullModel);
            Assert.Null(db.Entities.Single().dictionary);
        }
    }

    [Fact]
    public void Update_realistic_entity()
    {
        var collection = tempDatabase.CreateCollection<RealisticEntity>();

        var entity = new RealisticEntity
        {
            _id = ObjectId.GenerateNewId(),
            session = Guid.NewGuid(),
            lastCount = 1,
            lastModified = DateTime.UtcNow.Subtract(TimeSpan.FromDays(5))
        };

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(entity);
            db.SaveChanges();

            entity.session = Guid.NewGuid();
            entity.lastCount++;
            entity.lastModified = DateTime.UtcNow;
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(entity._id, foundEntity._id);
            Assert.Equal(entity.session, foundEntity.session);
            Assert.Equal(entity.lastCount, foundEntity.lastCount);
            Assert.Equal(entity.lastModified.ToBsonPrecision(), foundEntity.lastModified.ToBsonPrecision());
        }
    }

    [Fact]
    public void Update_only_updates_modified_fields()
    {
        var collection = tempDatabase.CreateCollection<RealisticEntity>();

        var session2 = Guid.NewGuid();

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(new RealisticEntity
            {
                _id = ObjectId.GenerateNewId(),
                session = Guid.NewGuid(),
                lastCount = 1,
                lastModified = DateTime.UtcNow.Subtract(TimeSpan.FromDays(5))
            });
            db.SaveChanges();
        }

        // Cause two updates to happen interleaved
        {
            using var db1 = SingleEntityDbContext.Create(collection);
            var entity1 = db1.Entities.First();
            entity1.lastCount++;

            using var db2 = SingleEntityDbContext.Create(collection);
            var entity2 = db2.Entities.First();
            entity2.session = session2;

            db1.SaveChanges();
            db2.SaveChanges();

            Assert.NotEqual(entity1.lastCount, entity2.lastCount);
            Assert.NotEqual(entity1.session, entity2.session);
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = db.Entities.First();

            // Ensure we have data from two interleaved updates
            Assert.Equal(entity.session, session2);
            Assert.Equal(2, entity.lastCount);
        }
    }

    [Theory]
    [InlineData(typeof(TestEnum), TestEnum.Value0, TestEnum.Value1)]
    [InlineData(typeof(TestEnum), TestEnum.Value1, TestEnum.Value0)]
    [InlineData(typeof(TestEnum?), TestEnum.Value0, TestEnum.Value1)]
    [InlineData(typeof(TestEnum?), TestEnum.Value0, null)]
    [InlineData(typeof(TestEnum?), null, TestEnum.Value1)]
    public void Entity_update_tests(Type valueType, object? initialValue, object? updatedValue)
    {
        var methodInfo = GetType().GetMethod(nameof(EntityAddTestImpl), BindingFlags.Instance | BindingFlags.NonPublic)!;
        methodInfo.MakeGenericMethod(valueType).Invoke(this, [initialValue, updatedValue]);
    }

    private enum TestEnum
    {
        Value0 = 0,
        Value1 = 1
    }

    private void EntityAddTestImpl<TValue>(TValue initialValue, TValue updatedValue)
    {
        var collection =
            tempDatabase.CreateCollection<Entity<TValue>>("EntityUpdateTest", typeof(TValue), initialValue, updatedValue);

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = new Entity<TValue> {_id = ObjectId.GenerateNewId(), Value = initialValue};
            db.Entities.Add(entity);
            db.SaveChanges();
            entity.Value = updatedValue;
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var foundEntity = db.Entities.Single();
            Assert.Equal(updatedValue, foundEntity.Value);
        }
    }
}
