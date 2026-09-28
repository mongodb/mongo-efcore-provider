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
using System.Linq.Expressions;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.FunctionalTests.Utilities;
using MongoDB.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// A nested anonymous member sourced from a reference-Include join scope
/// (<c>new { CustomerInfo = new { Name = o.Customer!.Name } }</c>) matches an in-memory oracle, including a
/// dangling FK. The optional navigation lowers to a left-outer <c>$unwind</c>, so the unmatched row exercises the
/// null-nested-leaf path of <c>BsonBinding.CreateGetPropertyValueAtPath</c>; a required one would drop it.
/// </summary>
/// <remarks>
/// The oracle does an explicit <c>GroupJoin</c>/<c>DefaultIfEmpty</c> rather than running <c>selector</c> over
/// objects: <c>o.Customer!.Name</c> throws <see cref="NullReferenceException"/> in LINQ-to-Objects, while EF's
/// translation propagates null, which is the behavior under test.
/// </remarks>
[XUnitCollection("QueryTests")]
public class NativeJoinScopeNestedProjectionTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    private class Order
    {
        public ObjectId Id { get; set; }
        public int OrderNo { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    private class Customer
    {
        public ObjectId Id { get; set; }
        public string? Name { get; set; }
        public int Rank { get; set; }
        public CustomerTier Tier { get; set; }
    }

    private enum CustomerTier
    {
        Standard = 0,
        Gold = 1
    }

    // A root scalar sibling beside a nested member sourced from the optional Include's inner side; depth-1 scope,
    // one level of nesting.
    private static readonly Expression<Func<Order, object>> Selector =
        o => new { o.OrderNo, CustomerInfo = new { o.Customer!.Name } };

    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Nested_projection_over_reference_include_matches_oracle(MongoQueryMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Nested_projection_over_reference_include_matches_oracle)) + "O" + suffix;
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName(
            nameof(Nested_projection_over_reference_include_matches_oracle)) + "C" + suffix;

        var matchedCustomerId = ObjectId.GenerateNewId();
        var matchedOrderId = ObjectId.GenerateNewId();
        var unmatchedOrderId = ObjectId.GenerateNewId();
        // A dangling FK: never inserted, so the $lookup runs and finds no match (unlike a null CustomerId).
        var danglingCustomerId = ObjectId.GenerateNewId();

        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Customer>().Add(new Customer { Id = matchedCustomerId, Name = "Alfreds" });
            seed.Set<Order>().AddRange(
                new Order { Id = matchedOrderId, OrderNo = 1, CustomerId = matchedCustomerId },   // matched
                new Order { Id = unmatchedOrderId, OrderNo = 2, CustomerId = danglingCustomerId }); // unmatched FK
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, mode);

        var actual = db.Set<Order>().Include(o => o.Customer)
            .OrderBy(o => o.OrderNo)
            .Select(Selector)
            .ToList();

        // Left-outer join in LINQ-to-Objects (see class remarks), applying the same nested shape.
        var orderSeeds = new[]
        {
            new { Id = matchedOrderId, OrderNo = 1, CustomerId = (ObjectId?)matchedCustomerId },
            new { Id = unmatchedOrderId, OrderNo = 2, CustomerId = (ObjectId?)danglingCustomerId }
        };
        var customerSeeds = new[] { new { Id = matchedCustomerId, Name = "Alfreds" } };

        var oracle = orderSeeds
            .GroupJoin(customerSeeds, o => o.CustomerId, c => (ObjectId?)c.Id, (o, cs) => new { o, cs })
            .SelectMany(x => x.cs.DefaultIfEmpty(), (x, c) => new { x.o.OrderNo, CustomerInfo = new { Name = c?.Name } })
            .OrderBy(x => x.OrderNo)
            .Cast<object>()
            .ToList();

        Assert.Equal(2, actual.Count);
        // Structural equality of the compiler-unified anonymous type: values on both rows, not just count.
        Assert.Equal(oracle, actual);

        // Named-value checks too, in case anonymous-type Equals hides a bug.
        dynamic matchedRow = actual[0];
        dynamic unmatchedRow = actual[1];
        Assert.Equal(1, (int)matchedRow.OrderNo);
        Assert.Equal("Alfreds", (string)matchedRow.CustomerInfo.Name);
        Assert.Equal(2, (int)unmatchedRow.OrderNo);
        Assert.Null((string?)unmatchedRow.CustomerInfo.Name);
    }

    // ---------------------------------------------------------------------------------------------------
    // End-to-end coverage of the nested arm's accept set; each shape it can see runs against a real database:
    //   * Inner-sourced plain field        -> ACCEPTED, Nested_projection_over_reference_include_matches_oracle
    //   * computed (MongoBinaryExpression) -> DECLINES, Nested_projection_with_computed_member_falls_back
    //   * Outer-sourced (MongoOuterField)  -> DECLINES, Nested_projection_with_outer_sourced_member_falls_back
    //   * value-converted field            -> DECLINES, Nested_projection_with_value_converted_member_falls_back
    //                                         (upstream of the arm entirely — see that test)
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A nested member over a value-converted property (<c>Tier</c>, stored as its enum name) declines upstream of
    /// the nested arm: the join-scope value translator refuses converted members for flat leaves too. That's why
    /// the arm has no <c>HasDefaultKeySerialization</c> guard; if the translator is ever widened, this test fails
    /// and the guard question must be revisited.
    /// </summary>
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Nested_projection_with_value_converted_member_falls_back(MongoQueryMode mode)
        => AssertNestedLeafDeclinesButStillReturnsCorrectRows(
            nameof(Nested_projection_with_value_converted_member_falls_back),
            mode,
            o => new { o.OrderNo, CustomerInfo = new { o.Customer!.Name, o.Customer!.Tier } },
            expected: new { OrderNo = 1, CustomerInfo = new { Name = "Alfreds", Tier = CustomerTier.Gold } });

    /// <summary>
    /// A computed nested member (<c>o.OrderNo + o.Customer!.Rank</c>) is a <c>MongoBinaryExpression</c>, which the
    /// shared read side's <c>MongoFieldExpression</c> cast can't accept; it must fall back, not throw
    /// <see cref="InvalidCastException"/> at compile time.
    /// </summary>
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Nested_projection_with_computed_member_falls_back(MongoQueryMode mode)
        => AssertNestedLeafDeclinesButStillReturnsCorrectRows(
            nameof(Nested_projection_with_computed_member_falls_back),
            mode,
            o => new { o.OrderNo, Wrap = new { Combo = o.OrderNo + o.Customer!.Rank } },
            expected: new { OrderNo = 1, Wrap = new { Combo = 1 + 7 } });

    /// <summary>
    /// An outer-sourced nested member (<c>Copy = new { N = o.OrderNo }</c>) is a <c>MongoOuterFieldExpression</c>,
    /// rejected by the same cast; falls back. The sibling <c>o.Customer!.Name</c> leaf keeps the join alive (EF
    /// drops an Include the projection never touches).
    /// </summary>
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Nested_projection_with_outer_sourced_member_falls_back(MongoQueryMode mode)
        => AssertNestedLeafDeclinesButStillReturnsCorrectRows(
            nameof(Nested_projection_with_outer_sourced_member_falls_back),
            mode,
            o => new { o.Customer!.Name, Copy = new { N = o.OrderNo } },
            expected: new { Name = "Alfreds", Copy = new { N = 1 } });

    /// <summary>
    /// Shared body for the decline cases: <see cref="MongoQueryMode.NativeOnly"/> throws (the only reliable proof
    /// of leaving the native path) while <see cref="MongoQueryMode.Native"/> returns the correct row via fallback.
    /// </summary>
    private void AssertNestedLeafDeclinesButStillReturnsCorrectRows(
        string testName, MongoQueryMode mode, Expression<Func<Order, object>> selector, object expected)
    {
        var (ordersName, customersName) = CreateCollectionNames(testName);

        var customerId = ObjectId.GenerateNewId();
        using (var seed = new JoinScopeDbContext(database, ordersName, customersName, MongoQueryMode.DriverLinq))
        {
            seed.Set<Customer>().Add(
                new Customer { Id = customerId, Name = "Alfreds", Rank = 7, Tier = CustomerTier.Gold });
            seed.Set<Order>().Add(new Order { Id = ObjectId.GenerateNewId(), OrderNo = 1, CustomerId = customerId });
            seed.SaveChanges();
        }

        using var db = new JoinScopeDbContext(database, ordersName, customersName, mode);

        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.Throws<NativeTranslationNotSupportedException>(
                () => db.Set<Order>().Include(o => o.Customer).Select(selector).ToList());
            return;
        }

        var actual = db.Set<Order>().Include(o => o.Customer).Select(selector).ToList();

        // Structural equality: right values, not just right row count.
        Assert.Equal([expected], actual);
    }

    private static (string Orders, string Customers) CreateCollectionNames(string testName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + "O" + suffix,
            TemporaryDatabaseFixtureBase.CreateCollectionName(testName) + "C" + suffix);
    }

    private class JoinScopeDbContext : DbContext
    {
        private readonly string _ordersCollection;
        private readonly string _customersCollection;

        public JoinScopeDbContext(
            TemporaryDatabaseFixture database, string ordersCollection, string customersCollection,
            MongoQueryMode mode)
            : base(new DbContextOptionsBuilder<JoinScopeDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Tier is value-converted (stored as its enum name) so
            // Nested_projection_with_value_converted_member_falls_back can pin that it never reaches the nested arm.
            modelBuilder.Entity<Customer>().ToCollection(_customersCollection)
                .Property(c => c.Tier).HasConversion<string>();
            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId).IsRequired(false);
            });
        }

        private sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime) => Interlocked.Increment(ref _count);
        }
    }
}
