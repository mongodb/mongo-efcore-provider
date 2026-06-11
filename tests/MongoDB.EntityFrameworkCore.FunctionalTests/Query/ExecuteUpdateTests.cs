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
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

public class ExecuteUpdateTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId _id { get; set; }
        public string Status { get; set; } = null!; // mapped to "state" via HasElementName
        public int Quantity { get; set; }
        [NotMapped] public string Unmapped { get; set; } = null!;
    }

    private class OrderWithConverter
    {
        public ObjectId _id { get; set; }
        public int Quantity { get; set; } // stored as string via HasConversion
    }

    private static readonly Action<ModelBuilder> ConfigureConverterModel = mb =>
    {
        mb.Entity<OrderWithConverter>().Property(e => e.Quantity).HasConversion<string>();
    };

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
    public void ExecuteUpdate_constant_set_updates_matching_and_returns_count()
    {
        using var db = CreateSeededContext();

        var updated = db.Entities
            .Where(o => o.Status == "open")
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, "shipped"));

        Assert.Equal(2, updated);
        Assert.Equal(2, db.Entities.Count(o => o.Status == "shipped"));
        Assert.Equal(1, db.Entities.Count(o => o.Status == "closed"));
    }

    [Fact]
    public void ExecuteUpdate_returns_matched_count_even_when_values_already_match()
    {
        using var db = CreateSeededContext();

        // Both matched docs already have Status "open", so the server reports ModifiedCount 0;
        // ExecuteUpdate must still return the matched-row count (2), matching EF Core's contract
        // (relational providers count a row even when SET writes its existing value).
        var updated = db.Entities
            .Where(o => o.Status == "open")
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, "open"));

        Assert.Equal(2, updated);
    }

    [Fact]
    public void ExecuteUpdate_with_ef_property_setter_updates_matching()
    {
        using var db = CreateSeededContext();

        // The setter target is EF.Property<string>(o, "Status") rather than a direct member access.
        var updated = db.Entities
            .Where(o => o.Status == "open")
            .ExecuteUpdate(s => s.SetProperty(o => EF.Property<string>(o, nameof(Order.Status)), "shipped"));

        Assert.Equal(2, updated);
        Assert.Equal(2, db.Entities.Count(o => o.Status == "shipped"));
    }

    [Fact]
    public void ExecuteUpdate_with_captured_variable_set_value()
    {
        using var db = CreateSeededContext();

        var newStatus = "processing";
        var updated = db.Entities
            .Where(o => o.Quantity >= 20)
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, newStatus));

        Assert.Equal(2, updated);
        Assert.Equal(2, db.Entities.Count(o => o.Status == "processing"));
    }

    [Fact]
    public void ExecuteUpdate_self_referencing_increments_matched_docs()
    {
        using var db = CreateSeededContext();

        var updated = db.Entities
            .Where(o => o.Status == "open")
            .ExecuteUpdate(s => s.SetProperty(o => o.Quantity, o => o.Quantity + 1));

        Assert.Equal(2, updated);
        var quantities = db.Entities.OrderBy(o => o.Quantity).Select(o => o.Quantity).ToList();
        // open: 10 -> 11, 20 -> 21; closed unchanged: 30
        Assert.Equal(new[] { 11, 21, 30 }, quantities);
    }

    [Fact]
    public async Task ExecuteUpdateAsync_self_referencing_increments_matched_docs()
    {
        using var db = CreateSeededContext();

        var updated = await db.Entities
            .Where(o => o.Quantity >= 20)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Quantity, o => o.Quantity * 2));

        Assert.Equal(2, updated);
        var quantities = db.Entities.OrderBy(o => o.Quantity).Select(o => o.Quantity).ToList();
        // 10 unchanged; 20 -> 40; 30 -> 60
        Assert.Equal(new[] { 10, 40, 60 }, quantities);
    }

    [Fact]
    public void ExecuteUpdate_mixed_constant_and_self_referencing_in_one_call()
    {
        using var db = CreateSeededContext();

        var updated = db.Entities
            .Where(o => o.Status == "open")
            .ExecuteUpdate(s => s
                .SetProperty(o => o.Status, "bumped")
                .SetProperty(o => o.Quantity, o => o.Quantity + 5));

        Assert.Equal(2, updated);
        var bumped = db.Entities.Where(o => o.Status == "bumped").OrderBy(o => o.Quantity).Select(o => o.Quantity).ToList();
        Assert.Equal(new[] { 15, 25 }, bumped);
        Assert.Equal(1, db.Entities.Count(o => o.Status == "closed"));
    }

    [Fact]
    public void ExecuteUpdate_without_predicate_updates_everything()
    {
        using var db = CreateSeededContext();

        var updated = db.Entities.ExecuteUpdate(s => s.SetProperty(o => o.Status, "all"));

        Assert.Equal(3, updated);
        Assert.Equal(3, db.Entities.Count(o => o.Status == "all"));
    }

    [Fact]
    public void ExecuteUpdate_with_OrderBy_Take_updates_only_targeted_rows()
    {
        using var db = CreateSeededContext();

        // Two-phase: phase 1 reads the top-2 _ids (ordered by Quantity), phase 2 applies the update.
        var updated = db.Entities.OrderBy(o => o.Quantity).Take(2)
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived"));

        Assert.Equal(2, updated);
        Assert.Equal(2, db.Entities.Count(o => o.Status == "archived"));
        // The highest-quantity row must be untouched.
        Assert.Equal(1, db.Entities.Count(o => o.Status == "closed"));
    }

    [Fact]
    public void ExecuteUpdate_two_phase_self_referencing_setter()
    {
        using var db = CreateSeededContext();

        // Two-phase: targets the single lowest-quantity document and increments its Quantity by 5.
        var updated = db.Entities.OrderBy(o => o.Quantity).Take(1)
            .ExecuteUpdate(s => s.SetProperty(o => o.Quantity, o => o.Quantity + 5));

        Assert.Equal(1, updated);
        // Lowest seeded Quantity is 10; +5 = 15.
        Assert.Contains(db.Entities.ToList(), o => o.Quantity == 15);
    }

    [Fact]
    public void ExecuteUpdate_two_phase_rolls_back_inside_user_transaction()
    {
        using var db = CreateSeededContext();

        using (var tx = db.Database.BeginTransaction())
        {
            db.Entities.OrderBy(o => o.Quantity).Take(2)
                .ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived"));
            tx.Rollback();
        }

        Assert.Equal(0, db.Entities.Count(o => o.Status == "archived"));
    }

    [Fact]
    public void ExecuteUpdate_two_phase_with_AutoTransactionBehavior_Never_throws_and_changes_nothing()
    {
        using var db = CreateSeededContext();
        db.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.OrderBy(o => o.Quantity).Take(2)
                .ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived")));

        Assert.Contains("AutoTransactionBehavior", ex.Message);
        Assert.Equal(0, db.Entities.Count(o => o.Status == "archived")); // nothing updated
    }

