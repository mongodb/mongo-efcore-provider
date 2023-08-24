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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

public sealed class UpdateEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public UpdateEntityTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
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
        var entity = new SimpleEntity {_id = ObjectId.GenerateNewId(), name = "Before"};

        {
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
            entity.name = "After";
            dbContext.SaveChanges();
        }

        var newDbContext = SingleEntityDbContext.Create(collection);
        var foundEntity = newDbContext.Entitites.Single();
        Assert.Equal(entity._id, foundEntity._id);
        Assert.Equal("After", foundEntity.name);
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
            var dbContext = SingleEntityDbContext.Create(collection);
            dbContext.Entitites.Add(entity);
            dbContext.SaveChanges();
            entity.session = Guid.NewGuid();
            entity.lastCount++;
            entity.lastModified = DateTime.UtcNow;
            dbContext.SaveChanges();
        }

        var newDbContext = SingleEntityDbContext.Create(collection);
        var foundEntity = newDbContext.Entitites.Single();
        Assert.Equal(entity._id, foundEntity._id);
        Assert.Equal(entity.session, foundEntity.session);
        Assert.Equal(entity.lastCount, foundEntity.lastCount);
        Assert.Equal(entity.lastModified.ToExpectedPrecision(), foundEntity.lastModified.ToExpectedPrecision());
    }
}
