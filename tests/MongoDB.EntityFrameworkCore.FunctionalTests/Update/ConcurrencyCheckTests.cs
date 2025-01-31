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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Update;

[XUnitCollection("UpdateTests")]
public class ConcurrencyCheckTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class ConcurrentEntity1
    {
        public ObjectId _id { get; set; }

        [ConcurrencyCheck]
        public string Text { get; set; }
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_that_has_been_modified()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "Initial state on instance 1"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        {
            using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = db2.Entities.First();
            entityInstance2.Text = "New state on instance 2";
            db2.SaveChanges();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db1.SaveChanges());
        Assert.Contains("1 modification", ex.Message);
        Assert.Equal(entityInstance1, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_that_has_been_modified()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "Initial state on instance 1"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        {
            await using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = await db2.Entities.FirstAsync();
            entityInstance2.Text = "New state on instance 2";
            await db2.SaveChangesAsync();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());
        Assert.Contains("1 modification", ex.Message);
        Assert.Equal(entityInstance1, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_that_is_missing()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "Initial state on instance 1"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        {
            using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = db2.Entities.First();
            db2.Remove(entityInstance2);
            db2.SaveChanges();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db1.SaveChanges());
        Assert.Contains("1 modification", ex.Message);
        Assert.Equal(entityInstance1, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_that_is_missing()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "Initial state on instance 1"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        {
            await using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = await db2.Entities.FirstAsync();
            db2.Remove(entityInstance2);
            await db2.SaveChangesAsync();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());
        Assert.Contains("1 modification", ex.Message);
        Assert.Equal(entityInstance1, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_entity_that_is_missing()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "I will be gone"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        db1.Remove(entityInstance1);
        db1.SaveChanges();

        db2.Remove(entityInstance2);
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        Assert.Contains("1 deletion", ex.Message);
        Assert.Equal(entityInstance2, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_entity_that_is_missing()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "I will be gone"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        await using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        db1.Remove(entityInstance1);
        await db1.SaveChangesAsync();

        db2.Remove(entityInstance2);
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Contains("1 deletion", ex.Message);
        Assert.Equal(entityInstance2, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_entity_that_has_been_modified()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "Original"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        entityInstance1.Text = "Modified";
        db1.SaveChanges();

        db2.Remove(entityInstance2);
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        Assert.Contains("1 deletion", ex.Message);
        Assert.Equal(entityInstance2, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_entity_that_has_been_modified()
    {
        var collection = database.CreateCollection<ConcurrentEntity1>();

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity1 {Text = "Original"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        await using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        entityInstance1.Text = "Modified";
        await db1.SaveChangesAsync();

        db2.Remove(entityInstance2);
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Contains("1 deletion", ex.Message);
        Assert.Equal(entityInstance2, Assert.Single(ex.Entries).Entity);
    }

    class ConcurrentEntity2
    {
        public ObjectId _id { get; set; }

        [ConcurrencyCheck]
        public string TextA { get; set; }

        [ConcurrencyCheck]
        public string TextB { get; set; }
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_two_checked_entity_that_has_been_modified()
    {
        var collection = database.CreateCollection<ConcurrentEntity2>();

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity2 {TextA = "Initial A on instance 1", TextB = "Initial B on instance 1"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        {
            using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = db2.Entities.First();
            entityInstance2.TextA = "New A state on instance 2";
            db2.SaveChanges();
        }

        entityInstance1.TextB = "Update B via instance 1";
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db1.SaveChanges());
        Assert.Contains("1 modification", ex.Message);
        Assert.Equal(entityInstance1, Assert.Single(ex.Entries).Entity);
    }

    [Fact]
    public async Task
        SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_two_checked_entity_that_has_been_modified()
    {
        var collection = database.CreateCollection<ConcurrentEntity2>();

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new ConcurrentEntity2 {TextA = "Initial A on instance 1", TextB = "Initial B on instance 1"};
        db1.Entities.Add(entityInstance1);
        await db1.SaveChangesAsync();

        {
            await using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = db2.Entities.First();
            entityInstance2.TextA = "New A state on instance 2";
            await db2.SaveChangesAsync();
        }

        entityInstance1.TextB = "Update B via instance 1";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());
        Assert.Contains("1 modification", ex.Message);
        Assert.Equal(entityInstance1, Assert.Single(ex.Entries).Entity);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SaveChanges_throws_DbUpdateConcurrencyException_with_subset_of_entries_from_batch_that_might_be_cause(
        bool conflictDeletes, bool conflictUpdates)
    {
        var collection = database.CreateCollection<ConcurrentEntity2>(values: [conflictDeletes, conflictUpdates]);

        using var db1 = SingleEntityDbContext.Create(collection);
        var deletes = Enumerable.Range(0, 4).Select(i => new ConcurrentEntity2 {TextA = "Delete " + i, TextB = "Hi"}).ToList();
        var updates = Enumerable.Range(0, 4).Select(i => new ConcurrentEntity2 {TextA = "Modify " + i, TextB = "Hi"}).ToList();
        db1.AddRange(deletes);
        db1.AddRange(updates.Take(2));
        db1.SaveChanges();

        {
            using var db2 = SingleEntityDbContext.Create(collection);

            if (conflictDeletes)
            {
                var idsToDelete = deletes.Take(2).Select(d => d._id);
                db2.RemoveRange(db2.Entities.Where(e => idsToDelete.Contains(e._id)));
            }

            if (conflictUpdates)
            {
                var idsToModify = updates.Take(2).Select(d => d._id);
                foreach (var entity in db2.Entities.Where(e => idsToModify.Contains(e._id)))
                {
                    entity.TextA = "Update by second context";
                }
            }

            db2.SaveChanges();
        }

        if (conflictDeletes)
        {
            db1.RemoveRange(deletes.Take(3));
        }

        if (conflictUpdates)
        {
            foreach (var entity in updates.Take(3))
            {
                entity.TextA = "Update in the first context";
            }
        }

        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db1.SaveChanges());
        var allEntities = ex.Entries.Select(e => e.Entity).OfType<ConcurrentEntity2>().ToList();

        if (conflictDeletes)
        {
            Assert.Contains(allEntities, e => deletes.Take(3).Contains(e));
        }

        if (conflictUpdates)
        {
            Assert.Contains(allEntities, e => updates.Take(3).Contains(e));
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_with_subset_of_entries_from_batch_that_might_be_cause(
        bool conflictDeletes, bool conflictUpdates)
    {
        var collection = database.CreateCollection<ConcurrentEntity2>(values: [conflictDeletes, conflictUpdates]);

        await using var db1 = SingleEntityDbContext.Create(collection);
        var deletes = Enumerable.Range(0, 4).Select(i => new ConcurrentEntity2 {TextA = "Delete " + i, TextB = "Hi"}).ToList();
        var updates = Enumerable.Range(0, 4).Select(i => new ConcurrentEntity2 {TextA = "Modify " + i, TextB = "Hi"}).ToList();
        await db1.AddRangeAsync(deletes);
        await db1.AddRangeAsync(updates.Take(2));
        await db1.SaveChangesAsync();

        {
            await using var db2 = SingleEntityDbContext.Create(collection);

            if (conflictDeletes)
            {
                var idsToDelete = deletes.Take(2).Select(d => d._id);
                db2.RemoveRange(db2.Entities.Where(e => idsToDelete.Contains(e._id)));
            }

            if (conflictUpdates)
            {
                var idsToModify = updates.Take(2).Select(d => d._id);
                foreach (var entity in db2.Entities.Where(e => idsToModify.Contains(e._id)))
                {
                    entity.TextA = "Update by second context";
                }
            }

            await db2.SaveChangesAsync();
        }

        if (conflictDeletes)
        {
            db1.RemoveRange(deletes.Take(3));
        }

        if (conflictUpdates)
        {
            foreach (var entity in updates.Take(3))
            {
                entity.TextA = "Update in the first context";
            }
        }

        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());
        var allEntities = ex.Entries.Select(e => e.Entity).OfType<ConcurrentEntity2>().ToList();

        if (conflictDeletes)
        {
            Assert.Contains(allEntities, e => deletes.Take(3).Contains(e));
        }

        if (conflictUpdates)
        {
            Assert.Contains(allEntities, e => updates.Take(3).Contains(e));
        }
    }
}
