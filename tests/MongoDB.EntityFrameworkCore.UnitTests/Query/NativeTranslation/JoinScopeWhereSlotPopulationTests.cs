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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Drives real <c>.Join(...)</c> queries through the QMTEV pipeline and asserts on the populated
/// <see cref="MongoSelectDefinition"/> directly, proving the native slot arms resolve join-scope keys against a
/// real EF-generated <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c>. End-to-end tests can't distinguish a
/// Where-arm decline from a Select-side decline, since both raise the same exception under NativeOnly.
/// </summary>
public class JoinScopeWhereSlotPopulationTests
{
    // The navigation properties are required: TranslateJoinCore's eligibility check finds navigations via
    // IEntityType.GetNavigations(), so a shadow FK with no CLR navigation would never record a JoinScope.
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
        public List<OrderLine> Lines { get; set; } = [];
    }

    // Third source for the chained-join tests; needs a real navigation property for the same reason.
    private class OrderLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order? Order { get; set; }
        public string Sku { get; set; } = "";
    }

    /// <summary>
    /// Drives a two-source join through preprocessing and the QMTEV (unlike
    /// <see cref="SlotPopulationTests.TranslateToMongoQuery{T}"/>, which skips preprocessing). Preprocessing is
    /// required: nav-expansion rewrites the result selector's anonymous type (<c>o</c>/<c>r</c>) into EF's
    /// <c>TransparentIdentifier</c> (<c>Outer</c>/<c>Inner</c>), the shape the slot arms recognize. Nothing executes.
    /// </summary>
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

    /// <summary>
    /// Three-source variant of <see cref="TranslateJoinQuery"/>.
    /// </summary>
    private static MongoQueryExpression TranslateThreeSourceJoinQuery(
        Func<IQueryable<Owner>, IQueryable<Order>, IQueryable<OrderLine>, IQueryable> buildQuery)
    {
        using var db = SingleEntityDbContext.Create<Owner>(mb =>
        {
            mb.Entity<Order>();
            mb.Entity<OrderLine>();
        });

        var query = buildQuery(db.Set<Owner>(), db.Set<Order>(), db.Set<OrderLine>());

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

    // Fixture for the embedded-outer-key-selector test: EmbeddedAddress needs its own navigation (LinkedTarget)
    // so RebindInnerShaperToOuterQuery, which resolves the navigation on the embedded type, finds one; Owner/Order
    // would take an unrelated decline path.
    private class RootWithEmbeddedKey
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public EmbeddedAddress? Address { get; set; }
    }

    private class EmbeddedAddress
    {
        public int LinkedTargetId { get; set; }
        public JoinTarget? LinkedTarget { get; set; }
    }

    private class JoinTarget
    {
        public int Id { get; set; }
        public string Label { get; set; } = "";
    }

    /// <summary>
    /// A depth-1 join whose outer key reaches through an embedded navigation (<c>o.Address.LinkedTargetId</c>)
    /// is eligible via <c>JoinLookupImplementsKeySelectors</c>' <c>EndsWith</c> branch:
    /// <c>lookup.LocalField</c> ("Address.LinkedTargetId") never equals the bare element name.
    /// </summary>
    /// <remarks>
    /// This is sound: <c>Navigation.DeclaringEntityType</c> is <c>EmbeddedAddress</c>, reached by walking the same
    /// "Address" segment that prefixed <c>LocalField</c>, so both sides derive from the same fact. The dotted
    /// <c>localField</c> is how Mongo addresses the nested key, and <c>MongoJoinScope</c> records nothing about how
    /// the outer key was reached.
    /// </remarks>
    [Fact]
    public void Depth_one_join_through_owned_navigation_key_selector_is_natively_eligible()
    {
        using var db = SingleEntityDbContext.Create<RootWithEmbeddedKey>(mb =>
        {
            mb.Entity<JoinTarget>();
            mb.Entity<RootWithEmbeddedKey>().OwnsOne(o => o.Address, ab =>
            {
                ab.HasOne(a => a.LinkedTarget).WithMany().HasForeignKey(a => a.LinkedTargetId);
            });
        });

        var query = db.Set<RootWithEmbeddedKey>()
            .Join(db.Set<JoinTarget>(), o => o.Address!.LinkedTargetId, t => t.Id, (o, t) => new { o, t })
            .Where(x => x.o.Name == "Alice");

        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);
        var preprocessed = preprocessor.Process(query.Expression);

        var visitor = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>().Create(compilationContext);
        var result = visitor.Visit(preprocessed);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        var mongoQ = Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);

        var joinInfo = Assert.Single(mongoQ.Joins);
        Assert.True(joinInfo.IsNativelyEligible);
        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Single(mongoQ.Select.JoinScope!.Levels);
    }

    [Fact]
    public void Two_eligible_chained_joins_build_a_two_level_scope_metadata_only()
    {
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
                .Where(x => x.o.Name == "Alice"));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);

        // Nav-expansion applies the result selector `new { e.o, e.r, l }` as a trailing Select of whole-entity
        // leaves across every scope, which NativeJoinScopeProjectionBinder confirms.
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);

        // The root-scope-only Where still populates a native $match (TryTranslateRootScopeOnly).
        var matchOp = Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(matchOp.Predicate);
    }

    [Fact]
    public void Where_reading_outer_scope_after_join_populates_predicate_natively()
    {
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.o.Name == "Alice"));

        // A MongoMatchOp on PipelineOps (rather than MarkNotNativelyRepresentable) proves the Where arm resolved
        // against the real, field-based TransparentIdentifier<Owner,Order>; a Type.GetProperty-only guard would
        // silently decline every real join.
        Assert.NotNull(mongoQ.Select.JoinScope);
        var matchOp = Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(matchOp.Predicate);

        // Route isn't asserted: EF appends a trailing Select of `new { o, r }` (two whole-entity leaves) that
        // routes this shape to Fallback regardless of the Where arm. PipelineOps is the signal — empty when the
        // Where arm declines, populated only when AddPredicateConjunct ran.
    }

    /// <summary>
    /// Depth-2 chain (<c>Owner.Join(Order).Join(OrderLine)</c>): the Where arm's chained branch
    /// (<see cref="NativeJoinScopeTranslator.TryTranslateRootScopeOnly"/>) resolves a root-scope predicate against
    /// the nested <c>TransparentIdentifier&lt;TransparentIdentifier&lt;Owner,Order&gt;,OrderLine&gt;</c>.
    /// </summary>
    [Fact]
    public void Where_reading_root_scope_after_chained_join_populates_predicate_natively()
    {
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
                .Where(x => x.o.Name == "Alice"));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);

        var matchOp = Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(matchOp.Predicate);
    }

    /// <summary>
    /// Same chain, for the <c>OrderBy</c> arm: a root-scope key populates a <see cref="MongoSortOp"/> on
    /// <c>PipelineOps</c>.
    /// </summary>
    [Fact]
    public void OrderBy_reading_root_scope_after_chained_join_populates_sort_natively()
    {
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
                .OrderBy(x => x.o.Name));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);

        var sortOp = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PipelineOps));
        var ordering = Assert.Single(sortOp.Orderings);
        Assert.True(ordering.Ascending);
    }

    /// <summary>
    /// Same chain, for the <c>ThenBy</c> arm's chained branch: appends to the existing sort rather than declining
    /// or overwriting it.
    /// </summary>
    [Fact]
    public void ThenBy_reading_root_scope_after_chained_join_populates_sort_natively()
    {
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o, e.r, l })
                .OrderBy(x => x.o.Name)
                .ThenBy(x => x.o.Id));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);

        var sortOp = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.Equal(2, sortOp.Orderings.Count);
        Assert.True(sortOp.Orderings[0].Ascending);
        Assert.True(sortOp.Orderings[1].Ascending);
    }

    [Fact]
    public void Where_reading_inner_scope_after_join_still_declines_gracefully()
    {
        // The Where arm is Outer-only (ReferencesInnerScope gate): $match ops are lowered before the $lookup that
        // materializes Inner, so an Inner predicate must mark the query non-native.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total > 0));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.PipelineOps);
        // Fallback for the trailing-Select reason too, so this alone proves nothing; empty PipelineOps does.
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    /// <summary>
    /// Unlike the Where arm, a depth-1 <c>OrderBy</c> key on the Inner side (<c>x.r.Total</c>) translates and
    /// defers into <see cref="MongoSelectDefinition.PostJoinOps"/>. The Owner/Order navigation is a collection
    /// (the <c>Join_Customers_Orders_Skip_Take</c> shape), so this does not require
    /// <see cref="LookupExpression.IsReference"/>.
    /// </summary>
    [Fact]
    public void OrderBy_reading_inner_scope_after_join_populates_sort_in_post_join_ops()
    {
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .OrderBy(x => x.r.Total));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Single(mongoQ.Select.JoinScope!.Levels);
        Assert.True(mongoQ.Select.JoinInnerAccessConfirmed);

        Assert.Empty(mongoQ.Select.PipelineOps);
        var sortOp = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PostJoinOps));
        var ordering = Assert.Single(sortOp.Orderings);
        Assert.True(ordering.Ascending);
    }

    /// <summary>
    /// An Outer <c>OrderBy</c> followed by an Inner <c>ThenBy</c> must stay in one <c>$sort</c>; splitting across
    /// <c>PipelineOps</c> and <c>PostJoinOps</c> would silently make the Inner key primary. See
    /// <see cref="MongoSelectDefinition.DeferTrailingSortPastConfirmedJoin"/>.
    /// </summary>
    [Fact]
    public void ThenBy_reading_inner_scope_after_outer_OrderBy_relocates_whole_sort_into_post_join_ops()
    {
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .OrderBy(x => x.o.Name)
                .ThenBy(x => x.r.Total));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.True(mongoQ.Select.JoinInnerAccessConfirmed);

        // The Outer key moved with the Inner key into one sort op.
        Assert.Empty(mongoQ.Select.PipelineOps);
        var sortOp = Assert.IsType<MongoSortOp>(Assert.Single(mongoQ.Select.PostJoinOps));
        Assert.Equal(2, sortOp.Orderings.Count);
        Assert.True(sortOp.Orderings[0].Ascending);
        Assert.True(sortOp.Orderings[1].Ascending);
    }
}
