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

using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;
using Xunit;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Structural recognition of reference-Include chains (<c>TryGetReferenceIncludeChain</c>) and related recognizers.
/// A double-hop <c>ti.Outer.Outer</c> base is admitted here because a genuine N=2 sibling chain produces it too; the
/// user-join-with-downstream-Include shape is rejected later by <c>TryConfirmReferenceIncludeChain</c>'s
/// <c>Joins.Count != chain.Count</c> check (covered by <c>NativeReferenceIncludeTests</c>).
/// </summary>
public class ReferenceIncludeRecognizerTests
{
    [Fact]
    public void Accepts_double_hop_entity_expression_as_a_length_one_chain()
    {
        // A double hop is a valid chain base; telling it apart from a user join is TryConfirmReferenceIncludeChain's job.
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: true, collectionNavigation: false);

        var chain = MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector);

        Assert.NotNull(chain);
        Assert.Single(chain);
    }

    [Fact]
    public void Accepts_single_hop_entity_expression_from_nav_expansion()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: false);

        var chain = MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector);

        Assert.NotNull(chain);
        Assert.Single(chain);
    }

    [Fact]
    public void Rejects_a_collection_navigation()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Rejects_a_bare_parameter_body()
    {
        var param = Expression.Parameter(typeof(object), "ti");
        var selector = Expression.Lambda(param, param);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Accepts_a_two_level_sibling_chain_with_different_target_types()
    {
        var selector = ReferenceIncludeTestTrees.BuildSiblingChain(sameTarget: false);

        var chain = MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector);

        Assert.NotNull(chain);
        Assert.Equal(2, chain.Count);
    }

    [Fact]
    public void Accepts_a_two_level_sibling_chain_with_the_same_target_type()
    {
        var selector = ReferenceIncludeTestTrees.BuildSiblingChain(sameTarget: true);

        var chain = MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector);

        Assert.NotNull(chain);
        Assert.Equal(2, chain.Count);
    }

    [Fact]
    public void Rejects_a_reference_and_collection_combo_at_any_chain_level()
    {
        // The outer (last-called) level is the collection: Orders.Include(o => o.Buyer).Include(o => o.Lines).
        var selector = ReferenceIncludeTestTrees.BuildSiblingChain(sameTarget: false, outerLevelIsCollection: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Mixed_recognizer_accepts_a_reference_and_collection_combo()
    {
        // Buyer (reference) inner, Lines (collection) outer — the shape the pure-reference recognizer declines.
        var selector = ReferenceIncludeTestTrees.BuildSiblingChain(sameTarget: false, outerLevelIsCollection: true);

        var matched = MongoQueryableMethodTranslatingExpressionVisitor.TryGetMixedReferenceAndCollectionIncludeChain(
            selector, out var referenceLevels, out _, out var collectionLevel);

        Assert.True(matched);
        Assert.Single(referenceLevels);
        Assert.NotNull(collectionLevel);
        Assert.True(((INavigation)collectionLevel.Navigation!).IsCollection);
    }

    [Fact]
    public void Mixed_recognizer_rejects_a_pure_reference_chain()
    {
        var selector = ReferenceIncludeTestTrees.BuildSiblingChain(sameTarget: false);

        var matched = MongoQueryableMethodTranslatingExpressionVisitor.TryGetMixedReferenceAndCollectionIncludeChain(
            selector, out var referenceLevels, out _, out var collectionLevel);

        Assert.False(matched);
        Assert.Empty(referenceLevels);
        Assert.Null(collectionLevel);
    }

    [Fact]
    public void Mixed_recognizer_rejects_a_bare_single_collection_include()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: true);

        var matched = MongoQueryableMethodTranslatingExpressionVisitor.TryGetMixedReferenceAndCollectionIncludeChain(
            selector, out var referenceLevels, out _, out var collectionLevel);

        Assert.False(matched);
        Assert.Empty(referenceLevels);
        Assert.Null(collectionLevel);
    }

    [Fact]
    public void Accepts_a_linear_two_hop_ThenInclude_chain()
    {
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(embeddedHopInBetween: false);

        var chain = MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(
            selector, out var transitiveLevels);

        Assert.NotNull(chain);
        Assert.Equal(2, chain.Count);
        Assert.Single(transitiveLevels);
    }

    [Fact]
    public void Rejects_a_ThenInclude_reached_through_an_embedded_hop()
    {
        // Buyer.Address (owned) -> Region (real): a real navigation reached through an embedded hop stays
        // declined (the driver-LINQ fallback handles it).
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(embeddedHopInBetween: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Accepts_an_embedded_hop_with_nothing_real_nested_past_it()
    {
        // Buyer.Address (owned, auto-included) with nothing nested past it must keep working (walker follows NavigationExpression).
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(
            embeddedHopInBetween: true, stopAtEmbeddedHop: true);

        var chain = MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(
            selector, out var transitiveLevels);

        Assert.NotNull(chain);
        Assert.Single(chain);
        Assert.Empty(transitiveLevels);
    }

    [Fact]
    public void Rejects_a_collection_ThenInclude()
    {
        // A collection ThenInclude is a mixed shape; see
        // Mixed_recognizer_accepts_a_reference_chain_with_a_trailing_collection_ThenInclude.
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(
            embeddedHopInBetween: false, thenIncludeIsCollection: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Mixed_recognizer_accepts_a_reference_chain_with_a_trailing_collection_ThenInclude()
    {
        // Orders.Include(o => o.Customer).ThenInclude(c => c.Orders): the collection is reached transitively via
        // NavigationExpression rather than as a sibling.
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(
            embeddedHopInBetween: false, thenIncludeIsCollection: true);

        var matched = MongoQueryableMethodTranslatingExpressionVisitor.TryGetMixedReferenceAndCollectionIncludeChain(
            selector, out var referenceLevels, out _, out var collectionLevel);

        Assert.True(matched);
        Assert.Single(referenceLevels);
        Assert.NotNull(collectionLevel);
        Assert.True(((INavigation)collectionLevel.Navigation!).IsCollection);
    }

    [Fact]
    public void Mixed_recognizer_rejects_a_further_ThenInclude_past_a_collection_ThenInclude()
    {
        // A collection ThenInclude must be terminal; a further hop past it must decline the whole chain rather than
        // silently drop the hop.
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(
            embeddedHopInBetween: false, thenIncludeIsCollection: true, collectionThenIncludeHasFurtherHop: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));

        var matched = MongoQueryableMethodTranslatingExpressionVisitor.TryGetMixedReferenceAndCollectionIncludeChain(
            selector, out var referenceLevels, out _, out var collectionLevel);

        Assert.False(matched);
        Assert.Empty(referenceLevels);
        Assert.Null(collectionLevel);
    }

    [Fact]
    public void Mixed_recognizer_rejects_a_second_collection_reached_via_two_different_ThenInclude_levels()
    {
        // At most one collection across the chain, including when the second is reached via a different sibling's
        // ThenInclude: Orders.Include(o => o.Customer.Orders).Include(o => o.SecondCustomer.Orders).
        var selector = ReferenceIncludeTestTrees.BuildTwoSiblingReferencesEachWithOwnCollectionThenInclude();

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));

        var matched = MongoQueryableMethodTranslatingExpressionVisitor.TryGetMixedReferenceAndCollectionIncludeChain(
            selector, out var referenceLevels, out _, out var collectionLevel);

        Assert.False(matched);
        Assert.Empty(referenceLevels);
        Assert.Null(collectionLevel);
    }

    // Customers.Include(c => c.Orders).Join(Orders, ...)...Select(c => c) yields ti => Include(ti.Outer, Orders):
    // a pure collection Include over a join scope. None of the other recognizers admit it —
    // IsSingleLevelCollectionIncludeSelector needs a bare parameter, TryGetReferenceIncludeChain declines any
    // collection level, and the mixed recognizer needs a reference level too.
    [Fact]
    public void Collection_include_recognizer_accepts_a_bare_collection_include_over_a_join_scope()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: true);

        var include = MongoQueryableMethodTranslatingExpressionVisitor.TryGetCollectionIncludeOverJoinScope(selector);

        Assert.NotNull(include);
        Assert.True(((INavigation)include.Navigation!).IsCollection);
    }

    [Fact]
    public void Collection_include_recognizer_rejects_a_bare_parameter_collection_include()
    {
        // IsSingleLevelCollectionIncludeSelector's shape (no join) must stay partitioned away.
        var navigation = ReferenceIncludeTestTrees.GetCollectionNavigation();
        var param = Expression.Parameter(navigation.DeclaringEntityType.ClrType, "c");
        var include = new IncludeExpression(param, param, navigation);
        var selector = Expression.Lambda(include, param);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetCollectionIncludeOverJoinScope(selector));
    }

    [Fact]
    public void Collection_include_recognizer_rejects_a_reference_include_over_a_join_scope()
    {
        // A reference (non-collection) Include over the same join-scope shape belongs to
        // TryGetReferenceIncludeChain instead.
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: false);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetCollectionIncludeOverJoinScope(selector));
    }
}

