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

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class SaveChangesTransactionTests(TemporaryDatabaseFixture tempDatabase)
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

    class ConcurrencyEntity
    {
        public ObjectId _id { get; set; }

        [ConcurrencyCheck]
        public string text { get; set; }
    }

    [Fact]
    public void SaveChanges_reverts_changes_on_DbConcurrencyException()
    {
        var collection = tempDatabase.GetExistingTemporaryCollection<ConcurrencyEntity>();
        var idToChange = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            db.AddRange(new ConcurrencyEntity {_id = idToChange, text = "Initial"});
            db.SaveChanges();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = db1.Entities.First(e => e._id == idToChange);

            var db2 = SingleEntityDbContext.Create(collection);
            var copy2 = db2.Entities.First(e => e._id == idToChange);

            copy1.text = "Change on 1";
            db1.SaveChanges();

            copy2.text = "Change on 2";
            db2.Entities.Add(new ConcurrencyEntity { _id = ObjectId.GenerateNewId(), text = "Insert" });

            Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(1, db.Entities.Count());
            Assert.Equal("Change on 1", db.Entities.First(e => e._id == idToChange).text);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_reverts_changes_on_DbConcurrencyException()
    {
        var collection = tempDatabase.GetExistingTemporaryCollection<ConcurrencyEntity>();
        var idToChange = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            await db.AddAsync(new ConcurrencyEntity {_id = idToChange, text = "Initial"});
            await db.SaveChangesAsync();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = await db1.Entities.FirstAsync(e => e._id == idToChange);

            var db2 = SingleEntityDbContext.Create(collection);
            var copy2 = await db2.Entities.FirstAsync(e => e._id == idToChange);

            copy1.text = "Change on 1";
            await db1.SaveChangesAsync();

            copy2.text = "Change on 2";
            await db2.Entities.AddAsync(new ConcurrencyEntity { _id = ObjectId.GenerateNewId(), text = "Insert" });

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(1, await db.Entities.CountAsync());
            Assert.Equal("Change on 1", (await db.Entities.FirstAsync(e => e._id == idToChange)).text);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_does_not_revert_when_transactions_disabled()
    {
        var collection = tempDatabase.GetExistingTemporaryCollection<ConcurrencyEntity>();
        var idToChange = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            await db.AddAsync(new ConcurrencyEntity {_id = idToChange, text = "Initial"});
            await db.SaveChangesAsync();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = await db1.Entities.FirstAsync(e => e._id == idToChange);

            var db2 = SingleEntityDbContext.Create(collection);
            db2.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
            var copy2 = await db2.Entities.FirstAsync(e => e._id == idToChange);

            copy1.text = "Change on 1";
            await db1.SaveChangesAsync();

            copy2.text = "Change on 2";
            await db2.Entities.AddAsync(new ConcurrencyEntity { _id = ObjectId.GenerateNewId(), text = "Insert" });

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(2, await db.Entities.CountAsync());
            Assert.Equal("Change on 1", (await db.Entities.FirstAsync(e => e._id == idToChange)).text);
        }
    }
}
