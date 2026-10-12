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
/// An aggregate or quantifier over a cross-document collection reached through a collection navigation, in a <c>Where</c>
/// or an <c>OrderBy</c>/<c>ThenBy</c> Hand-computed over raw seeds with an empty group and orders whose customer is dangling or null.
/// <para>
/// <see cref="MongoQueryMode.NativeOnly"/> and <see cref="MongoQueryMode.Native"/> must answer exactly. The explicit
/// <see cref="MongoQueryMode.DriverLinq"/> leg may answer correctly or refuse loudly, never answer wrongly.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferencedCollectionPredicateTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

    // A1: 10, 20, 600 | A2: 700 | B1: 30 | C0: none. Order 6 is dangling (ZZ), 7 has no customer.
    private string[] Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new[]
        {
            TemporaryDatabaseFixtureBase.CreateCollectionName("rcpCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("rcpOrds") + suffix
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

    /// <summary>NativeOnly and Native answer exactly; DriverLinq answers exactly or refuses.</summary>
    private void Assert3(Func<Ctx, List<string>> query, List<string> expected)
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode);
            List<string>? rows = null;
            try
            {
                rows = query(db);
            }
            catch (Exception) when (mode == MongoQueryMode.DriverLinq)
            {
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

    private static List<string> Ids(IQueryable<Cust> q) => q.AsEnumerable().Select(c => c.Id).ToList();

    private static List<string> SortedIds(IQueryable<Cust> q) => q.AsEnumerable().Select(c => c.Id).OrderBy(s => s).ToList();

    [Fact]
    public void Where_Any_with_a_predicate()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Any(o => o.Total > 100))), ["A1", "A2"]);

    [Fact]
    public void Where_not_Any_with_a_predicate_keeps_the_empty_group()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => !c.Orders.Any(o => o.Total > 100))), ["B1", "C0"]);

    [Fact]
    public void Where_Any_without_a_predicate()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Any())), ["A1", "A2", "B1"]);

    [Fact]
    public void Where_All_with_a_predicate_keeps_the_empty_group()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.All(o => o.Total > 15))), ["A2", "B1", "C0"]);

    [Fact]
    public void Where_Count_with_a_predicate_compared()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Count(o => o.Total > 15) > 1)), ["A1"]);

    [Fact]
    public void Where_Sum_compared()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Sum(o => o.Total) > 100)), ["A1", "A2"]);

    [Fact]
    public void Where_nullable_Max_compared_never_matches_the_empty_group()
        // C# null < 650 is false; the aggregation $lt would order null below every number and answer true.
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Max(o => (int?)o.Total) < 650)), ["A1", "B1"]);

    [Fact]
    public void Where_nullable_Min_is_null_only_for_the_empty_group()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Min(o => (int?)o.Total) == null)), ["C0"]);

    [Fact]
    public void Where_two_quantifiers_combined()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Any(o => o.Total > 100) && c.Orders.Count > 1)), ["A1"]);

    [Fact]
    public void Where_a_computed_Sum_compared()
        => Assert3(db => SortedIds(db.Set<Cust>().Where(c => c.Orders.Sum(o => o.Total * 2) > 1300)), ["A2"]);

    [Fact]
    public void OrderBy_a_computed_Sum_then_key()
        => Assert3(db => Ids(db.Set<Cust>().OrderByDescending(c => c.Orders.Sum(o => o.Total + o.Id)).ThenBy(c => c.Id)),
            ["A2", "A1", "B1", "C0"]);

    private static List<string> CountAbove(Ctx db, int cutoff)
        => SortedIds(db.Set<Cust>().Where(c => c.Orders.Count(o => o.Total > cutoff) > 0));

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    public void Where_parameterized_predicate_is_read_per_execution(MongoQueryMode mode)
    {
        var names = Seed();

        using var db = Create(names, mode);
        NativeModeAssert.TwiceWithDifferentValues(v => CountAbove(db, (int)v!), 15, ["A1", "A2", "B1"], 650, ["A2"]);
    }

    [Fact]
    public void OrderBy_Count_descending_then_key()
        => Assert3(db => Ids(db.Set<Cust>().OrderByDescending(c => c.Orders.Count).ThenBy(c => c.Id)), ["A1", "A2", "B1", "C0"]);

    [Fact]
    public void OrderBy_Sum_then_key_with_Take()
        => Assert3(db => Ids(db.Set<Cust>().OrderBy(c => c.Orders.Sum(o => o.Total)).ThenBy(c => c.Id).Take(2)), ["C0", "B1"]);

    [Fact]
    public void OrderBy_nullable_Min_puts_the_empty_group_first()
        => Assert3(db => Ids(db.Set<Cust>().OrderBy(c => c.Orders.Min(o => (int?)o.Total)).ThenBy(c => c.Id)), ["C0", "A1", "B1", "A2"]);

    [Fact]
    public void OrderBy_key_then_ThenBy_aggregate_shares_one_sort()
        // Names are unique, so the primary key alone decides: [A1, A2, B1, C0]. The ThenBy aggregate must only break ties (there are
        // none). Two separate $sort stages would let the later one win and answer by Sum alone: [A2, A1, B1, C0].
        => Assert3(db => Ids(db.Set<Cust>().OrderBy(c => c.Name).ThenByDescending(c => c.Orders.Sum(o => o.Total))),
            ["A1", "A2", "B1", "C0"]);

    [Fact]
    public void Where_Any_then_OrderBy_Count_then_Take()
        => Assert3(db => Ids(db.Set<Cust>().Where(c => c.Orders.Any()).OrderByDescending(c => c.Orders.Count).ThenBy(c => c.Id).Take(2)),
            ["A1", "A2"]);

    [Fact]
    public void Take_then_Where_Any_pages_before_filtering()
        => Assert3(db => SortedIds(db.Set<Cust>().OrderBy(c => c.Id).Take(3).Where(c => c.Orders.Any(o => o.Total > 100))), ["A1", "A2"]);

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
