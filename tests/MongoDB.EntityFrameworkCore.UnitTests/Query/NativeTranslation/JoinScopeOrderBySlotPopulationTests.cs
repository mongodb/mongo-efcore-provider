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
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// <see cref="NativeSlotPopulator"/>'s conditional sort-key arm (a nav-null-check ternary over a single-level
/// join scope's Inner side, e.g. <c>o =&gt; o.Customer != null ? o.Customer.City : ""</c>), exercised against a
/// real EF-generated <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c>. Harness copied from
/// <c>NativeJoinScopeProjectionBinderTests.TranslateJoinQuery</c>.
/// </summary>
public class JoinScopeOrderBySlotPopulationTests
{
    // Real CLR navigations are required: JoinScope eligibility resolves the join's navigation via
    // IEntityType.GetNavigations(), which a shadow FK alone doesn't satisfy.
    private class Owner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<Order> Orders { get; set; } = [];
    }

    private class Order
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public Owner? Owner { get; set; }
        public decimal Total { get; set; }
    }

    private static MongoQueryExpression TranslateJoinQuery(
        Func<IQueryable<Owner>, IQueryable<Order>, IQueryable> buildQuery)
    {
        using var db = SingleEntityDbContext.Create<Owner>(mb => mb.Entity<Order>());

        var query = buildQuery(db.Set<Owner>(), db.Set<Order>());

        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);
        var preprocessed = preprocessor.Process(query.Expression);

        var visitor = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>().Create(compilationContext);
        var result = visitor.Visit(preprocessed);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        return Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);
    }

    [Fact]
    public void Conditional_sort_key_over_an_optional_reference_left_join_goes_native()
    {
        // Order must be the outer side: RebindInnerShaperToOuterQuery finds a reference navigation only when
        // the outer entity is the dependent. Owner-outer resolves to the collection Owner.Orders (tested below).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            orders.GroupJoin(owners, r => r.OwnerId, o => o.Id, (r, os) => new { r, os })
                .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.r, o })
                .OrderBy(x => x.o != null ? x.o.Name : ""));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.True(mongoQ.Select.JoinScope!.Levels[0].IsLeftOuter);

        var sortOp = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PostJoinOps));
        var ordering = Assert.Single(sortOp.Orderings);
        Assert.True(ordering.Ascending);

        var leaf = Assert.IsType<MongoConditionalExpression>(ordering.KeySelector);
        var test = Assert.IsType<MongoLookupNullCheckExpression>(leaf.Test);
        Assert.True(test.IsNotNull);
        Assert.Equal(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix, test.LookupAlias);

        Assert.True(mongoQ.Select.JoinInnerAccessConfirmed);
        Assert.Empty(mongoQ.Select.PipelineOps);
    }

    [Fact]
    public void Outer_key_then_conditional_inner_key_land_in_one_sort_stage()
    {
        // The earlier outer-only key must move into PostJoinOps with the inner ThenBy; two separate $sort
        // stages would silently mis-order (see DeferTrailingSortPastConfirmedJoin).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            orders.GroupJoin(owners, r => r.OwnerId, o => o.Id, (r, os) => new { r, os })
                .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.r, o })
                .OrderBy(x => x.r.Total)
                .ThenBy(x => x.o != null ? x.o.Name : ""));

        Assert.Empty(mongoQ.Select.PipelineOps);
        var sortOp = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PostJoinOps));
        Assert.Equal(2, sortOp.Orderings.Count);
        Assert.IsType<MongoConditionalExpression>(sortOp.Orderings[1].KeySelector);
    }

    [Fact]
    public void Conditional_sort_key_over_a_required_reference_join_declines()
    {
        // After an inner Join, "Inner != null" is always true, so it isn't a real null check; the arm must
        // decline (same IsLeftOuter guard as TryBindConditionalProjection, at a different call site).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            orders.Join(owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
                .OrderBy(x => x.o != null ? x.o.Name : ""));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.False(mongoQ.Select.JoinScope!.Levels[0].IsLeftOuter);
        Assert.Empty(mongoQ.Select.PostJoinOps);
        Assert.True(mongoQ.Select.HasUnsupportedOperator);
    }

    [Fact]
    public void Conditional_sort_key_over_a_collection_navigation_join_declines()
    {
        // Owner-outer resolves to the collection Owner.Orders, which has no single "is it null" answer once
        // flattened; declines via the Navigation.IsCollection guard, not IsLeftOuter.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.GroupJoin(orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o, rs })
                .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, r })
                .OrderBy(x => x.r != null ? x.r.Total : 0m));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.True(mongoQ.Select.JoinScope!.Levels[0].IsLeftOuter);
        Assert.Empty(mongoQ.Select.PostJoinOps);
        Assert.True(mongoQ.Select.HasUnsupportedOperator);
    }
}
