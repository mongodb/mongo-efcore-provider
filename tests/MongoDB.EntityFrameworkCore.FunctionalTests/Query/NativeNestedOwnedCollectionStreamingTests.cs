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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An owned collection whose elements own further navigations (<c>Order.Lines</c> -> <c>Meta</c> reference and <c>Discounts</c>
/// collection) on the streaming materializer: populated, empty, null and missing arrays at each level, a missing owned reference,
/// the synthesized ordinal keys of the nested owned entities, and tracking modes. Native and DriverLinq must agree, and the
/// native results are also checked against hand-computed values over raw documents.
/// </summary>
public class NativeNestedOwnedCollectionStreamingTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Order
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public Shipping Ship { get; set; }

        // No `= []` initializers on purpose: an empty collection can only come from the provider.
        public List<LineItem> Lines { get; set; }
    }

    public class Shipping
    {
        public string Carrier { get; set; }
    }

    public class LineItem
    {
        public string? Sku { get; set; }
        public int Qty { get; set; }
        public LineMeta Meta { get; set; }
        public List<Discount> Discounts { get; set; }
    }

    public class LineMeta
    {
        public string Note { get; set; }
    }

    public class Discount
    {
        public string Code { get; set; }
        public int Pct { get; set; }
    }

    private static readonly Action<ModelBuilder> OrderModel = mb =>
        mb.Entity<Order>(o =>
        {
            o.OwnsOne(x => x.Ship);
            o.OwnsMany(x => x.Lines, l =>
            {
                l.OwnsOne(x => x.Meta);
                l.Navigation(x => x.Meta).IsRequired(false);
                l.OwnsMany(x => x.Discounts);
            });
        });

    private static SingleEntityDbContext<Order> CreateContext(IMongoCollection<Order> collection, MongoQueryMode mode)
        => SingleEntityDbContext.Create(
            collection,
            modelBuilderAction: OrderModel,
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

    private string UniqueCollectionName(string name)
        => TemporaryDatabaseFixtureBase.CreateCollectionName(name) + Guid.NewGuid().ToString("N")[..8];

    private static BsonDocument Line(string sku, int qty, BsonValue? meta, BsonValue? discounts)
    {
        var doc = new BsonDocument { { "Sku", sku }, { "Qty", qty } };
        if (meta is not null)
        {
            doc.Add("Meta", meta);
        }

        if (discounts is not null)
        {
            doc.Add("Discounts", discounts);
        }

        return doc;
    }

    private static BsonDocument Disc(string code, int pct) => new() { { "Pct", pct }, { "Code", code } };

    // A null value means the element is absent; BsonNull.Value means present and explicitly null.
    private static BsonDocument Row(string name, BsonValue? lines)
    {
        var doc = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Name", name }, { "Ship", new BsonDocument("Carrier", "c-" + name) } };
        if (lines is not null)
        {
            doc.Add("Lines", lines);
        }

        return doc;
    }

    // Names sort deterministically: a < b < c < d < e < f.
    private IMongoCollection<Order> Seed(string name)
    {
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(name));
        coll.InsertMany([
            Row("a-full", new BsonArray
            {
                Line("s1", 1, new BsonDocument("Note", "n1"), new BsonArray { Disc("x", 5), Disc("y", 10) }),
                Line("s2", 2, new BsonDocument("Note", "n2"), new BsonArray { Disc("z", 15) }),
                Line("s3", 3, new BsonDocument("Note", "n3"), new BsonArray())
            }),
            Row("b-empty", new BsonArray()),
            Row("c-no-lines", lines: null),
            Row("d-null-lines", BsonNull.Value),
            Row("e-ragged", new BsonArray
            {
                Line("r1", 1, meta: null, discounts: null),
                Line("r2", 2, BsonNull.Value, BsonNull.Value),
                Line("r3", 3, new BsonDocument("Note", "n3"), new BsonArray { Disc("only", 1) })
            }),
            Row("f-single", new BsonArray { Line("q1", 9, new BsonDocument("Note", "nq"), new BsonArray { Disc("a", 1), Disc("b", 2), Disc("c", 3) }) })
        ]);
        return database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName);
    }

    private static void AssertStreamingEligible(DbContext db)
        => Assert.True(
            StreamingEligibility.IsEligible(db.Model.FindEntityType(typeof(Order))!),
            "Order must be streaming-eligible or these tests do not exercise the streaming materializer.");

    // One line per order: "name|carrier|sku:qty:note:code/pct,code/pct;...". A missing or null array renders as "[]" (normalized to empty).
    private static string Describe(Order o)
        => o.Name + "|" + o.Ship?.Carrier + "|" + string.Join(
            ";",
            o.Lines.Select(l => $"{l.Sku}:{l.Qty}:{l.Meta?.Note ?? "-"}:" + string.Join(",", l.Discounts.Select(d => $"{d.Code}/{d.Pct}"))));

    private static readonly string[] Expected =
    [
        "a-full|c-a-full|s1:1:n1:x/5,y/10;s2:2:n2:z/15;s3:3:n3:",
        "b-empty|c-b-empty|",
        "c-no-lines|c-c-no-lines|",
        "d-null-lines|c-d-null-lines|",
        "e-ragged|c-e-ragged|r1:1:-:;r2:2:-:;r3:3:n3:only/1",
        "f-single|c-f-single|q1:9:nq:a/1,b/2,c/3"
    ];

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Nested_owned_collection_elements_read_every_level(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Nested_owned_collection_elements_read_every_level) + mode);

        using var db = CreateContext(collection, mode);
        if (mode != MongoQueryMode.DriverLinq)
        {
            AssertStreamingEligible(db);
        }

        Assert.Equal(Expected, db.Entities.AsNoTracking().OrderBy(o => o.Name).ToList().Select(Describe).ToArray());
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void Nested_owned_collection_elements_read_when_tracked(MongoQueryMode mode)
    {
        var collection = Seed(nameof(Nested_owned_collection_elements_read_when_tracked) + mode);

        using var db = CreateContext(collection, mode);

        Assert.Equal(Expected, db.Entities.OrderBy(o => o.Name).ToList().Select(Describe).ToArray());
    }

    [Fact]
    public void Nested_owned_collection_elements_read_with_identity_resolution()
    {
        var collection = Seed(nameof(Nested_owned_collection_elements_read_with_identity_resolution));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        Assert.Equal(
            Expected,
            db.Entities.AsNoTrackingWithIdentityResolution().OrderBy(o => o.Name).ToList().Select(Describe).ToArray());
    }

    // The tracked owned entities' primary keys, rendered "<type>=<key values>", must be the same whichever path materialized them.
    private static string[] TrackedKeys(DbContext db)
        => db.ChangeTracker.Entries()
            .Where(e => e.Metadata.IsOwned())
            .Select(e => e.Metadata.ShortName() + "=" + string.Join(
                ",", e.Metadata.FindPrimaryKey()!.Properties.Select(p => e.Property(p.Name).CurrentValue is ObjectId ? "<id>" : e.Property(p.Name).CurrentValue)))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Nested_owned_entities_get_the_synthesized_ordinal_keys_of_every_enclosing_collection()
    {
        var collection = Seed(nameof(Nested_owned_entities_get_the_synthesized_ordinal_keys_of_every_enclosing_collection));

        string[] native;
        using (var db = CreateContext(collection, MongoQueryMode.NativeOnly))
        {
            _ = db.Entities.Where(o => o.Name == "a-full").ToList();
            native = TrackedKeys(db);
        }

        string[] oracle;
        using (var db = CreateContext(collection, MongoQueryMode.DriverLinq))
        {
            _ = db.Entities.Where(o => o.Name == "a-full").ToList();
            oracle = TrackedKeys(db);
        }

        // s1 owns discounts 1 and 2, s2 owns discount 1, s3 owns none: a discount's key carries its line's ordinal.
        Assert.Contains("Discount=<id>,1,1", native);
        Assert.Contains("Discount=<id>,1,2", native);
        Assert.Contains("Discount=<id>,2,1", native);
        Assert.DoesNotContain("Discount=<id>,3,1", native);
        Assert.Equal(3, native.Count(k => k.StartsWith("LineItem=", StringComparison.Ordinal)));
        Assert.Equal(oracle, native);
    }

    [Fact]
    public void Nested_owned_collection_elements_read_with_a_filter_and_ordering()
    {
        var collection = Seed(nameof(Nested_owned_collection_elements_read_with_a_filter_and_ordering));

        using var db = CreateContext(collection, MongoQueryMode.NativeOnly);

        var rows = db.Entities.AsNoTracking().Where(o => o.Name.CompareTo("e") >= 0).OrderByDescending(o => o.Name).ToList();

        Assert.Equal([Expected[5], Expected[4]], rows.Select(Describe).ToArray());
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void A_required_owned_reference_missing_from_a_collection_element_throws(MongoQueryMode mode)
    {
        // Pins the existing behavior of the default model (Meta required): the same InvalidOperationException from both paths.
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(nameof(A_required_owned_reference_missing_from_a_collection_element_throws) + mode));
        coll.InsertOne(Row("req", new BsonArray { Line("r1", 1, meta: null, discounts: new BsonArray()) }));

        using var db = SingleEntityDbContext.Create(
            database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName),
            modelBuilderAction: mb => mb.Entity<Order>().OwnsMany(x => x.Lines, l =>
            {
                l.OwnsOne(x => x.Meta);
                l.OwnsMany(x => x.Discounts);
            }),
            optionsBuilderAction: b =>
            {
                b.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
                new MongoDbContextOptionsBuilder(b).UseQueryMode(mode);
            });

        var ex = Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().ToList());
        Assert.Contains("Field 'Meta' required but not present", ex.Message);
    }

    [Theory]
    [InlineData(MongoQueryMode.NativeOnly)]
    [InlineData(MongoQueryMode.DriverLinq)]
    public void An_element_does_not_inherit_the_previous_elements_owned_reference_or_scalar(MongoQueryMode mode)
    {
        // Element state is reused across iterations: a later element without Meta/Sku must not read the earlier element's values.
        var coll = database.MongoDatabase.GetCollection<BsonDocument>(UniqueCollectionName(nameof(An_element_does_not_inherit_the_previous_elements_owned_reference_or_scalar) + mode));
        coll.InsertOne(Row("stale", new BsonArray
        {
            Line("s1", 1, new BsonDocument("Note", "n1"), new BsonArray { Disc("x", 5) }),
            new BsonDocument("Qty", 2),
            Line("s3", 3, BsonNull.Value, discounts: null)
        }));

        using var db = CreateContext(database.MongoDatabase.GetCollection<Order>(coll.CollectionNamespace.CollectionName), mode);

        Assert.Equal(["stale|c-stale|s1:1:n1:x/5;:2:-:;s3:3:-:"], db.Entities.AsNoTracking().ToList().Select(Describe).ToArray());
    }
}