#if EF9
    // EF10 changed the setter parameter to an Action and validates "at least one SetProperty" upstream,
    // so the empty-setter guard only applies to the EF9 translation path (where the raw lambda reaches us).
    [Fact]
    public void ExecuteUpdate_with_no_set_property_throws()
    {
        using var db = CreateSeededContext();

        // A setter lambda with no SetProperty call must be rejected, not run as a no-op updateMany.
        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.ExecuteUpdate(s => s));
        Assert.Contains("SetProperty", ex.Message);
    }
#endif

    [Fact]
    public void ExecuteUpdate_targeting_unmapped_property_throws()
    {
        using var db = CreateSeededContext();

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.ExecuteUpdate(s => s.SetProperty(o => o.Unmapped, "x")));
        Assert.Contains("Only mapped root scalar properties can be updated", ex.Message);
    }

    [Fact]
    public void ExecuteUpdate_self_referencing_value_converter_property_throws()
    {
        var collection = database.CreateCollection<OrderWithConverter>();
        using var db = SingleEntityDbContext.Create(collection, ConfigureConverterModel);

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.Entities.ExecuteUpdate(s => s.SetProperty(o => o.Quantity, o => o.Quantity + 1)));
        Assert.Contains("Self-referencing ExecuteUpdate on property", ex.Message);
        Assert.Contains("value converter", ex.Message);
    }

    [Fact]
    public void ExecuteUpdate_constant_set_on_value_converter_property_persists_converted_shape()
    {
        var collection = database.CreateCollection<OrderWithConverter>();
        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureConverterModel))
        {
            seedDb.AddRange(
                new OrderWithConverter { _id = ObjectId.GenerateNewId(), Quantity = 1 },
                new OrderWithConverter { _id = ObjectId.GenerateNewId(), Quantity = 2 });
            seedDb.SaveChanges();
        }

        using var db = SingleEntityDbContext.Create(collection, ConfigureConverterModel);
        var updated = db.Entities.ExecuteUpdate(s => s.SetProperty(o => o.Quantity, 99));
        Assert.Equal(2, updated);

        // The constant must be serialized through the converter-aware property path, so it persists
        // as a BSON string ("99"), not a raw int — matching how SaveChanges writes the property.
        var raw = collection.Database
            .GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty).ToList();
        Assert.Equal(2, raw.Count);
        Assert.All(raw, d => Assert.Equal(BsonType.String, d["Quantity"].BsonType));
        Assert.All(raw, d => Assert.Equal("99", d["Quantity"].AsString));

        // And it round-trips back through EF as the original CLR int.
        Assert.All(db.Entities.ToList(), o => Assert.Equal(99, o.Quantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteUpdate_logs_ExecutedBulkUpdate_event_with_count(bool async)
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var collection = database.CreateCollection<Order>(nameof(ExecuteUpdate_logs_ExecutedBulkUpdate_event_with_count), async);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureModel))
        {
            seedDb.AddRange(
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 10 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 20 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "closed", Quantity = 30 });
            seedDb.SaveChanges();
        }

        using var db = SingleEntityDbContext.Create(collection, loggerFactory, ConfigureModel);

        var updated = async
            ? await db.Entities.Where(o => o.Status == "open")
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "shipped"))
            : db.Entities.Where(o => o.Status == "open")
                .ExecuteUpdate(s => s.SetProperty(o => o.Status, "shipped"));

        Assert.Equal(2, updated);

        var executingMessage = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutingBulkUpdate);
        Assert.Contains("Executing Bulk Update", executingMessage);
        Assert.Contains($"Collection='{collection.CollectionNamespace}'", executingMessage);

        var executedMessage = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutedBulkUpdate);
        Assert.Contains("Executed Bulk Update", executedMessage);
        Assert.Contains($"Collection='{collection.CollectionNamespace}'", executedMessage);
        Assert.Contains("Modified=2", executedMessage);
    }

    [Fact]
    public void ExecuteUpdate_two_phase_empty_target_returns_zero()
    {
        using var db = CreateSeededContext();

        // Two-phase phase 1 finds no targets, so phase 2 is skipped and nothing is written.
        var updated = db.Entities.Where(o => o.Status == "nonexistent").OrderBy(o => o.Quantity).Take(5)
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, "x"));

        Assert.Equal(0, updated);
        Assert.Equal(0, db.Entities.Count(o => o.Status == "x"));
    }

    [Fact]
    public void ExecuteUpdate_two_phase_returns_matched_count_even_when_values_already_match()
    {
        using var db = CreateSeededContext();

        // Two-phase (OrderBy + Take) targeting the two "open" docs and setting Status to its existing value:
        // the server reports ModifiedCount 0, but ExecuteUpdate must still return the matched-row count (2)
        // through the { _id: { $in: ... } } UpdateMany path, matching EF Core's contract.
        var updated = db.Entities.Where(o => o.Status == "open").OrderBy(o => o.Quantity).Take(2)
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, "open"));

        Assert.Equal(2, updated);
    }

    [Fact]
    public void ExecuteUpdate_with_Where_and_Skip_Take_scopes_to_window()
    {
        using var db = CreateSeededContext();

        // "open" docs are Quantity 10 and 20; Skip(1).Take(1) over ascending Quantity targets only the 20.
        var updated = db.Entities.Where(o => o.Status == "open").OrderBy(o => o.Quantity).Skip(1).Take(1)
            .ExecuteUpdate(s => s.SetProperty(o => o.Status, "windowed"));

        Assert.Equal(1, updated);
        Assert.Equal(1, db.Entities.Count(o => o.Status == "windowed"));
        Assert.Contains(db.Entities.ToList(), o => o.Status == "windowed" && o.Quantity == 20);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteUpdate_two_phase_logs_ExecutingBulkUpdate_with_target_count(bool async)
    {
        var (loggerFactory, spyLogger) = SpyLoggerProvider.Create();
        var collection = database.CreateCollection<Order>(
            nameof(ExecuteUpdate_two_phase_logs_ExecutingBulkUpdate_with_target_count), async);

        using (var seedDb = SingleEntityDbContext.Create(collection, ConfigureModel))
        {
            seedDb.AddRange(
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 10 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "open", Quantity = 20 },
                new Order { _id = ObjectId.GenerateNewId(), Status = "closed", Quantity = 30 });
            seedDb.SaveChanges();
        }

        using var db = SingleEntityDbContext.Create(collection, loggerFactory, ConfigureModel);

        // Two-phase path (OrderBy + Take), exercised both sync and async.
        var updated = async
            ? await db.Entities.OrderBy(o => o.Quantity).Take(2)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, "archived"))
            : db.Entities.OrderBy(o => o.Quantity).Take(2)
                .ExecuteUpdate(s => s.SetProperty(o => o.Status, "archived"));

        Assert.Equal(2, updated);

        var executingMessage = spyLogger.GetLogMessageByEventId(MongoEventId.ExecutingBulkUpdate);
        Assert.Contains("Executing Bulk Update", executingMessage);
        Assert.Contains($"Collection='{collection.CollectionNamespace}'", executingMessage);
        Assert.Contains("two-phase", executingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2 target(s)", executingMessage);
    }
}

#endif
