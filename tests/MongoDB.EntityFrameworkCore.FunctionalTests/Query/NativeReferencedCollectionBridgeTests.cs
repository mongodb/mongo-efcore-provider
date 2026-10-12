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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// The driver-LINQ bridge parity of the aggregate, quantifier, predicate and ordering leaves over a cross-document collection. Same
/// data and expectations as <see cref="NativeReferencedCollectionAggregateTests"/> and
/// <see cref="NativeReferencedCollectionPredicateTests"/>, but every mode, explicit <see cref="MongoQueryMode.DriverLinq"/> included,
/// must answer exactly, not refuse.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferencedCollectionBridgeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Cust
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<Ord> Orders { get; set; } = [];
    }

    public class Ord
    {
        public int Id { get; set; }
        public int Total { get; set; }
        public string? CustId { get; set; }
        public Cust? Cust { get; set; }
    }

    // Customers A1 (orders 10, 20, 600), A2 (700), B1 (30) and C0 (none). Order 6 is dangling (ZZ) and 7 has no customer.
    private string[] Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new[]
        {
            TemporaryDatabaseFixtureBase.CreateCollectionName("rcbCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("rcbOrds") + suffix
        };

        database.MongoDatabase.GetCollection<BsonDocument>(names[0]).InsertMany(
        [
            new BsonDocument { { "_id", "A1" }, { "Name", "Alpha1" } },
            new BsonDocument { { "_id", "A2" }, { "Name", "Alpha2" } },
            new BsonDocument { { "_id", "B1" }, { "Name", "Beta1" } },
            new BsonDocument { { "_id", "C0" }, { "Name", "Nobody" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names[1]).InsertMany(
        [
            new BsonDocument { { "_id", 1 }, { "Total", 10 }, { "CustId", "A1" } },
            new BsonDocument { { "_id", 2 }, { "Total", 20 }, { "CustId", "A1" } },
            new BsonDocument { { "_id", 3 }, { "Total", 600 }, { "CustId", "A1" } },
            new BsonDocument { { "_id", 4 }, { "Total", 700 }, { "CustId", "A2" } },
            new BsonDocument { { "_id", 5 }, { "Total", 30 }, { "CustId", "B1" } },
            new BsonDocument { { "_id", 6 }, { "Total", 40 }, { "CustId", "ZZ" } },
            new BsonDocument { { "_id", 7 }, { "Total", 50 }, { "CustId", BsonNull.Value } }
        ]);
        return names;
    }

    private void Strict3<T>(Func<Ctx, IQueryable<T>> query, Func<T, string> format, List<string> expected, bool sorted = true)
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);
            var formatted = query(db).AsEnumerable().Select(format);
            var rows = (sorted ? formatted.OrderBy(s => s) : formatted).ToList();
            Assert.True(expected.SequenceEqual(rows), $"{mode} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}]");
        }
    }

    // ---- projected aggregates and quantifiers

    [Fact]
    public void Any_without_a_predicate()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Any() }), z => z.Id + ":" + z.V,
            ["A1:True", "A2:True", "B1:True", "C0:False"]);

    [Fact]
    public void Any_with_a_predicate()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Any(o => o.Total > 100) }), z => z.Id + ":" + z.V,
            ["A1:True", "A2:True", "B1:False", "C0:False"]);

    [Fact]
    public void All_with_a_predicate_is_true_for_the_empty_group()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.All(o => o.Total > 15) }), z => z.Id + ":" + z.V,
            ["A1:False", "A2:True", "B1:True", "C0:True"]);

    [Fact]
    public void Count_with_a_predicate()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Count(o => o.Total > 15) }), z => z.Id + ":" + z.V,
            ["A1:2", "A2:1", "B1:1", "C0:0"]);

    [Fact]
    public void Sum_is_zero_for_the_empty_group()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total) }), z => z.Id + ":" + z.V,
            ["A1:630", "A2:700", "B1:30", "C0:0"]);

    [Fact]
    public void Min_over_a_nullable_selector_is_null_for_the_empty_group()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Min(o => (int?)o.Total) }), z => z.Id + ":" + z.V,
            ["A1:10", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Max_over_a_nullable_selector_is_null_for_the_empty_group()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Max(o => (int?)o.Total) }), z => z.Id + ":" + z.V,
            ["A1:600", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Average_over_a_nullable_selector_is_null_for_the_empty_group()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Average(o => (double?)o.Total) }), z => z.Id + ":" + z.V,
            ["A1:210", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Two_aggregates_in_one_projection()
        => Strict3(db => db.Set<Cust>().Select(c => new { c.Id, N = c.Orders.Count, S = c.Orders.Sum(o => o.Total) }),
            z => z.Id + ":" + z.N + ":" + z.S, ["A1:3:630", "A2:1:700", "B1:1:30", "C0:0:0"]);

    [Fact]
    public void Min_over_a_non_nullable_selector_throws_for_the_empty_group()
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);

            // The outcome (a throw) is the contract; the exception type for the malformed read is not (the driver's is a FormatException).
            Assert.ThrowsAny<Exception>(
                () => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Min(o => o.Total) }).AsEnumerable().ToList());
        }
    }

    // ---- predicates and orderings

    private void StrictIds(Func<Ctx, IQueryable<Cust>> query, List<string> expected, bool sorted = true)
        => Strict3(query, c => c.Id, expected, sorted);

    [Fact]
    public void Where_Any_with_a_predicate()
        => StrictIds(db => db.Set<Cust>().Where(c => c.Orders.Any(o => o.Total > 100)), ["A1", "A2"]);

    [Fact]
    public void Where_not_Any_with_a_predicate_keeps_the_empty_group()
        => StrictIds(db => db.Set<Cust>().Where(c => !c.Orders.Any(o => o.Total > 100)), ["B1", "C0"]);

    [Fact]
    public void Where_All_with_a_predicate_keeps_the_empty_group()
        => StrictIds(db => db.Set<Cust>().Where(c => c.Orders.All(o => o.Total > 15)), ["A2", "B1", "C0"]);

    [Fact]
    public void Where_Count_with_a_predicate_compared()
        => StrictIds(db => db.Set<Cust>().Where(c => c.Orders.Count(o => o.Total > 15) > 1), ["A1"]);

    [Fact]
    public void Where_Sum_compared()
        => StrictIds(db => db.Set<Cust>().Where(c => c.Orders.Sum(o => o.Total) > 100), ["A1", "A2"]);

    [Fact]
    public void Where_nullable_Max_compared_never_matches_the_empty_group()
        => StrictIds(db => db.Set<Cust>().Where(c => c.Orders.Max(o => (int?)o.Total) < 650), ["A1", "B1"]);

    [Fact]
    public void Where_nullable_Min_is_null_only_for_the_empty_group()
        => StrictIds(db => db.Set<Cust>().Where(c => c.Orders.Min(o => (int?)o.Total) == null), ["C0"]);

    [Fact]
    public void OrderBy_Count_descending_then_key()
        => StrictIds(db => db.Set<Cust>().OrderByDescending(c => c.Orders.Count).ThenBy(c => c.Id), ["A1", "A2", "B1", "C0"], sorted: false);

    [Fact]
    public void OrderBy_Sum_then_key_with_Take()
        => StrictIds(db => db.Set<Cust>().OrderBy(c => c.Orders.Sum(o => o.Total)).ThenBy(c => c.Id).Take(2), ["C0", "B1"], sorted: false);

    [Fact]
    public void OrderBy_nullable_Min_puts_the_empty_group_first()
        => StrictIds(db => db.Set<Cust>().OrderBy(c => c.Orders.Min(o => (int?)o.Total)).ThenBy(c => c.Id), ["C0", "A1", "B1", "A2"], sorted: false);

    // ---- a parameterized predicate, run twice on one context

    private static List<string> CountAbove(Ctx db, int cutoff)
        => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Count(o => o.Total > cutoff) })
            .AsEnumerable().Select(z => z.Id + ":" + z.V).OrderBy(s => s).ToList();

    [Fact]
    public void Parameterized_predicate_is_read_per_execution_in_every_mode()
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);
            NativeModeAssert.TwiceWithDifferentValues(
                v => CountAbove(db, (int)v!), 15, ["A1:2", "A2:1", "B1:1", "C0:0"], 650, ["A1:0", "A2:1", "B1:0", "C0:0"]);
        }
    }

    private Ctx Create(string[] names, MongoQueryMode mode)
    {
        var options = new DbContextOptionsBuilder<Ctx>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new Ctx(options, names);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class Ctx(DbContextOptions<Ctx> options, string[] names) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Cust>().ToCollection(names[0]);
            mb.Entity<Ord>(b =>
            {
                b.ToCollection(names[1]);
                b.HasOne(o => o.Cust).WithMany(c => c.Orders).HasForeignKey(o => o.CustId);
            });
        }
    }
}
