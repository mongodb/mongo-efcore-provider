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
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// EF-392 Task 5, shape 2: <c>NativeJoinScopeProjectionBinder</c>, driven through the REAL EF Core
/// translation pipeline rather than a hand-built fixture.
/// </summary>
/// <remarks>
/// <para>
/// The harness (<see cref="TranslateJoinQuery"/>) is deliberately the one
/// <see cref="JoinScopeWhereSlotPopulationTests"/> established for Task 4, for the reason recorded there and
/// paid for twice on this plan: a real EF-generated <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> exposes
/// <c>Outer</c>/<c>Inner</c> as public FIELDS and is only produced by nav-expansion during PREPROCESSING. A
/// hand-declared fixture type with auto-properties named "Outer"/"Inner" looks right, compiles, and makes
/// every assertion here pass while the production guard it is supposed to exercise is dead code. Running the
/// real preprocessor + QMTEV means the shapes under test are the shapes production sees, by construction.
/// No database is touched — the pipeline is driven exactly through native slot/projection population and
/// then stopped.
/// </para>
/// </remarks>
public class NativeJoinScopeProjectionBinderTests
{
    // Real CLR navigation properties are required, not decorative — TranslateJoinCore's JoinScope eligibility
    // resolves the join's navigation via IEntityType.GetNavigations(), which a convention-only shadow FK does
    // not satisfy. Same reasoning (and same fixture) as JoinScopeWhereSlotPopulationTests.
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

    // Deliberately SEPARATE fixture types for the chained (Task 6) test below, rather than adding a "Lines"
    // navigation onto the Order class above and sharing it: MEASURED — adding a List<OrderLine> navigation to
    // the SAME Order class the depth-1 tests above share, while TranslateJoinQuery's own two-source model only
    // ever registers Order (never OrderLine) via mb.Entity<Order>(), makes the MongoDB provider's conventions
    // treat that unregistered-target collection navigation as OWNED (embedded) rather than a cross-collection
    // reference — which then makes EF's nav-expansion auto-include it into ANY whole-Order-entity access,
    // wrapping what used to be a bare `MemberExpression` leaf in an `IncludeExpression` and breaking EVERY
    // depth-1 whole-entity-leaf test above (`Binds_a_whole_inner_entity_leaf_mixed_with_a_scalar`,
    // `Binds_both_whole_entity_leaves_with_no_scalars`, `Binds_a_duplicated_inner_leaf_without_crashing`) even
    // though none of them ever reference the new nav — confirmed by reverting only this file's source changes
    // and observing the exact same four failures purely from the fixture edit. Fully separate types for the
    // chain test side-step this entirely, since ChainOrder never coexists with an unregistered-OrderLine model.
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
    /// Three-source variant of <see cref="TranslateJoinQuery"/>, for the chained (depth-2) projection-binder
    /// test below — same pipeline/rationale, just with a second <c>Join</c> source added, over the dedicated
    /// <c>Chain*</c> fixture types (see their own remarks for why they're separate from Owner/Order above).
    /// Mirrors <c>JoinScopeWhereSlotPopulationTests.TranslateThreeSourceJoinQuery</c>.
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
        // `(o, r) => new { o.Name, r.Total }` is normalized by nav-expansion into a TransparentIdentifier
        // join plus a trailing Select(x => new { x.Outer.Name, x.Inner.Total }) — the shape this binder owns.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);

