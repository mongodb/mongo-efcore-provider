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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class TransactionTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class TextEntity
    {
        public ObjectId _id { get; set; }
        public string text { get; set; }
    }

    [Fact]
    public void SaveChanges_reverts_changes_on_driver_exception()
    {
        var collection = tempDatabase.GetExistingTemporaryCollection<TextEntity>();
        var idToDuplicate = ObjectId.GenerateNewId();
        var idToDelete = ObjectId.GenerateNewId();

        {
            // Setup two entities
            var db = SingleEntityDbContext.Create(collection);
            db.AddRange(
                new TextEntity {_id = idToDuplicate, text = "Original"},
                new TextEntity {_id = idToDelete, text = "Delete"});
            db.SaveChanges();
        }

        {
            // Attempt to delete one and duplicate the other
            var db = SingleEntityDbContext.Create(collection);
            var toDelete = db.Entities.First(e => e._id == idToDelete);
            db.Entities.AddRange(
                new TextEntity { _id = idToDuplicate, text = "Duplicate" },
                new TextEntity { _id = ObjectId.GenerateNewId(), text = "Insert" });
            db.Entities.Remove(toDelete);
            Assert.Throws<MongoBulkWriteException<BsonDocument>>(() => db.SaveChanges());
        }

        {
            // Ensure database is in original state
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(2, db.Entities.Count());
            Assert.Equal("Original", db.Entities.First(e => e._id == idToDuplicate).text);
            Assert.Equal("Delete", db.Entities.First(e => e._id == idToDelete).text);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_reverts_changes_on_driver_exception()
    {
        var collection = tempDatabase.GetExistingTemporaryCollection<TextEntity>();
        var idToDuplicate = ObjectId.GenerateNewId();
        var idToDelete = ObjectId.GenerateNewId();

        {
            // Setup two entities
            var db = SingleEntityDbContext.Create(collection);
            await db.AddRangeAsync(
                new TextEntity {_id = idToDuplicate, text = "Original"},
                new TextEntity {_id = idToDelete, text = "Delete"});
            await db.SaveChangesAsync();
        }

        {
            // Attempt to delete one and duplicate the other
            var db = SingleEntityDbContext.Create(collection);
            var toDelete = await db.Entities.FirstAsync(e => e._id == idToDelete);
            await db.Entities.AddRangeAsync(
                new TextEntity { _id = idToDuplicate, text = "Duplicate" },
                new TextEntity { _id = ObjectId.GenerateNewId(), text = "Insert" });
            db.Entities.Remove(toDelete);
            await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => db.SaveChangesAsync());
        }

        {
            // Ensure database is in original state
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(2, await db.Entities.CountAsync());
            Assert.Equal("Original", (await db.Entities.FirstAsync(e => e._id == idToDuplicate)).text);
            Assert.Equal("Delete", (await db.Entities.FirstAsync(e => e._id == idToDelete)).text);
        }
    }
}
