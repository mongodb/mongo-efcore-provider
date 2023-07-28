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

public sealed class DeleteEntityTests : IDisposable
{
    private readonly TemporaryDatabase _tempDatabase = TestServer.CreateTemporaryDatabase();
    public void Dispose() => _tempDatabase.Dispose();

    class SimpleEntity
    {
        public string _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void Entity_delete()
    {
        var collection = _tempDatabase.CreateTemporaryCollection<SimpleEntity>();
        collection.InsertOne(new SimpleEntity {_id = ObjectId.GenerateNewId().ToString(), name = "DeleteMe"});

        var dbContext = SingleEntityDbContext.Create(collection);
        var entity = dbContext.Entitites.Single();

        dbContext.Remove(entity);
        dbContext.SaveChanges();

        Assert.Empty(dbContext.Entitites);
    }
}
