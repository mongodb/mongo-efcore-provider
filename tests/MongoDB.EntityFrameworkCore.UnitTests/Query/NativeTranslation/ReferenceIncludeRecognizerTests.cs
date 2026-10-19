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
/// The recognizer's single-hop conjunct is LOAD-BEARING, not defence-in-depth: a user-authored join with a
/// downstream Include DOES produce a trailing IncludeExpression, differing from EF's nav-expansion shape only
/// by a double hop (ti.Outer.Outer vs ti.Outer). A predicate matching merely Member.Name == "Outer" admits it,
/// because the OUTERMOST hop of ti.Outer.Outer is also named "Outer". See design §5.1.
/// </summary>
public class ReferenceIncludeRecognizerTests
{
    [Fact]
    public void Rejects_double_hop_entity_expression_from_a_user_join()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: true, collectionNavigation: false);

        Assert.False(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    [Fact]
    public void Accepts_single_hop_entity_expression_from_nav_expansion()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: false);

        Assert.True(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    [Fact]
    public void Rejects_a_collection_navigation()
    {
        var selector = ReferenceIncludeTestTrees.Build(doubleHop: false, collectionNavigation: true);

        Assert.False(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }

    [Fact]
    public void Rejects_a_bare_parameter_body()
    {
        var param = Expression.Parameter(typeof(object), "ti");
        var selector = Expression.Lambda(param, param);

        Assert.False(
            MongoQueryableMethodTranslatingExpressionVisitor.IsSingleLevelReferenceIncludeSelector(selector));
    }
}

/// <summary>
/// Builds the tree shape EF's nav-expansion (or a user-authored join) produces ahead of a single-level
/// reference/collection Include: a TransparentIdentifier-typed parameter, an IncludeExpression whose
/// EntityExpression is either <c>ti.Outer</c> (the genuine nav-expansion shape) or <c>ti.Outer.Outer</c> (the
/// double-hop shape a user-authored join produces — see the design note this file's tests are guarding).
/// Needs a real <see cref="INavigation"/>, so it stands up a throwaway model — follow the same
/// <see cref="SingleEntityDbContext"/> + <c>HasOne</c>/<c>HasMany</c> pattern
/// <c>MongoPipelineFactoryTests.ReferenceNavigation</c>/<c>ChildrenNavigation</c> already use in this directory,
/// rather than inventing a second model-construction approach.
/// </summary>
internal static class ReferenceIncludeTestTrees
{
    private class Customer
    {
        public int Id { get; set; }
    }

    private class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public Customer? Customer { get; set; }
        public List<Order>? RelatedOrders { get; set; }
    }

    // Mirrors the shape of EF's own internal TransparentIdentifier<TOuter, TInner> closely enough for the
    // recognizer's structural checks: an Outer/Inner pair, and a type name starting with "TransparentIdentifier".
    private class TransparentIdentifier<TOuter, TInner>
    {
        public TOuter Outer { get; set; } = default!;
        public TInner Inner { get; set; } = default!;
    }

    private static INavigation GetNavigation(bool collectionNavigation)
    {
        using var db = SingleEntityDbContext.Create<Order>(mb =>
        {
            mb.Entity<Customer>();
            if (collectionNavigation)
            {
                mb.Entity<Order>().HasMany(o => o.RelatedOrders!).WithOne().HasForeignKey(o => o.CustomerId);
            }
            else
            {
                mb.Entity<Order>().HasOne(o => o.Customer).WithMany().HasForeignKey(o => o.CustomerId);
            }
        });

        var navigationName = collectionNavigation ? nameof(Order.RelatedOrders) : nameof(Order.Customer);
        return db.Model.FindEntityType(typeof(Order))!.FindNavigation(navigationName)!;
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
}
