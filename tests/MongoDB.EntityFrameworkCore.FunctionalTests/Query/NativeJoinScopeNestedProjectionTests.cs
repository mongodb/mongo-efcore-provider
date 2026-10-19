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
/// Differential-correctness functional test (real DB) for the native-join-scope-nested-projection design
/// (<c>docs/superpowers/specs/2026-09-08-native-join-scope-nested-projection-design.md</c>): a nested
/// anonymous-projection member sourced from a reference-Include join scope —
/// <c>new { CustomerInfo = new { Name = o.Customer!.Name } }</c> over <c>Orders.Include(o => o.Customer)</c>
/// — matches an in-memory LINQ oracle, including the UNMATCHED-FK (dangling reference, no matching Customer
/// document) case. Since a reference Include over an OPTIONAL navigation lowers to a LEFT-OUTER
/// <c>$lookup</c>/<c>$unwind</c>, the unmatched row is exactly where a native-vs-fallback bug in the nested
/// leaf's dotted-path read (<c>BsonBinding.CreateGetPropertyValueAtPath</c>'s absent-intermediate-segment
/// handling) would most likely hide — a REQUIRED navigation would just drop that row via an inner unwind,
/// never exercising the null-nested-leaf path at all.
/// </summary>
/// <remarks>
/// The oracle is NOT built by literally executing <c>selector</c> against a plain in-memory object graph
/// with a null <c>Customer</c> navigation: <c>o.Customer!.Name</c>'s null-forgiving <c>!</c> operator emits
/// no runtime null-check at all (it is a compile-time-only warning suppression), so compiling and running
/// that exact expression tree via <c>IQueryable</c>-over-<c>List&lt;T&gt;</c> (which just runs the compiled
/// delegate — no EF null-propagating SQL/MQL translation is involved) would throw
/// <see cref="NullReferenceException"/> for the unmatched row, not produce <see langword="null"/>. EF's own
/// translation of this exact shape against a LEFT-OUTER join is what makes an absent join match propagate as
/// null instead of throwing — that translated behavior is precisely the thing under test, so the oracle
/// instead performs the left-outer join explicitly with <c>GroupJoin</c>/<c>DefaultIfEmpty</c> (ordinary,
/// well-understood LINQ-to-Objects semantics, sharing no code with the provider) and applies the SAME nested
/// anonymous shape to the joined pair.
/// </remarks>
/// <remarks>
/// <b>Why the <c>NativeOnly</c> expectations are <c>#if EF8 || EF9</c>-split.</b> The split is NOT specific
/// to nested projections, and not a limitation of the binder arm under test. On EF8/EF9 an OPTIONAL reference
/// navigation — the shape a reference <c>Include</c> produces — is lowered by EF's nav-expansion onto EF's own
/// internal <c>LeftJoin</c> shim, and <c>NativeSlotPopulator.PopulateNativeSlots</c>' candidate-join arm
/// matches only <c>QueryableMethods.{Join,GroupJoin}</c> plus, under <c>#if !EF8 &amp;&amp; !EF9</c>,
/// <c>QueryableMethods.LeftJoin</c> — which does not exist before EF10. The shim therefore hits that method's
/// catch-all and marks the select non-natively-representable BEFORE any Select-side binder runs, so
/// <c>NativeJoinScopeProjectionBinder.TryBindProjection</c> is never reached at all. Consequently NO wrapped
/// projection over an optional-reference join goes native on EF8/EF9 (including the flat
/// <c>new { o.OrderNo, o.Customer.Name }</c> shape that long predates this feature), while a REQUIRED
/// reference navigation lowers to <c>QueryableMethods.Join</c> and goes native on all three EF versions. See
/// the same explanation, with the measurement, on the nested arm in <c>NativeJoinScopeProjectionBinder</c>.
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

    // The exact motivating shape from the design doc, adapted to this project's model: a top-level scalar
    // sibling (OrderNo, off the query root) alongside a NESTED anonymous member (CustomerInfo) whose own
    // member (Name) is sourced from the join's INNER side (Customer, an optional/nullable reference
    // navigation reached via Include). Depth-1 join scope, single level of nesting — squarely Design §"in
    // scope (v1)".
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
        // A dangling FK: generated but NEVER inserted into the Customers collection, so the $lookup this
        // reference Include emits genuinely finds no match for it (not merely null-CustomerId, which would
        // be a different, less interesting case — the $lookup itself still runs and fails to match).
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

#if EF8 || EF9
        // On EF8/EF9 this shape never reaches the native binder at all — see the class remarks for the exact
        // mechanism (EF's internal LeftJoin shim is not in NativeSlotPopulator's candidate-join arm before
        // EF10). MongoQueryMode.NativeOnly correctly forbids the driver-LINQ fallback this shape still needs
        // there, so it must throw rather than execute; MongoQueryMode.Native (which allows the fallback) is
        // unaffected and is exercised below like on EF10.
        if (mode == MongoQueryMode.NativeOnly)
        {
            Assert.Throws<NativeTranslationNotSupportedException>(() =>
                db.Set<Order>().Include(o => o.Customer)
                    .OrderBy(o => o.OrderNo)
                    .Select(Selector)
                    .ToList());
            return;
        }
