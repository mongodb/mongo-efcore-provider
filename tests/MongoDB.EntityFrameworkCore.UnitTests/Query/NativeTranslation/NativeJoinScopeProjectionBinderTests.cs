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
/// <c>NativeJoinScopeProjectionBinder</c>, driven through the real EF Core translation pipeline (preprocessor +
/// QMTEV, no database).
/// </summary>
/// <remarks>
/// A real <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> exposes <c>Outer</c>/<c>Inner</c> as fields and is
/// only produced by nav-expansion; a hand-built fixture with "Outer"/"Inner" properties passes while leaving the
/// production guard dead. Same harness as <see cref="JoinScopeWhereSlotPopulationTests"/>.
/// </remarks>
public class NativeJoinScopeProjectionBinderTests
{
    // Real CLR navigations are required: JoinScope eligibility resolves the join's navigation via
    // IEntityType.GetNavigations(), which a shadow FK doesn't satisfy.
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
    public void Binds_a_bare_nav_null_check_ternary_over_a_left_join()
    {
        // `(r, o) => o != null ? o.Name : ""` after a LeftJoin (nav-expands to `ti.Inner != null ? ...`).
        //
        // Order-outer/Owner-inner on purpose: RebindInnerShaperToOuterQuery finds a reference nav only when the
        // outer side holds the FK. Owner-outer would resolve to the Owner.Orders collection nav, which the
        // degenerate-check guard declines (a collection has no single "is null" answer).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            orders.GroupJoin(owners, r => r.OwnerId, o => o.Id, (r, os) => new { r, os })
                .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.r, o })
                .Select(x => x.o != null ? x.o.Name : ""));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.True(mongoQ.Select.JoinScope!.Levels[0].IsLeftOuter);
        Assert.Equal(
            [NativeProjectionBinder.SyntheticBareProjectionAlias],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var leaf = Assert.IsType<MongoConditionalExpression>(mongoQ.Select.Projection[0].Expression);
        var test = Assert.IsType<MongoLookupNullCheckExpression>(leaf.Test);
        Assert.True(test.IsNotNull);
        Assert.Equal(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix, test.LookupAlias);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
    }

    [Fact]
    public void Declines_when_the_checked_level_is_an_inner_not_left_outer_join()
    {
        // A plain Join drops unmatched rows rather than unwinding them as null, so the null check is degenerate —
        // must decline, not admit an always-true test.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => x.r != null ? x.r.Total : 0m));

        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_conditional_whose_test_is_not_a_scope_null_check()
    {
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.GroupJoin(orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o, rs })
                .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, r })
                .Select(x => x.o.Name == "Alice" ? 1m : 0m));

        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    // Separate fixture types for the chained test: adding a List<OrderLine> nav to the shared Order (while the
    // two-source model never registers OrderLine) makes conventions treat it as owned, so nav-expansion
    // auto-includes it into every whole-Order access and breaks the depth-1 whole-entity-leaf tests above.
    private class ChainOwner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<ChainOrder> Orders { get; set; } = [];
    }

    private class ChainOrder
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public ChainOwner? Owner { get; set; }
        public decimal Total { get; set; }
        public List<ChainOrderLine> Lines { get; set; } = [];
    }

    private class ChainOrderLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public ChainOrder? Order { get; set; }
        public string Sku { get; set; } = "";
    }

    /// <summary>
    /// Three-source variant of <see cref="TranslateJoinQuery"/> over the <c>Chain*</c> fixture types.
    /// </summary>
    private static MongoQueryExpression TranslateThreeSourceJoinQuery(
        Func<IQueryable<ChainOwner>, IQueryable<ChainOrder>, IQueryable<ChainOrderLine>, IQueryable> buildQuery)
    {
        using var db = SingleEntityDbContext.Create<ChainOwner>(mb =>
        {
            mb.Entity<ChainOrder>();
            mb.Entity<ChainOrderLine>();
        });

        var query = buildQuery(db.Set<ChainOwner>(), db.Set<ChainOrder>(), db.Set<ChainOrderLine>());

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
    public void Binds_a_scalar_only_wrapped_projection_from_both_sides()
    {
        // Nav-expansion normalizes this into a TransparentIdentifier join plus a trailing
        // Select(x => new { x.Outer.Name, x.Inner.Total }) — the shape this binder owns.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);

        // Aliases are the anonymous type's member names, which the shaper derives too, so $project and the
        // alias-addressed read agree.
        Assert.Equal(["Name", "Total"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // The Inner leaf resolves through the join's $lookup alias; the Outer leaf reads the root document.
        var innerLeaf = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.StartsWith(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix + ".", innerLeaf.ElementName);
        // MongoOuterFieldExpression — see NativeJoinScopeTranslatorTests.Translates_outer_side_member_access_unprefixed.
        var outerLeaf = Assert.IsType<MongoOuterFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.DoesNotContain(".", outerLeaf.ElementName);

        // The deferred $lookup was registered and the candidate join confirmed, so the query routes natively.
        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_whole_inner_entity_leaf_mixed_with_a_scalar()
    {
        // `new { o.Name, r }` — whole Inner mixed with a scalar Outer. The Inner leaf stages under the fixed,
        // self-referential alias scope.InnerPrefix, not "r" — see NativeJoinScopeProjectionBinder's "Alias space".
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var innerPrefix = mongoQ.Select.JoinScope!.Levels[0].InnerPrefix;

        // The emitted alias is the join's $lookup prefix, not the member name "r".
        Assert.Equal(["Name", innerPrefix], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // MongoOuterFieldExpression — see NativeJoinScopeTranslatorTests.Translates_outer_side_member_access_unprefixed.
        var outerLeaf = Assert.IsType<MongoOuterFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.DoesNotContain(".", outerLeaf.ElementName);

        // Self-referential: both the alias and the MongoElementRefExpression's own path are innerPrefix.
        var innerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(innerPrefix, innerLeaf.Path);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_whole_outer_entity_leaf_that_is_also_include_wrapped()
    {
        // `new { o, r.Total }` where `o` carries `.Include(o => o.Orders)`: nav-expansion hands the leaf over as an
        // IncludeExpression wrapping ti.Outer, which the recognizer must unwrap to see a scope-0 whole-entity leaf.
        // A ThenInclude back to Order.Owner isn't used: EF throws NavigationBaseIncludeIgnored for it. The unwrap
        // is a loop, so depth is covered structurally (and by the chain test below).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Include(o => o.Orders)
                .Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(["o", "Total"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var outerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, outerLeaf.Path);

        // The scalar Inner sibling leaf is unaffected by the Include on the Outer leaf.
        var innerLeaf = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.StartsWith(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix + ".", innerLeaf.ElementName);

        // Two lookups: the join's (_lookup_Orders, force-unwound) and the Include's (_lookup_Orders_include).
        // Collapsing them into one would leave the Include's collection shaper unable to read an array.
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_duplicated_whole_inner_entity_leaf_that_is_also_reference_include_wrapped()
    {
        // `new { a = r, b = r }` where `r` carries `.Include(r => r.Owner)` — Include on the Inner side plus a
        // duplicated leaf. Nav-expansion lowers the Include to a further join, which TryResolveReferenceIncludeLevel
        // stages beside the deduplicated Inner leaf; otherwise Order.Owner would be silently null. End to end:
        // NativeJoinTests.Reference_included_Inner_leaf_of_a_join_scope_materializes_correctly.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders.Include(r => r.Owner), o => o.Id, r => r.OwnerId, (o, r) => new { a = r, b = r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var levels = mongoQ.Select.JoinScope!.Levels;
        Assert.Equal(2, levels.Count);

        // The duplicated Inner leaf and the Include's target level each stage their fixed alias once, as
        // self-referential element refs.
        Assert.Equal(
            [levels[0].InnerPrefix, levels[1].InnerPrefix],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.All(mongoQ.Select.Projection, p =>
            Assert.Equal(p.Alias, Assert.IsType<MongoElementRefExpression>(p.Expression).Path));

        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_both_whole_entity_leaves_with_no_scalars()
    {
        // `new { o, r }` — the shape of the spec suite's Applied_to_projection/GroupJoin_projection/
        // Select_Navigations. Both whole-entity leaves go native.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var innerPrefix = mongoQ.Select.JoinScope!.Levels[0].InnerPrefix;

        Assert.Equal(["o", innerPrefix], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var outerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, outerLeaf.Path);

        var innerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(innerPrefix, innerLeaf.Path);
        // Not the member's own alias "r".
        Assert.NotEqual("r", innerLeaf.Path);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_duplicated_inner_leaf_without_crashing()
    {
        // `new { a = r, b = r }`: both members want the same fixed alias, which would crash MongoPipelineFactory
        // with "Duplicate element name" in Native/NativeOnly. The dedup guard must stage it once, and only when
        // the existing entry really is a previous Inner leaf. The DriverLinq leg is pinned in NativeJoinTests.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { a = r, b = r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var innerPrefix = mongoQ.Select.JoinScope!.Levels[0].InnerPrefix;

        // Exactly one staged entry for the fixed alias.
        var projection = Assert.Single(mongoQ.Select.Projection);
        Assert.Equal(innerPrefix, projection.Alias);
        var innerLeaf = Assert.IsType<MongoElementRefExpression>(projection.Expression);
        Assert.Equal(innerPrefix, innerLeaf.Path);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_duplicated_outer_leaf_with_a_dead_projection_field()
    {
        // `new { a = o, b = o }` — no guard needed: each Outer leaf stages under its own alias (both reading
        // $$ROOT), and AddToProjection dedups the bind side to one index, so both materialize correctly.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { a = o, b = o }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(["a", "b"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        foreach (var projection in mongoQ.Select.Projection)
        {
            var leaf = Assert.IsType<MongoElementRefExpression>(projection.Expression);
            Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, leaf.Path);
        }

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_when_a_sibling_leaf_reached_through_the_whole_entity_reference_is_untranslatable()
    {
        // `new { o, Foo = o.Name.ToUpper() }`: the whole Outer leaf is native but the ToUpper sibling isn't.
        // One untranslatable sibling must decline the whole projection — no partial commit.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, Foo = o.Name.ToUpper() }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_computed_leaf_beside_a_whole_entity_leaf()
    {
        // `new { o, X = r.Total * 2 }`: the arithmetic leaf translates fine but is stopped by the whole-entity
        // sibling readability guard before the commit block — a MongoBinaryExpression has no document path for
        // the whole-document fallback legs the `o` leaf forces. Other decline tests here fail earlier, in
        // TryTranslateValue. End-to-end read correctness:
        // NativeJoinTests.Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, X = r.Total * 2 }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);

        // Control: the same computed leaf without the whole-entity sibling binds, so the decline above is due to
        // the guard, not to the arithmetic being untranslatable.
        var computedOnly = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, X = r.Total * 2 }));

        Assert.Equal(["Name", "X"], computedOnly.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.Equal(NativeRoute.Projection, computedOnly.Select.Route);
    }

    [Fact]
    public void Binds_a_whole_outer_entity_leaf_mixed_with_a_scalar()
    {
        // `new { o, r.Total }` — whole Outer mixed with a scalar Inner; the Outer leaf stages a $$ROOT reference.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(["o", "Total"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var outerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, outerLeaf.Path);

        var innerLeaf = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.StartsWith(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix + ".", innerLeaf.ElementName);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_shape_outside_this_chunks_scope()
    {
        // A method call over a join-scope member is untranslatable; it declines the whole projection, including
        // the sibling that would have translated.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { Upper = o.Name.ToUpper(), r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    /// <summary>
    /// A DTO whose member name equals the <c>$lookup</c> alias registered for <c>Owner.Orders</c>
    /// (<c>LookupExpression.LookupAliasPrefix</c> + nav name). Contrived, but the only way to reach the collision,
    /// whose failure mode is silent.
    /// </summary>
    private class CollidingAliasDto
    {
        public string Name { get; set; } = "";

        // ReSharper disable once InconsistentNaming
        public decimal _lookup_Orders { get; set; }
    }

    [Fact]
    public void Declines_a_leaf_whose_alias_collides_with_the_joins_own_lookup_alias()
    {
        // AddToProjection uniquifies aliases by appending a counter, and the join already holds "_lookup_<Nav>".
        // Unguarded, the shaper would read "_lookup_Orders0" while $project wrote "_lookup_Orders" — a silently
        // dropped value. Both leaves translate, so only the alias-collision check can decline this.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId,
                (o, r) => new CollidingAliasDto { Name = o.Name, _lookup_Orders = r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_bare_whole_inner_entity_select_without_populating_a_projection()
    {
        // `select r` (the sibling arm in TranslateSelect) carries no projection — the whole-entity route reads the
        // $lookup's unwound alias — so the signal is lookup registered + candidate confirmed + Route == WholeEntity.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r }).Select(x => x.r));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.WholeEntity, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_second_chained_join_reusing_the_first_joins_target_type_at_the_correct_alias()
    {
        // Both joins target the same entity type in the same positions, so a translator resolving by CLR type
        // would misresolve `r2.Total` against the first join's InnerPrefix — silently wrong data. Resolution by
        // the actual member-name hop chain (MongoTransparentScopeResolver.ScopeRerootingVisitor) must re-root
        // `r2.Total` to scope 2 (Levels[1]) and `o.Name` to scope 0.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => x.o)
                .Join(orders, o => o.Id, r2 => r2.OwnerId, (o, r2) => new { o.Name, r2.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;
        Assert.Equal(2, scope.Levels.Count);
        Assert.Equal(2, mongoQ.Joins.Count);

        // Both lookups are registered: the first by the intermediate Select's whole-entity confirm arm, the second
        // by TranslateJoinCore's multi-join flattening once Joins.Count > 1, regardless of the trailing projection.
        Assert.Equal(2, mongoQ.Lookups.Count);

        Assert.Equal(["Name", "Total"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // Name (root, scope 0): plain MongoFieldExpression, unprefixed.
        var name = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal("Name", name.ElementName);

        // Total (scope 2) must use Levels[1].InnerPrefix — Levels[0]'s would mean it misresolved to the first join.
        Assert.NotEqual(scope.Levels[0].InnerPrefix, scope.Levels[1].InnerPrefix);
        var total = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix + ".Total", total.ElementName);

        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_two_level_chain_projection_naming_every_scope_as_a_whole_entity()
    {
        // A two-join chain whose trailing selector names every scope as a whole entity: root `cr` (0), the first
        // join's Inner `or` (1), the second join's Inner `od` (2). Mirrors
        // NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { cr = e.o, or = e.r, od = l }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;
        Assert.Equal(2, scope.Levels.Count);
        Assert.Equal(2, mongoQ.Joins.Count);

        // "cr" uses its own alias; "or"/"od" use their level's fixed InnerPrefix alias, like the depth-1 tests.
        Assert.Equal(["cr", scope.Levels[0].InnerPrefix, scope.Levels[1].InnerPrefix],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.Equal(3, mongoQ.Select.Projection.Count);

        // cr (root) reads $$ROOT under its own alias.
        var rootLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, rootLeaf.Path);

        // or/od each read their own level's fixed InnerPrefix alias, not the member's alias.
        var level1Leaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(scope.Levels[0].InnerPrefix, level1Leaf.Path);
        Assert.NotEqual("or", level1Leaf.Path);

        var level2Leaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[2].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix, level2Leaf.Path);
        Assert.NotEqual("od", level2Leaf.Path);
        Assert.NotEqual(level1Leaf.Path, level2Leaf.Path);

        // Every level's $lookup is registered and every candidate join confirmed once, so the chain is native.
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_two_level_chain_root_whole_entity_leaf_that_is_also_include_wrapped()
    {
        // As Binds_a_two_level_chain_projection_naming_every_scope_as_a_whole_entity, but the root leaf also has
        // `.Include(o => o.Orders)` — the unwrap must work inside a Levels.Count > 1 scope.
        // Including the last level (`l.Order`) instead is avoided: ChainOrder coincidentally matches Levels[0]'s
        // InnerEntityType, so the Include gets folded in as a third chain level (the CLR-type-match gap
        // NativeJoinScopeTranslator's remarks describe).
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Include(o => o.Orders)
                .Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { cr = e.o, or = e.r, od = l }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;
        Assert.Equal(2, scope.Levels.Count);

        Assert.Equal(["cr", scope.Levels[0].InnerPrefix, scope.Levels[1].InnerPrefix],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var level2Leaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[2].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix, level2Leaf.Path);

        // Three lookups: the root Include on `o.Orders` registers its own (renamed) lookup, separate from the
        // first join's lookup over the same navigation.
        Assert.Equal(3, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_two_level_chain_projection_with_scalar_leaves_at_every_scope()
    {
        // Every leaf is a scalar rooted at exactly one scope: e.o.Name (0), e.r.Total (1), l.Sku (2). Mirrors
        // NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties without the
        // Skip/Take.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new
                {
                    OwnerName = e.o.Name,
                    OrderTotal = e.r.Total,
                    LineSku = l.Sku
                }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;
        Assert.Equal(2, scope.Levels.Count);

        Assert.Equal(["OwnerName", "OrderTotal", "LineSku"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // OwnerName (root, scope 0): plain MongoFieldExpression, unprefixed.
        var ownerName = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal("Name", ownerName.ElementName);

        // OrderTotal (join #1's Inner, scope 1): prefixed with Levels[0].InnerPrefix.
        var orderTotal = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(scope.Levels[0].InnerPrefix + ".Total", orderTotal.ElementName);

        // LineSku (join #2's Inner, scope 2): prefixed with Levels[1].InnerPrefix.
        var lineSku = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[2].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix + ".Sku", lineSku.ElementName);

        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_chain_scalar_leaf_mixed_with_a_whole_entity_leaf()
    {
        // A whole-entity root leaf alongside a chain-scalar leaf (scope 2): the sibling-readability guard accepts
        // the plain MongoFieldExpression sibling.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { cr = e.o, or = e.r, LineSku = l.Sku }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;

        Assert.Equal(["cr", scope.Levels[0].InnerPrefix, "LineSku"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var lineSku = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[2].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix + ".Sku", lineSku.ElementName);

        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_chain_scalar_leaf_that_spans_two_scopes()
    {
        // `Mixed = e.r.Total + l.Id` spans scopes 1 and 2 in one leaf; must decline the whole projection, not
        // partially commit the other leaf.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o.Name, Mixed = e.r.Total + l.Id }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);

        // Not empty: TranslateJoinCore registers both lookups once Joins.Count > 1, before this binder runs.
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_nested_wrapped_leaf_over_a_two_level_chain()
    {
        // A nested wrapped leaf over a two-level chain is out of scope (gated to Levels.Count == 1). The scalar
        // arm must exclude nested-projection-shaped leaves before TryTranslateSingleScope, or the NewExpression is
        // admitted as a MongoDocumentConstructionExpression. Must decline the whole projection.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o.Name, Nested = new { Value = l.Sku } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_bare_nav_null_check_ternary_over_a_two_level_chain_whose_second_level_is_not_left_outer()
    {
        // The ternary's test resolves (via TryMatchScopeNullCheck) to the second join's Inner, but that join is a
        // plain Join, so "Inner != null" is always true and TryBindConditionalProjection's `!level.IsLeftOuter`
        // guard declines. Not a depth restriction — see
        // Binds_a_bare_nav_null_check_ternary_over_a_two_level_chain for the LeftJoin positive case.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e, l })
                .Select(x => x.l != null ? x.l.Sku : ""));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    // Self-contained fixture for the LeftJoin-at-level-2 test: the second level's outer side must hold the FK
    // (so RebindInnerShaperToOuterQuery resolves a reference nav). Grafting that onto ChainOrder breaks the chain
    // tests above, whose model never registers the new target, so conventions treat it as owned.
    private class RegionChainOwner
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<RegionChainOrder> Orders { get; set; } = [];
    }

    private class RegionChainOrder
    {
        public int Id { get; set; }
        public int OwnerId { get; set; }
        public RegionChainOwner? Owner { get; set; }
        public int? RegionId { get; set; }
        public RegionChainRegion? Region { get; set; }
    }

    private class RegionChainRegion
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Binds_a_bare_nav_null_check_ternary_over_a_two_level_chain()
    {
        // Two-level chain (Owner->Order plain Join; Order->Region LeftJoin, Order holding the FK) with a bare
        // nav-null-check ternary on the second level's Inner — the depth-2 counterpart of
        // Binds_a_bare_nav_null_check_ternary_over_a_left_join. Binds natively; TryBindConditionalProjection has no
        // depth restriction.
        using var db = SingleEntityDbContext.Create<RegionChainOwner>(mb =>
        {
            mb.Entity<RegionChainOrder>();
            mb.Entity<RegionChainRegion>();
        });

        var query = db.Set<RegionChainOwner>().Join(db.Set<RegionChainOrder>(), o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
            .GroupJoin(db.Set<RegionChainRegion>(), e => e.r.RegionId, g => g.Id, (e, gs) => new { e.o, e.r, gs })
            .SelectMany(x => x.gs.DefaultIfEmpty(), (x, g) => new { x.o, x.r, g })
            .Select(x => x.g != null ? x.g.Name : "none");

        var ccFactory = db.GetService<IQueryCompilationContextFactory>();
        var compilationContext = ccFactory.Create(async: false);

        var preprocessor = db.GetService<IQueryTranslationPreprocessorFactory>().Create(compilationContext);
        var preprocessed = preprocessor.Process(query.Expression);

        var visitor = db.GetService<IQueryableMethodTranslatingExpressionVisitorFactory>().Create(compilationContext);
        var result = visitor.Visit(preprocessed);

        Assert.NotNull(result);
        var shaped = Assert.IsAssignableFrom<ShapedQueryExpression>(result);
        var mongoQ = Assert.IsType<MongoQueryExpression>(shaped.QueryExpression);

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);
        Assert.Single(mongoQ.Select.Projection);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Bare_scalar_leaf_over_a_left_join_goes_native()
    {
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.GroupJoin(orders, o => o.Id, r => r.OwnerId, (o, rs) => new { o, rs })
                .SelectMany(x => x.rs.DefaultIfEmpty(), (x, r) => new { x.o, r })
                .Select(x => x.r.Total));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(
            [NativeProjectionBinder.SyntheticBareProjectionAlias],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
    }

    [Fact]
    public void Bare_scalar_leaf_matching_the_real_nav_expanded_shape_goes_native()
    {
        // The shape EF's null-check removal produces for `o.Owner != null ? o.Owner.Name : null` after
        // nav-expansion: a plain LeftJoin + bare `x.Inner.Name`.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            orders.GroupJoin(owners, r => r.OwnerId, o => o.Id, (r, os) => new { r, os })
                .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.r, o })
                .Select(x => x.o.Name));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(
            [NativeProjectionBinder.SyntheticBareProjectionAlias],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());
    }
}