/// <summary>
/// Builds the trees nav-expansion (or a user join) produces ahead of reference/collection Includes. Uses a throwaway
/// <see cref="SingleEntityDbContext"/> model for real <see cref="INavigation"/>s.
/// </summary>
internal static class ReferenceIncludeTestTrees
{
    private class Customer
    {
        public int Id { get; set; }
        public List<Order>? Orders { get; set; }
    }

    private class Vendor
    {
        public int Id { get; set; }
    }

    private class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public int VendorId { get; set; }
        public int OwnerCustomerId { get; set; }
        public Customer? Customer { get; set; }
        public Vendor? Vendor { get; set; }
        public Customer? SecondCustomer { get; set; }
        public List<Order>? RelatedOrders { get; set; }
    }

    // Separate model for BuildThenIncludeChain: a real reference, a collection, and an owned hop wrapping a real
    // navigation off Mid (mirrors Buyer.Address.Region). Kept apart so it can't perturb the model above.
    private class Leaf
    {
        public int Id { get; set; }
        public int? NextLeafId { get; set; }
        public Leaf? Next { get; set; }
    }

    private class OwnedHop
    {
        public int LeafId { get; set; }
        public Leaf? Leaf { get; set; }
    }

    private class Mid
    {
        public int Id { get; set; }
        public int LeafId { get; set; }
        public Leaf? Leaf { get; set; }
        public List<Leaf>? Leaves { get; set; }
        public OwnedHop? Owned { get; set; }
    }

    private class ThenIncludeRoot
    {
        public int Id { get; set; }
        public int MidId { get; set; }
        public Mid? Mid { get; set; }
    }

    // Close enough to EF's TransparentIdentifier<TOuter, TInner> for the recognizers: Outer/Inner and the type name.
    private class TransparentIdentifier<TOuter, TInner>
    {
        public TOuter Outer { get; set; } = default!;
        public TInner Inner { get; set; } = default!;
    }

    private static IModel BuildModel(bool includeSecondCustomerNavigation, bool includeCollectionNavigation)
    {
        using var db = SingleEntityDbContext.Create<Order>(mb =>
        {
            mb.Entity<Customer>();
            mb.Entity<Vendor>();
            mb.Entity<Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
            mb.Entity<Order>().HasOne(o => o.Vendor).WithMany().HasForeignKey(o => o.VendorId);
            if (includeSecondCustomerNavigation)
            {
                mb.Entity<Order>().HasOne(o => o.SecondCustomer).WithMany().HasForeignKey(o => o.CustomerId);
            }

            if (includeCollectionNavigation)
            {
                mb.Entity<Order>().HasMany(o => o.RelatedOrders!).WithOne().HasForeignKey(o => o.CustomerId);
            }
        });

        return db.Model;
    }

    private static INavigation GetNavigation(bool collectionNavigation)
    {
        var model = BuildModel(includeSecondCustomerNavigation: false, includeCollectionNavigation: collectionNavigation);
        var navigationName = collectionNavigation ? nameof(Order.RelatedOrders) : nameof(Order.Customer);
        return model.FindEntityType(typeof(Order))!.FindNavigation(navigationName)!;
    }

    /// <summary>A collection navigation, for an <see cref="IncludeExpression"/> over a bare parameter.</summary>
    public static INavigation GetCollectionNavigation() => GetNavigation(collectionNavigation: true);

    /// <summary>
    /// Builds <c>ti =&gt; Include(ti.Outer, Nav, ti.Inner)</c> (single hop, <paramref name="doubleHop"/> false)
    /// or <c>ti =&gt; Include(ti.Outer.Outer, Nav, ti.Inner)</c> (double hop, <paramref name="doubleHop"/> true).
    /// </summary>
    public static LambdaExpression Build(bool doubleHop, bool collectionNavigation)
    {
        var navigation = GetNavigation(collectionNavigation);

        if (!doubleHop)
        {
            var tiType = typeof(TransparentIdentifier<Order, Customer>);
            var param = Expression.Parameter(tiType, "ti");
            var outerAccess = Expression.MakeMemberAccess(param, tiType.GetProperty("Outer")!);
            var innerAccess = Expression.MakeMemberAccess(param, tiType.GetProperty("Inner")!);
            var include = new IncludeExpression(outerAccess, innerAccess, navigation);
            return Expression.Lambda(include, param);
        }

        // ti : TransparentIdentifier<TransparentIdentifier<Order, object>, Customer>, so ti.Outer.Outer is Order,
        // as in a user-authored join.
        var innerTiType = typeof(TransparentIdentifier<Order, object>);
        var outerTiType = typeof(TransparentIdentifier<,>).MakeGenericType(innerTiType, typeof(Customer));
        var outerParam = Expression.Parameter(outerTiType, "ti");
        var outerOuterAccess = Expression.MakeMemberAccess(outerParam, outerTiType.GetProperty("Outer")!);
        var doubleOuterAccess = Expression.MakeMemberAccess(outerOuterAccess, innerTiType.GetProperty("Outer")!);
        var outerInnerAccess = Expression.MakeMemberAccess(outerParam, outerTiType.GetProperty("Inner")!);
        var doubleHopInclude = new IncludeExpression(doubleOuterAccess, outerInnerAccess, navigation);
        return Expression.Lambda(doubleHopInclude, outerParam);
    }

    /// <summary>
    /// Builds the two-sibling shape <c>ti2 =&gt; Include(Include(ti2.Outer.Outer, NavA, ti2.Outer.Inner), NavB, ti2.Inner)</c>
    /// (e.g. <c>Lines.Include(l =&gt; l.Order).Include(l =&gt; l.Product)</c>). <paramref name="sameTarget"/> makes both
    /// navigations target <see cref="Customer"/>; <paramref name="outerLevelIsCollection"/> makes NavB the collection
    /// <c>RelatedOrders</c> (the reference + collection combo).
    /// </summary>
    public static LambdaExpression BuildSiblingChain(bool sameTarget, bool outerLevelIsCollection = false)
    {
        var model = BuildModel(includeSecondCustomerNavigation: sameTarget, includeCollectionNavigation: outerLevelIsCollection);
        var orderEntityType = model.FindEntityType(typeof(Order))!;
        var navigationA = orderEntityType.FindNavigation(nameof(Order.Customer))!;
        var navigationB = outerLevelIsCollection
            ? orderEntityType.FindNavigation(nameof(Order.RelatedOrders))!
            : orderEntityType.FindNavigation(sameTarget ? nameof(Order.SecondCustomer) : nameof(Order.Vendor))!;

        // ti1 : TransparentIdentifier<Order, TargetA>, ti2 : TransparentIdentifier<ti1, TargetB>
        var targetAType = typeof(Customer);
        var targetBType = outerLevelIsCollection ? typeof(List<Order>) : sameTarget ? typeof(Customer) : typeof(Vendor);
        var ti1Type = typeof(TransparentIdentifier<,>).MakeGenericType(typeof(Order), targetAType);
        var ti2Type = typeof(TransparentIdentifier<,>).MakeGenericType(ti1Type, targetBType);

        var ti2Param = Expression.Parameter(ti2Type, "ti2");
        var ti2Outer = Expression.MakeMemberAccess(ti2Param, ti2Type.GetProperty("Outer")!); // ti1
        var ti2Inner = Expression.MakeMemberAccess(ti2Param, ti2Type.GetProperty("Inner")!); // TargetB

        var ti1OuterViaTi2 = Expression.MakeMemberAccess(ti2Outer, ti1Type.GetProperty("Outer")!); // Order (ti2.Outer.Outer)
        var ti1InnerViaTi2 = Expression.MakeMemberAccess(ti2Outer, ti1Type.GetProperty("Inner")!); // TargetA (ti2.Outer.Inner)

        var innerInclude = new IncludeExpression(ti1OuterViaTi2, ti1InnerViaTi2, navigationA);
        var outerInclude = new IncludeExpression(innerInclude, ti2Inner, navigationB);

        return Expression.Lambda(outerInclude, ti2Param);
    }

    /// <summary>
    /// Two sibling reference levels targeting <see cref="Customer"/>, each with its own collection <c>ThenInclude</c>
    /// of <see cref="Customer.Orders"/>: <c>Orders.Include(o =&gt; o.Customer.Orders).Include(o =&gt; o.SecondCustomer.Orders)</c>.
    /// </summary>
    public static LambdaExpression BuildTwoSiblingReferencesEachWithOwnCollectionThenInclude()
    {
        using var db = SingleEntityDbContext.Create<Order>(mb =>
        {
            mb.Entity<Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
            mb.Entity<Order>().HasOne(o => o.Vendor).WithMany().HasForeignKey(o => o.VendorId);
            mb.Entity<Order>().HasOne(o => o.SecondCustomer).WithMany().HasForeignKey(o => o.CustomerId);
            mb.Entity<Customer>().HasMany(c => c.Orders!).WithOne().HasForeignKey(o => o.OwnerCustomerId);
        });
        var model = db.Model;

        var orderEntityType = model.FindEntityType(typeof(Order))!;
        var customerEntityType = model.FindEntityType(typeof(Customer))!;
        var navigationA = orderEntityType.FindNavigation(nameof(Order.Customer))!;
        var navigationB = orderEntityType.FindNavigation(nameof(Order.SecondCustomer))!;
        var customerOrdersNavigation = customerEntityType.FindNavigation(nameof(Customer.Orders))!;

        var ti1Type = typeof(TransparentIdentifier<Order, Customer>);
        var ti2Type = typeof(TransparentIdentifier<,>).MakeGenericType(ti1Type, typeof(Customer));

        var ti2Param = Expression.Parameter(ti2Type, "ti2");
        var ti2Outer = Expression.MakeMemberAccess(ti2Param, ti2Type.GetProperty("Outer")!); // ti1
        var ti2Inner = Expression.MakeMemberAccess(ti2Param, ti2Type.GetProperty("Inner")!); // Customer (via SecondCustomer)

        var ti1OuterViaTi2 = Expression.MakeMemberAccess(ti2Outer, ti1Type.GetProperty("Outer")!); // Order
        var ti1InnerViaTi2 = Expression.MakeMemberAccess(ti2Outer, ti1Type.GetProperty("Inner")!); // Customer (via Customer)

        var innerThenInclude = new IncludeExpression(ti1InnerViaTi2, ti1InnerViaTi2, customerOrdersNavigation);
        var innerInclude = new IncludeExpression(ti1OuterViaTi2, innerThenInclude, navigationA);

        var outerThenInclude = new IncludeExpression(ti2Inner, ti2Inner, customerOrdersNavigation);
        var outerInclude = new IncludeExpression(innerInclude, outerThenInclude, navigationB);

        return Expression.Lambda(outerInclude, ti2Param);
    }

    /// <summary>
    /// Builds <c>ti =&gt; Include(ti.Outer, Root.Mid, NavigationExpression)</c>, with a <c>ThenInclude</c> chain nested
    /// via <c>NavigationExpression</c> (a sibling nests via <c>EntityExpression</c>):
    /// <list type="bullet">
    /// <item><description>Default: <c>Include(Mid).ThenInclude(Leaf)</c>, or <c>ThenInclude(Leaves)</c> when
    /// <paramref name="thenIncludeIsCollection"/>.</description></item>
    /// <item><description><paramref name="embeddedHopInBetween"/>: <c>Mid.Owned</c> wrapping a real nav to <c>Leaf</c>,
    /// or just <c>Mid.Owned</c> when <paramref name="stopAtEmbeddedHop"/>.</description></item>
    /// </list>
    /// </summary>
    public static LambdaExpression BuildThenIncludeChain(
        bool embeddedHopInBetween, bool thenIncludeIsCollection = false, bool stopAtEmbeddedHop = false,
        bool collectionThenIncludeHasFurtherHop = false)
    {
        using var db = SingleEntityDbContext.Create<ThenIncludeRoot>(mb =>
        {
            mb.Entity<Leaf>().HasOne(l => l.Next).WithMany().HasForeignKey(l => l.NextLeafId);
            mb.Entity<Mid>().HasOne(m => m.Leaf).WithMany().HasForeignKey(m => m.LeafId);
            mb.Entity<Mid>().HasMany(m => m.Leaves!).WithOne().HasForeignKey("MidId");
            mb.Entity<Mid>().OwnsOne(m => m.Owned, o => o.HasOne(x => x.Leaf).WithMany().HasForeignKey(x => x.LeafId));
            mb.Entity<ThenIncludeRoot>().HasOne(r => r.Mid).WithMany().HasForeignKey(r => r.MidId);
        });

        var midEntityType = db.Model.FindEntityType(typeof(Mid))!;
        var midNavigation = db.Model.FindEntityType(typeof(ThenIncludeRoot))!.FindNavigation(nameof(ThenIncludeRoot.Mid))!;

        var tiType = typeof(TransparentIdentifier<ThenIncludeRoot, Mid>);
        var param = Expression.Parameter(tiType, "ti");
        var outerAccess = Expression.MakeMemberAccess(param, tiType.GetProperty("Outer")!);
        var innerAccess = Expression.MakeMemberAccess(param, tiType.GetProperty("Inner")!);

        Expression navigationExpression = innerAccess;
        if (embeddedHopInBetween)
        {
            var ownedNavigation = midEntityType.FindNavigation(nameof(Mid.Owned))!;
            Expression ownedNavigationExpression = innerAccess;
            if (!stopAtEmbeddedHop)
            {
                var leafViaOwnedNavigation = ownedNavigation.TargetEntityType.FindNavigation(nameof(OwnedHop.Leaf))!;
                ownedNavigationExpression = new IncludeExpression(innerAccess, innerAccess, leafViaOwnedNavigation);
            }

            navigationExpression = new IncludeExpression(innerAccess, ownedNavigationExpression, ownedNavigation);
        }
        else
        {
            var leafOrLeavesNavigation = midEntityType.FindNavigation(
                thenIncludeIsCollection ? nameof(Mid.Leaves) : nameof(Mid.Leaf))!;

            Expression leafNavigationExpression = innerAccess;
            if (collectionThenIncludeHasFurtherHop)
            {
                var nextNavigation = leafOrLeavesNavigation.TargetEntityType.FindNavigation(nameof(Leaf.Next))!;
                leafNavigationExpression = new IncludeExpression(innerAccess, innerAccess, nextNavigation);
            }

            navigationExpression = new IncludeExpression(innerAccess, leafNavigationExpression, leafOrLeavesNavigation);
        }

        var rootInclude = new IncludeExpression(outerAccess, navigationExpression, midNavigation);
        return Expression.Lambda(rootInclude, param);
    }
}
