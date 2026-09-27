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
/// EF-322, Phase 2 Group A: proves <see cref="NativeSlotPopulator"/>'s new conditional ORDER BY sort-key arm
/// (a bare nav-null-check ternary reaching a single-level join scope's Inner side, e.g.
/// <c>o =&gt; o.Customer != null ? o.Customer.City : ""</c>) actually resolves against a REAL, EF-generated
/// <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> parameter — not a hand-mocked one. Harness deliberately
/// copied from <c>NativeJoinScopeProjectionBinderTests.TranslateJoinQuery</c> (see that file's own remarks for
/// why a hand-declared "Outer"/"Inner" fixture would exercise nothing real).
/// </summary>
public class JoinScopeOrderBySlotPopulationTests
{
    // Real CLR navigation properties are required, not decorative — TranslateJoinCore's JoinScope eligibility
    // resolves the join's navigation via IEntityType.GetNavigations(), which a convention-only shadow FK does
    // not satisfy. Same reasoning as JoinScopeWhereSlotPopulationTests/NativeJoinScopeProjectionBinderTests.
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
        // `(r, o) => o != null ? o.Name : ""` as an OrderBy key after a LeftJoin — mirrors
        // NativeJoinScopeProjectionBinderTests.Binds_a_bare_nav_null_check_ternary_over_a_left_join's SELECT
        // shape, but as a sort key instead of a projection. Order-outer/Owner-inner (not the "natural" reading
        // order) is DELIBERATE: RebindInnerShaperToOuterQuery resolves a join's Navigation by searching the
        // OUTER entity's own navigation set first for one matching the outer key selector's FK property AND
        // IsOnDependent — i.e. it only ever finds a REFERENCE nav when the OUTER side is the dependent
        // (FK-holding) entity. Owner-outer/Order-inner resolves to Owner.Orders — a COLLECTION nav (tested
        // separately below) — not the reference nav Order.Owner this test targets.
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
        // An earlier Outer-only key (r.Total) recorded in PipelineOps as a MongoSortOp, followed by a
        // ThenBy reaching the Inner side via the new conditional arm, must relocate the WHOLE existing sort
        // into PostJoinOps rather than leaving two separate $sort stages — see
        // DeferTrailingSortPastConfirmedJoin's own remarks for why splitting them is a silent wrong-order bug.
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
        // A plain (inner) Join for a REQUIRED reference nav: "Inner != null" is unconditionally true (a
        // dropped, unmatched row never reaches the sort at all), so it is not a real check — the arm must
        // decline, not silently treat it as one. Mirrors TryBindConditionalProjection's own IsLeftOuter guard,
        // proven independently here since this is a different call site.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            orders.Join(owners, r => r.OwnerId, o => o.Id, (r, o) => new { r, o })
                .OrderBy(x => x.o != null ? x.o.Name : ""));

        // The join itself is a plain required Join (Order.Owner is non-nullable via its FK), so the ternary
        // must decline the conditional arm and fall through to the "not natively representable" catch-all —
        // proven here by the query staying off the native OrderBy path (no MongoSortOp with a conditional
        // key recorded); the whole query's Route ends up Fallback, which NativeOnly-mode functional tests
        // cover end-to-end. This unit test only needs to prove the ARM itself declines for this shape.
        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.False(mongoQ.Select.JoinScope!.Levels[0].IsLeftOuter);
        Assert.Empty(mongoQ.Select.PostJoinOps);
        Assert.True(mongoQ.Select.HasUnsupportedOperator);
    }

    [Fact]
    public void Conditional_sort_key_over_a_collection_navigation_join_declines()
    {
        // Owner-outer/Order-inner resolves to Owner.Orders — a COLLECTION navigation (Lookup.IsReference is
        // false) — not a reference nav. A collection has no single "is it null" answer once flattened by
        // SelectMany(DefaultIfEmpty()), so this is a DIFFERENT degenerate case from the required-Join test
        // above (different guard: Navigation.IsCollection, not IsLeftOuter) and needs its own proof.
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
