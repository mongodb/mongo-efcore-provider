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

#if !EF8

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class ExecuteDeleteTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId _id { get; set; }
        public string Status { get; set; } = null!; // mapped to "state" via HasElementName
        public int Quantity { get; set; }
    }

    private static readonly Action<ModelBuilder> ConfigureModel = mb =>
    {
        mb.Entity<Order>().Property(e => e.Status).HasElementName("state");
    };

    private SingleEntityDbContext<Order> CreateSeededContext([CallerMemberName] string? collectionName = null)
    {
        var collection = database.CreateCollection<Order>(collectionName);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureModel))
        {
            seedDb.AddRange(
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 10 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 20 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "closed", Quantity = 30 });
            seedDb.SaveChanges();
        }

        return SingleEntityDbContext.Create(collection, ConfigureModel);
    }

    [Fact]
    public void ExecuteDelete_with_predicate_deletes_matching_and_returns_count()
    {
        using var db = CreateSeededContext();

        var deleted = db.Entities.Where(o => o.Status == "open").ExecuteDelete();

        Assert.Equal(2, deleted);
        Assert.Equal(1, db.Entities.Count());
        Assert.Equal("closed", db.Entities.Single().Status);
    }

    [Fact]
    public async Task ExecuteDeleteAsync_with_predicate_deletes_matching_and_returns_count()
    {
        using var db = CreateSeededContext();

        var deleted = await db.Entities.Where(o => o.Quantity >= 20).ExecuteDeleteAsync();

        Assert.Equal(2, deleted);
        Assert.Equal(1, db.Entities.Count());
        Assert.Equal(10, db.Entities.Single().Quantity);
    }

    [Fact]
    public void ExecuteDelete_without_predicate_deletes_everything()
    {
        using var db = CreateSeededContext();

        var deleted = db.Entities.ExecuteDelete();

        Assert.Equal(3, deleted);
        Assert.Equal(0, db.Entities.Count());
    }

    [Fact]
    public void ExecuteDelete_with_OrderBy_and_no_Take_deletes_everything()
    {
        // OrderBy alone (without Skip/Take) triggers the two-phase path but deletes all documents —
        // semantically equivalent to the single-command path.
        using var db = CreateSeededContext();

        var deleted = db.Entities.OrderBy(o => o.Quantity).ExecuteDelete();

        Assert.Equal(3, deleted);
        Assert.Equal(0, db.Entities.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteDelete_logs_ExecutedBulkDelete_event_with_count(bool async)
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var collection = database.CreateCollection<Order>(nameof(ExecuteDelete_logs_ExecutedBulkDelete_event_with_count), async);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureModel))
        {
            seedDb.AddRange(
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 10 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 20 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "closed", Quantity = 30 });
            seedDb.SaveChanges();
        }

        using var db = SingleEntityDbContext.Create(collection, loggerFactory, ConfigureModel);

        var deleted = async
            ? await db.Entities.Where(o => o.Status == "open").ExecuteDeleteAsync()
            : db.Entities.Where(o => o.Status == "open").ExecuteDelete();

        Assert.Equal(2, deleted);

        var executingMessage = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutingBulkDelete);
        Assert.Contains("Executing Bulk Delete", executingMessage);
        Assert.Contains($"Collection='{collection.CollectionNamespace}'", executingMessage);

        var executedMessage = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedBulkDelete);
        Assert.Contains("Executed Bulk Delete", executedMessage);
        Assert.Contains($"Collection='{collection.CollectionNamespace}'", executedMessage);
        Assert.Contains("Deleted=2", executedMessage);
    }

    [Fact]
    public void ExecuteDelete_with_two_Where_clauses_deletes_matching_both_and_returns_count()
    {
        using var db = CreateSeededContext();

        var deleted = db.Entities
            .Where(o => o.Status == "open")
            .Where(o => o.Quantity >= 20)
            .ExecuteDelete();

        Assert.Equal(1, deleted);
        Assert.Equal(2, db.Entities.Count());
        Assert.DoesNotContain(db.Entities.ToList(), o => o.Status == "open" && o.Quantity >= 20);
    }

    [Fact]
    public void ExecuteDelete_with_OrderBy_Take_deletes_lowest_quantities()
    {
        using var db = CreateSeededContext();

        var deleted = db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();

        Assert.Equal(2, deleted);
        var remaining = db.Entities.OrderBy(o => o.Quantity).ToList();
        Assert.Single(remaining);
        Assert.Equal(30, remaining[0].Quantity);
    }

    [Fact]
    public void ExecuteDelete_with_Where_and_Skip_Take_scopes_to_window()
    {
        using var db = CreateSeededContext();

        var deleted = db.Entities.Where(o => o.Status == "open").OrderBy(o => o.Quantity).Skip(1).Take(1).ExecuteDelete();

        Assert.Equal(1, deleted);
        Assert.Equal(2, db.Entities.Count());
        Assert.DoesNotContain(db.Entities.ToList(), o => o.Quantity == 20);
    }

    [Fact]
    public void ExecuteDelete_two_phase_empty_target_returns_zero()
    {
        using var db = CreateSeededContext();

        var deleted = db.Entities.Where(o => o.Status == "nonexistent").OrderBy(o => o.Quantity).Take(5).ExecuteDelete();

        Assert.Equal(0, deleted);
        Assert.Equal(3, db.Entities.Count());
    }

    [Fact]
    public void ExecuteDelete_two_phase_rolls_back_inside_user_transaction()
    {
        using var db = CreateSeededContext();
        using (var tx = db.Database.BeginTransaction())
        {
            var deleted = db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();
            Assert.Equal(2, deleted);
            tx.Rollback();
        }

        Assert.Equal(3, db.Entities.Count());
    }

    [Fact]
    public void ExecuteDelete_two_phase_with_AutoTransactionBehavior_Never_throws_and_changes_nothing()
    {
        using var db = CreateSeededContext();
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete());

        Assert.Contains("AutoTransactionBehavior", ex.Message);
        Assert.Equal(3, db.Entities.Count()); // nothing deleted
    }

    [Fact]
    public void ExecuteDelete_single_command_with_AutoTransactionBehavior_Never_still_works()
    {
        using var db = CreateSeededContext();
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

        // Where-only path is single-command and needs no transaction — Never must not affect it.
        var deleted = db.Entities.Where(o => o.Status == "open").ExecuteDelete();

        Assert.Equal(2, deleted);
    }

    [Fact]
    public void ExecuteDelete_with_Distinct_deletes_matching()
    {
        // Distinct on a root entity triggers the two-phase execution path: phase 1 reads the
        // distinct matching _ids, phase 2 deletes by { _id: { $in: [...] } }.
        using var db = CreateSeededContext();

        var deleted = db.Entities.Where(o => o.Status == "open").Distinct().ExecuteDelete();

        Assert.Equal(2, deleted);
        Assert.Equal(1, db.Entities.Count());
        Assert.Equal("closed", db.Entities.Single().Status);
    }

    [Fact(Skip = "Requires a standalone (non-replica-set) MongoDB to exercise the transaction-unsupported path; "
        + "CI/local use a replica set (atlas-local). The error mapping is covered by IsTransactionsUnsupported. EF-107")]
    public void ExecuteDelete_two_phase_on_standalone_throws_actionable_error()
    {
        // Intentionally skipped — see Skip reason.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteDelete_two_phase_logs_ExecutingBulkDelete_with_target_count(bool async)
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var collection = database.CreateCollection<Order>(
            nameof(ExecuteDelete_two_phase_logs_ExecutingBulkDelete_with_target_count), async);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureModel))
        {
            seedDb.AddRange(
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 10 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 20 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "closed", Quantity = 30 });
            seedDb.SaveChanges();
        }

        using var db = SingleEntityDbContext.Create(collection, loggerFactory, ConfigureModel);

        // Two-phase path: OrderBy + Take triggers phase-1 id selection then phase-2 DeleteByIds.
        var deleted = async
            ? await db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDeleteAsync()
            : db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();

        Assert.Equal(2, deleted);

        var executingMessage = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutingBulkDelete);
        Assert.Contains("Executing Bulk Delete", executingMessage);
        Assert.Contains($"Collection='{collection.CollectionNamespace}'", executingMessage);
        Assert.Contains("two-phase", executingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 target(s)", executingMessage);
    }

    [Fact]
    public void ExecuteDelete_two_phase_commits_inside_user_transaction()
    {
        using var db = CreateSeededContext();
        using (var tx = db.Database.BeginTransaction())
        {
            // Two-phase runs on the ambient session; the provider must NOT commit the user's transaction.
            var deleted = db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();
            Assert.Equal(2, deleted);
            tx.Commit();
        }

        // Committed by the user: the two-phase delete persisted.
        Assert.Equal(1, db.Entities.Count());
        Assert.Equal(30, db.Entities.Single().Quantity);
    }

    private class LineItem
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public string Status { get; set; } = null!;
        public int Quantity { get; set; }
    }

    private static readonly Action<ModelBuilder> ConfigureCompositeKeyModel = mb =>
    {
        // Composite primary key maps to a composite _id sub-document in MongoDB.
        mb.Entity<LineItem>().HasKey(e => new { e.OrderId, e.ProductId });
    };

    private SingleEntityDbContext<LineItem> CreateSeededCompositeKeyContext([CallerMemberName] string? collectionName = null)
    {
        var collection = database.CreateCollection<LineItem>(collectionName);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureCompositeKeyModel))
        {
            seedDb.AddRange(
                new LineItem { OrderId = 1, ProductId = 1, Status = "open", Quantity = 10 },
                new LineItem { OrderId = 1, ProductId = 2, Status = "open", Quantity = 20 },
                new LineItem { OrderId = 2, ProductId = 1, Status = "closed", Quantity = 30 });
            seedDb.SaveChanges();
        }

        return SingleEntityDbContext.Create(collection, ConfigureCompositeKeyModel);
    }

    [Fact]
    public void ExecuteDelete_two_phase_with_composite_key_deletes_targeted_rows()
    {
        using var db = CreateSeededCompositeKeyContext();

        // Two-phase on a composite-key entity: phase 1 must collect composite _id sub-documents,
        // phase 2 deletes by { _id: { $in: [ { OrderId, ProductId }, ... ] } }.
        var deleted = db.Entities.OrderBy(o => o.Quantity).Take(2).ExecuteDelete();

        Assert.Equal(2, deleted);
        var remaining = db.Entities.Single();
        Assert.Equal(30, remaining.Quantity);
    }
}

#endif
