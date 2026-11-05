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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An element <c>Select</c> over an owned collection whose lambda reads only scalar members of the element
/// (<c>o.Lines.Select(l => l.Sku).ToList()</c>) is evaluated by the cursor straight off the reader: the owned element, and the owned types it
/// holds, are never constructed. A lambda that needs more than that (the element itself, a nested owned member, a method of the user's types)
/// reads the collection whole and runs the <c>Select</c> over it, as before. What the query returns is the same either way.
/// </summary>
public class NativeOwnedCollectionSelectStreamingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public List<Line> Lines { get; set; } = [];
    }

    private class Line
    {
        // Counts every instance the materializer constructs, so a test can tell whether elements were built.
        public static int Constructed;

        public Line() => Interlocked.Increment(ref Constructed);

        public string Sku { get; set; } = "";
        public int Qty { get; set; }
        public int? Rebate { get; set; }
        public Tag? Tag { get; set; }
    }

    private class Tag
    {
        public string Label { get; set; } = "";
    }

    private sealed class SelectContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder mb)
            => mb.Entity<Order>(b =>
            {
                b.ToCollection("SelectOrders" + suffix);
                b.OwnsMany(o => o.Lines, l => l.OwnsOne(x => x.Tag));
            });
    }

    private SelectContext Db(MongoQueryMode mode, string suffix)
        => new(
            new DbContextOptionsBuilder<SelectContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    // Ragged data: elements with and without the optional members, orders whose Lines array is empty, null or absent, and an element that omits
    // a non-nullable scalar.
    private string Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using (var seed = Db(MongoQueryMode.Native, suffix))
        {
            seed.Orders.AddRange(
                new Order
                {
                    Id = 1, Code = "a",
                    Lines =
                    [
                        new Line { Sku = "s1", Qty = 1, Rebate = 5, Tag = new Tag { Label = "t1" } },
                        new Line { Sku = "s2", Qty = 2 }
                    ]
                },
                new Order { Id = 2, Code = "b", Lines = [new Line { Sku = "s3", Qty = 3, Rebate = 7 }] },
                new Order { Id = 3, Code = "c" });
            seed.SaveChanges();
        }

        var orders = database.MongoDatabase.GetCollection<BsonDocument>("SelectOrders" + suffix);
        orders.InsertOne(new BsonDocument { { "_id", 4 }, { "Code", "d" }, { "Lines", BsonNull.Value } });
        orders.InsertOne(new BsonDocument { { "_id", 5 }, { "Code", "e" } });
        return suffix;
    }

    private List<string> Run(Func<SelectContext, IEnumerable<string>> query)
    {
        var suffix = Seed();
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return query(db).ToList();
        });
    }

    [Fact]
    public void A_scalar_member_select_constructs_no_element()
    {
        var suffix = Seed();
        using var db = Db(MongoQueryMode.NativeOnly, suffix);

        Line.Constructed = 0;
        var skus = db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() })
            .AsEnumerable().Select(r => $"{r.Code}:[{string.Join(",", r.Skus)}]").ToList();

        Assert.Equal(["a:[s1,s2]", "b:[s3]", "c:[]", "d:[]", "e:[]"], skus);
        Assert.Equal(0, Line.Constructed);
    }

    [Fact]
    public void A_select_that_calls_a_method_of_the_users_type_still_constructs_the_element()
    {
        var suffix = Seed();
        using var db = Db(MongoQueryMode.NativeOnly, suffix);

        Line.Constructed = 0;
        var tags = db.Orders.AsNoTracking().OrderBy(o => o.Id)
            .Select(o => new { o.Code, Items = o.Lines.Select(l => Tagged(l.Sku)).ToList() })
            .AsEnumerable().Select(r => $"{r.Code}:[{string.Join(",", r.Items)}]").ToList();

        Assert.Equal(["a:[<s1>,<s2>]", "b:[<s3>]", "c:[]", "d:[]", "e:[]"], tags);
        Assert.NotEqual(0, Line.Constructed);
    }

    // A method of the test's own type: running it while the driver deserializes the batch could have side effects, so the cursor leaves it to the shaper.
    private static string Tagged(string sku) => $"<{sku}>";

    [Fact]
    public void Scalar_member_selects_return_what_driver_linq_returns()
        => Assert.Equal(
            [
                "sku a:[s1,s2]", "sku b:[s3]", "sku c:[]", "sku d:[]", "sku e:[]",
                "twice a:[2,4]", "twice b:[6]", "twice c:[]", "twice d:[]", "twice e:[]",
                "pair a:[s1x1,s2x2]", "pair b:[s3x3]", "pair c:[]", "pair d:[]", "pair e:[]",
                "rebate a:[5,-1]", "rebate b:[7]", "rebate c:[]", "rebate d:[]", "rebate e:[]",
                "pick a:[s1,none]", "pick b:[s3]", "pick c:[]", "pick d:[]", "pick e:[]",
                "upper a:[S1,S2]", "upper b:[S3]", "upper c:[]", "upper d:[]", "upper e:[]"
            ],
            Run(db =>
                Describe("sku", db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => l.Sku).ToList() })
                    .AsEnumerable().Select(r => (r.Code, r.Items.Select(i => i))))
                .Concat(Describe("twice", db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => l.Qty * 2).ToList() })
                    .AsEnumerable().Select(r => (r.Code, r.Items.Select(i => i.ToString())))))
                .Concat(Describe("pair", db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => new { l.Sku, l.Qty }).ToList() })
                    .AsEnumerable().Select(r => (r.Code, r.Items.Select(i => $"{i.Sku}x{i.Qty}")))))
                .Concat(Describe("rebate", db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => l.Rebate ?? -1).ToList() })
                    .AsEnumerable().Select(r => (r.Code, r.Items.Select(i => i.ToString())))))
                .Concat(Describe("pick", db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => l.Qty == 1 || l.Qty == 3 ? l.Sku : "none").ToList() })
                    .AsEnumerable().Select(r => (r.Code, r.Items.Select(i => i)))))
                .Concat(Describe("upper", db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => l.Sku.ToUpper()).ToList() })
                    .AsEnumerable().Select(r => (r.Code, r.Items.Select(i => i)))))));

    private static IEnumerable<string> Describe(string name, IEnumerable<(string Code, IEnumerable<string> Items)> rows)
        => rows.Select(r => $"{name} {r.Code}:[{string.Join(",", r.Items)}]");

    [Fact]
    public void An_element_that_omits_a_required_member_the_select_reads_throws()
    {
        var suffix = Seed();
        var orders = database.MongoDatabase.GetCollection<BsonDocument>("SelectOrders" + suffix);
        orders.InsertOne(
            new BsonDocument
            {
                { "_id", 6 }, { "Code", "f" },
                { "Lines", new BsonArray { new BsonDocument { { "Qty", 1 } } } }
            });

        using var db = Db(MongoQueryMode.NativeOnly, suffix);
        var query = db.Orders.AsNoTracking().Where(o => o.Id == 6).Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() });

        Assert.ThrowsAny<Exception>(() => query.ToList());
    }
}
