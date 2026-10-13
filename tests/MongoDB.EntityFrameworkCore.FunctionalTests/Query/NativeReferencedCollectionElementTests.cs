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
/// A projected element leaf over a cross-document collection: <c>FirstOrDefault</c> of the group (a whole entity) and
/// <c>ToList</c> of the group (whole entities or a scalar projection), written through a collection navigation and as a
/// <c>GroupJoin</c> on a model with no navigation.
/// Hand-computed over raw seeds, including a customer with no orders (the empty group: <c>FirstOrDefault</c> is null,
/// <c>ToList</c> is empty) and orders whose customer is dangling or null (never in any group).
/// <para>
/// Every mode must answer exactly, including the explicit <see cref="MongoQueryMode.DriverLinq"/> one. A shape the bridge
/// cannot answer must fail loudly, never come back empty.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferencedCollectionElementTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
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

    // Customers A1 (orders 1:10, 2:20, 3:600), A2 (4:700), B1 (5:30) and C0 (none). Order 6 is dangling (ZZ) and 7 has no customer.
    private string[] Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new[]
        {
            TemporaryDatabaseFixtureBase.CreateCollectionName("rceCusts") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("rceOrds") + suffix
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

    private void Strict3<T>(bool withNavigation, Func<Ctx, IQueryable<T>> query, Func<T, string> format, List<string> expected)
    {
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native, MongoQueryMode.DriverLinq })
        {
            using var db = Create(names, mode, withNavigation);
            var rows = query(db).AsEnumerable().Select(format).OrderBy(s => s).ToList();
            Assert.True(expected.SequenceEqual(rows), $"{mode} returned [{string.Join(", ", rows)}], expected [{string.Join(", ", expected)}]");
        }
    }

    private static string Id(Ord? o) => o is null ? "null" : o.Id.ToString();

    private static string Ids(IEnumerable<Ord> os) => string.Join(",", os.Select(o => o.Id));

    // ---- through a collection navigation (N12, N13)

    [Fact]
    public void Navigation_FirstOrDefault_of_an_ordered_group()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, F = c.Orders.OrderBy(o => o.Id).FirstOrDefault() }),
            z => z.Id + ":" + Id(z.F), ["A1:1", "A2:4", "B1:5", "C0:null"]);

    [Fact]
    public void Navigation_FirstOrDefault_descending_is_the_last_by_the_ordering()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, F = c.Orders.OrderByDescending(o => o.Total).FirstOrDefault() }),
            z => z.Id + ":" + Id(z.F), ["A1:3", "A2:4", "B1:5", "C0:null"]);

    [Fact]
    public void Navigation_FirstOrDefault_with_a_predicate()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, F = c.Orders.OrderBy(o => o.Id).FirstOrDefault(o => o.Total > 15) }),
            z => z.Id + ":" + Id(z.F), ["A1:2", "A2:4", "B1:5", "C0:null"]);

    [Fact]
    public void Navigation_FirstOrDefault_is_null_when_the_predicate_matches_nothing()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, F = c.Orders.OrderBy(o => o.Id).FirstOrDefault(o => o.Total > 650) }),
            z => z.Id + ":" + Id(z.F), ["A1:null", "A2:4", "B1:null", "C0:null"]);

    [Fact]
    public void Navigation_ToList_of_a_filtered_ordered_group()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, Os = c.Orders.Where(o => o.Total > 15).OrderBy(o => o.Id).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:2,3", "A2:4", "B1:5", "C0:"]);

    [Fact]
    public void Navigation_ToList_of_the_ordered_group_keeps_the_ordering()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, Os = c.Orders.OrderByDescending(o => o.Id).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:3,2,1", "A2:4", "B1:5", "C0:"]);

    [Fact]
    public void Navigation_ToList_of_a_scalar_projection()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, Ts = c.Orders.OrderBy(o => o.Id).Select(o => o.Total).ToList() }),
            z => z.Id + ":" + string.Join(",", z.Ts), ["A1:10,20,600", "A2:700", "B1:30", "C0:"]);

    [Fact]
    public void Navigation_ToList_with_an_aggregate_beside_it()
    {
        // The driver-LINQ shaper reads whole documents, so a computed sibling leaf (the Count) cannot be read there: it fails loudly at
        // compile time rather than answering. The native modes answer.
        var names = Seed();
        foreach (var mode in new[] { MongoQueryMode.NativeOnly, MongoQueryMode.Native })
        {
            using var db = Create(names, mode, withNavigation: true);
            var rows = db.Set<Cust>().Select(c => new { c.Id, N = c.Orders.Count, Os = c.Orders.OrderBy(o => o.Id).ToList() })
                .AsEnumerable().Select(z => z.Id + ":" + z.N + ":" + Ids(z.Os)).OrderBy(s => s).ToList();
            Assert.Equal(["A1:3:1,2,3", "A2:1:4", "B1:1:5", "C0:0:"], rows);
        }

        using var driver = Create(names, MongoQueryMode.DriverLinq, withNavigation: true);
        Assert.ThrowsAny<Exception>(() => driver.Set<Cust>()
            .Select(c => new { c.Id, N = c.Orders.Count, Os = c.Orders.OrderBy(o => o.Id).ToList() })
            .AsEnumerable().ToList());
    }

    // ---- a GroupJoin on a model with no navigation (G12, G13)

    [Fact]
    public void GroupJoin_FirstOrDefault_of_an_ordered_group()
        => Strict3(false,
            db => db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId,
                (c, g) => new { c.Id, F = g.OrderBy(o => o.Id).FirstOrDefault() }),
            z => z.Id + ":" + Id(z.F), ["A1:1", "A2:4", "B1:5", "C0:null"]);

    [Fact]
    public void GroupJoin_FirstOrDefault_with_a_predicate()
        => Strict3(false,
            db => db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId,
                (c, g) => new { c.Id, F = g.OrderBy(o => o.Id).FirstOrDefault(o => o.Total > 15) }),
            z => z.Id + ":" + Id(z.F), ["A1:2", "A2:4", "B1:5", "C0:null"]);

    [Fact]
    public void GroupJoin_ToList_of_the_ordered_group()
        => Strict3(false,
            db => db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId,
                (c, g) => new { c.Id, Os = g.OrderBy(o => o.Id).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:1,2,3", "A2:4", "B1:5", "C0:"]);

    [Fact]
    public void GroupJoin_ToList_of_a_filtered_group()
        => Strict3(false,
            db => db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId,
                (c, g) => new { c.Id, Os = g.Where(o => o.Total > 15).OrderBy(o => o.Id).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:2,3", "A2:4", "B1:5", "C0:"]);

    [Fact]
    public void Two_entity_leaves_over_the_same_group_in_one_projection()
        => Strict3(true,
            db => db.Set<Cust>().Select(c => new
            {
                c.Id,
                All = c.Orders.OrderByDescending(o => o.Id).ToList(),
                First = c.Orders.OrderBy(o => o.Total).FirstOrDefault()
            }),
            z => z.Id + ":" + Ids(z.All) + ":" + Id(z.First), ["A1:3,2,1:1", "A2:4:4", "B1:5:5", "C0::null"]);

    // ---- constant paging after the ordering

    [Fact]
    public void Navigation_ToList_with_Take()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, Os = c.Orders.OrderBy(o => o.Id).Take(2).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:1,2", "A2:4", "B1:5", "C0:"]);

    [Fact]
    public void Navigation_ToList_with_Skip_and_Take()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, Os = c.Orders.OrderBy(o => o.Id).Skip(1).Take(1).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:2", "A2:", "B1:", "C0:"]);

    [Fact]
    public void Navigation_ToList_with_a_filter_before_the_paging()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, Os = c.Orders.Where(o => o.Total > 15).OrderBy(o => o.Id).Skip(1).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:3", "A2:", "B1:", "C0:"]);

    [Fact]
    public void Navigation_FirstOrDefault_after_Skip()
        => Strict3(true, db => db.Set<Cust>().Select(c => new { c.Id, F = c.Orders.OrderBy(o => o.Id).Skip(1).FirstOrDefault() }),
            z => z.Id + ":" + Id(z.F), ["A1:2", "A2:null", "B1:null", "C0:null"]);

    [Fact]
    public void GroupJoin_ToList_with_Take()
        => Strict3(false,
            db => db.Set<Cust>().GroupJoin(db.Set<Ord>(), c => c.Id, o => o.CustId,
                (c, g) => new { c.Id, Os = g.OrderByDescending(o => o.Id).Take(2).ToList() }),
            z => z.Id + ":" + Ids(z.Os), ["A1:3,2", "A2:4", "B1:5", "C0:"]);

    [Fact]
    public void A_filter_after_the_Take_filters_the_page_and_is_not_moved_into_the_lookup()
    {
        // The page is the group's first two orders (1:10, 2:20 for A1); the filter then keeps the one above 15. Applying the filter before
        // the page would answer A1:2,3 instead. The shape is declined, so native-only fails loudly rather than answering either.
        var names = Seed();
        using var db = Create(names, MongoQueryMode.NativeOnly, withNavigation: true);

        Assert.ThrowsAny<Exception>(() => db.Set<Cust>()
            .Select(c => new { c.Id, Os = c.Orders.OrderBy(o => o.Id).Take(2).Where(o => o.Total > 15).ToList() })
            .AsEnumerable().ToList());
    }

    // ---- shapes the driver-LINQ bridge declines

    [Fact]
    public void A_member_named_like_a_stored_field_is_refused_by_the_bridge_not_overwritten()
    {
        // The driver-LINQ translation copies the group's array to the member's alias in the whole document; `Name` is also a stored field of
        // the customer, so that would clobber it. The native modes project under the alias and are unaffected.
        var names = Seed();
        using (var native = Create(names, MongoQueryMode.NativeOnly, withNavigation: true))
        {
            var rows = native.Set<Cust>().Select(c => new { c.Id, Name = c.Orders.OrderBy(o => o.Id).ToList() })
                .AsEnumerable().Select(z => z.Id + ":" + Ids(z.Name)).OrderBy(s => s).ToList();
            Assert.Equal(["A1:1,2,3", "A2:4", "B1:5", "C0:"], rows);
        }

        using var driver = Create(names, MongoQueryMode.DriverLinq, withNavigation: true);
        Assert.ThrowsAny<Exception>(() => driver.Set<Cust>()
            .Select(c => new { c.Id, Name = c.Orders.OrderBy(o => o.Id).ToList() })
            .AsEnumerable().ToList());
    }

    // ---- per-execution parameters

    private static List<string> FirstAbove(Ctx db, int cutoff)
        => db.Set<Cust>().Select(c => new { c.Id, F = c.Orders.OrderBy(o => o.Id).FirstOrDefault(o => o.Total > cutoff) })
            .AsEnumerable().Select(z => z.Id + ":" + Id(z.F)).OrderBy(s => s).ToList();

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Parameterized_FirstOrDefault_predicate_is_read_per_execution(MongoQueryMode mode)
    {
        var names = Seed();

        // One context and one lambda shape: the second run hits the compiled-query cache, so a baked value would repeat
        // the first run's rows.
        using var db = Create(names, mode, withNavigation: true);
        NativeModeAssert.TwiceWithDifferentValues(
            v => FirstAbove(db, (int)v!), 15, ["A1:2", "A2:4", "B1:5", "C0:null"], 650, ["A1:null", "A2:4", "B1:null", "C0:null"]);
    }

    private Ctx Create(string[] names, MongoQueryMode mode, bool withNavigation)
    {
        var options = new DbContextOptionsBuilder<Ctx>()
            .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new Ctx(options, names, withNavigation);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class Ctx(DbContextOptions<Ctx> options, string[] names, bool withNavigation) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Cust>().ToCollection(names[0]);
            mb.Entity<Ord>(b =>
            {
                b.ToCollection(names[1]);
                if (withNavigation)
                {
                    b.HasOne(o => o.Cust).WithMany(c => c.Orders).HasForeignKey(o => o.CustId);
                }
            });

            if (!withNavigation)
            {
                mb.Entity<Cust>().Ignore(c => c.Orders);
                mb.Entity<Ord>().Ignore(o => o.Cust);
            }
        }
    }
}