        // Aliases are the anonymous type's own member names — the same names the shaper side derives from the
        // same members, which is what makes the emitted $project and the alias-addressed read agree.
        Assert.Equal(["Name", "Total"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // The Inner leaf resolves through the join's $lookup alias; the Outer leaf reads the root document.
        var innerLeaf = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.StartsWith(mongoQ.Select.JoinScope!.Levels[0].InnerPrefix + ".", innerLeaf.ElementName);
        // MongoOuterFieldExpression, not MongoFieldExpression — see NativeJoinScopeTranslatorTests'
        // Translates_outer_side_member_access_unprefixed for why (same TranslateOperand call site).
        var outerLeaf = Assert.IsType<MongoOuterFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.DoesNotContain(".", outerLeaf.ElementName);

        // The join's $lookup was registered (deferred until this Select confirmed the shape) and the
        // candidate join was confirmed, so the query routes natively rather than to the driver-LINQ fallback.
        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_whole_inner_entity_leaf_mixed_with_a_scalar()
    {
        // `new { o.Name, r }` — Inner captured WHOLE, mixed with a scalar Outer leaf. EF-444 Task 2: the Inner
        // leaf now stages too, but under a FIXED, self-referential alias (scope.InnerPrefix) rather than the
        // member's own alias ("r") — see NativeJoinScopeProjectionBinder's "Alias space" remarks for why.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var innerPrefix = mongoQ.Select.JoinScope!.Levels[0].InnerPrefix;

        // The emitted alias is the join's own $lookup prefix, NOT the member name "r".
        Assert.Equal(["Name", innerPrefix], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // MongoOuterFieldExpression, not MongoFieldExpression — see NativeJoinScopeTranslatorTests'
        // Translates_outer_side_member_access_unprefixed for why (same TranslateOperand call site).
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
    public void Binds_both_whole_entity_leaves_with_no_scalars()
    {
        // `new { o, r }` — mirrors the exact shape SpecificationTests' Applied_to_projection/
        // GroupJoin_projection/Select_Navigations exercise. Both sides are whole-entity leaves; both now go
        // native (EF-444 Tasks 1+2).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var innerPrefix = mongoQ.Select.JoinScope!.Levels[0].InnerPrefix;

        Assert.Equal(["o", innerPrefix], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        var outerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, outerLeaf.Path);

        var innerLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(innerPrefix, innerLeaf.Path);
        // Explicitly NOT the member's own alias "r" — the single most important correction this task makes.
        Assert.NotEqual("r", innerLeaf.Path);

        Assert.Single(mongoQ.Lookups);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_duplicated_inner_leaf_without_crashing()
    {
        // `new { a = r, b = r }` — the real hazard the Task 0 spike found: both members want the SAME fixed
        // self-referential alias (scope.InnerPrefix), which would otherwise crash MongoPipelineFactory with
        // "Duplicate element name" at pipeline-build time under MongoQueryMode.Native/NativeOnly (an explicit
        // DriverLinq builds no native pipeline at all, so it cannot crash there — that leg's own correctness is
        // pinned by the NativeJoinTests theory of the same name). The dedup guard must stage the alias exactly
        // once, and must do so only when the entry already holding it really is a previous Inner leaf.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { a = r, b = r }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var innerPrefix = mongoQ.Select.JoinScope!.Levels[0].InnerPrefix;

        // Exactly ONE staged entry for the fixed alias, not two — that is precisely what the guard prevents.
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
        // `new { a = o, b = o }` — the mirror case. No guard is needed here: each Outer leaf stages under its
        // OWN alias (unlike Inner's fixed alias), so both "a" and "b" are legitimately distinct $project
        // fields, both reading $$ROOT. AddToProjection dedups the BIND side back to a single index regardless,
        // so both members materialize correctly even though the emitted $project carries one field ("b") that
        // nothing ends up reading.
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
        // `new { o, Foo = o.Name.ToUpper() }` — the whole OUTER leaf (`o`) is itself perfectly native (Task 1),
        // but a SIBLING leaf reached through that same reference (`o.Name.ToUpper()`, a string transform
        // NativeJoinScopeTranslator/MongoExpressionTranslator does not support) is not. Out of scope per the
        // design doc's out-of-scope list: EF-444 gives whole-entity leaves a native shaper, it does not widen
        // the scalar/computed translator's own acceptance set. One untranslatable sibling must still decline
        // the WHOLE projection — no partial commit — even though the whole-entity leaf alone would succeed.
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
        // FINAL-REVIEW FINDING 7. `new { o, X = r.Total * 2 }` — the EF-444 Task 4 carve-out, isolated at the
        // UNIT level for the first time. Every other decline test in this class declines EARLIER, inside
        // NativeJoinScopeTranslator.TryTranslateValue (a string transform / method call it cannot translate),
        // so none of them ever reaches the guard under test here. This leaf translates PERFECTLY WELL —
        // arithmetic over an Inner scalar is squarely inside the translator's acceptance set — and is stopped
        // only by the whole-entity-sibling readability guard that runs just before the commit block: a
        // MongoBinaryExpression is neither a MongoElementRefExpression nor a MongoFieldExpression, so it has no
        // document path for the whole-document fallback legs the `o` leaf forces.
        //
        // The end-to-end proof that the resulting fallback still reads CORRECTLY lives in
        // NativeJoinTests.Computed_leaf_beside_a_whole_entity_leaf_declines_and_still_reads_correctly; this
        // test pins that the decline happens at THIS guard, which routing alone cannot distinguish.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, X = r.Total * 2 }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);

        // NOT VACUOUS: the same computed leaf WITHOUT the whole-entity sibling binds fine, so the decline above
        // is attributable to the whole-entity-leaf interaction and not to the arithmetic being untranslatable.
        // Without this half, a future regression that broke arithmetic translation outright would leave the
        // assertions above still green while the guard they name became dead code.
        var computedOnly = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o.Name, X = r.Total * 2 }));

        Assert.Equal(["Name", "X"], computedOnly.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.Equal(NativeRoute.Projection, computedOnly.Select.Route);
    }

    [Fact]
    public void Binds_a_whole_outer_entity_leaf_mixed_with_a_scalar()
    {
        // `new { o, r.Total }` — Outer captured WHOLE, mixed with a scalar Inner leaf. EF-444 Task 1: the Outer
        // leaf now stages a $$ROOT reference instead of declining the whole projection.
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
        // A method call over a join-scope member — outside NativeJoinScopeTranslator's acceptance set. One
        // untranslatable leaf declines the whole projection, including the sibling leaf that WOULD have
        // translated (no partial commit).
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { Upper = o.Name.ToUpper(), r.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Empty(mongoQ.Lookups);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    /// <summary>
    /// A DTO whose member name is spelled EXACTLY like the <c>$lookup</c> alias the provider registers for the
    /// <c>Owner.Orders</c> navigation (<c>LookupExpression.LookupAliasPrefix</c> + the navigation's name).
    /// Contrived, and deliberately so — it is the only way to reach the collision, and the collision's failure
    /// mode is silent.
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
        // MongoQueryExpression.AddToProjection uniquifies aliases case-insensitively by appending a counter,
        // and a join query already carries the inner entity's projection under the "_lookup_<Nav>" alias by the
        // time this binder runs. Left unguarded, the shaper would read "_lookup_Orders0" while the emitted
        // $project wrote "_lookup_Orders" — a silently dropped value, not an error. Both leaves here translate
        // fine, so the ONLY thing that can decline this projection is the alias-collision check.
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
        // Shape 1 (the sibling arm in TranslateSelect): `select r`. It carries no projection at all — the
        // whole-entity route reads the inner entity out of the $lookup's unwound alias — so the signal is
        // "lookup registered + candidate confirmed + Route == WholeEntity", NOT a populated Projection.
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
        // The structural closure of NativeJoinScopeTranslator's documented RESIDUAL GAP, re-verified after the
        // native-chained-join-scalar-projection plan's Task 4 widening. A JoinScope built from a chained
        // second join whose OWN flat TransparentIdentifier<TOuter,TInner> coincidentally matches the FIRST
        // join's Outer/Inner CLR types (both joins here target the SAME entity type in the SAME positions) is
        // the gap's own worked example: a translator that resolved this leaf by comparing CLR types (the OLD
        // flat NativeJoinScopeTranslator.TryTranslateValue) would misresolve `r2.Total` against the FIRST
        // join's InnerPrefix — silently wrong data. This test is now the POSITIVE proof that
        // TryTranslateSingleScope's resolution — by the ACTUAL member-name hop chain
        // (MongoTransparentScopeResolver.ScopeRerootingVisitor), never by comparing a scope's recorded CLR
        // type against some OTHER level's type — targets the SECOND join's own alias correctly, rather than
        // the gap's hazard materializing. The residual gap therefore stays closed by resolving correctly, not
        // by declining: `o.Name` structurally re-roots to scope 0 (the root Owner, unprefixed), and `r2.Total`
        // structurally re-roots to scope 2 — the SECOND join's own Levels[1], not Levels[0] — because the
        // actual rootParam here is a genuinely two-level-nested TransparentIdentifier
        // (TransparentIdentifier<TransparentIdentifier<Owner,Order>,Order>), which
        // MongoQueryableMethodTranslatingExpressionVisitor.IsSingleEligibleNativeJoinScope's own chain-rebuild
        // produces for this shape — confirmed by asserting Levels[1].InnerPrefix is NOT Levels[0].InnerPrefix
        // below, and that `Total` is emitted under the SECOND (Levels[1]) prefix.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Select(x => x.o)
                .Join(orders, o => o.Id, r2 => r2.OwnerId, (o, r2) => new { o.Name, r2.Total }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;
        Assert.Equal(2, scope.Levels.Count);
        Assert.Equal(2, mongoQ.Joins.Count);

        // Both lookups are registered: the first by the intermediate Select's own bare-whole-entity-leaf
        // confirm arm (which fires while Joins.Count is still 1), the second unconditionally by
        // TranslateJoinCore's own multi-join flattening once Joins.Count > 1 — measured, and independent of
        // whether the trailing projection ends up binding a leaf against either level.
        Assert.Equal(2, mongoQ.Lookups.Count);

        Assert.Equal(["Name", "Total"], mongoQ.Select.Projection.Select(p => p.Alias).ToArray());

        // Name (root, scope 0): plain MongoFieldExpression, unprefixed.
        var name = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal("Name", name.ElementName);

        // Total (the SECOND join's own Inner, scope 2 == Levels[1]): prefixed with Levels[1].InnerPrefix, NOT
        // Levels[0].InnerPrefix — the alias that would have been used had this leaf misresolved against the
        // FIRST join instead, per the gap's worked example.
        Assert.NotEqual(scope.Levels[0].InnerPrefix, scope.Levels[1].InnerPrefix);
        var total = Assert.IsType<MongoFieldExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix + ".Total", total.ElementName);

        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_two_level_chain_projection_naming_every_scope_as_a_whole_entity()
    {
        // The native-chained-join-scope plan's own motivating shape (Task 1's end-to-end test, and
        // NorthwindMiscellaneousQueryMongoTest.Multiple_joins_Where_Order_Any): a genuine two-join chain whose
        // trailing selector is a THREE-leaf, ALL-WHOLE-ENTITY projection naming every scope in the chain — the
        // root (`cr`, scope index 0), the first join's Inner side (`or`, scope index 1), and the second join's
        // Inner side (`od`, scope index 2). Mirrors this file's own depth-1
        // `Binds_both_whole_entity_leaves_with_no_scalars` coverage, just resolved against a real two-level
        // chain instead of one level.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { cr = e.o, or = e.r, od = l }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        var scope = mongoQ.Select.JoinScope!;
        Assert.Equal(2, scope.Levels.Count);
        Assert.Equal(2, mongoQ.Joins.Count);

        // "cr" (root) is emitted under its OWN alias; "or"/"od" (Inner leaves at levels 1/2) are emitted
        // under their own LEVEL's fixed, self-referential InnerPrefix alias instead — NOT the member's own
        // alias — exactly like the depth-1 Inner-leaf tests above.
        Assert.Equal(["cr", scope.Levels[0].InnerPrefix, scope.Levels[1].InnerPrefix],
            mongoQ.Select.Projection.Select(p => p.Alias).ToArray());
        Assert.Equal(3, mongoQ.Select.Projection.Count);

        // cr (root) reads $$ROOT under its own alias.
        var rootLeaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[0].Expression);
        Assert.Equal(MongoElementRefExpression.WholeRootDocumentPath, rootLeaf.Path);

        // or (level 1's Inner) and od (level 2's Inner) each read their own level's fixed, self-referential
        // InnerPrefix alias — NOT the member's own alias ("or"/"od").
        var level1Leaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[1].Expression);
        Assert.Equal(scope.Levels[0].InnerPrefix, level1Leaf.Path);
        Assert.NotEqual("or", level1Leaf.Path);

        var level2Leaf = Assert.IsType<MongoElementRefExpression>(mongoQ.Select.Projection[2].Expression);
        Assert.Equal(scope.Levels[1].InnerPrefix, level2Leaf.Path);
        Assert.NotEqual("od", level2Leaf.Path);
        Assert.NotEqual(level1Leaf.Path, level2Leaf.Path);

        // Every level's own $lookup is registered, and every level's candidate join is confirmed exactly
        // once, so the chain routes fully natively.
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);
    }

    [Fact]
    public void Binds_a_two_level_chain_projection_with_scalar_leaves_at_every_scope()
    {
        // Native-chained-join-scalar-projection plan (2026-09-18). Every leaf is a plain scalar rooted at
        // exactly one scope: e.o.Name (root, scope 0), e.r.Total (join #1's Inner, scope 1), l.Sku (join #2's
        // Inner, scope 2). Mirrors the motivating shape in
        // NorthwindMiscellaneousQueryMongoTest.Join_Customers_Orders_Orders_Skip_Take_Same_Properties, minus the
        // trailing Skip/Take (a separate, still-open gap).
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
        // `new { cr = e.o, or = e.r, LineSku = l.Sku }` — a whole-entity leaf (root) alongside a chain-scalar
        // leaf (scope 2). The existing whole-entity sibling-readability guard already accepts a plain
        // MongoFieldExpression sibling; this leaf produces exactly that, so no guard change is needed — this
        // test verifies that, not just the leaf's own translation.
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
        // `Mixed = e.r.Total + l.Id` spans scope 1 (Total) AND scope 2 (Id) in a single leaf — out of scope for
        // this plan (see the design doc's Scope/Out section). Must decline the WHOLE projection, not partially
        // commit the other, individually-translatable leaf.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o.Name, Mixed = e.r.Total + l.Id }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.Projection);

        // NOT Assert.Empty: both lookups are ALREADY registered before this binder ever runs — unconditionally,
        // by TranslateJoinCore's own multi-join flattening the moment Joins.Count > 1 (this shape's Joins.Count
        // is 2) — independent of whether the trailing projection binds. This mirrors the same, pre-existing
        // measured behavior asserted by
        // Binds_a_second_chained_join_reusing_the_first_joins_target_type_at_the_correct_alias above; verified
        // unrelated to this task's wiring change (reproduces identically with the OLD outright-decline arm
        // too).
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }

    [Fact]
    public void Declines_a_nested_wrapped_leaf_over_a_two_level_chain()
    {
        // `Nested = new { Value = l.Sku }` trailing a genuine 2-level chain (scope.Levels.Count == 2) is
        // explicitly out of scope for the native-chained-join-scalar-projection plan (design doc's Scope/Out
        // section: "a nested wrapped leaf ... over a chain — already separately gated to Levels.Count == 1").
        // Regression pin: the ordinary (scalar/computed) leaf arm's `TryTranslateSingleScope` call must exclude
        // a nested-projection-shaped leafBody before calling it, or the leaf's inner NewExpression round-trips
        // through MongoExpressionTranslator's own generic NewExpression→MongoDocumentConstructionExpression
        // handling and gets wrongly admitted. Must decline the WHOLE projection, not partially commit the
        // sibling scalar leaf either.
        var mongoQ = TranslateThreeSourceJoinQuery((owners, orders, lines) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Join(lines, e => e.r.Id, l => l.OrderId, (e, l) => new { e.o.Name, Nested = new { Value = l.Sku } }));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Equal(2, mongoQ.Select.JoinScope!.Levels.Count);
        Assert.Empty(mongoQ.Select.Projection);
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }
}
