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
public class RowVersionTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class BasicEntity
    {
        public ObjectId _id { get; set; }
        public string Text { get; set; }
    }

    public class TimestampedIntEntity : BasicEntity
    {
        [Timestamp] public int Version { get; set; }
    }

    class TimestampedUIntEntity : BasicEntity
    {
        [Timestamp] public uint Version { get; set; }
    }

    class TimestampedLongEntity : BasicEntity
    {
        [Timestamp] public long Version { get; set; }
    }

    public class TimestampedULongEntity : BasicEntity
    {
        [Timestamp] public ulong Version { get; set; }
    }

    public class UnattributedTimestampedULongEntity : BasicEntity
    {
        public ulong Version { get; set; }
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_int()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<TimestampedIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_uint()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<TimestampedUIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_long()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<TimestampedLongEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_ulong()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<TimestampedULongEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_unattributed()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<UnattributedTimestampedULongEntity>(
            mb => mb.Entity<UnattributedTimestampedULongEntity>().Property(e => e.Version).IsRowVersion());

    private void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<T>(Action<ModelBuilder>? mb = null)
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Modifying " + typeof(T).Name);

        using var db1 = SingleEntityDbContext.Create(collection, mb);
        var entityInstance1 = new T {Text = "Initial state on instance 1"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();
        var firstVersion = db1.Entry(entityInstance1).Property("Version").CurrentValue;

        {
            using var db2 = SingleEntityDbContext.Create(collection, mb);
            var entityInstance2 = db2.Entities.First();
            entityInstance2.Text = "New state on instance 2";
            db2.SaveChanges();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db1.SaveChanges());
        Assert.Contains("1 modification", ex.Message);

        Assert.Equal(firstVersion, db1.Entry(entityInstance1).Property("Version").CurrentValue);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_int()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<
            TimestampedIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_uint()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<
            TimestampedUIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_long()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<
            TimestampedLongEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified_ulong()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<
            TimestampedULongEntity>();

    private async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_entity_already_modified<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Modifying async " + typeof(T).Name);

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "Initial state on instance 1"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();
        var firstVersion = db1.Entry(entityInstance1).Property("Version").CurrentValue;

        {
            await using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = await db2.Entities.FirstAsync();
            entityInstance2.Text = "New state on instance 2";
            await db2.SaveChangesAsync();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());
        Assert.Contains("1 modification", ex.Message);

        Assert.Equal(firstVersion, db1.Entry(entityInstance1).Property("Version").CurrentValue);
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_int()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<TimestampedIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_uint()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<TimestampedUIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_long()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<TimestampedLongEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_ulong()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<TimestampedULongEntity>();

    private void SaveChanges_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Missing " + typeof(T).Name);

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "Initial state on instance 1"};
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
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_int()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<TimestampedIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_uint()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<
            TimestampedUIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_long()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<
            TimestampedLongEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity_ulong()
        => await
            SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<TimestampedULongEntity>();

    private async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_modifying_missing_entity<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Missing async " + typeof(T).Name);

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "Initial state on instance 1"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();
        var firstVersion = db1.Entry(entityInstance1).Property("Version").CurrentValue;

        {
            await using var db2 = SingleEntityDbContext.Create(collection);
            var entityInstance2 = await db2.Entities.FirstAsync();
            db2.Remove(entityInstance2);
            await db2.SaveChangesAsync();
        }

        entityInstance1.Text = "Update via instance 1";
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db1.SaveChangesAsync());
        Assert.Contains("1 modification", ex.Message);

        Assert.Equal("_version", db1.Entities.EntityType.FindProperty("Version")?.GetElementName());
        Assert.Equal(firstVersion, db1.Entry(entityInstance1).Property("Version").CurrentValue);
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_int()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_uint()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedUIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_long()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedLongEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_ulong()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedULongEntity>();

    private void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Deleting " + typeof(T).Name);

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "I will be gone"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        db1.Remove(entityInstance1);
        db1.SaveChanges();

        db2.Remove(entityInstance2);
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        Assert.Contains("1 deletion", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_int()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_uint()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedUIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_long()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<TimestampedLongEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity_ulong()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<
            TimestampedULongEntity>();

    private async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_missing_entity<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Deleting async " + typeof(T).Name);

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "I will be gone"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        await using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        db1.Remove(entityInstance1);
        await db1.SaveChangesAsync();

        db2.Remove(entityInstance2);
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Contains("1 deletion", ex.Message);
    }

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_int()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<TimestampedIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_uint()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<TimestampedUIntEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_long()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<TimestampedLongEntity>();

    [Fact]
    public void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_ulong()
        => SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<TimestampedULongEntity>();

    private void SaveChanges_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Deleting modified " + typeof(T).Name);

        using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "Original"};
        db1.Entities.Add(entityInstance1);
        db1.SaveChanges();

        using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        entityInstance1.Text = "Modified";
        db1.SaveChanges();

        db2.Remove(entityInstance2);
        var ex = Assert.Throws<DbUpdateConcurrencyException>(() => db2.SaveChanges());
        Assert.Contains("1 deletion", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_int()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<
            TimestampedIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_uint()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<
            TimestampedUIntEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_long()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<
            TimestampedLongEntity>();

    [Fact]
    public async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity_ulong()
        => await SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<
            TimestampedULongEntity>();

    private async Task SaveChangesAsync_throws_DbUpdateConcurrencyException_when_deleting_modified_entity<T>()
        where T : BasicEntity, new()
    {
        var collection = tempDatabase.CreateCollection<T>("Deleting modified async " + typeof(T).Name);

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new T {Text = "Original"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        await using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        entityInstance1.Text = "Modified";
        await db1.SaveChangesAsync();

        db2.Remove(entityInstance2);
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Contains("1 deletion", ex.Message);
    }

    [Fact]
    public async Task SaveChangesAsync_succeeds_after_rollback_by_setting_rowversion_original_value_to_latest()
    {
        var collection = tempDatabase.CreateCollection<TimestampedULongEntity>("Applying rollbacked TimestampedULongEntity");

        await using var db1 = SingleEntityDbContext.Create(collection);
        var entityInstance1 = new TimestampedULongEntity {Text = "Original"};
        await db1.Entities.AddAsync(entityInstance1);
        await db1.SaveChangesAsync();

        await using var db2 = SingleEntityDbContext.Create(collection);
        var entityInstance2 = db2.Entities.First();

        entityInstance1.Text = "Modified 1";
        await db1.SaveChangesAsync();

        entityInstance2.Text = "Modified 2";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());

        db2.Entry(entityInstance2).Property("Version").OriginalValue = entityInstance1.Version;
        await db2.SaveChangesAsync();
    }
}
