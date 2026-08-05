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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

/// <summary>
/// EF-370: the <c>$unwind</c> following a cross-collection <c>$lookup</c> must follow the join semantics the
/// query actually asked for. EF navigation expansion lowers a REQUIRED reference navigation to an inner
/// <see cref="Queryable.Join{TOuter,TInner,TKey,TResult}"/> and an OPTIONAL one to a <c>LeftJoin</c>; a
/// left-outer <c>$unwind</c> for the former returns rows that should have been excluded.
/// <para>
/// MongoDB enforces no referential integrity, so a <b>dangling foreign key</b> — a value matching no document
/// in the target collection — is an ordinary data state. This fixture is the only one in the suite that seeds
/// one, and that absence is a large part of why the defect survived: with every FK resolvable, an inner and a
/// left-outer join return identical rows.
/// </para>
/// <para>
/// Assertions here are on <b>row counts and identities, never MQL</b>. Wrong MQL for this defect is
/// indistinguishable from the MQL of a legitimately different query, so an MQL baseline cannot catch it.
/// </para>
/// </summary>
[XUnitCollection("QueryTests")]
public class RequiredNavigationUnwindTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---------------------------------------------------------------------------------------------------
    // Required navigations: INNER join semantics. These run on ALL THREE EF majors.
    //
    // The version asymmetry is deliberate and asserted rather than merely accommodated (see
    // Optional_reference_Include_is_not_translated_on_EF8_EF9 below): only the LeftJoin *dispatch case* in
    // MongoQueryableMethodTranslatingExpressionVisitor is `#if !EF8 && !EF9`, not cross-collection Include as
    // a whole. A required navigation lowers to Queryable.Join, which dispatches on every version, so the
    // defect reproduced — with silently wrong rows — on EF8 and EF9 too. Reading the EF-X020 limitation as
    // "cross-collection reference Include does not work before EF10" is what made EF-369 initially look
    // EF10-only, and it is wrong.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Required_single_reference_Include_excludes_dangling_foreign_key()
    {
        using var db = Setup();

        var lines = db.Lines.Include(l => l.Product).ToList();

        // L5's prod_id matches no product, so it is excluded: Product is required.
        Assert.Equal(["L1", "L2", "L3", "L4", "L6"], LineNames(lines));
        Assert.All(lines, l => Assert.NotNull(l.Product));
    }

    // EF-368: the same seed, now asserted ACROSS MODES. This is the assertion that would have caught
    // EF-370's row-count divergence, and it is the slice's gate: the native pipeline and the driver-LINQ
    // fallback must return the same rows over a dangling foreign key, not merely both "work".
    [Theory]
    [InlineData(MongoQueryMode.Native)]
    [InlineData(MongoQueryMode.DriverLinq)]
    [InlineData(MongoQueryMode.NativeOnly)]
    public void Required_single_reference_Include_excludes_dangling_foreign_key_in_every_mode(MongoQueryMode mode)
    {
        using var db = CreateContext(mode);

        var orders = db.Orders.Include(o => o.Buyer).OrderBy(o => o.OrderName).ToList();

        // O3's buyer_id is dangling and Buyer is required => inner join => O3 absent, in EVERY mode.
        Assert.Equal(3, orders.Count);
        Assert.All(orders, o => Assert.NotNull(o.Buyer));
        Assert.DoesNotContain(orders, o => o.Buyer == null);
    }

    [Fact]
    public void Required_two_reference_Includes_exclude_dangling_foreign_keys()
    {
        using var db = Setup();

        var lines = db.Lines
            .Include(l => l.Order)
            .Include(l => l.Product)
            .ToList();

        // L4 has a dangling ord_id, L5 a dangling prod_id; both navigations are required.
        Assert.Equal(["L1", "L2", "L3", "L6"], LineNames(lines));
        Assert.All(lines, l =>
        {
            Assert.NotNull(l.Order);
            Assert.NotNull(l.Product);
        });
    }

    [Fact]
    public void Required_two_hop_ThenInclude_excludes_dangling_foreign_key_at_either_hop()
    {
        using var db = Setup();

        var lines = db.Lines
            .Include(l => l.Order)
            .ThenInclude(o => o.Buyer)
            .ToList();

        // L4's order is missing; L3's order O3 exists but its buyer_id dangles. Both hops are required, so
        // both rows are excluded.
        Assert.Equal(["L1", "L2", "L5", "L6"], LineNames(lines));
        Assert.All(lines, l =>
        {
            Assert.NotNull(l.Order);
            Assert.NotNull(l.Order.Buyer);
        });
    }

    [Fact]
    public void User_authored_Join_is_inner()
    {
        using var db = Setup();

        var pairs = db.Lines
            .Join(db.Orders, l => l.OrderId, o => o._id, (l, o) => new { l.LineName, o.OrderName })
            .OrderBy(x => x.LineName)
            .ToList();

        // A user Join is unambiguously inner, so L4 (dangling ord_id) contributes nothing.
        Assert.Equal(["L1", "L2", "L3", "L5", "L6"], pairs.Select(p => p.LineName).ToArray());
        Assert.All(pairs, p => Assert.NotNull(p.OrderName));
    }

    [Fact]
    public void Collection_Include_still_preserves_principals_with_no_children()
    {
        using var db = Setup();

        // NOTE ON THIS BRANCH ONLY (NativeQueryOngoing): a single-level collection Include (o.Lines) went
        // native in EF-339, so this query takes the NATIVE path — MongoSelectLowerer.AppendLookupStages'
        // IsNativeCollectionLookup arm, which emits a flat $lookup with NO $unwind stage at all (the joined
        // array is materialized directly by the DOM shaper). LookupExpression.PreserveNullAndEmptyArrays
        // only governs an $unwind stage, so it is never consulted here. The "childless principal survives"
        // assertion below is therefore a flat-array-materialization regression test, not coverage of
        // EF-370's required-navigation $unwind-semantics fix (which this file otherwise exists to cover) —
        // it passes trivially regardless of that flag's value. On the main-bound branch (PR #328), the same
        // test DOES exercise the fix, because reference/collection Include there always falls back to
        // driver-LINQ's $lookup+$unwind path (see MongoEFToLinqTranslatingExpressionVisitor.LeftJoin.cs),
        // which the flag governs. Do not read this test's pass as confirming the unwind fix on this branch.
        var orders = db.Orders.Include(o => o.Lines).OrderBy(o => o.OrderName).ToList();

        // An Include must never drop principals. O4 has no lines at all and must still be returned.
        Assert.Equal(["O1", "O2", "O3", "O4"], orders.Select(o => o.OrderName).ToArray());
        Assert.Equal(["L1", "L5", "L6"], LineNames(orders.Single(o => o.OrderName == "O1").Lines));
        Assert.Empty(orders.Single(o => o.OrderName == "O4").Lines);
    }

    [Fact]
    public void Reference_ThenInclude_under_collection_Include_still_preserves_elements()
    {
        using var db = Setup();

        var orders = db.Orders
            .Include(o => o.Lines)
            .ThenInclude(l => l.Product)
            .OrderBy(o => o.OrderName)
            .ToList();

        // The site-3 boundary (MongoProjectionBindingExpressionVisitor.AddReferenceLookupStages): the nested
        // reference $unwind runs inside the collection lookup's sub-pipeline, so a non-preserving unwind
        // would drop collection ELEMENTS. L5's product is dangling yet L5 must still appear under O1, with a
        // null Product. This inconsistency with the required-navigation rule above is deliberate — see the
        // comment at that site.
        var o1Lines = orders.Single(o => o.OrderName == "O1").Lines;
        Assert.Equal(["L1", "L5", "L6"], LineNames(o1Lines));
        Assert.Null(o1Lines.Single(l => l.LineName == "L5").Product);
        Assert.NotNull(o1Lines.Single(l => l.LineName == "L1").Product);
    }

    // ---------------------------------------------------------------------------------------------------
    // Optional navigations: LEFT-OUTER semantics, EF10 only.
    //
    // EF lowers an optional reference navigation to Queryable.LeftJoin, whose dispatch case in
    // MongoQueryableMethodTranslatingExpressionVisitor.VisitMethodCall is `#if !EF8 && !EF9`. On EF8/EF9 the
    // shape therefore never reaches the provider's translator and EF Core itself throws "could not be
    // translated" (EF-X020) — asserted below rather than left implicit.
    // ---------------------------------------------------------------------------------------------------

