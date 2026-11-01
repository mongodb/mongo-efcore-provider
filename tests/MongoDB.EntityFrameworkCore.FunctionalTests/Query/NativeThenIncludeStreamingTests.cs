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

using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A <c>ThenInclude</c> off a reference Include is read straight off the cursor, like the Include it hangs off: a further reference is a
/// <c>$lookup</c> of its own beside its parent's, and a collection is an array inside its parent's document. The graph, the identity of shared
/// entities and what is tracked must be what the DOM shaper produced, including for a parent or a child that is missing.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeThenIncludeStreamingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Line
    {
        public int Id { get; set; }
        public int? OrderId { get; set; }
        public Order? Order { get; set; }
    }

    private class Order
    {
        public int Id { get; set; }
        public int? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Note> Notes { get; set; } = [];
    }

    private class Ticket
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public Customer Customer { get; set; } = null!;
    }

    private class Shipment
    {
        public int Id { get; set; }
        public int TicketId { get; set; }
        public Ticket Ticket { get; set; } = null!;
    }

    private class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public int CustomerId { get; set; }
    }

    private sealed class ChainContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Line> Lines => Set<Line>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Ticket> Tickets => Set<Ticket>();
        public DbSet<Shipment> Shipments => Set<Shipment>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Line>(b =>
            {
                b.ToCollection("ChainLines" + suffix);
                b.HasOne(l => l.Order).WithMany().HasForeignKey(l => l.OrderId);
            });
            mb.Entity<Order>(b =>
            {
                b.ToCollection("ChainOrders" + suffix);
                b.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
            });
            mb.Entity<Customer>(b =>
            {
                b.ToCollection("ChainCustomers" + suffix);
                b.HasMany(c => c.Notes).WithOne().HasForeignKey(n => n.CustomerId);
            });
            mb.Entity<Note>().ToCollection("ChainNotes" + suffix);
            mb.Entity<Ticket>(b =>
            {
                b.ToCollection("ChainTickets" + suffix);
                b.HasOne(t => t.Customer).WithMany().HasForeignKey(t => t.CustomerId).IsRequired();
            });
            mb.Entity<Shipment>(b =>
            {
                b.ToCollection("ChainShipments" + suffix);
                b.HasOne(s => s.Ticket).WithMany().HasForeignKey(s => s.TicketId).IsRequired();
            });
        }
    }

    private ChainContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<ChainContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    // Tickets 1 and 2 share customer 100 (two notes), ticket 3 has customer 101 (no notes), ticket 4's customer is not there; shipments 1 and 2 are
    // for ticket 1, shipment 3 for ticket 3. The relationships are required: a document whose reference is missing is not a result.
    // Lines 1 and 2 share order 10 (customer 100, two notes); line 3 has order 11 (customer 101, no notes); line 4 has order 12 with no customer;
    // line 5 points at an order that is not there; line 6 has no order at all. Order 13 (customer 99 is missing) is only reached directly.
    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var lines = database.MongoDatabase.GetCollection<BsonDocument>("ChainLines" + suffix);
        var orders = database.MongoDatabase.GetCollection<BsonDocument>("ChainOrders" + suffix);
        var customers = database.MongoDatabase.GetCollection<BsonDocument>("ChainCustomers" + suffix);
        var notes = database.MongoDatabase.GetCollection<BsonDocument>("ChainNotes" + suffix);
        var tickets = database.MongoDatabase.GetCollection<BsonDocument>("ChainTickets" + suffix);
        var shipments = database.MongoDatabase.GetCollection<BsonDocument>("ChainShipments" + suffix);

        customers.InsertMany(
        [
            new BsonDocument { { "_id", 100 }, { "Name", "ann" } },
            new BsonDocument { { "_id", 101 }, { "Name", "bob" } }
        ]);
        notes.InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Text", "n1" }, { "CustomerId", 100 } },
            new BsonDocument { { "_id", 2 }, { "Text", "n2" }, { "CustomerId", 100 } }
        ]);
        orders.InsertMany(
        [
            Order(10, 100), Order(11, 101), Order(12, null), Order(13, 99)
        ]);
        lines.InsertMany(
        [
            Line(1, 10), Line(2, 10), Line(3, 11), Line(4, 12), Line(5, 77), Line(6, null)
        ]);
        tickets.InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "CustomerId", 100 } },
            new BsonDocument { { "_id", 2 }, { "CustomerId", 100 } },
            new BsonDocument { { "_id", 3 }, { "CustomerId", 101 } },
            new BsonDocument { { "_id", 4 }, { "CustomerId", 99 } }
        ]);
        shipments.InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "TicketId", 1 } },
            new BsonDocument { { "_id", 2 }, { "TicketId", 1 } },
            new BsonDocument { { "_id", 3 }, { "TicketId", 3 } },
            new BsonDocument { { "_id", 4 }, { "TicketId", 4 } }
        ]);
        return suffix;

        static BsonDocument Order(int id, int? customerId)
            => new() { { "_id", id }, { "CustomerId", customerId == null ? BsonNull.Value : customerId } };

        static BsonDocument Line(int id, int? orderId)
            => new() { { "_id", id }, { "OrderId", orderId == null ? BsonNull.Value : orderId } };
    }

    private static Type CursorRowType<T>(IQueryable<T> query)
    {
        var enumerator = ((IEnumerable)query).GetEnumerator();
        try
        {
            return enumerator.GetType().GetGenericArguments()[0];
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private static void AssertStreams<T>(IQueryable<T> query)
        => Assert.NotEqual(typeof(BsonDocument), CursorRowType(query));

    private static string Describe(Customer c)
        => c.Name + "[" + string.Join(",", c.Notes.OrderBy(n => n.Id).Select(n => n.Text)) + "]";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_reference_then_a_reference_streams_and_matches_driver_linq(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var query = tracked ? db.Lines.AsTracking() : db.Lines.AsNoTracking();
            var lines = query.Include(l => l.Order).ThenInclude(o => o!.Customer).OrderBy(l => l.Id).ToList();
            return lines.Select(l => $"{l.Id}:{(l.Order == null ? "-" : l.Order.Id + "/" + (l.Order.Customer?.Name ?? "-"))}").ToList();
        });

        Assert.Equal(["1:10/ann", "2:10/ann", "3:11/bob", "4:12/-", "5:-", "6:-"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Lines.AsNoTracking().Include(l => l.Order).ThenInclude(o => o!.Customer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_collection_under_an_optional_reference_that_matched_nothing_is_no_customer(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var query = tracked ? db.Orders.AsTracking() : db.Orders.AsNoTracking();
            return query.Include(o => o.Customer).ThenInclude(c => c!.Notes).OrderBy(o => o.Id).ToList()
                .Select(o => $"{o.Id}:{(o.Customer == null ? "-" : Describe(o.Customer))}").ToList();
        });

        Assert.Equal(["10:ann[n1,n2]", "11:bob[]", "12:-", "13:-"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Orders.AsNoTracking().Include(o => o.Customer).ThenInclude(c => c!.Notes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_required_reference_then_a_collection_streams_and_matches_driver_linq(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var query = tracked ? db.Tickets.AsTracking() : db.Tickets.AsNoTracking();
            return query.Include(t => t.Customer).ThenInclude(c => c.Notes).OrderBy(t => t.Id).ToList()
                .Select(t => $"{t.Id}:{Describe(t.Customer)}").ToList();
        });

        Assert.Equal(["1:ann[n1,n2]", "2:ann[n1,n2]", "3:bob[]"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Tickets.AsNoTracking().Include(t => t.Customer).ThenInclude(c => c.Notes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Three_levels_of_required_references_and_a_collection_stream_and_match_driver_linq(bool tracked)
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var query = tracked ? db.Shipments.AsTracking() : db.Shipments.AsNoTracking();
            return query.Include(s => s.Ticket).ThenInclude(t => t.Customer).ThenInclude(c => c.Notes).OrderBy(s => s.Id).ToList()
                .Select(s => $"{s.Id}:{s.Ticket.Id}/{Describe(s.Ticket.Customer)}").ToList();
        });

        Assert.Equal(["1:1/ann[n1,n2]", "2:1/ann[n1,n2]", "3:3/bob[]"], results);

        using var streaming = Db(MongoQueryMode.NativeOnly, suffix);
        AssertStreams(streaming.Shipments.AsNoTracking().Include(s => s.Ticket).ThenInclude(t => t.Customer).ThenInclude(c => c.Notes));
    }

    [Fact]
    public void A_tracked_chain_shares_one_instance_per_entity_and_tracks_each_once()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var lines = db.Lines.AsTracking().Include(l => l.Order).ThenInclude(o => o!.Customer).OrderBy(l => l.Id).ToList();
            return new List<string>
            {
                $"order={ReferenceEquals(lines[0].Order, lines[1].Order)}",
                $"customer={ReferenceEquals(lines[0].Order!.Customer, lines[1].Order!.Customer)}",
                $"lines={db.ChangeTracker.Entries<Line>().Count()}",
                $"orders={db.ChangeTracker.Entries<Order>().Count()}",
                $"customers={db.ChangeTracker.Entries<Customer>().Count()}",
            };
        });

        Assert.Equal(["order=True", "customer=True", "lines=6", "orders=3", "customers=2"], results);
    }
}
