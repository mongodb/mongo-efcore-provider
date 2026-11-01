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

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// <c>First</c>, <c>FirstOrDefault</c>, <c>Single</c> and <c>SingleOrDefault</c> read their row off the cursor with the same one-pass reader as a
/// list. The entity (with its owned parts), a projection, the errors for none or too many rows, and tracking must be what the DOM shaper gave.
/// </summary>
public class NativeStreamingSingleResultTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private static SingleEntityDbContext<T> CreateContext<T>(
        IMongoCollection<T> collection, MongoQueryMode mode, Action<ModelBuilder>? model = null)
        where T : class
        => SingleEntityDbContext.Create(
            collection,
            model,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private class Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public int Count { get; set; }
        public Shipping? Shipping { get; set; }
        public List<Line> Lines { get; set; } = [];
    }

    private class Shipping
    {
        public string Carrier { get; set; } = "";
    }

    private class Line
    {
        public string Sku { get; set; } = "";
    }

    private static readonly Action<ModelBuilder> OrderModel = mb =>
        mb.Entity<Order>(b =>
        {
            b.OwnsOne(o => o.Shipping);
            b.OwnsMany(o => o.Lines);
        });

    private IMongoCollection<Order> SeedOrders([CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Order>(name);
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument
            {
                { "_id", 1 }, { "Code", "one" }, { "Count", 10 },
                { "Shipping", new BsonDocument { { "Carrier", "air" } } },
                { "Lines", new BsonArray { new BsonDocument { { "Sku", "a" } }, new BsonDocument { { "Sku", "b" } } } }
            },
            new BsonDocument { { "_id", 2 }, { "Code", "two" }, { "Count", 20 }, { "Shipping", BsonNull.Value }, { "Lines", new BsonArray() } },
            new BsonDocument { { "_id", 3 }, { "Code", "three" }, { "Count", 20 } },
        ]);
        return collection;
    }

    private static string Describe(Order? o)
        => o == null ? "<null>" : $"{o.Id}:{o.Code}:{o.Shipping?.Carrier ?? "-"}:[{string.Join(",", o.Lines.Select(l => l.Sku))}]";

    [Fact]
    public void Single_result_entity_queries_match_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return new List<string>
            {
                Describe(db.Entities.AsNoTracking().OrderBy(o => o.Id).First()),
                Describe(db.Entities.AsNoTracking().OrderBy(o => o.Id).FirstOrDefault()),
                Describe(db.Entities.AsNoTracking().Single(o => o.Id == 1)),
                Describe(db.Entities.AsNoTracking().SingleOrDefault(o => o.Id == 2)),
                Describe(db.Entities.AsNoTracking().SingleOrDefault(o => o.Id == 99)),
                Describe(db.Entities.AsNoTracking().FirstOrDefault(o => o.Count > 100)),
                Describe(db.Entities.OrderBy(o => o.Id).First()),
            };
        });

        Assert.Equal(
            [
                "1:one:air:[a,b]", "1:one:air:[a,b]", "1:one:air:[a,b]", "2:two:-:[]", "<null>", "<null>", "1:one:air:[a,b]"
            ],
            results);
    }

    [Fact]
    public void Single_result_projections_match_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var scalar = db.Entities.AsNoTracking().OrderBy(o => o.Id).Select(o => o.Code).First();
            var pair = db.Entities.AsNoTracking().Where(o => o.Id == 2).Select(o => new { o.Code, Doubled = o.Count * 2 }).Single();
            var lines = db.Entities.AsNoTracking().Where(o => o.Id == 1).Select(o => new { o.Code, o.Lines }).Single();
            var shipping = db.Entities.AsNoTracking().Where(o => o.Id == 1).Select(o => new { o.Code, o.Shipping }).Single();
            var wrapped = db.Entities.AsNoTracking().Where(o => o.Id == 2).Select(o => Describe(o)).Single();
            return new List<string>
            {
                scalar,
                $"{pair.Code}|{pair.Doubled}",
                $"{lines.Code}|{string.Join(",", lines.Lines.Select(l => l.Sku))}",
                $"{shipping.Code}|{shipping.Shipping?.Carrier}",
                wrapped,
            };
        });

        Assert.Equal(["one", "two|40", "one|a,b", "one|air", "2:two:-:[]"], results);
    }

    [Fact]
    public void Errors_for_no_row_and_for_too_many_rows_are_the_same()
    {
        var collection = SeedOrders();

        foreach (var mode in new[] { MongoQueryMode.DriverLinq, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode, OrderModel);

            Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().First(o => o.Id == 99));
            Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().Single(o => o.Id == 99));
            Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().Single(o => o.Count == 20));
            Assert.Throws<InvalidOperationException>(() => db.Entities.Single(o => o.Count == 20));
            Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().Where(o => o.Count == 20).Select(o => o.Code).Single());
        }
    }

    [Fact]
    public void Tracked_single_result_is_tracked_once_and_resolves_to_the_tracked_instance()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var first = db.Entities.Single(o => o.Id == 1);
            var again = db.Entities.First(o => o.Id == 1);
            var wrapped = db.Entities.Where(o => o.Id == 1).Select(o => new { Item = o, o.Code.Length }).Single();
            return new List<string>
            {
                $"same={ReferenceEquals(first, again)}/{ReferenceEquals(first, wrapped.Item)}",
                $"entries={db.ChangeTracker.Entries<Order>().Count()}",
                $"state={db.Entry(first).State}",
            };
        });

        Assert.Equal(["same=True/True", "entries=1", "state=Unchanged"], results);
    }
}
