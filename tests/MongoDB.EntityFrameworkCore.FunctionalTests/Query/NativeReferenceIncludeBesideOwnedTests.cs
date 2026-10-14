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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A reference <c>Include</c> beside owned navigations on the root (<c>Item.Account</c> with the auto-included
/// <c>Shipping</c>, <c>Tags</c> and <c>Lines</c> -> <c>Meta</c>/<c>Discounts</c>): EF nests the owned auto-includes and the
/// reference include in one selector, and the owned siblings must be transparent to the recognizer. Each shape runs in
/// <c>NativeOnly</c> (goes native), <c>Native</c> and <c>DriverLinq</c> (oracle), over rows and full nested owned data.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeReferenceIncludeBesideOwnedTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    public class Account
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Carrier
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Reply
    {
        public ObjectId Id { get; set; }
        public ObjectId ItemId { get; set; }
        public string Text { get; set; } = "";
    }

    public class Item
    {
        public ObjectId Id { get; set; }
        public string Title { get; set; } = "";
        public ObjectId? AccountId { get; set; }
        public Account Account { get; set; }
        public Shipping Shipping { get; set; }
        public List<Tag> Tags { get; set; }
        public List<LineItem> Lines { get; set; }
        public List<Reply> Replies { get; set; }
    }

    public class Shipping
    {
        public string Method { get; set; }
        public ObjectId? CarrierId { get; set; }
        public Carrier Carrier { get; set; }
    }

    public class Tag
    {
        public string Label { get; set; }
    }

    public class LineItem
    {
        public string Sku { get; set; }
        public LineMeta Meta { get; set; }
        public List<Discount> Discounts { get; set; }
        public ObjectId? ProductId { get; set; }
        public Product Product { get; set; }
    }

    public class LineMeta
    {
        public string Note { get; set; }
    }

    public class Discount
    {
        public string Code { get; set; }
    }

    private class BesideDbContext : DbContext
    {
        private readonly string _suffix;

        public DbSet<Item> Items { get; set; } = null!;
        public DbSet<Account> Accounts { get; set; } = null!;
        public DbSet<Carrier> Carriers { get; set; } = null!;
        public DbSet<Reply> Replies { get; set; } = null!;
        public DbSet<Product> Products { get; set; } = null!;

        public BesideDbContext(TemporaryDatabaseFixture database, string suffix, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<BesideDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName, b => b.UseQueryMode(mode))
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _suffix = suffix;
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Account>().ToCollection("accounts" + _suffix);
            mb.Entity<Carrier>().ToCollection("carriers" + _suffix);
            mb.Entity<Reply>().ToCollection("replies" + _suffix);
            mb.Entity<Product>().ToCollection("products" + _suffix);
            mb.Entity<Item>(i =>
            {
                i.ToCollection("items" + _suffix);
                i.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId);
                i.HasMany(x => x.Replies).WithOne().HasForeignKey(r => r.ItemId);
                i.OwnsOne(x => x.Shipping, s => s.HasOne(x => x.Carrier).WithMany().HasForeignKey(x => x.CarrierId));
                i.Navigation(x => x.Shipping).IsRequired(false);
                i.OwnsMany(x => x.Tags);
                i.OwnsMany(x => x.Lines, l =>
                {
                    l.OwnsOne(x => x.Meta);
                    l.Navigation(x => x.Meta).IsRequired(false);
                    l.OwnsMany(x => x.Discounts);
                    l.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
                });
            });
        }
    }

    private static readonly ObjectId Acme = new("000000000000000000000a01");
    private static readonly ObjectId Globex = new("000000000000000000000a02");
    private static readonly ObjectId Widget = new("000000000000000000000c01");
    private static readonly ObjectId FastCo = new("000000000000000000000b01");

    private string Seed(string test)
    {
        var suffix = "_" + test + "_" + Guid.NewGuid().ToString("N")[..8];
        var db = database.MongoDatabase;
        db.GetCollection<BsonDocument>("accounts" + suffix).InsertMany([
            new BsonDocument { { "_id", Acme }, { "Name", "acme" } },
            new BsonDocument { { "_id", Globex }, { "Name", "globex" } }
        ]);
        db.GetCollection<BsonDocument>("products" + suffix).InsertOne(new BsonDocument { { "_id", Widget }, { "Name", "widget" } });
        db.GetCollection<BsonDocument>("carriers" + suffix).InsertOne(new BsonDocument { { "_id", FastCo }, { "Name", "fastco" } });

        BsonDocument Line(string sku, string? note, params string[] codes)
        {
            var d = new BsonDocument("Sku", sku);
            d.Add("ProductId", sku == "s1" ? Widget : BsonNull.Value);
            if (note is not null)
            {
                d.Add("Meta", new BsonDocument("Note", note));
            }

            d.Add("Discounts", new BsonArray(codes.Select(c => new BsonDocument("Code", c))));
            return d;
        }

        var items = db.GetCollection<BsonDocument>("items" + suffix);
        var i1 = ObjectId.GenerateNewId();
        var i2 = ObjectId.GenerateNewId();
        items.InsertMany([
            new BsonDocument
            {
                { "_id", i1 }, { "Title", "t1" }, { "AccountId", Acme },
                { "Shipping", new BsonDocument { { "Method", "air" }, { "CarrierId", FastCo } } },
                { "Tags", new BsonArray { new BsonDocument("Label", "x"), new BsonDocument("Label", "y") } },
                { "Lines", new BsonArray { Line("s1", "n1", "d1", "d2"), Line("s2", "n2", "d3") } }
            },
            new BsonDocument
            {
                { "_id", i2 }, { "Title", "t2" }, { "AccountId", Globex },
                { "Shipping", new BsonDocument { { "Method", "sea" }, { "CarrierId", BsonNull.Value } } },
                { "Tags", new BsonArray() },
                { "Lines", new BsonArray() }
            },
            // no Shipping, no Tags
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "t3" }, { "AccountId", Acme },
                { "Lines", new BsonArray { Line("s3", note: null) } }
            },
            // dangling AccountId
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "t4" }, { "AccountId", ObjectId.GenerateNewId() },
                { "Shipping", new BsonDocument("Method", "rail") },
                { "Tags", new BsonArray { new BsonDocument("Label", "z") } },
                { "Lines", new BsonArray { Line("s4", "n4", "d4") } }
            },
            // no AccountId
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "Title", "t5" },
                { "Shipping", new BsonDocument("Method", "air") },
                { "Tags", BsonNull.Value }
            }
        ]);
        db.GetCollection<BsonDocument>("replies" + suffix).InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ItemId", i1 }, { "Text", "r1" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ItemId", i1 }, { "Text", "r2" } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "ItemId", i2 }, { "Text", "r3" } }
        ]);
        return suffix;
    }

    private static string Describe(Item i)
        => string.Join(
            "|",
            i.Title,
            i.Account?.Name ?? "-",
            i.Shipping is null ? "-" : i.Shipping.Method + "/" + (i.Shipping.Carrier?.Name ?? "-"),
            string.Join(",", (i.Tags ?? []).Select(t => t.Label)),
            string.Join(";", (i.Lines ?? []).Select(l => $"{l.Sku}:{l.Meta?.Note ?? "-"}{(l.Product is null ? "" : "@" + l.Product.Name)}:" + string.Join(",", (l.Discounts ?? []).Select(d => d.Code)))),
            string.Join(",", (i.Replies ?? []).Select(r => r.Text).Order()));

    // Runs `query` in every mode and returns the NativeOnly rows; Native and DriverLinq must produce the same rows.
    // `expectNative` false means NativeOnly must refuse (a decline pin) while Native still answers like the oracle.
    private string[] RunAllModes(string test, Func<BesideDbContext, IEnumerable<Item>> query, bool expectNative = true, bool ordered = true)
    {
        var suffix = Seed(test);

        string[] Run(MongoQueryMode mode)
        {
            using var db = new BesideDbContext(database, suffix, mode);
            var rows = query(db).Select(Describe);
            return (ordered ? rows : rows.Order()).ToArray();
        }

        var oracle = Run(MongoQueryMode.DriverLinq);
        Assert.Equal(oracle, Run(MongoQueryMode.Native));

        if (expectNative)
        {
            var native = Run(MongoQueryMode.NativeOnly);
            Assert.Equal(oracle, native);
            return native;
        }

        Assert.ThrowsAny<NativeTranslationNotSupportedException>(() => Run(MongoQueryMode.NativeOnly));
        return oracle;
    }

    [Fact]
    public void Include_beside_owned_navigations_reads_every_level()
    {
        var rows = RunAllModes(
            nameof(Include_beside_owned_navigations_reads_every_level),
            db => db.Items.Include(i => i.Account).OrderBy(i => i.Title).ToList());

        Assert.Equal(
            [
                "t1|acme|air/-|x,y|s1:n1:d1,d2;s2:n2:d3|",
                "t2|globex|sea/-|||",
                "t3|acme|-||s3:-:|",
                "t4|-|rail/-|z|s4:n4:d4|",
                "t5|-|air/-|||"
            ],
            rows);
    }

    [Fact]
    public void Include_beside_owned_navigations_with_an_owned_predicate()
    {
        var rows = RunAllModes(
            nameof(Include_beside_owned_navigations_with_an_owned_predicate),
            db => db.Items.Include(i => i.Account).Where(i => i.Shipping.Method == "air").OrderBy(i => i.Title).ToList());

        Assert.Equal(["t1", "t5"], rows.Select(r => r.Split('|')[0]).ToArray());
    }

    [Fact]
    public void Include_beside_owned_navigations_with_a_non_owned_predicate()
    {
        var rows = RunAllModes(
            nameof(Include_beside_owned_navigations_with_a_non_owned_predicate),
            db => db.Items.Include(i => i.Account).Where(i => i.Title != "t3").OrderBy(i => i.Title).ToList());

        Assert.Equal(["t1", "t2", "t4", "t5"], rows.Select(r => r.Split('|')[0]).ToArray());
    }

    [Fact]
    public void Include_beside_owned_navigations_ordered_by_an_owned_member()
    {
        var rows = RunAllModes(
            nameof(Include_beside_owned_navigations_ordered_by_an_owned_member),
            db => db.Items.Include(i => i.Account).OrderBy(i => i.Shipping.Method).ThenBy(i => i.Title).ToList());

        // Missing Shipping sorts first (t3), then air (t1, t5), rail, sea.
        Assert.Equal(["t3", "t1", "t5", "t4", "t2"], rows.Select(r => r.Split('|')[0]).ToArray());
    }

    [Fact]
    public void Include_beside_owned_navigations_with_paging()
    {
        var rows = RunAllModes(
            nameof(Include_beside_owned_navigations_with_paging),
            db => db.Items.Where(i => i.Title != "t4").Include(i => i.Account).OrderByDescending(i => i.Title).Skip(1).Take(2).ToList());

        Assert.Equal(["t3", "t2"], rows.Select(r => r.Split('|')[0]).ToArray());
    }

    [Fact]
    public void Owned_Include_listed_before_the_reference_Include()
    {
        var rows = RunAllModes(
            nameof(Owned_Include_listed_before_the_reference_Include),
            db => db.Items.Include(i => i.Shipping).Include(i => i.Lines).Include(i => i.Account).OrderBy(i => i.Title).ToList());

        Assert.Equal(5, rows.Length);
        Assert.StartsWith("t1|acme|air/-|x,y|s1:n1:d1,d2;s2:n2:d3|", rows[0]);
    }

    [Fact]
    public void Include_beside_owned_navigations_tracked_and_no_tracking()
    {
        var suffix = Seed(nameof(Include_beside_owned_navigations_tracked_and_no_tracking));

        string[] Run(MongoQueryMode mode, Func<IQueryable<Item>, IQueryable<Item>> shape)
        {
            using var db = new BesideDbContext(database, suffix, mode);
            return shape(db.Items.Include(i => i.Account).OrderBy(i => i.Title)).ToList().Select(Describe).ToArray();
        }

        var oracle = Run(MongoQueryMode.DriverLinq, q => q);
        Assert.Equal(oracle, Run(MongoQueryMode.NativeOnly, q => q));
        Assert.Equal(oracle, Run(MongoQueryMode.NativeOnly, q => q.AsNoTracking()));
        Assert.Equal(oracle, Run(MongoQueryMode.NativeOnly, q => q.AsNoTrackingWithIdentityResolution()));
    }

    [Fact]
    public void Owned_siblings_with_a_collection_Include_to_another_collection_go_native()
    {
        // A non-owned collection Include beside the reference Include and the owned siblings is the mixed reference/collection chain.
        var rows = RunAllModes(
            nameof(Owned_siblings_with_a_collection_Include_to_another_collection_go_native),
            db => db.Items.Include(i => i.Account).Include(i => i.Replies).OrderBy(i => i.Title).ToList(),
            expectNative: true);

        Assert.StartsWith("t1|acme|air/-|x,y|s1:n1:d1,d2;s2:n2:d3|r1,r2", rows[0]);
    }

    [Fact]
    public void A_reference_hosted_by_the_owned_sibling_beside_the_reference_Include_answers_like_the_oracle()
    {
        // Include(Account) beside Include(Shipping.Carrier): the second reference lives behind the owned hop, a non-embedded include
        // nested past an embedded sibling. Whatever path it takes the rows must equal the driver-LINQ oracle, and the carrier must load
        // (an admission that ignored the nested include would answer air/-).
        var suffix = Seed(nameof(A_reference_hosted_by_the_owned_sibling_beside_the_reference_Include_answers_like_the_oracle));

        string[] Run(MongoQueryMode mode, bool thenInclude)
        {
            using var db = new BesideDbContext(database, suffix, mode);
            var query = thenInclude
                ? db.Items.Include(i => i.Account).Include(i => i.Shipping).ThenInclude(s => s.Carrier)
                : db.Items.Include(i => i.Account).Include(i => i.Shipping.Carrier);
            return query.OrderBy(i => i.Title).ToList().Select(Describe).ToArray();
        }

        foreach (var thenInclude in new[] { false, true })
        {
            var oracle = Run(MongoQueryMode.DriverLinq, thenInclude);
            Assert.StartsWith("t1|acme|air/fastco|", oracle[0]);
            Assert.Equal(oracle, Run(MongoQueryMode.Native, thenInclude));
        }
    }

    [Fact]
    public void A_reference_hosted_by_an_owned_collection_element_beside_the_reference_Include_loads_the_product()
    {
        // Include(Account) beside Lines.ThenInclude(Product): the product reference lives in an owned collection element. Whatever path
        // the query takes, the product must load exactly as in the oracle: an admission that treated the owned collection as transparent
        // would leave Product null.
        var suffix = Seed(nameof(A_reference_hosted_by_an_owned_collection_element_beside_the_reference_Include_loads_the_product));

        string[] Run(MongoQueryMode mode)
        {
            using var db = new BesideDbContext(database, suffix, mode);
            return db.Items.Include(i => i.Account).Include(i => i.Lines).ThenInclude(l => l.Product).OrderBy(i => i.Title).ToList()
                .Select(Describe).ToArray();
        }

        // Driver-LINQ cannot translate a ThenInclude through an owned collection; Native must refuse it too rather than return rows
        // whose Product is silently null (an admission treating the owned collection as transparent would).
        string[]? oracle = null;
        var oracleFailure = Record.Exception(() => oracle = Run(MongoQueryMode.DriverLinq));
        string[]? native = null;
        var nativeFailure = Record.Exception(() => native = Run(MongoQueryMode.Native));

        if (oracleFailure is null)
        {
            Assert.Null(nativeFailure);
            Assert.Contains("s1:n1@widget:", oracle![0]);
            Assert.Equal(oracle, native);
        }
        else
        {
            Assert.NotNull(nativeFailure);
        }
    }
}