#endif

        var actual = db.Set<Order>().Include(o => o.Customer)
            .OrderBy(o => o.OrderNo)
            .Select(Selector)
            .ToList();

        // The independent oracle: an ordinary LINQ-to-Objects left-outer join (see the class remarks for why
        // this — not a literal re-execution of Selector against a null Customer — is the correct oracle
        // construction here), applying the SAME nested anonymous shape to the (Order, Customer?) pair.
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
        // Structural equality of the (compiler-unified, since the shapes match exactly) anonymous type —
        // proves the native result agrees with the oracle on BOTH rows, not merely on row count.
        Assert.Equal(oracle, actual);

        // Named-value assertions too, so a bug that happened to preserve anonymous-type Equals (e.g. via a
        // coincidentally-matching hash/serialization round trip) can't hide: read the actual matched/dangling
        // values back explicitly.
        dynamic matchedRow = actual[0];
        dynamic unmatchedRow = actual[1];
        Assert.Equal(1, (int)matchedRow.OrderNo);
        Assert.Equal("Alfreds", (string)matchedRow.CustomerInfo.Name);
        Assert.Equal(2, (int)unmatchedRow.OrderNo);
        Assert.Null((string?)unmatchedRow.CustomerInfo.Name);
    }

    // ---------------------------------------------------------------------------------------------------
    // End-to-end coverage for the ACCEPT SET of the binder's nested arm (final-review Finding 2). Before the
    // final-review fix the arm accepted any MongoExpression NativeJoinScopeTranslator.TryTranslateValue
    // returned for a nested member, and only ONE of those shapes (an Inner-sourced plain field) was ever
    // executed against a database — which is exactly why the other two shapes shipped as compile-time
    // InvalidCastExceptions in the DEFAULT Native mode. Every shape the arm can now see has a real-DB test:
    //   * Inner-sourced plain field        -> ACCEPTED, Nested_projection_over_reference_include_matches_oracle
    //   * computed (MongoBinaryExpression) -> DECLINES, Nested_projection_with_computed_member_falls_back
    //   * Outer-sourced (MongoOuterField)  -> DECLINES, Nested_projection_with_outer_sourced_member_falls_back
    //   * value-converted field            -> DECLINES, Nested_projection_with_value_converted_member_falls_back
    //                                         (upstream of the arm entirely — see that test)
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A nested member over a VALUE-CONVERTED property (<c>Tier</c>, stored as its enum NAME) DECLINES —
    /// and, unlike the two cases below, it declines UPSTREAM of the nested arm: the join-scope value
    /// translator refuses a value-converted member for a FLAT join-scope leaf too (measured: an ordinary
    /// <c>new { C = x.Inner.ConvertedProperty }</c> over the same join is <c>NativeRoute.Fallback</c>,
    /// while the identical projection of an unconverted property is <c>NativeRoute.Projection</c>). That is
    /// pre-existing behavior this feature neither introduced nor widened, and it is why the nested arm needs
    /// no <c>NativeGroupByBinder.HasDefaultKeySerialization</c> guard of its own (the guard its sibling
    /// <c>NativeProjectionBinder.TryGetDocumentConstructionLeaf</c> applies): no value-converted member can
    /// reach the arm to be guarded. This test exists so that stops being an unverified claim — if the
    /// translator is ever widened to admit converted members, this test flips to a failure and the guard
    /// question must be re-answered rather than silently inherited.
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
    /// A COMPUTED nested member (<c>o.OrderNo + o.Customer!.Rank</c>) is NOT natively representable by the
    /// nested arm — it translates to a <c>MongoBinaryExpression</c>, which the shared read side's
    /// <c>MongoFieldExpression</c> cast cannot accept. It must DECLINE (fall back), not crash. This is the
    /// exact shape the final review reproduced as an <see cref="InvalidCastException"/> at query-compile
    /// time in the default <see cref="MongoQueryMode.Native"/> mode.
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
    /// An OUTER-sourced nested member (<c>Copy = new { N = o.OrderNo }</c> — no Inner access inside the
    /// nested body) likewise DECLINES: the two-scope translator resolves an outer-rooted access to a
    /// <c>MongoOuterFieldExpression</c>, a sealed SIBLING of <c>MongoFieldExpression</c>, which the same read
    /// side cast rejects. The sibling top-level <c>o.Customer!.Name</c> leaf is what forces the join to exist
    /// at all (a projection that never touches the navigation would make EF drop the Include outright, so
    /// there would be no join scope to bind).
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
    /// Shared body for the two DECLINE cases: seeds one matched Order/Customer pair, then asserts that
    /// <see cref="MongoQueryMode.NativeOnly"/> THROWS (proving the shape really does leave the native path —
    /// MQL shape alone could not prove this, see Query/AGENTS.md) while the default
    /// <see cref="MongoQueryMode.Native"/> still returns the correct row via the driver-LINQ fallback, i.e.
    /// exactly the behavior this shape had before the nested arm existed.
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

        // Structural equality against a compiler-unified anonymous instance of the same shape — proves the
        // fallback produced the right VALUES, not merely the right row count.
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

            // Tier is deliberately VALUE-CONVERTED (stored as its enum NAME, not its numeric value) so
            // Nested_projection_with_value_converted_member_falls_back can pin that such a member never
            // reaches the binder's nested arm at all. See the "DELIBERATELY NOT applying" remarks on that arm
            // in NativeJoinScopeProjectionBinder for why it therefore needs no
            // NativeGroupByBinder.HasDefaultKeySerialization guard.
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
