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
/// EF-370: the <c>$unwind</c> after a cross-collection <c>$lookup</c> must follow the join semantics the
/// query actually asked for — EF lowers a REQUIRED reference navigation to an inner
/// <see cref="Queryable.Join{TOuter,TInner,TKey,TResult}"/> and an OPTIONAL one to <c>LeftJoin</c>. This is
/// the only fixture in the suite that seeds a <b>dangling foreign key</b> (a value matching no document,
/// which MongoDB's lack of referential integrity permits); without one, inner and left-outer joins return
/// identical rows. Assertions are on row counts/identities, never MQL, since wrong MQL here is
/// indistinguishable from a legitimately different query.
/// </summary>
[XUnitCollection("QueryTests")]
public class RequiredNavigationUnwindTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    // ---------------------------------------------------------------------------------------------------
    // Required navigations: INNER join semantics. Run on ALL THREE EF majors, deliberately: a required
    // navigation lowers to Queryable.Join, which dispatches on every version (unlike LeftJoin, gated
    // `#if !EF8 && !EF9`), so the defect reproduced there too — reading EF-X020 as "cross-collection
    // reference Include doesn't work before EF10" is wrong.
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

    // The same seed, now asserted ACROSS QUERY MODES: the native pipeline and the driver-LINQ fallback must
    // return the same rows over a dangling foreign key, not merely both "work". This is the assertion that
    // would have caught EF-370's row-count divergence.
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

        // Under the native pipeline a single-level collection Include takes MongoSelectLowerer's
        // IsNativeCollectionLookup arm: a flat $lookup with NO $unwind at all, the array materialized
        // directly by the shaper. LookupExpression.PreserveNullAndEmptyArrays only governs an $unwind, so it
        // is never consulted here — this is flat-array-materialization coverage, not unwind-semantics
        // coverage, and it passes regardless of that flag. The reference-navigation tests above are what
        // pin the EF-370 fix.
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
    // Optional navigations: LEFT-OUTER semantics, EF10 only — EF lowers these to Queryable.LeftJoin, whose
    // dispatch case is `#if !EF8 && !EF9`, so on EF8/EF9 EF Core itself throws "could not be translated"
    // (EF-X020), asserted below rather than left implicit.
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

        // Only the Buyer half exercises the unwind-semantics fix: a required reference Include emits
        // $lookup + a non-preserving $unwind, which is why O3 is dropped below. The Lines half is a flat
        // native $lookup with no $unwind (see the note on
        // Collection_Include_still_preserves_principals_with_no_children), so the O4 assertion is
        // materialization coverage rather than unwind coverage.
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

    [Fact]
    public void User_authored_Join_over_a_nullable_foreign_key_stays_inner_after_flattening()
    {
        using var db = Setup();

        var rows = db.Lines
            .Join(db.Carriers, l => l.CarrierId, c => (ObjectId?)c._id, (l, c) => new { l, c })
            .Join(db.Products, x => x.l.ProductId, p => p._id, (x, p) => new { x.l.LineName, x.c.CarrierName, p.ProductName })
            .ToList();

        // The first Join is over Line.CarrierId, the model's only navigation with a NULLABLE FK and no
        // IsRequired() — yet a user Join is unambiguously inner regardless of FK optionality. L2 (no
        // carrier) and L6 (dangling carrier) are excluded by the first join alone. The second join then
        // forces the first to be retroactively flattened; if that flattening fell back to inferring
        // left/inner from ForeignKey.IsRequired (false here), it would wrongly preserve L2/L6 with a null
        // Carrier, and both go on to match a real product in the second join, producing spurious rows.
        Assert.Equal(
            ["L1/C1/P1", "L3/C1/P1", "L4/C1/P1"],
            rows.Select(r => r.LineName + "/" + r.CarrierName + "/" + r.ProductName).OrderBy(x => x).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------
    // EF-369, on REQUIRED navigations so it runs on all three majors (Ef369MultiJoinComposedTests uses
    // nullable FKs throughout, so it's gated `#if !EF8 && !EF9`; the StripJoinForLookup fix itself is not).
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
    // Base-source paging must not be reordered relative to a row-dropping $unwind: composing something
    // above the Includes takes the reattach path in StripJoinForLookup, which emits the $lookup/$unwind
    // stages right after the root source - i.e. potentially before a Take written below the joins.
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
