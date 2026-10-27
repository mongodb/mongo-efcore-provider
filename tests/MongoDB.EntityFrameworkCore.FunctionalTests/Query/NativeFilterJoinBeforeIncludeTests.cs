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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A <c>Where</c> over a join's inner side followed by paging and a reference <c>Include</c> of a second navigation: the
/// paging belongs to the filtered rows, ahead of the Include's join. Native admits it only when the Include's join is
/// left-outer (row-preserving); a REQUIRED navigation's inner join can drop rows the page already counted, so that shape
/// must stay declined.
/// </summary>
public class NativeFilterJoinBeforeIncludeTests(TemporaryDatabaseFixture database) : IClassFixture<TemporaryDatabaseFixture>
{
    public class Order
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Product
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    public class Line
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? OrderId { get; set; }
        public Order? Order { get; set; }
        public ObjectId? ProductId { get; set; }
        public Product? Product { get; set; }
    }

    public class RequiredLine
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public ObjectId? OrderId { get; set; }
        public Order? Order { get; set; }
        public ObjectId ProductId { get; set; }
        public Product Product { get; set; } = null!;
    }

    // L2 is filtered out by the Order predicate; L3 has no product, L4 a dangling one.
    [Theory]
    [InlineData("skip_take", new[] { "L3:-", "L4:-", "L5:Widget" })]
    [InlineData("take", new[] { "L1:Widget", "L3:-", "L4:-" })]
    [InlineData("skip", new[] { "L3:-", "L4:-", "L5:Widget" })]
    public void Paging_between_a_filter_join_and_an_optional_include_pages_the_filtered_rows(string shape, string[] expected)
    {
        var names = Seed();
        NativeModeAssert.NativeAndExpected(
            mode =>
            {
                using var db = Create(names, mode, typeof(Line));
                var q = db.Set<Line>().OrderBy(l => l.Name).Where(l => l.Order!.Name != "O2");
                q = shape switch
                {
                    "skip_take" => q.Skip(1).Take(3),
                    "take" => q.Take(3),
                    _ => q.Skip(1)
                };
                return q.Include(l => l.Product).AsEnumerable().Select(l => $"{l.Name}:{l.Product?.Name ?? "-"}").ToList();
            },
            [.. expected]);
    }

    [Fact]
    public void Paging_between_a_filter_join_and_a_required_include_declines_cleanly()
    {
        var names = Seed();
        NativeModeAssert.DeclinesCleanly(
            mode =>
            {
                using var db = Create(names, mode, typeof(RequiredLine));
                return db.Set<RequiredLine>().OrderBy(l => l.Name).Where(l => l.Order!.Name != "O2")
                    .Skip(1).Take(3).Include(l => l.Product)
                    .AsEnumerable().Select(l => l.Name).ToList();
            });
    }

    private sealed record Names(string Lines, string Orders, string Products);

    private SingleContext Create(Names names, MongoQueryMode mode, Type lineType)
    {
        var builder = new DbContextOptionsBuilder<SingleContext>()
            .UseMongoDB(
                database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                o => o.UseQueryMode(mode))
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new SingleContext(builder.Options, lineType, names);
    }

    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory
    {
        private static int _count;
        public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
    }

    private sealed class SingleContext(DbContextOptions<SingleContext> options, Type lineType, Names names)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Order>(b =>
            {
                b.ToCollection(names.Orders);
                b.Property(o => o.Name).HasElementName("name");
            });
            mb.Entity<Product>(b =>
            {
                b.ToCollection(names.Products);
                b.Property(p => p.Name).HasElementName("name");
            });

            if (lineType == typeof(Line))
            {
                mb.Entity<Line>(b =>
                {
                    b.ToCollection(names.Lines);
                    b.Property(l => l.Name).HasElementName("name");
                    b.Property(l => l.OrderId).HasElementName("ord_id");
                    b.Property(l => l.ProductId).HasElementName("prod_id");
                    b.HasOne(l => l.Order).WithMany().HasForeignKey(l => l.OrderId);
                    b.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId);
                });
            }
            else
            {
                mb.Entity<RequiredLine>(b =>
                {
                    b.ToCollection(names.Lines);
                    b.Property(l => l.Name).HasElementName("name");
                    b.Property(l => l.OrderId).HasElementName("ord_id");
                    b.Property(l => l.ProductId).HasElementName("prod_id");
                    b.HasOne(l => l.Order).WithMany().HasForeignKey(l => l.OrderId);
                    b.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).IsRequired();
                });
            }
        }
    }

    private Names Seed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var names = new Names(
            TemporaryDatabaseFixtureBase.CreateCollectionName("fjLines") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("fjOrders") + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName("fjProducts") + suffix);

        var o1 = ObjectId.GenerateNewId();
        var o2 = ObjectId.GenerateNewId();
        var widget = ObjectId.GenerateNewId();
        database.MongoDatabase.GetCollection<BsonDocument>(names.Orders).InsertMany(
        [
            new BsonDocument { { "_id", o1 }, { "name", "O1" } },
            new BsonDocument { { "_id", o2 }, { "name", "O2" } }
        ]);
        database.MongoDatabase.GetCollection<BsonDocument>(names.Products).InsertOne(
            new BsonDocument { { "_id", widget }, { "name", "Widget" } });
        database.MongoDatabase.GetCollection<BsonDocument>(names.Lines).InsertMany(
        [
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L1" }, { "ord_id", o1 }, { "prod_id", widget } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L2" }, { "ord_id", o2 }, { "prod_id", widget } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L3" }, { "ord_id", o1 } },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "name", "L4" }, { "ord_id", o1 }, { "prod_id", ObjectId.GenerateNewId() }
            },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "name", "L5" }, { "ord_id", o1 }, { "prod_id", widget } }
        ]);
        return names;
    }
}
