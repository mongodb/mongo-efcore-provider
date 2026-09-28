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
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A navigation-less (EF-377, raw-key) join whose key is one COMPONENT of a composite primary key. Such a property
/// is stored nested under <c>_id</c> (<c>{ _id: { OrderId: 1, ProductId: 7 } }</c>), so the raw-key
/// <c>$lookup</c> must match on <c>_id.ProductId</c> (<c>LookupExpression.GetFieldPath</c>), not the bare element
/// name — which names no field at all, silently matching nothing. Differential against an in-memory LINQ oracle,
/// all three modes, inner and left-outer.
/// </summary>
[XUnitCollection("QueryTests")]
public class NativeCompositeKeyJoinTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private class LineItem
    {
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    [Theory]
    [InlineData(MongoQueryMode.Native, false)]
    [InlineData(MongoQueryMode.DriverLinq, false)]
    [InlineData(MongoQueryMode.NativeOnly, false)]
    [InlineData(MongoQueryMode.Native, true)]
    [InlineData(MongoQueryMode.DriverLinq, true)]
    [InlineData(MongoQueryMode.NativeOnly, true)]
    public void Navigation_less_join_onto_a_composite_key_component_matches_oracle(MongoQueryMode mode, bool leftOuter)
    {
        var products = new[] { new Product { Id = 7, Name = "Widget" }, new Product { Id = 8, Name = "Gadget" } };
        var lineItems = new[]
        {
            new LineItem { OrderId = 1, ProductId = 7, Quantity = 2 },
            new LineItem { OrderId = 2, ProductId = 7, Quantity = 5 },
        };

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var productsName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Product)) + suffix + mode + leftOuter;
        var lineItemsName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(LineItem)) + suffix + mode + leftOuter;

        using (var seed = new CompositeKeyJoinDbContext(database, productsName, lineItemsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Product>().AddRange(products);
            seed.Set<LineItem>().AddRange(lineItems);
            seed.SaveChanges();
        }

        using var db = new CompositeKeyJoinDbContext(database, productsName, lineItemsName, mode);

        System.Collections.Generic.List<(string Name, int? Quantity)> actual, expected;
        if (leftOuter)
        {
            actual = (from p in db.Set<Product>()
                      join li in db.Set<LineItem>() on p.Id equals li.ProductId into g
                      from li in g.DefaultIfEmpty()
                      select new { p.Name, Quantity = li != null ? (int?)li.Quantity : null })
                .AsEnumerable().Select(x => (x.Name, x.Quantity)).ToList();
            expected = (from p in products
                        join li in lineItems on p.Id equals li.ProductId into g
                        from li in g.DefaultIfEmpty()
                        select (p.Name, li != null ? (int?)li.Quantity : null)).ToList();
        }
        else
        {
            actual = db.Set<Product>()
                .Join(db.Set<LineItem>(), p => p.Id, li => li.ProductId, (p, li) => new { p.Name, li.Quantity })
                .AsEnumerable().Select(x => (x.Name, (int?)x.Quantity)).ToList();
            expected = products
                .Join(lineItems, p => p.Id, li => li.ProductId, (p, li) => (p.Name, (int?)li.Quantity)).ToList();
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(x => x.Name).ThenBy(x => x.Quantity), actual.OrderBy(x => x.Name).ThenBy(x => x.Quantity));
    }

    // The CHAINED form, which is also broken in RELEASED versions (MEASURED against v10.0.4: no rows): a chain of two
    // or more joins registers every hop's $lookup at translation time, so even the driver-LINQ path emits the raw-key
    // lookups — both the first hop's foreignField and the second hop's through-hop-prefixed localField are
    // composite-PK components that live under `_id`.
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Chained_navigation_less_join_onto_a_composite_key_component_matches_oracle(MongoQueryMode mode)
    {
        var products = new[] { new Product { Id = 7, Name = "Widget" }, new Product { Id = 8, Name = "Gadget" } };
        var lineItems = new[]
        {
            new LineItem { OrderId = 1, ProductId = 7, Quantity = 2 },
            new LineItem { OrderId = 2, ProductId = 7, Quantity = 5 },
        };

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var productsName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(Product)) + suffix + "chain" + mode;
        var lineItemsName = TemporaryDatabaseFixtureBase.CreateCollectionName(nameof(LineItem)) + suffix + "chain" + mode;

        using (var seed = new CompositeKeyJoinDbContext(database, productsName, lineItemsName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Product>().AddRange(products);
            seed.Set<LineItem>().AddRange(lineItems);
            seed.SaveChanges();
        }

        using var db = new CompositeKeyJoinDbContext(database, productsName, lineItemsName, mode);

        var actual = db.Set<Product>()
            .Join(db.Set<LineItem>(), p => p.Id, li => li.ProductId, (p, li) => new { p, li })
            .Join(db.Set<Product>(), x => x.li.ProductId, p2 => p2.Id, (x, p2) => new { x.p.Name, x.li.Quantity, P2 = p2.Name })
            .AsEnumerable().Select(x => (x.Name, x.Quantity, x.P2)).OrderBy(x => x.Quantity).ToList();
        var expected = products
            .Join(lineItems, p => p.Id, li => li.ProductId, (p, li) => new { p, li })
            .Join(products, x => x.li.ProductId, p2 => p2.Id, (x, p2) => (x.p.Name, x.li.Quantity, p2.Name))
            .OrderBy(x => x.Quantity).ToList();

        Assert.Equal(2, expected.Count);
        Assert.Equal(expected, actual);
    }

    private class CompositeKeyJoinDbContext : DbContext
    {
        private readonly string _productsCollection;
        private readonly string _lineItemsCollection;

        public CompositeKeyJoinDbContext(
            TemporaryDatabaseFixture database, string productsCollection, string lineItemsCollection, MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<CompositeKeyJoinDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _productsCollection = productsCollection;
            _lineItemsCollection = lineItemsCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Product>().ToCollection(_productsCollection);
            modelBuilder.Entity<LineItem>(b =>
            {
                b.ToCollection(_lineItemsCollection);
                b.HasKey(li => new { li.OrderId, li.ProductId });
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
