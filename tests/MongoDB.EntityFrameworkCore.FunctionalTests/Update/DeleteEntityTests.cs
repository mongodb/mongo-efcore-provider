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

public sealed class DeleteEntityTests : IClassFixture<TemporaryDatabaseFixture>
{
    private readonly TemporaryDatabaseFixture _tempDatabase;

    public DeleteEntityTests(TemporaryDatabaseFixture tempDatabase)
    {
        _tempDatabase = tempDatabase;
    }

    class SimpleEntityWithStringId
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    class SimpleEntityWithObjectIdId
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    class SimpleEntityWithGuidId
    {
        public Guid _id { get; set; }
        public string name { get; set; }
    }

    class SimpleEntityWithIntId
    {
        public int _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void Entity_delete_with_string_id()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntityWithStringId>();
        collection.InsertOne(new SimpleEntityWithStringId {_id = ObjectId.GenerateNewId().ToString(), name = "DeleteMe"});

        var dbContext = SingleEntityDbContext.Create(collection);
        var entity = dbContext.Entitites.Single();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        Assert.Empty(dbContext.Entitites);
    }

    [Fact]
    public void Entity_delete_with_objectid_id()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntityWithObjectIdId>();
        collection.InsertOne(new SimpleEntityWithObjectIdId {_id = ObjectId.GenerateNewId(), name = "DeleteMe"});

        var dbContext = SingleEntityDbContext.Create(collection);
        var entity = dbContext.Entitites.Single();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        Assert.Empty(dbContext.Entitites);
    }

    [Fact]
    public void Entity_delete_with_guid_id()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntityWithGuidId>();
        collection.InsertOne(new SimpleEntityWithGuidId {_id = Guid.NewGuid(), name = "DeleteMe"});

        var dbContext = SingleEntityDbContext.Create(collection);
        var entity = dbContext.Entitites.Single();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        Assert.Empty(dbContext.Entitites);
    }

    [Fact]
    public void Entity_delete_with_int_id()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntityWithIntId>();
        collection.InsertOne(new SimpleEntityWithIntId {_id = new Random().Next(), name = "DeleteMe"});

        var dbContext = SingleEntityDbContext.Create(collection);
        var entity = dbContext.Entitites.Single();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        Assert.Empty(dbContext.Entitites);
    }

}
