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
/// EF-392 (sibling reference Includes): <c>TryGetReferenceIncludeChain</c> replaced
/// <c>IsSingleLevelReferenceIncludeSelector</c> to recognize N&gt;=1 nested reference Includes, not just one.
/// The single-hop-vs-double-hop distinction from the original recognizer is preserved but reinterpreted: a
/// double-hop <c>ti.Outer.Outer</c> base is now ADMITTED by this method alone (a pure <c>.Outer</c>* chain of
/// any length is accepted), because that hop depth is ALSO exactly what a genuine N=2 sibling chain produces.
/// What still rejects the user-join-with-downstream-Include shape is
/// <c>TryConfirmReferenceIncludeChain</c>'s <c>Joins.Count != chain.Count</c> check (a different method,
/// exercised by the functional differential tests in <c>NativeReferenceIncludeTests.cs</c>, not here) — this
/// file only tests the STRUCTURAL recognition step in isolation.
/// </summary>
public class ReferenceIncludeRecognizerTests
{
    [Fact]
    public void Accepts_double_hop_entity_expression_as_a_length_one_chain()
    {
        // No longer rejected by TryGetReferenceIncludeChain itself — a double hop is a valid chain base
        // (it's what a genuine N=2 sibling chain's innermost level also produces). Disambiguating this from
        // a user join with a downstream Include is TryConfirmReferenceIncludeChain's job (Joins.Count check),
        // not this method's.
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
        // The OUTER level (last .Include() called) carries the collection navigation — mirrors
        // Orders.Include(o => o.Buyer).Include(o => o.Lines) (Buyer inner, Lines outer).
        var selector = ReferenceIncludeTestTrees.BuildSiblingChain(sameTarget: false, outerLevelIsCollection: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Mixed_recognizer_accepts_a_reference_and_collection_combo()
    {
        // EF-392 (reference + collection combo): Orders.Include(o => o.Buyer).Include(o => o.Lines) —
        // Buyer (reference) inner, Lines (collection) outer, the exact shape TryGetReferenceIncludeChain
        // above declines (by design — a pure reference-only recognizer).
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
        // Buyer.Address(owned).Region(real) — EF-407's shape. Deliberately kept declining, unchanged
        // scope: a real navigation reached THROUGH an embedded hop is a different, harder combo than a
        // real navigation reached DIRECTLY off a reference Include's target, and is not part of this
        // slice (it already works correctly via the driver-LINQ fallback, per EF-407's investigation).
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(embeddedHopInBetween: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }

    [Fact]
    public void Accepts_an_embedded_hop_with_nothing_real_nested_past_it()
    {
        // The already-shipped EF-368 shape: Buyer.Address (owned, auto-included), no further ThenInclude
        // at all. Must keep working unchanged now that the walker also follows NavigationExpression.
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
        var selector = ReferenceIncludeTestTrees.BuildThenIncludeChain(
            embeddedHopInBetween: false, thenIncludeIsCollection: true);

        Assert.Null(MongoQueryableMethodTranslatingExpressionVisitor.TryGetReferenceIncludeChain(selector));
    }
}

/// <summary>
/// Builds the tree shapes EF's nav-expansion (or a user-authored join) produces ahead of a single-level
/// reference/collection Include, or a chain of sibling reference Includes. Needs real <see cref="INavigation"/>s,
/// so it stands up a throwaway model — follows the same <see cref="SingleEntityDbContext"/> +
/// <c>HasOne</c>/<c>HasMany</c> pattern <c>MongoPipelineFactoryTests.ReferenceNavigation</c>/<c>ChildrenNavigation</c>
/// already use in this directory, rather than inventing a second model-construction approach.
/// </summary>
internal static class ReferenceIncludeTestTrees
{
    private class Customer
    {
        public int Id { get; set; }
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
        public Customer? Customer { get; set; }
        public Vendor? Vendor { get; set; }
        public Customer? SecondCustomer { get; set; }
        public List<Order>? RelatedOrders { get; set; }
    }

    // For BuildThenIncludeChain: an entirely separate model — a real (non-embedded) reference navigation
    // off Mid, a collection navigation off Mid, and an embedded (owned) hop on Mid wrapping a further real
    // navigation to Leaf — mirrors Buyer.Address(owned).Region(real) from EF-407. Kept independent of
    // Order/Customer/Vendor above so it can't perturb the model those other builders already rely on.
    private class Leaf
    {
        public int Id { get; set; }
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

    // Mirrors the shape of EF's own internal TransparentIdentifier<TOuter, TInner> closely enough for the
    // recognizer's structural checks: an Outer/Inner pair, and a type name starting with "TransparentIdentifier".
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

        // Double hop: ti : TransparentIdentifier<TransparentIdentifier<Order, object>, Customer>
        // so ti.Outer.Outer resolves to Order, exactly as a user-authored join's synthesized shape does.
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
    /// Builds the two-sibling nav-expansion shape:
    /// <c>ti2 =&gt; Include(Include(ti2.Outer.Outer, NavA, ti2.Outer.Inner), NavB, ti2.Inner)</c> — the
    /// nested-<see cref="IncludeExpression"/>-via-<c>EntityExpression</c> tree
    /// <c>Docs.Include(d =&gt; d.Author).Include(d =&gt; d.Editor)</c>/
    /// <c>Lines.Include(l =&gt; l.Order).Include(l =&gt; l.Product)</c> compile to. When
    /// <paramref name="sameTarget"/>, both navigations target <see cref="Customer"/> (mirrors
    /// <c>Doc.Author</c>/<c>Doc.Editor</c> both targeting <c>Buyer</c>); otherwise NavA targets
    /// <see cref="Customer"/> and NavB targets <see cref="Vendor"/>. When
    /// <paramref name="outerLevelIsCollection"/>, NavB (the outer/last-called level) is the collection
    /// navigation <c>RelatedOrders</c> instead — mirrors the "reference + collection" combo
    /// (<c>Orders.Include(o =&gt; o.Buyer).Include(o =&gt; o.Lines)</c>), which must still be rejected.
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
    /// Builds <c>ti =&gt; Include(ti.Outer, Root.Mid, NavigationExpression)</c> — a single top-level
    /// <c>Include</c> whose <c>NavigationExpression</c> carries a further nested chain, mirroring
    /// nav-expansion's <c>ThenInclude</c> shape (nested via <c>NavigationExpression</c>, not
    /// <c>EntityExpression</c> like a sibling). Three shapes, selected by the flags:
    /// <list type="bullet">
    /// <item><description>Default (<paramref name="embeddedHopInBetween"/> false): a plain linear 2-hop
    /// chain, <c>Include(Mid).ThenInclude(Leaf)</c> — or, when <paramref name="thenIncludeIsCollection"/>,
    /// <c>Include(Mid).ThenInclude(Leaves)</c> (a collection <c>ThenInclude</c>, which must decline).</description></item>
    /// <item><description><paramref name="embeddedHopInBetween"/> true, <paramref name="stopAtEmbeddedHop"/>
    /// false: <c>Mid.Owned</c> (embedded) wrapping a further REAL nav to <c>Leaf</c> — EF-407's shape,
    /// which must decline.</description></item>
    /// <item><description><paramref name="embeddedHopInBetween"/> true, <paramref name="stopAtEmbeddedHop"/>
    /// true: just <c>Mid.Owned</c> (embedded), nothing real nested past it — the already-shipped EF-368
    /// shape, which must keep working.</description></item>
    /// </list>
    /// </summary>
    public static LambdaExpression BuildThenIncludeChain(
        bool embeddedHopInBetween, bool thenIncludeIsCollection = false, bool stopAtEmbeddedHop = false)
    {
        using var db = SingleEntityDbContext.Create<ThenIncludeRoot>(mb =>
        {
            mb.Entity<Leaf>();
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
            navigationExpression = new IncludeExpression(innerAccess, innerAccess, leafOrLeavesNavigation);
        }

        var rootInclude = new IncludeExpression(outerAccess, navigationExpression, midNavigation);
        return Expression.Lambda(rootInclude, param);
    }
}