#if !EF8 && !EF9
    [Fact]
    public void Optional_reference_Include_still_preserves_principals()
    {
        using var db = Setup();

        var lines = db.Lines.Include(l => l.Carrier).ToList();

        // Carrier is optional: L2 has no carr_id and L6's dangles, but neither row may be dropped.
        Assert.Equal(["L1", "L2", "L3", "L4", "L5", "L6"], LineNames(lines));
        Assert.Null(lines.Single(l => l.LineName == "L2").Carrier);
        Assert.Null(lines.Single(l => l.LineName == "L6").Carrier);
        Assert.NotNull(lines.Single(l => l.LineName == "L1").Carrier);
    }

    [Fact]
    public void Mixed_required_and_optional_Includes_apply_each_navigations_own_semantics()
    {
        using var db = Setup();

        var lines = db.Lines
            .Include(l => l.Carrier)
            .Include(l => l.Order)
            .ToList();

        // Two joins in one pipeline with opposite semantics: the required Order drops L4 (dangling ord_id),
        // while the optional Carrier keeps L2 (no FK) and L6 (dangling FK) with a null navigation.
        Assert.Equal(["L1", "L2", "L3", "L5", "L6"], LineNames(lines));
        Assert.All(lines, l => Assert.NotNull(l.Order));
        Assert.Null(lines.Single(l => l.LineName == "L2").Carrier);
        Assert.Null(lines.Single(l => l.LineName == "L6").Carrier);
    }

    [Fact]
    public void User_authored_GroupJoin_is_left_outer()
    {
        // EF10-only for the same EF-X020 reason as the cases above, not because of FK requiredness: on
        // EF8/EF9 GroupJoin + DefaultIfEmpty does not translate at all (see the EF8/EF9 arm of
        // NorthwindJoinQueryMongoTest.GroupJoin_DefaultIfEmpty).
        using var db = Setup();

        var pairs = db.Lines
            .GroupJoin(db.Orders, l => l.OrderId, o => o._id, (l, os) => new { l, os })
            .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.l.LineName, Order = o })
            .OrderBy(x => x.LineName)
            .ToList();

        // GroupJoin + DefaultIfEmpty is left-outer: every line survives, L4 has no matching order.
        Assert.Equal(["L1", "L2", "L3", "L4", "L5", "L6"], pairs.Select(p => p.LineName).ToArray());
        Assert.Null(pairs.Single(p => p.LineName == "L4").Order);
        Assert.Equal("O1", pairs.Single(p => p.LineName == "L1").Order.OrderName);
    }

    [Fact]
    public void User_authored_LeftJoin_is_left_outer()
    {
        using var db = Setup();

        var pairs = db.Lines
            .LeftJoin(db.Orders, l => l.OrderId, o => o._id, (l, o) => new { l.LineName, OrderName = o == null ? null : o.OrderName })
            .OrderBy(x => x.LineName)
            .ToList();

        // LeftJoin is left-outer: L4 survives with no matching order.
        Assert.Equal(["L1", "L2", "L3", "L4", "L5", "L6"], pairs.Select(p => p.LineName).ToArray());
        Assert.Null(pairs.Single(p => p.LineName == "L4").OrderName);
        Assert.Equal("O1", pairs.Single(p => p.LineName == "L1").OrderName);
    }
