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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class TransactionRollbackManagerTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class SimpleEntity
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void RollbackTransaction_via_database_facade_discards_changes()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        db.Database.BeginTransaction();
        db.Entities.Add(new SimpleEntity { name = "should-not-persist" });
        db.SaveChanges();

        db.Database.RollbackTransaction();

        using var check = SingleEntityDbContext.Create(collection);
        Assert.Empty(check.Entities);
    }

    [Fact]
    public async Task RollbackTransactionAsync_via_database_facade_discards_changes()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        await using var db = SingleEntityDbContext.Create(collection);
        await db.Database.BeginTransactionAsync();
        db.Entities.Add(new SimpleEntity { name = "should-not-persist" });
        await db.SaveChangesAsync();

        await db.Database.RollbackTransactionAsync();

        await using var check = SingleEntityDbContext.Create(collection);
        Assert.Empty(check.Entities);
    }

    [Fact]
    public void RollbackTransaction_then_new_transaction_can_commit()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using var db = SingleEntityDbContext.Create(collection);

        db.Database.BeginTransaction();
        db.Entities.Add(new SimpleEntity { name = "rolled-back" });
        db.SaveChanges();
        db.Database.RollbackTransaction();

        db.Database.BeginTransaction();
        db.Entities.Add(new SimpleEntity { name = "committed" });
        db.SaveChanges();
        db.Database.CommitTransaction();

        using var check = SingleEntityDbContext.Create(collection);
        var names = check.Entities.Select(e => e.name).ToList();
        Assert.Single(names);
        Assert.Contains("committed", names);
    }

    [Fact]
    public async Task RollbackTransactionAsync_then_new_transaction_can_commit()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        await using var db = SingleEntityDbContext.Create(collection);

        await db.Database.BeginTransactionAsync();
        db.Entities.Add(new SimpleEntity { name = "rolled-back" });
        await db.SaveChangesAsync();
        await db.Database.RollbackTransactionAsync();

        await db.Database.BeginTransactionAsync();
        db.Entities.Add(new SimpleEntity { name = "committed" });
        await db.SaveChangesAsync();
        await db.Database.CommitTransactionAsync();

        await using var check = SingleEntityDbContext.Create(collection);
        var names = await check.Entities.Select(e => e.name).ToListAsync();
        Assert.Single(names);
        Assert.Contains("committed", names);
    }

    [Fact]
    public void RollbackTransaction_clears_current_transaction()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        db.Database.BeginTransaction();
        Assert.NotNull(db.Database.CurrentTransaction);

        db.Database.RollbackTransaction();
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task RollbackTransactionAsync_clears_current_transaction()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        await using var db = SingleEntityDbContext.Create(collection);
        await db.Database.BeginTransactionAsync();
        Assert.NotNull(db.Database.CurrentTransaction);

        await db.Database.RollbackTransactionAsync();
        Assert.Null(db.Database.CurrentTransaction);
    }
}
