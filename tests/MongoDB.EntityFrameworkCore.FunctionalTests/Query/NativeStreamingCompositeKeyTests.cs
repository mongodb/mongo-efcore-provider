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

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// An entity with a composite primary key is stored with the parts of its key as the elements of an <c>_id</c> sub-document. The one-pass
/// reader descends into it and must give each part its value, keep the other elements apart from the key's, and report a missing part.
/// </summary>
public class NativeStreamingCompositeKeyTests(TemporaryDatabaseFixture database)
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

    private class Invoice
    {
        public string Region { get; set; } = "";
        public int Number { get; set; }
        public string Customer { get; set; } = "";
        public decimal? Total { get; set; }
        public string? City { get; set; }
    }

    private static readonly Action<ModelBuilder> InvoiceModel = mb =>
        mb.Entity<Invoice>().HasKey(i => new { i.Region, i.Number });

    private IMongoCollection<Invoice> SeedInvoices([CallerMemberName] string? name = null)
    {
        var collection = database.CreateCollection<Invoice>(name);
        using var seed = CreateContext(collection, MongoQueryMode.Native, InvoiceModel);
        seed.Entities.AddRange(
            new Invoice { Region = "eu", Number = 1, Customer = "ann", Total = 10m, City = "Oslo" },
            new Invoice { Region = "eu", Number = 2, Customer = "bob", Total = null },
            new Invoice { Region = "us", Number = 1, Customer = "cy", Total = 30m, City = "Rome" });
        seed.SaveChanges();
        return collection;
    }

    private static string Describe(Invoice i)
        => $"{i.Region}/{i.Number}:{i.Customer}:{i.Total?.ToString() ?? "-"}:{i.City ?? "-"}";

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

    [Fact]
    public void Composite_key_entity_query_streams()
    {
        var collection = SeedInvoices();
        using var db = CreateContext(collection, MongoQueryMode.NativeOnly, InvoiceModel);

        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Entities.AsNoTracking()));
        Assert.NotEqual(typeof(BsonDocument), CursorRowType(db.Entities));
    }

    [Fact]
    public void Composite_key_parts_and_the_other_elements_read_correctly()
    {
        var collection = SeedInvoices();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, InvoiceModel);
            var all = db.Entities.AsNoTracking().OrderBy(i => i.Region).ThenBy(i => i.Number).AsEnumerable().Select(Describe).ToList();
            var one = db.Entities.AsNoTracking().Single(i => i.Region == "us" && i.Number == 1);
            return all.Concat([Describe(one)]).ToList();
        });

        Assert.Equal(
            ["eu/1:ann:10:Oslo", "eu/2:bob:-:-", "us/1:cy:30:Rome", "us/1:cy:30:Rome"],
            results);
    }

    [Fact]
    public void Tracked_composite_key_query_tracks_by_the_whole_key()
    {
        var collection = SeedInvoices();

        var results = NativeModeAssert.NativeAndParity(mode =>
        {
            using var db = CreateContext(collection, mode, InvoiceModel);
            var tracked = db.Entities.Single(i => i.Region == "eu" && i.Number == 2);
            var all = db.Entities.OrderBy(i => i.Region).ThenBy(i => i.Number).ToList();
            return new List<string>
            {
                $"entries={db.ChangeTracker.Entries<Invoice>().Count()}",
                $"same={ReferenceEquals(tracked, all[1])}",
                $"keys={string.Join(",", db.ChangeTracker.Entries<Invoice>().Select(e => e.Entity.Region + e.Entity.Number).OrderBy(k => k))}",
            };
        });

        Assert.Equal("entries=3", results[0]);
        Assert.Equal("same=True", results[1]);
        Assert.Equal("keys=eu1,eu2,us1", results[2]);
    }

    [Fact]
    public void A_missing_key_part_is_reported_as_missing()
    {
        var collection = SeedInvoices();
        var raw = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);
        raw.InsertOne(new BsonDocument { { "_id", new BsonDocument { { "Region", "xx" } } }, { "Customer", "dan" } });

        foreach (var mode in new[] { MongoQueryMode.DriverLinq, MongoQueryMode.Native })
        {
            using var db = CreateContext(collection, mode, InvoiceModel);
            Assert.Throws<InvalidOperationException>(() => db.Entities.AsNoTracking().Where(i => i.Customer == "dan").ToList());
        }
    }
}