#endif

#if EF8 || EF9
    [Fact]
    public void Optional_reference_Include_is_not_translated_on_EF8_EF9()
    {
        // The counterpart of the three `#if !EF8 && !EF9` cases above, asserted so the asymmetry is a
        // recorded fact rather than an unexplained absence. EF lowers the optional navigation to
        // Queryable.LeftJoin, which has no dispatch case before EF10, so EF Core rejects the whole query
        // (EF-X020). The REQUIRED cases in this class run on all three majors precisely because a required
        // navigation lowers to Queryable.Join instead.
        using var db = Setup();

        Assert.Throws<InvalidOperationException>(() => db.Lines.Include(l => l.Carrier).ToList());
    }
#endif

    [Fact]
    public void Collection_Include_beside_a_required_reference_Include_preserves_childless_principals()
    {
        using var db = Setup();

        // NOTE ON THIS BRANCH ONLY (NativeQueryOngoing): only HALF of this test exercises EF-370's fix. The
        // Buyer half (a required reference Include) still falls back to driver-LINQ's $lookup+$unwind path
        // and DOES exercise LookupExpression.PreserveNullAndEmptyArrays via the required-navigation
        // unwind-semantics fix (o3 is correctly dropped below because its buyer_id dangles). The Lines half
        // (a single-level collection Include) is native as of EF-339 — a flat $lookup with no $unwind stage
        // at all, per MongoSelectLowerer.AppendLookupStages' IsNativeCollectionLookup arm — so the
        // "childless principal survives" assertion on O4 is, like the test above, a flat-array
        // materialization regression check rather than unwind-semantics coverage; it passes regardless of
        // PreserveNullAndEmptyArrays. See the note on Collection_Include_still_preserves_principals_with_no_children.
        var orders = db.Orders
            .Include(o => o.Lines)
            .Include(o => o.Buyer)
            .ToList();

        // Two lookups in one pipeline, each with its own semantics: the required Buyer reference drops O3
        // (its buyer_id dangles), while the Lines collection keeps O4, which has no lines at all. A
        // collection Include must never drop a childless principal.
        Assert.Equal(["O1", "O2", "O4"], orders.Select(o => o.OrderName).OrderBy(n => n).ToArray());
        Assert.Empty(orders.Single(o => o.OrderName == "O4").Lines);
        Assert.All(orders, o => Assert.NotNull(o.Buyer));
    }

    [Fact]
    public void User_authored_Join_over_a_collection_navigation_is_inner()
    {
        using var db = Setup();

        var rows = db.Orders
            .Join(db.Lines, o => o._id, l => l.OrderId, (o, l) => new { o, l })
            .Join(db.Buyers, x => x.o.BuyerId, b => b._id, (x, b) => new { x.o.OrderName, x.l.LineName })
            .ToList();

        // Both joins are inner. O4 has no lines and must contribute nothing; O3's buyer_id dangles so O3/L3
        // must not appear either. This is the shape that reaches the model-derived fallback in
        // TranslateJoinCore with a COLLECTION navigation (Order.Lines) standing in for the first join, so it
        // pins the fallback against being softened to "always preserve for a collection" - which would
        // resurrect O4 with an empty line.
        Assert.Equal(
            ["O1/L1", "O1/L5", "O1/L6", "O2/L2"],
            rows.Select(r => r.OrderName + "/" + r.LineName).OrderBy(x => x).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------
    // EF-369, on REQUIRED navigations so it runs on all three majors.
    //
    // Ef369MultiJoinComposedTests covers the same defect, but its model gives every navigation a nullable
    // foreign key, so EF lowers it to LeftJoin and the whole file is `#if !EF8 && !EF9`. The
    // StripJoinForLookup fix is un-gated, so it needs un-gated coverage: a required navigation lowers to
    // Queryable.Join, which dispatches on every major, and the discarded-operator defect reproduced there.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Required_two_hop_ThenInclude_with_composed_nav_Where()
    {
        using var db = Setup();

        var lines = db.Lines
            .Include(l => l.Order)
            .ThenInclude(o => o.Buyer)
            .Where(l => l.Order.Buyer.BuyerName == "Alice")
            .ToList();

        // Both hops are inner, so L3 (dangling buyer) and L4 (dangling order) are gone regardless; of the
        // survivors L1/L2/L5/L6 only L2's order belongs to Bob.
        Assert.Equal(["L1", "L5", "L6"], LineNames(lines));
    }

    [Fact]
    public void Required_two_reference_Includes_with_composed_nav_Where()
    {
        using var db = Setup();

        var lines = db.Lines
            .Include(l => l.Order)
            .Include(l => l.Product)
            .Where(l => l.Order.OrderName == "O1")
            .ToList();

        // Survivors of the two inner joins are L1/L2/L3/L6; of those only L1 and L6 are on O1.
        Assert.Equal(["L1", "L6"], LineNames(lines));
    }

    // ---------------------------------------------------------------------------------------------------
    // Base-source paging must not be reordered relative to a row-dropping $unwind.
    //
    // The two shapes below differ only in whether anything is composed ABOVE the Includes. The composed
    // one takes the reattach path in StripJoinForLookup, which emits the join $lookup/$unwind stages right
    // after the root source - i.e. potentially BEFORE operators that were written below the joins. An
    // inner $unwind drops rows, so running it ahead of the base source's Take changes which rows the Take
    // sees. Both are asserted so the two shapes can never silently disagree again.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Base_source_paging_is_applied_before_a_required_navigations_inner_unwind()
    {
        using var db = Setup();

        var lines = db.Lines
            .OrderBy(l => l.LineName)
            .Take(4)
            .Include(l => l.Order)
            .Include(l => l.Product)
            .Where(l => l.Product.ProductName == "P1")
            .ToList();

        // Take(4) is below the joins, so the base source is L1..L4. L4's ord_id dangles (dropped by the
        // required Order join) and L2 is on P2, leaving L1 and L3. L6 is outside the Take and appearing
        // would mean the unwinds ran first.
        Assert.Equal(["L1", "L3"], LineNames(lines));
    }

    [Fact]
    public void Base_source_paging_is_applied_before_a_required_navigations_inner_unwind_without_composition()
    {
        using var db = Setup();

        var lines = db.Lines
            .OrderBy(l => l.LineName)
            .Take(4)
            .Include(l => l.Order)
            .Include(l => l.Product)
            .ToList();

        // The control for the case above: nothing is composed above the Includes, so the lookups are
        // tail-appended and the ordering is not in question. Base source L1..L4, less the dangling L4.
        Assert.Equal(["L1", "L2", "L3"], LineNames(lines));
    }

    private static string[] LineNames(IEnumerable<Line> lines)
        => lines.Select(l => l.LineName).OrderBy(n => n).ToArray();

    // BSON element names deliberately differ from the CLR property names so the tests also prove the
    // $lookup/$unwind pipeline goes through EF's element-name mapping.
    //
    // Seed (the dangling foreign keys are the point of this fixture):
    //   Buyers    B1 "Alice", B2 "Bob"
    //   Orders    O1 -> B1, O2 -> B2, O3 -> dangling buyer, O4 -> B1 (no lines)
    //   Products  P1, P2
    //   Carriers  C1
    //   Lines     L1 O1/P1/C1, L2 O2/P2/no carrier, L3 O3/P1/C1,
    //             L4 dangling order/P1/C1, L5 O1/dangling product/C1, L6 O1/P1/dangling carrier
    // EF-368: mode-differential entry point mirroring the CreateContext(MongoQueryMode, ...) idiom used in
    // NativeReferenceIncludeTests.cs. Setup() (mode defaults to Native) is unchanged for every pre-existing
    // test in this file.
    private RequiredNavDbContext CreateContext(MongoQueryMode mode) => Setup(mode);

    private RequiredNavDbContext Setup(MongoQueryMode mode = MongoQueryMode.Native)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var buyersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ReqNavBuyers") + suffix;
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ReqNavOrders") + suffix;
        var productsName = TemporaryDatabaseFixtureBase.CreateCollectionName("ReqNavProducts") + suffix;
        var carriersName = TemporaryDatabaseFixtureBase.CreateCollectionName("ReqNavCarriers") + suffix;
        var linesName = TemporaryDatabaseFixtureBase.CreateCollectionName("ReqNavLines") + suffix;

        var buyer1 = ObjectId.GenerateNewId();
        var buyer2 = ObjectId.GenerateNewId();
        var order1 = ObjectId.GenerateNewId();
        var order2 = ObjectId.GenerateNewId();
        var order3 = ObjectId.GenerateNewId();
        var order4 = ObjectId.GenerateNewId();
        var product1 = ObjectId.GenerateNewId();
        var product2 = ObjectId.GenerateNewId();
        var carrier1 = ObjectId.GenerateNewId();

        // Never inserted anywhere: the dangling targets.
        var missingBuyer = ObjectId.GenerateNewId();
        var missingOrder = ObjectId.GenerateNewId();
        var missingProduct = ObjectId.GenerateNewId();
        var missingCarrier = ObjectId.GenerateNewId();

        database.MongoDatabase.GetCollection<BsonDocument>(buyersName).InsertMany([
            new BsonDocument { { "_id", buyer1 }, { "bname", "Alice" } },
            new BsonDocument { { "_id", buyer2 }, { "bname", "Bob" } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(ordersName).InsertMany([
            new BsonDocument { { "_id", order1 }, { "oname", "O1" }, { "buyer_id", buyer1 } },
            new BsonDocument { { "_id", order2 }, { "oname", "O2" }, { "buyer_id", buyer2 } },
            new BsonDocument { { "_id", order3 }, { "oname", "O3" }, { "buyer_id", missingBuyer } },
            new BsonDocument { { "_id", order4 }, { "oname", "O4" }, { "buyer_id", buyer1 } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(productsName).InsertMany([
            new BsonDocument { { "_id", product1 }, { "pname", "P1" } },
            new BsonDocument { { "_id", product2 }, { "pname", "P2" } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(carriersName).InsertMany([
            new BsonDocument { { "_id", carrier1 }, { "cname", "C1" } }
        ]);

        database.MongoDatabase.GetCollection<BsonDocument>(linesName).InsertMany([
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "lname", "L1" },
                { "ord_id", order1 }, { "prod_id", product1 }, { "carr_id", carrier1 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "lname", "L2" },
                { "ord_id", order2 }, { "prod_id", product2 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "lname", "L3" },
                { "ord_id", order3 }, { "prod_id", product1 }, { "carr_id", carrier1 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "lname", "L4" },
                { "ord_id", missingOrder }, { "prod_id", product1 }, { "carr_id", carrier1 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "lname", "L5" },
                { "ord_id", order1 }, { "prod_id", missingProduct }, { "carr_id", carrier1 }
            },
            new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "lname", "L6" },
                { "ord_id", order1 }, { "prod_id", product1 }, { "carr_id", missingCarrier }
            }
        ]);

        return new RequiredNavDbContext(database, buyersName, ordersName, productsName, carriersName, linesName, mode);
    }

    class Buyer
    {
        public ObjectId _id { get; set; }
        public string BuyerName { get; set; }
        public List<Order> Orders { get; set; }
    }

    class Order
    {
        public ObjectId _id { get; set; }
        public string OrderName { get; set; }
        public ObjectId BuyerId { get; set; }
        public Buyer Buyer { get; set; }
        public List<Line> Lines { get; set; }
    }

    class Product
    {
        public ObjectId _id { get; set; }
        public string ProductName { get; set; }
        public List<Line> Lines { get; set; }
    }

    class Carrier
    {
        public ObjectId _id { get; set; }
        public string CarrierName { get; set; }
        public List<Line> Lines { get; set; }
    }

    class Line
    {
        public ObjectId _id { get; set; }
        public string LineName { get; set; }
        public ObjectId OrderId { get; set; }
        public Order Order { get; set; }
        public ObjectId ProductId { get; set; }
        public Product Product { get; set; }

        // The only OPTIONAL navigation in the model; nullable FK, no IsRequired().
        public ObjectId? CarrierId { get; set; }
        public Carrier Carrier { get; set; }
    }

    class RequiredNavDbContext : DbContext
    {
        private readonly string _buyersCollection;
        private readonly string _ordersCollection;
        private readonly string _productsCollection;
        private readonly string _carriersCollection;
        private readonly string _linesCollection;

        public DbSet<Buyer> Buyers { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Carrier> Carriers { get; set; }
        public DbSet<Line> Lines { get; set; }

        public RequiredNavDbContext(
            TemporaryDatabaseFixture database,
            string buyersCollection,
            string ordersCollection,
            string productsCollection,
            string carriersCollection,
            string linesCollection,
            MongoQueryMode mode = MongoQueryMode.Native)
            : base(new DbContextOptionsBuilder<RequiredNavDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName,
                    b => b.UseQueryMode(mode))
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options)
        {
            _buyersCollection = buyersCollection;
            _ordersCollection = ordersCollection;
            _productsCollection = productsCollection;
            _carriersCollection = carriersCollection;
            _linesCollection = linesCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Buyer>(b =>
            {
                b.ToCollection(_buyersCollection);
                b.Property(x => x.BuyerName).HasElementName("bname");
            });

            modelBuilder.Entity<Product>(b =>
            {
                b.ToCollection(_productsCollection);
                b.Property(x => x.ProductName).HasElementName("pname");
            });

            modelBuilder.Entity<Carrier>(b =>
            {
                b.ToCollection(_carriersCollection);
                b.Property(x => x.CarrierName).HasElementName("cname");
            });

            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
                b.Property(x => x.OrderName).HasElementName("oname");
                b.Property(x => x.BuyerId).HasElementName("buyer_id");
                b.HasOne(x => x.Buyer)
                    .WithMany(x => x.Orders)
                    .HasForeignKey(x => x.BuyerId)
                    .IsRequired();
            });

            modelBuilder.Entity<Line>(b =>
            {
                b.ToCollection(_linesCollection);
                b.Property(x => x.LineName).HasElementName("lname");
                b.Property(x => x.OrderId).HasElementName("ord_id");
                b.Property(x => x.ProductId).HasElementName("prod_id");
                b.Property(x => x.CarrierId).HasElementName("carr_id");
                b.HasOne(x => x.Order)
                    .WithMany(x => x.Lines)
                    .HasForeignKey(x => x.OrderId)
                    .IsRequired();
                b.HasOne(x => x.Product)
                    .WithMany(x => x.Lines)
                    .HasForeignKey(x => x.ProductId)
                    .IsRequired();
                b.HasOne(x => x.Carrier)
                    .WithMany(x => x.Lines)
                    .HasForeignKey(x => x.CarrierId)
                    .IsRequired(false);
            });
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;

            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }
}
