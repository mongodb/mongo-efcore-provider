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
/// An element <c>Select</c> over an owned collection (<c>o.Lines.Select(l => l.Sku).ToList()</c>) reads only the owned navigations its lambda touches,
/// so the pipeline leaves the others out of each element instead of shipping, and materializing, a whole array it then discards. What the query
/// returns must not change: every stored scalar stays, and a missing optional reference or collection already read as null and empty. The one
/// exception is a Select the cursor evaluates in place (see <c>NativeOwnedCollectionSelectStreamingTests</c>), which needs only the scalars it reads.
/// </summary>
public class NativeOwnedArrayPruningTests(TemporaryDatabaseFixture database)
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
        public string Sku { get; set; } = "";
        public int Qty { get; set; }
        public Meta? Meta { get; set; }
        public List<Discount> Discounts { get; set; } = [];
    }

    private class Meta
    {
        public string Tag { get; set; } = "";
    }

    private class Discount
    {
        public int Percent { get; set; }
    }

    private sealed class PruneContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder mb)
            => mb.Entity<Order>(b =>
            {
                b.ToCollection("PruneOrders" + suffix);
                b.OwnsMany(o => o.Lines, l =>
                {
                    l.OwnsOne(x => x.Meta);
                    l.OwnsMany(x => x.Discounts);
                });
            });
    }

    private PruneContext Db(MongoQueryMode mode, string suffix, MongoClient? client = null)
        => new(
            new DbContextOptionsBuilder<PruneContext>()
                .UseMongoDB(client ?? database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, o => o.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options,
            suffix);

    // Ragged data: an element with and without Meta, with no, one and two discounts, and orders whose Lines array is empty, null or absent.
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
                        new Line { Sku = "s1", Qty = 1, Meta = new Meta { Tag = "t1" }, Discounts = [new Discount { Percent = 5 }, new Discount { Percent = 10 }] },
                        new Line { Sku = "s2", Qty = 2 }
                    ]
                },
                new Order { Id = 2, Code = "b", Lines = [new Line { Sku = "s3", Qty = 3, Discounts = [new Discount { Percent = 7 }] }] },
                new Order { Id = 3, Code = "c" });
            seed.SaveChanges();
        }

        var orders = database.MongoDatabase.GetCollection<BsonDocument>("PruneOrders" + suffix);
        orders.InsertOne(new BsonDocument { { "_id", 4 }, { "Code", "d" }, { "Lines", BsonNull.Value } });
        orders.InsertOne(new BsonDocument { { "_id", 5 }, { "Code", "e" } });
        return suffix;
    }

    private static string Describe(Order o, IEnumerable<string> values) => $"{o.Code}:[{string.Join(",", values)}]";

    [Fact]
    public void An_element_select_of_a_scalar_leaves_the_owned_navigations_out_of_the_pipeline()
    {
        var suffix = Seed();
        var commands = new List<string>();
        var settings = database.Client.Settings.Clone();
        settings.ClusterConfigurator = b => b.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(e =>
        {
            if (e.CommandName == "aggregate" && e.Command["aggregate"].AsString == "PruneOrders" + suffix)
            {
                commands.Add(e.Command.ToJson());
            }
        });

        using var db = Db(MongoQueryMode.NativeOnly, suffix, new MongoClient(settings));
        var skus = db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() })
            .AsEnumerable().Select(r => $"{r.Code}:[{string.Join(",", r.Skus)}]").ToList();

        Assert.Equal(["a:[s1,s2]", "b:[s3]", "c:[]", "d:[]", "e:[]"], skus);
        var command = Assert.Single(commands);
        Assert.True(command.Contains("Sku"), command);
        Assert.DoesNotContain("Qty", command);
        Assert.DoesNotContain("Discounts", command);
        Assert.DoesNotContain("Meta", command);
    }

    [Fact]
    public void A_pruned_element_select_returns_what_driver_linq_returns()
    {
        var suffix = Seed();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            var skus = db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() })
                .AsEnumerable().Select(r => $"skus {r.Code}:[{string.Join(",", r.Skus)}]");
            var pairs = db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, Items = o.Lines.Select(l => new { l.Sku, l.Qty }).ToList() })
                .AsEnumerable().Select(r => $"pairs {r.Code}:[{string.Join(",", r.Items.Select(i => $"{i.Sku}x{i.Qty}"))}]");
            return skus.Concat(pairs).ToList();
        });

        Assert.Equal(
            [
                "skus a:[s1,s2]", "skus b:[s3]", "skus c:[]", "skus d:[]", "skus e:[]",
                "pairs a:[s1x1,s2x2]", "pairs b:[s3x3]", "pairs c:[]", "pairs d:[]", "pairs e:[]"
            ],
            results);
    }

    private List<string> Run(Func<PruneContext, IEnumerable<string>> query)
    {
        var suffix = Seed();
        return NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = Db(mode, suffix);
            return query(db).ToList();
        });
    }

    [Fact]
    public void An_array_also_projected_whole_is_read_whole()
        => Assert.Equal(
            ["a:[s1/2/t1,s2/0/]+[s1,s2]", "b:[s3/1/]+[s3]", "c:[]+[]", "d:[]+[]", "e:[]+[]"],
            Run(db => db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, o.Lines, Skus = o.Lines.Select(l => l.Sku).ToList() })
                .AsEnumerable().Select(r => $"{r.Code}:[{string.Join(",", r.Lines.Select(l => $"{l.Sku}/{l.Discounts.Count}/{l.Meta?.Tag}"))}]+[{string.Join(",", r.Skus)}]")));
}
