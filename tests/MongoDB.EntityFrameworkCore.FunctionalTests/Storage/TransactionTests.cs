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

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Storage;

[XUnitCollection("StorageTests")]
public class TransactionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class SimpleEntity
    {
        public ObjectId _id { get; set; }
        public string name { get; set; }
    }

    [Fact]
    public void Explicit_transactions_can_be_used_around_SaveChanges()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new SimpleEntity { name = "test" });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Database.BeginTransaction();
            var entity = db.Entities.First();
            entity.name = "updated";
            db.SaveChanges();
            db.Database.CommitTransaction();
        }

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            Assert.Equal("updated", entity.name);
        }
    }

    [Fact]
    public async Task Explicit_transactions_can_be_used_around_SaveChangesAsync()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        await using (var db = SingleEntityDbContext.Create(collection))
        {
            await db.Entities.AddAsync(new SimpleEntity { name = "test" });
            await db.SaveChangesAsync();
        }

        await using (var db = SingleEntityDbContext.Create(collection))
        {
            await db.Database.BeginTransactionAsync();
            var entity = await db.Entities.FirstOrDefaultAsync();
            entity.name = "updated";
            await db.SaveChangesAsync();
            await db.Database.CommitTransactionAsync();
        }

        await using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = await db.Entities.FirstOrDefaultAsync();
            Assert.Equal("updated", entity.name);
        }
    }

    [Fact]
    public void Explicit_transactions_with_TransactionOptions_can_be_used_around_SaveChanges()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Entities.Add(new SimpleEntity { name = "test" });
            db.SaveChanges();
        }

        using (var db = SingleEntityDbContext.Create(collection, loggerFactory))
        {
            var options = new Driver.TransactionOptions(readConcern: new Optional<ReadConcern>(ReadConcern.Majority));
            using var transaction = db.Database.BeginTransaction(options);
            var entity = db.Entities.First();
            entity.name = "updated";
            db.SaveChanges();
            transaction.Commit();
        }

        // Verify logging including read concern
        var beginTransactionMessage = spyLogger.GetLogMessageByEventId(MongoEventId.TransactionStarting);
        Assert.Contains("Beginning transaction", beginTransactionMessage);
        Assert.Contains(" ReadConcern '{ \"level\" : \"majority\" }'", beginTransactionMessage);
        Assert.Contains("Began transaction", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionStarted));
        Assert.Contains("Committing transaction.", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionCommitting));
        Assert.Contains("Committed transaction.", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionCommitted));

        using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = db.Entities.First();
            Assert.Equal("updated", entity.name);
        }
    }

    [Fact]
    public async Task Explicit_transactions_with_TransactionOptions_can_be_used_around_SaveChangesAsync()
    {
        var collection = database.CreateCollection<SimpleEntity>();
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();

        await using (var db = SingleEntityDbContext.Create(collection))
        {
            await db.Entities.AddAsync(new SimpleEntity { name = "test" });
            await db.SaveChangesAsync();
        }

        await using (var db = SingleEntityDbContext.Create(collection, loggerFactory))
        {
            var options = new Driver.TransactionOptions(readConcern: new Optional<ReadConcern>(ReadConcern.Majority));
            var transaction = await db.Database.BeginTransactionAsync(options);
            var entity = await db.Entities.FirstOrDefaultAsync();
            entity.name = "updated";
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        // Verify logging including read concern
        var beginTransactionMessage = spyLogger.GetLogMessageByEventId(MongoEventId.TransactionStarting);
        Assert.Contains("Beginning transaction", beginTransactionMessage);
        Assert.Contains(" ReadConcern '{ \"level\" : \"majority\" }'", beginTransactionMessage);
        Assert.Contains("Began transaction", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionStarted));
        Assert.Contains("Committing transaction.", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionCommitting));
        Assert.Contains("Committed transaction.", spyLogger.GetLogMessageByEventId(MongoEventId.TransactionCommitted));

        await using (var db = SingleEntityDbContext.Create(collection))
        {
            var entity = await db.Entities.FirstOrDefaultAsync();
            Assert.Equal("updated", entity.name);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_transactions_with_manual_accept_changes_are_supported(bool shouldSucceed)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: [shouldSucceed]);

        using var db = SingleEntityDbContext.Create(collection);
        var transaction = db.Database.BeginTransaction();
        var entity = new SimpleEntity { name = "test" };
        db.Entities.Add(entity);
        db.SaveChanges(false);

        if (shouldSucceed)
        {
            transaction.Commit();
            db.ChangeTracker.AcceptAllChanges();
            using var dbCheck = SingleEntityDbContext.Create(collection);
            Assert.Equal("test", Assert.Single(db.Entities).name);
        }
        else
        {
            transaction.Rollback();
            using var dbCheck = SingleEntityDbContext.Create(collection);
            Assert.Empty(db.Entities);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_transactions_with_manual_accept_changes_are_supported_async(bool shouldSucceed)
    {
        var collection = database.CreateCollection<SimpleEntity>(values: [shouldSucceed]);

        await using var db = SingleEntityDbContext.Create(collection);
        var transaction = await db.Database.BeginTransactionAsync();
        var entity = new SimpleEntity { name = "test" };
        db.Entities.Add(entity);
        await db.SaveChangesAsync(false);

        if (shouldSucceed)
        {
            await transaction.CommitAsync();
            db.ChangeTracker.AcceptAllChanges();
            await using var dbCheck = SingleEntityDbContext.Create(collection);
            Assert.Equal("test", Assert.Single(db.Entities).name);
        }
        else
        {
            await transaction.RollbackAsync();
            await using var dbCheck = SingleEntityDbContext.Create(collection);
            Assert.Empty(db.Entities);
        }
    }

    [Fact]
    public void Implicit_transactions_are_used_for_multiple_root_operations_and_rollback_on_error()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        // Pre-insert a conflicting document to cause duplicate key on the second insert
        var conflictingId = ObjectId.GenerateNewId();
        collection.InsertOne(new SimpleEntity { _id = conflictingId, name = "existing" });

        // Ensure implicit transactions are enabled when needed (default is WhenNeeded)
        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;

            db.Entities.AddRange(
                new SimpleEntity { _id = ObjectId.GenerateNewId(), name = "ok-1" }, // will succeed if executed alone
                new SimpleEntity { _id = conflictingId, name = "dup-key-should-fail" }); // will throw duplicate key

            // SaveChanges should throw and roll back the successful first insert due to implicit transaction
            Assert.ThrowsAny<Exception>(() => db.SaveChanges());
        }

        // Only the original pre-insert should remain; the attempted batch should be fully rolled back
        using (var dbCheck = SingleEntityDbContext.Create(collection))
        {
            var all = dbCheck.Entities.ToList();
            Assert.Single(all);
            Assert.Equal("existing", all[0].name);
        }
    }

    [Fact]
    public async Task Implicit_transactions_are_used_for_multiple_root_operations_and_rollback_on_error_async()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        var conflictingId = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new SimpleEntity { _id = conflictingId, name = "existing" });

        await using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;

            await db.Entities.AddRangeAsync(
                new SimpleEntity { _id = ObjectId.GenerateNewId(), name = "ok-1" },
                new SimpleEntity { _id = conflictingId, name = "dup-key-should-fail" });

            await Assert.ThrowsAnyAsync<Exception>(async () => await db.SaveChangesAsync());
        }

        await using (var dbCheck = SingleEntityDbContext.Create(collection))
        {
            var all = await dbCheck.Entities.ToListAsync();
            Assert.Single(all);
            Assert.Equal("existing", all[0].name);
        }
    }

    [Fact]
    public void AutoTransactionBehavior_Never_disables_implicit_transactions_leading_to_partial_commits_on_error()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        var conflictingId = ObjectId.GenerateNewId();
        collection.InsertOne(new SimpleEntity { _id = conflictingId, name = "existing" });

        using (var db = SingleEntityDbContext.Create(collection))
        {
            db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

            // Two root operations; without implicit transactions, first insert will commit, second throws
            db.Entities.AddRange(
                new SimpleEntity { _id = ObjectId.GenerateNewId(), name = "will-commit" },
                new SimpleEntity { _id = conflictingId, name = "dup-key-should-fail" });

            Assert.ThrowsAny<Exception>(() => db.SaveChanges());
        }

        using (var dbCheck = SingleEntityDbContext.Create(collection))
        {
            var names = dbCheck.Entities.Select(e => e.name).ToList();
            // Expect both original + the first of the batch (partial commit) due to disabled implicit transactions
            Assert.Contains("existing", names);
            Assert.Contains("will-commit", names);
        }
    }

    [Fact]
    public void Explicit_transaction_dispose_without_commit_or_rollback_throws_and_changes_not_visible()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        // Use `using` to force Dispose without commit/rollback
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var db = SingleEntityDbContext.Create(collection);
            using var transaction = db.Database.BeginTransaction();
            db.Entities.Add(new SimpleEntity { name = "pending" });
            db.SaveChanges();
            // Intentionally not committing or rolling back; disposing transaction should throw
        });

        Assert.Contains("Dispose", ex.Message);

        using var dbCheck = SingleEntityDbContext.Create(collection);
        Assert.Empty(dbCheck.Entities); // nothing committed
    }

    [Fact]
    public void Ambient_TransactionScope_blocks_begin_explicit_transaction()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        using var scope = new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled);

        var ex = Assert.Throws<InvalidOperationException>(() => db.Database.BeginTransaction());
        Assert.Contains("ambient transaction", ex.Message, StringComparison.OrdinalIgnoreCase);

        scope.Complete();
    }

    [Fact]
    public void Database_EnlistTransaction_is_not_supported()
    {
        using var db = SingleEntityDbContext.Create(database.CreateCollection<SimpleEntity>());

        using var committable = new CommittableTransaction();
        var ex = Assert.Throws<NotSupportedException>(() => db.Database.EnlistTransaction(committable));
        Assert.Equal("The current provider doesn't support System.Transaction.", ex.Message);
    }

    [Fact]
    public void Nested_explicit_transactions_are_rejected()
    {
        using var db = SingleEntityDbContext.Create(database.CreateCollection<SimpleEntity>());
        using var transaction = db.Database.BeginTransaction();

        var ex = Assert.Throws<InvalidOperationException>(() => db.Database.BeginTransaction());
        Assert.Contains("already in a transaction", ex.Message, StringComparison.OrdinalIgnoreCase);

        transaction.Rollback();
    }

    [Fact]
    public void Multiple_SaveChanges_inside_single_explicit_transaction_are_atomic_until_commit()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        using var transaction = db.Database.BeginTransaction();

        db.Entities.Add(new SimpleEntity { name = "first" });
        db.SaveChanges();

        db.Entities.Add(new SimpleEntity { name = "second" });
        db.SaveChanges();

        // Changes should not be visible outside the transaction
        using (var other = SingleEntityDbContext.Create(collection))
        {
            Assert.Empty(other.Entities);
        }

        transaction.Commit();

        using var dbCheck = SingleEntityDbContext.Create(collection);
        var names = dbCheck.Entities.Select(e => e.name).OrderBy(n => n).ToList();
        Assert.Equal(["first", "second"], names);
    }

    [Fact]
    public async Task Multiple_SaveChanges_inside_single_explicit_transaction_are_atomic_until_commit_async()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        await using var db = SingleEntityDbContext.Create(collection);
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Entities.AddAsync(new SimpleEntity { name = "first" });
        await db.SaveChangesAsync();

        await db.Entities.AddAsync(new SimpleEntity { name = "second" });
        await db.SaveChangesAsync();

        await using (var other = SingleEntityDbContext.Create(collection))
        {
            Assert.Empty(await other.Entities.ToListAsync());
        }

        await transaction.CommitAsync();

        await using var dbCheck = SingleEntityDbContext.Create(collection);
        var names = (await dbCheck.Entities.Select(e => e.name).ToListAsync()).OrderBy(n => n).ToList();
        Assert.Equal(["first", "second"], names);
    }

    [Fact]
    public void SaveChanges_throw_inside_explicit_transaction_does_not_affect_db_until_commit_and_rollback_discards()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        // Pre-insert to cause duplicate key on second insert inside the transaction
        var conflictingId = ObjectId.GenerateNewId();
        collection.InsertOne(new SimpleEntity { _id = conflictingId, name = "existing" });

        using var db = SingleEntityDbContext.Create(collection);
        using var transaction = db.Database.BeginTransaction();

        db.Entities.AddRange(
            new SimpleEntity { _id = ObjectId.GenerateNewId(), name = "transaction-ok" },
            new SimpleEntity { _id = conflictingId, name = "transaction-dup" });

        // Act: SaveChanges should throw, but nothing should become visible to other contexts yet.
        Assert.ThrowsAny<Exception>(() => db.SaveChanges());

        // The same DbContext (same transaction) can still see its own pending changes if it were to query
        // (MongoDB read-your-own-writes within a transaction). We only assert external visibility here.
        using (var outsider = SingleEntityDbContext.Create(collection))
        {
            var names = outsider.Entities.Select(e => e.name).ToList();
            Assert.Contains("existing", names); // original
            Assert.DoesNotContain("transaction-ok", names); // not visible outside the transaction
            Assert.DoesNotContain("transaction-dup", names);
        }

        // Rollback explicitly; the staged writes should be discarded.
        transaction.Rollback();

        using var check = SingleEntityDbContext.Create(collection);
        var final = check.Entities.Select(e => e.name).OrderBy(n => n).ToList();
        Assert.Equal(["existing"], final);
    }

    [Fact]
    public async Task SaveChangesAsync_throw_inside_explicit_transaction_does_not_affect_db_until_commit_and_rollback_discards()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        var conflictingId = ObjectId.GenerateNewId();
        await collection.InsertOneAsync(new SimpleEntity { _id = conflictingId, name = "existing" });

        await using var db = SingleEntityDbContext.Create(collection);
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Entities.AddRangeAsync(
            new SimpleEntity { _id = ObjectId.GenerateNewId(), name = "transaction-ok" },
            new SimpleEntity { _id = conflictingId, name = "transaction-dup" });

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());

        await using (var outsider = SingleEntityDbContext.Create(collection))
        {
            var names = await outsider.Entities.Select(e => e.name).ToListAsync();
            Assert.Contains("existing", names);
            Assert.DoesNotContain("transaction-ok", names);
            Assert.DoesNotContain("transaction-dup", names);
        }

        await transaction.RollbackAsync();

        await using var check = SingleEntityDbContext.Create(collection);
        var final = (await check.Entities.Select(e => e.name).ToListAsync()).OrderBy(n => n).ToList();
        Assert.Equal(["existing"], final);
    }

    [Fact]
    public void Same_context_reads_its_own_writes_inside_explicit_transaction()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        using var db = SingleEntityDbContext.Create(collection);
        using var transaction = db.Database.BeginTransaction();

        db.Entities.Add(new SimpleEntity { name = "first" });
        db.SaveChanges();

        // Same context in same transaction sees the inserted doc
        Assert.Equal("first", Assert.Single(db.Entities).name);

        db.Entities.Add(new SimpleEntity { name = "second" });
        db.SaveChanges();

        // Projections visible inside same transaction
        Assert.Equal(["first", "second"], db.Entities.Select(e => e.name).OrderBy(n => n));
        Assert.Equal(2, db.Entities.Count());

        // Entity reads visible inside same transaction
        Assert.Single(db.Entities, e => e.name == "first");
        Assert.Single(db.Entities, e => e.name == "second");

        // Other context still does not see them
        using (var outsider = SingleEntityDbContext.Create(collection))
        {
            Assert.Empty(outsider.Entities);
        }

        transaction.Rollback();

        using var check = SingleEntityDbContext.Create(collection);
        Assert.Empty(check.Entities);
    }

    [Fact]
    public async Task Same_context_reads_its_own_writes_inside_explicit_transaction_async()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        await using var db = SingleEntityDbContext.Create(collection);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Entities.AddAsync(new SimpleEntity { name = "first" });
        await db.SaveChangesAsync();

        // Same context in same transaction sees the inserted doc
        Assert.Equal("first", Assert.Single(db.Entities).name);

        await db.Entities.AddAsync(new SimpleEntity { name = "second" });
        await db.SaveChangesAsync();

        // Projections visible inside same transaction
        Assert.Equal(["first", "second"], db.Entities.Select(e => e.name).OrderBy(n => n));
        Assert.Equal(2, await db.Entities.CountAsync());

        // Entity reads visible inside same transaction
        Assert.Single(db.Entities, e => e.name == "first");
        Assert.Single(db.Entities, e => e.name == "second");

        // Other context still does not see them
        await using (var outsider = SingleEntityDbContext.Create(collection))
        {
            Assert.Empty(outsider.Entities);
        }

        await transaction.RollbackAsync();

        await using var check = SingleEntityDbContext.Create(collection);
        Assert.Empty(check.Entities);
    }

    [Fact]
    public void Disposing_explicit_transaction_without_commit_or_rollback_throws_and_does_not_commit()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var db = SingleEntityDbContext.Create(collection);
            using var transaction = db.Database.BeginTransaction();
            db.Entities.Add(new SimpleEntity { name = "pending" });
            db.SaveChanges();
            // Transaction goes out of scope without Commit/Rollback -> Dispose should throw
        });

        Assert.Contains("Dispose", ex.Message);
        using var check = SingleEntityDbContext.Create(collection);
        Assert.Empty(check.Entities);
    }

    [Fact]
    public async Task Disposing_explicit_transaction_without_commit_or_rollback_throws_and_does_not_commit_async()
    {
        var collection = database.CreateCollection<SimpleEntity>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var db = SingleEntityDbContext.Create(collection);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Entities.AddAsync(new SimpleEntity { name = "pending" });
            await db.SaveChangesAsync();
            // Transaction goes out of scope without Commit/Rollback -> DisposeAsync should throw
        });

        Assert.Contains("Dispose", ex.Message);
        await using var check = SingleEntityDbContext.Create(collection);
        Assert.Empty(await check.Entities.ToListAsync());
    }
}
