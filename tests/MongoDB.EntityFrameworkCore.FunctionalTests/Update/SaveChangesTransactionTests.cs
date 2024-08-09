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
public class SaveChangesTransactionTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class TextEntity
    {
        public ObjectId _id { get; set; }
        public string text { get; set; }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public void SaveChanges_behavior_on_driver_exception(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateTemporaryCollection<TextEntity>("SaveChanges_DriverException" + transactionBehavior);
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
            db.Database.AutoTransactionBehavior = transactionBehavior;
            var toDelete = db.Entities.First(e => e._id == idToDelete);
            db.Entities.AddRange(
                new TextEntity {_id = ObjectId.GenerateNewId(), text = "Insert"},
                new TextEntity {_id = idToDuplicate, text = "Duplicate"});
            db.Entities.Remove(toDelete);
            Assert.Throws<MongoBulkWriteException<BsonDocument>>(() => db.SaveChanges());
        }

        {
            // Ensure database is in original state
            var db = SingleEntityDbContext.Create(collection);
            if (transactionBehavior == AutoTransactionBehavior.Never)
            {
                Assert.Equal(3, db.Entities.Count());
            }
            else
            {
                Assert.Equal(2, db.Entities.Count());
                Assert.Equal("Original", db.Entities.First(e => e._id == idToDuplicate).text);
                Assert.Equal("Delete", db.Entities.First(e => e._id == idToDelete).text);
            }
        }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public async Task SaveChangesAsync_behavior_on_driver_exception(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateTemporaryCollection<TextEntity>("SaveChangesAsync_DriverException" + transactionBehavior);
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
            db.Database.AutoTransactionBehavior = transactionBehavior;
            var toDelete = await db.Entities.FirstAsync(e => e._id == idToDelete);
            await db.Entities.AddRangeAsync(
                new TextEntity {_id = ObjectId.GenerateNewId(), text = "Insert"},
                new TextEntity {_id = idToDuplicate, text = "Duplicate"});
            db.Entities.Remove(toDelete);
            await Assert.ThrowsAsync<MongoBulkWriteException<BsonDocument>>(() => db.SaveChangesAsync());
        }

        {
            // Ensure database is in original state
            var db = SingleEntityDbContext.Create(collection);

            if (transactionBehavior == AutoTransactionBehavior.Never)
            {
                Assert.Equal(3, db.Entities.Count());
            }
            else
            {
                Assert.Equal(2, db.Entities.Count());
                Assert.Equal("Original", db.Entities.First(e => e._id == idToDuplicate).text);
                Assert.Equal("Delete", db.Entities.First(e => e._id == idToDelete).text);
            }
        }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public void SaveChanges_behavior_on_DbConcurrencyException(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateTemporaryCollection<TextEntity>("SaveChanges_DbConcurrencyException" + transactionBehavior);
        var idToDelete = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            db.AddRange(new TextEntity {_id = idToDelete, text = "Initial"});
            db.SaveChanges();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = db1.Entities.First(e => e._id == idToDelete);

            var db2 = SingleEntityDbContext.Create(collection);
            db2.Database.AutoTransactionBehavior = transactionBehavior;
            db2.Entities.Add(new TextEntity {text = "Should I rollback?"});
            var copy2 = db2.Entities.First(e => e._id == idToDelete);

            db1.Entities.Remove(copy1);
            db1.SaveChanges();

            copy2.text = "Change on 2";
            Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(transactionBehavior == AutoTransactionBehavior.Never ? 1 : 0, db.Entities.Count());
        }
    }

    [Theory]
    [InlineData(AutoTransactionBehavior.WhenNeeded)]
    [InlineData(AutoTransactionBehavior.Never)]
    public async Task SaveChangesAsync_behavior_on_DbConcurrencyException(AutoTransactionBehavior transactionBehavior)
    {
        var collection =
            tempDatabase.CreateTemporaryCollection<TextEntity>("SaveChangesAsync_DbConcurrencyException" + transactionBehavior);
        var idToDelete = ObjectId.GenerateNewId();

        {
            var db = SingleEntityDbContext.Create(collection);
            await db.AddRangeAsync(new TextEntity {_id = idToDelete, text = "Initial"});
            await db.SaveChangesAsync();
        }

        {
            var db1 = SingleEntityDbContext.Create(collection);
            var copy1 = await db1.Entities.FirstAsync(e => e._id == idToDelete);

            var db2 = SingleEntityDbContext.Create(collection);
            db2.Database.AutoTransactionBehavior = transactionBehavior;
            db2.Entities.Add(new TextEntity {text = "Should I rollback?"});
            var copy2 = await db2.Entities.FirstAsync(e => e._id == idToDelete);

            db1.Entities.Remove(copy1);
            await db1.SaveChangesAsync();

            copy2.text = "Change on 2";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        }

        {
            var db = SingleEntityDbContext.Create(collection);
            Assert.Equal(transactionBehavior == AutoTransactionBehavior.Never ? 1 : 0, db.Entities.Count());
        }
    }
}
