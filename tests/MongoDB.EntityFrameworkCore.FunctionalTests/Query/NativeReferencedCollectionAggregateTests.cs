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
/// A projected aggregate or quantifier over a cross-document collection reached through a collection navigation
/// (<c>c.Orders.Sum(o =&gt; o.Total)</c>), design: <c>docs/superpowers/specs/2026-10-08-native-referenced-collection-aggregates-design.md</c>,
/// slice 1. Hand-computed over raw seeds, including a customer with no orders (the empty group) and orders whose
/// customer is dangling or null (never counted).
/// <para>
/// <see cref="MongoQueryMode.NativeOnly"/> and <see cref="MongoQueryMode.Native"/> must answer exactly. The explicit
/// <see cref="MongoQueryMode.DriverLinq"/> leg may answer correctly or refuse loudly, never answer wrongly: the driver-LINQ
/// bridge does not implement these leaves yet.
/// </para>
/// </summary>
public class NativeReferencedCollectionAggregateTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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
            TemporaryDatabaseFixtureBase.CreateCollectionName("rcaCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("rcaOrds") + suffix
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

    private void Assert3<T>(Func<Ctx, IQueryable<T>> query, Func<T, string> format, List<string> expected)
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);
            List<string>? rows = null;
            try
            {
                rows = query(db).AsEnumerable().Select(format).OrderBy(s => s).ToList();
            }
            catch (Exception) when (mode == MongoQueryMode.DriverLinq)
            {
                // The bridge may refuse these leaves for now (never answer wrongly).
            }

            if (rows is not null)
            {
                Assert.True(expected.SequenceEqual(rows), $"{mode} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}]");
            }
            else
            {
                Assert.Equal(MongoQueryMode.DriverLinq, mode);
            }
        }
    }

    [Fact]
    public void Any_without_a_predicate()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Any() }), z => z.Id + ":" + z.V,
            ["A1:True", "A2:True", "B1:True", "C0:False"]);

    [Fact]
    public void Any_with_a_predicate()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Any(o => o.Total > 100) }), z => z.Id + ":" + z.V,
            ["A1:True", "A2:True", "B1:False", "C0:False"]);

    [Fact]
    public void All_with_a_predicate_is_true_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.All(o => o.Total > 15) }), z => z.Id + ":" + z.V,
            ["A1:False", "A2:True", "B1:True", "C0:True"]);

    [Fact]
    public void Count_with_a_predicate()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Count(o => o.Total > 15) }), z => z.Id + ":" + z.V,
            ["A1:2", "A2:1", "B1:1", "C0:0"]);

    [Fact]
    public void Sum_is_zero_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total) }), z => z.Id + ":" + z.V,
            ["A1:630", "A2:700", "B1:30", "C0:0"]);

    [Fact]
    public void Min_over_a_nullable_selector_is_null_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Min(o => (int?)o.Total) }), z => z.Id + ":" + z.V,
            ["A1:10", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Max_over_a_nullable_selector_is_null_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Max(o => (int?)o.Total) }), z => z.Id + ":" + z.V,
            ["A1:600", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Average_over_a_nullable_selector_is_null_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Average(o => (double?)o.Total) }), z => z.Id + ":" + z.V,
            ["A1:210", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Two_aggregates_in_one_projection()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, N = c.Orders.Count, S = c.Orders.Sum(o => o.Total) }),
            z => z.Id + ":" + z.N + ":" + z.S,
            ["A1:3:630", "A2:1:700", "B1:1:30", "C0:0:0"]);

    // ---- a computed selector: arithmetic over the element's own numeric properties. Orders (Id, Total): A1 (1, 10) (2, 20) (3, 600),
    // A2 (4, 700), B1 (5, 30), C0 none.

    [Fact]
    public void Sum_of_a_product_is_zero_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total * 2) }), z => z.Id + ":" + z.V,
            ["A1:1260", "A2:1400", "B1:60", "C0:0"]);

    [Fact]
    public void Sum_of_a_sum_of_two_properties()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total + o.Id) }), z => z.Id + ":" + z.V,
            ["A1:636", "A2:704", "B1:35", "C0:0"]);

    [Fact]
    public void Sum_of_an_integer_division_truncates_each_element()
        // 10/7 + 20/7 + 600/7 = 1 + 2 + 85, not (630 / 7) = 90: C# divides integers one element at a time.
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total / 7) }), z => z.Id + ":" + z.V,
            ["A1:88", "A2:100", "B1:4", "C0:0"]);

    [Fact]
    public void Max_of_a_computed_nullable_selector_is_null_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Max(o => (int?)(o.Total * 2)) }), z => z.Id + ":" + z.V,
            ["A1:1200", "A2:1400", "B1:60", "C0:"]);

    [Fact]
    public void Average_of_a_computed_nullable_selector_is_null_for_the_empty_group()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Average(o => (double?)(o.Total * 2)) }), z => z.Id + ":" + z.V,
            ["A1:420", "A2:1400", "B1:60", "C0:"]);

    [Fact]
    public void Min_of_a_computed_selector_over_a_widened_double()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Min(o => (double?)o.Total / 4) }), z => z.Id + ":" + z.V,
            ["A1:2.5", "A2:175", "B1:7.5", "C0:"]);

    [Fact]
    public void A_computed_selector_beside_a_property_selector_in_one_projection()
        => Assert3(db => db.Set<Cust>().Select(c => new { c.Id, A = c.Orders.Sum(o => o.Total), B = c.Orders.Sum(o => o.Total * 2) }),
            z => z.Id + ":" + z.A + ":" + z.B, ["A1:630:1260", "A2:700:1400", "B1:30:60", "C0:0:0"]);

    [Fact]
    public void Min_over_a_non_nullable_selector_throws_for_the_empty_group()
    {
        // LINQ's Min over an empty sequence of a non-nullable type throws; the whole query fails because C0 has no orders.
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode);
            Assert.ThrowsAny<InvalidOperationException>(
                () => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Min(o => o.Total) }).AsEnumerable().ToList());
        }
    }

    [Fact]
    public void Sum_of_a_computed_selector_with_a_captured_factor_is_read_per_execution()
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode);
            NativeModeAssert.TwiceWithDifferentValues(
                v => SumTimes(db, (int)v!), 2, ["A1:1260", "A2:1400", "B1:60", "C0:0"], 3, ["A1:1890", "A2:2100", "B1:90", "C0:0"]);
        }
    }

    private static List<string> SumTimes(Ctx db, int factor)
        => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total * factor) })
            .AsEnumerable().Select(z => z.Id + ":" + z.V).OrderBy(s => s).ToList();

    [Fact]
    public void A_selector_that_is_not_plain_arithmetic_is_never_read_as_something_else()
    {
        // A conditional is not arithmetic over the element: it must refuse (or answer exactly), never fall back to a property read.
        var names = Seed();
        using var db = Create(names, MongoQueryMode.NativeOnly);

        var exception = Record.Exception(() => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total > 15 ? 1 : 0) })
            .AsEnumerable().Select(z => z.Id + ":" + z.V).OrderBy(s => s).ToList());

        if (exception is null)
        {
            Assert.Equal(["A1:2", "A2:1", "B1:1", "C0:0"],
                db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Sum(o => o.Total > 15 ? 1 : 0) })
                    .AsEnumerable().Select(z => z.Id + ":" + z.V).OrderBy(s => s).ToList());
        }
    }

    private static List<string> CountAbove(Ctx db, int cutoff)
        => db.Set<Cust>().Select(c => new { c.Id, V = c.Orders.Count(o => o.Total > cutoff) })
            .AsEnumerable().Select(z => z.Id + ":" + z.V).OrderBy(s => s).ToList();

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Parameterized_predicate_is_read_per_execution(MongoQueryMode mode)
    {
        var names = Seed();

        // One context and one lambda shape: the second run hits the compiled-query cache, so a baked value would repeat
        // the first run's rows.
        using var db = Create(names, mode);
        NativeModeAssert.TwiceWithDifferentValues(
            v => CountAbove(db, (int)v!), 15, ["A1:2", "A2:1", "B1:1", "C0:0"], 650, ["A1:0", "A2:1", "B1:0", "C0:0"]);
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
