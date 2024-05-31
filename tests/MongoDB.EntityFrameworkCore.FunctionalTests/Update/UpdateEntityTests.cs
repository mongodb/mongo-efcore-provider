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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class UpdateEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public UpdateEntityTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

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

    [Fact]
    public void Update_simple_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntity>();
        var entity = new SimpleEntity
        {
            _id = ObjectId.GenerateNewId(), name = "Before"
        };

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
    public void Update_realistic_entity()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<RealisticEntity>();

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
            _tempDatabase.CreateTemporaryCollection<Entity<TValue>>("EntityUpdateTest", typeof(TValue), initialValue, updatedValue);

        {
            using var db = SingleEntityDbContext.Create(collection);
            var entity = new Entity<TValue>
            {
                _id = ObjectId.GenerateNewId(), Value = initialValue
            };
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
