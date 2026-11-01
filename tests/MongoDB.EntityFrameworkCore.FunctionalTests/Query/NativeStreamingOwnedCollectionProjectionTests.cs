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
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A root-level owned collection in an untracked projection (<c>new { o.Code, o.Lines }</c>) is read by the cursor straight into a list of its
/// elements. The elements, the nested owned references and collections inside them, and an absent array must read exactly as the DOM shaper
/// read them.
/// </summary>
public class NativeStreamingOwnedCollectionProjectionTests(TemporaryDatabaseFixture database)
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
        public List<Line> Lines { get; set; } = [];
    }

    private class Line
    {
        public string Sku { get; set; } = "";
        public int Qty { get; set; }
        public Meta Meta { get; set; } = new();
        public List<Discount> Discounts { get; set; } = [];
    }

    private class Meta
    {
        public string Category { get; set; } = "";
        public double Weight { get; set; }
    }

    private class Discount
    {
        public string Kind { get; set; } = "";
        public double Amount { get; set; }
    }

    private static readonly Action<ModelBuilder> OrderModel = mb =>
        mb.Entity<Order>().OwnsMany(o => o.Lines, l =>
        {
            l.OwnsOne(x => x.Meta);
            l.OwnsMany(x => x.Discounts);
        });

    private static BsonDocument LineDoc(string sku, int qty, string category, double weight, params (string Kind, double Amount)[] discounts)
        => new()
        {
            { "Sku", sku },
            { "Qty", qty },
            { "Meta", new BsonDocument { { "Category", category }, { "Weight", weight } } },
            { "Discounts", new BsonArray(discounts.Select(d => new BsonDocument { { "Kind", d.Kind }, { "Amount", d.Amount } })) },
        };

    // Ragged on purpose: full elements, an empty array, an explicit null array and a missing one.
    private IMongoCollection<Order> SeedOrders([CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Order>(name);
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertMany([
            new BsonDocument
            {
                { "_id", 1 }, { "Code", "one" },
                { "Lines", new BsonArray { LineDoc("a", 1, "x", 1.5, ("k1", 2.5), ("k2", 3)), LineDoc("b", 2, "y", 2.5) } }
            },
            new BsonDocument { { "_id", 2 }, { "Code", "two" }, { "Lines", new BsonArray() } },
            new BsonDocument { { "_id", 3 }, { "Code", "three" }, { "Lines", BsonNull.Value } },
            new BsonDocument { { "_id", 4 }, { "Code", "four" } },
            new BsonDocument { { "_id", 5 }, { "Code", "five" }, { "Lines", new BsonArray { LineDoc("c", 3, "z", 0, ("k3", 1)) } } },
        ]);
        return collection;
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
    {
        var rowType = CursorRowType(query);
        Assert.True(
            MongoProjectedRowState.IsRowType(rowType),
            $"Expected the streaming projection reader, but the cursor yields {rowType}.");
    }

    private static string Describe(IEnumerable<Line> lines)
        => string.Join(
            ",",
            lines.Select(l => $"{l.Sku}:{l.Qty}:{l.Meta.Category}/{l.Meta.Weight}:" +
                              string.Join("/", l.Discounts.Select(d => d.Kind + "=" + d.Amount))));

    [Fact]
    public void Owned_collection_in_a_projection_streams()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);

        AssertStreams(db.Entities.AsNoTracking().Select(o => new { o.Code, o.Lines }));
    }

    [Fact]
    public void Owned_collection_with_nested_owned_elements_matches_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return db.Entities.AsNoTracking().OrderBy(o => o.Id)
                .Select(o => new { o.Code, o.Lines })
                .AsEnumerable()
                .Select(o => $"{o.Code}|{Describe(o.Lines)}")
                .ToList();
        });

        Assert.Equal(
            [
                "one|a:1:x/1.5:k1=2.5/k2=3,b:2:y/2.5:",
                "two|",
                "three|",
                "four|",
                "five|c:3:z/0:k3=1"
            ],
            results);
    }

    [Fact]
    public void Absent_and_null_arrays_read_as_empty_collections()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return db.Entities.AsNoTracking().OrderBy(o => o.Id)
                .Select(o => new { o.Code, o.Lines })
                .AsEnumerable()
                .Select(o => o.Lines != null ? o.Lines.Count : -1)
                .ToList();
        });

        Assert.Equal([2, 0, 0, 0, 1], results);
    }

    [Fact]
    public void Collection_alone_and_a_client_select_over_it_match_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            var lists = db.Entities.AsNoTracking().OrderBy(o => o.Id).Select(o => o.Lines).AsEnumerable().Select(Describe).ToList();
            var skus = db.Entities.AsNoTracking().OrderBy(o => o.Id)
                .Select(o => new { o.Code, Skus = o.Lines.Select(l => l.Sku).ToList() })
                .AsEnumerable()
                .Select(o => $"{o.Code}|{string.Join(",", o.Skus)}")
                .ToList();
            return lists.Concat(skus).ToList();
        });

        Assert.Equal(
            [
                "a:1:x/1.5:k1=2.5/k2=3,b:2:y/2.5:", "", "", "", "c:3:z/0:k3=1",
                "one|a,b", "two|", "three|", "four|", "five|c"
            ],
            results);
    }

    [Fact]
    public void Collection_with_scalars_and_a_filter_matches_driver_linq()
    {
        var collection = SeedOrders();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, OrderModel);
            return db.Entities.AsNoTracking().Where(o => o.Id > 1).OrderByDescending(o => o.Id)
                .Select(o => new { o.Id, o.Code, o.Lines })
                .AsEnumerable()
                .Select(o => $"{o.Id}|{o.Code}|{o.Lines.Count}")
                .ToList();
        });

        Assert.Equal(["5|five|1", "4|four|0", "3|three|0", "2|two|0"], results);
    }

    [Fact]
    public void Tracked_query_still_refuses_an_owned_collection_without_its_owner()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.Native, OrderModel);

        // EF's own rule, hit before any shaper is chosen: the streamed form is untracked-only, so it never changes it.
        Assert.Throws<InvalidOperationException>(() => db.Entities.Select(o => new { o.Code, o.Lines }).ToList());
    }

    [Fact]
    public void Elements_are_independent_instances_per_row()
    {
        var collection = SeedOrders();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, OrderModel);

        var rows = db.Entities.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Code, o.Lines }).ToList();

        // The reader reuses its locals between elements: no two elements, nor their nested collections, may share an instance.
        var lines = rows.SelectMany(r => r.Lines).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(3, lines.Distinct().Count());
        Assert.Equal(3, lines.Select(l => l.Meta).Distinct().Count());
        Assert.Equal(["a", "b", "c"], lines.Select(l => l.Sku).ToArray());
        Assert.Equal(["k1", "k2"], lines[0].Discounts.Select(d => d.Kind).ToArray());
        Assert.Empty(lines[1].Discounts);
    }
}
