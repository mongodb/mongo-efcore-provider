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
/// Drives a genuine <c>.Join(...).Where(...)</c> query through the REAL QMTEV pipeline (same harness pattern
/// as <see cref="SlotPopulationTests"/>, extended to two entity types) to prove
/// <see cref="MongoDB.EntityFrameworkCore.Query.NativeTranslation.NativeSlotPopulator"/>'s <c>Where</c> arm
/// actually resolves a join-scope predicate against a REAL, EF-generated
/// <c>TransparentIdentifier&lt;TOuter,TInner&gt;</c> parameter — not a hand-mocked one. This is the test the
/// EF-392 Task 4 review round asked for: the original functional test
/// (<c>NativeJoinTests.Where_after_join_reading_outer_scope_goes_native</c>) can't distinguish "the Where arm
/// translated and the Select arm declined" from "the Where arm itself declined" because both raise the exact
/// same <c>NativeTranslationNotSupportedException</c> under <c>NativeOnly</c> — Task 5 (the Select-side
/// binder) hasn't landed yet, so no query shape can get all the way through to prove the Where arm's success
/// via an end-to-end result. This test sidesteps that entirely by asserting on the populated
/// <see cref="MongoSelectDefinition"/> directly, deterministically, with no database.
/// </summary>
public class JoinScopeWhereSlotPopulationTests
{
    // Owner/Order navigations are required, not decorative: TranslateJoinCore's eligibility check
    // (Query/Visitors/MongoQueryableMethodTranslatingExpressionVisitor.cs, "joinInfo.Navigation is {}
    // eligibleNavigation") only finds a navigation via IEntityType.GetNavigations(), which requires a real
    // CLR navigation PROPERTY — a shadow/convention-only FK relationship with no nav property (which is what
    // a bare `OwnerId` FK-name convention alone would produce) does not satisfy it, so JoinScope would never
    // get recorded and this whole test file would trivially assert null forever. Mirrors the functional
    // NativeJoinTests.cs Owner/Order fixture's own nav properties for the same reason.
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

    // Third source for the chained-join (Task 3) test below - same navigation-property requirement as
    // Owner/Order above (TranslateJoinCore's eligibility check needs a real CLR navigation property to
    // resolve, not a shadow/convention-only FK).
    private class OrderLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public Order? Order { get; set; }
        public string Sku { get; set; } = "";
    }

    /// <summary>
    /// Drives a two-source join query through the REAL EF Core translation pipeline —
    /// <see cref="IQueryTranslationPreprocessorFactory"/> THEN QMTEV, unlike
    /// <see cref="SlotPopulationTests.TranslateToMongoQuery{T}"/>, which skips preprocessing as unnecessary
    /// for its simple flat-entity cases. Skipping it here would NOT be equivalent: a join's own result
    /// selector (`(o, r) => new { o, r }`) only gets rewritten from the C#-compiler's raw anonymous type
    /// (property names "o"/"r") to EF's normalized flat `TransparentIdentifier<TOuter,TInner>` shape
    /// ("Outer"/"Inner") during preprocessing's nav-expansion — confirmed empirically: the first version of
    /// this test skipped preprocessing and asserted on a body shaped `x.o.Name`, which the Where arm
    /// (correctly) never recognizes as join-scope-shaped, since it isn't yet at that point in a real
    /// pipeline either. Only real `db.Set&lt;T&gt;()` queryables are used as roots (no hand-rolled stub) so
    /// nav-expansion has the real, model-backed shape it needs to key off; execution never happens (no
    /// database is touched — the pipeline is driven exactly through where the native slots are populated
    /// and then stopped).
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
    /// Three-source variant of <see cref="TranslateJoinQuery"/>, for Task 3's chained-join metadata test —
    /// same pipeline, same rationale (real preprocessing is required to get EF's normalized
    /// <c>TransparentIdentifier</c> shape), just with a second <c>Join</c> source added.
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

    // Fixture for the embedded-outer-key-selector regression test below (EF-380 shape, depth 1, NO prior
    // join). Deliberately separate from Owner/Order: EmbeddedAddress needs a REAL navigation of its own
    // (LinkedTarget, FK LinkedTargetId) to JoinTarget so RebindInnerShaperToOuterQuery's navigation
    // resolution — which walks the embedded segment via the navigation graph and then searches for a
    // navigation ON THE EMBEDDED TYPE, not the root — actually finds one; reusing Owner/Order would leave
    // that resolution returning null (no navigation on Address-shaped types pointing at Order), which
    // exercises a completely different (and uninteresting) decline path.
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
    /// Regression test for a task-review finding on the Task 3 <c>JoinLookupImplementsKeySelectors</c> fix
    /// (see <c>.superpowers/sdd/2026-09-07-native-chained-join-scope/task-3-report.md</c>, "Fix round 1"):
    /// the fix's own comment claims the new <c>EndsWith</c> branch "never fires without a transitive hop",
    /// but it ALSO fires for a depth-1 (no prior join) join whose OUTER key selector reaches through an
    /// owned/embedded navigation (the pre-existing EF-380 shape, <c>o.Address.LinkedTargetId</c>) — because
    /// <c>RebindInnerShaperToOuterQuery</c> walks <c>searchEntityType</c> forward through the embedded
    /// segment BEFORE resolving the navigation, so <c>joinInfo.Navigation.DeclaringEntityType</c> ends up
    /// being the OWNED type (<c>EmbeddedAddress</c>), not the root, and the existing (pre-Task-3)
    /// <c>embeddedPath</c> prefixing (<c>lookup.LocalField = $"{embeddedPath}.{lookup.LocalField}"</c>)
    /// means <c>lookup.LocalField</c> ("Address.LinkedTargetId") never equals the bare element name
    /// ("LinkedTargetId") either — only the new <c>EndsWith</c> branch can agree here.
    /// </summary>
    /// <remarks>
    /// This IS safe, and the join genuinely becomes (correctly) eligible where it previously declined for
    /// an unrelated-to-embedding reason (the same exact-match brittleness the transitive-hop fix targets).
    /// Walking the proof through <c>JoinLookupImplementsKeySelectors</c>: <c>joinInfo.Navigation</c> is
    /// resolved by <c>RebindInnerShaperToOuterQuery</c> by walking the SAME embedded segment
    /// (<c>"Address"</c>) that produced the lookup's own <c>LocalField</c> prefix — so
    /// <c>outerAnchorEntityType</c> (<c>Navigation.DeclaringEntityType</c> == <c>EmbeddedAddress</c>) is
    /// exactly the type <c>outerKeyName</c> ("LinkedTargetId") must be looked up against to get the RIGHT
    /// property (the one actually reached by <c>o.Address.LinkedTargetId</c>), and
    /// <c>lookup.LocalField</c> ("Address.LinkedTargetId") is that SAME property's element name
    /// ("LinkedTargetId") prefixed by that SAME embedded path ("Address") — so the <c>EndsWith</c> check is
    /// comparing two values built from the identical structural fact, not coincidentally agreeing. The
    /// resulting <c>$lookup</c>'s <c>localField: "Address.LinkedTargetId"</c> is also literally correct: on
    /// the outer document, <c>LinkedTargetId</c> really does live nested under the embedded <c>Address</c>
    /// sub-document, so a dotted <c>localField</c> is exactly how Mongo addresses it. Reading
    /// <c>mongoQ.Select.JoinScope</c> afterward is equally sound: <c>MongoJoinScope</c>/<c>MongoJoinScopeLevel</c>
    /// only record the join's INNER entity type/alias/left-outer-ness — nothing about how the OUTER side's
    /// own key was reached — so an embedded vs. root-property outer key makes no difference to what a
    /// consuming <c>Where</c>/<c>Select</c> arm resolves "Outer" to (still the query's own root entity).
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

        // UPDATED for the native-chained-join-scope plan's Task 6 (the confirming Select-side widening this
        // task's own comment above deferred to): EF's nav-expansion always applies the join's pending result
        // selector `new { e.o, e.r, l }` LAST — an implicit trailing Select whose three leaves are ALL
        // whole-entity references spanning every scope in this chain (o at scope 0, e.r at scope 1, l at
        // scope 2) — which NativeJoinScopeProjectionBinder now (Task 6) confirms fully, so both signals flip
        // from this task's own original (deliberately temporary) pinned state.
        Assert.False(mongoQ.Select.HasUnconfirmedCandidateJoin);
        Assert.Equal(2, mongoQ.Lookups.Count);
        Assert.Equal(NativeRoute.Projection, mongoQ.Select.Route);

        // The Where predicate itself (`x.o.Name == "Alice"`, root-scope-only) still populates a native
        // $match — Task 4's TryTranslateRootScopeOnly arm, unaffected by Task 6's Select-side widening.
        var matchOp = Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(matchOp.Predicate);
    }

    [Fact]
    public void Where_reading_outer_scope_after_join_populates_predicate_natively()
    {
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.o.Name == "Alice"));

        // JoinScope really did get recorded for this eligible single-level join, and the Where arm's
        // fallback branch (NativeJoinScopeTranslator.TryTranslatePredicate) really did run and succeed
        // against the REAL field-based TransparentIdentifier<Owner,Order> parameter EF's nav-expansion
        // produced — not a synthetic stand-in. A MongoMatchOp landing on PipelineOps (rather than
        // MarkNotNativelyRepresentable() being called) is the direct, unambiguous signal of that; before the
        // review-round fix (Type.GetProperty-only guard), this predicate ALWAYS declined for every real join,
        // silently, because GetProperty never finds "Outer"/"Inner" on a real (field-based) EF-generated
        // TransparentIdentifier.
        Assert.NotNull(mongoQ.Select.JoinScope);
        var matchOp = Assert.IsType<MongoMatchOp>(Assert.Single(mongoQ.Select.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(matchOp.Predicate);

        // Deliberately NOT asserting Route == WholeEntity here (confirmed empirically: EF Core's own
        // pipeline always appends a trailing identity Select over the join's raw anonymous-type result even
        // when the user's query has no explicit .Select() at all — visible in the MarkNotNativelyRepresentable
        // call stack as MongoQueryableMethodTranslatingExpressionVisitor.TranslateSelect). That Select
        // projects a raw anonymous `new { o, r }` shape — two WHOLE-ENTITY leaves, which
        // NativeJoinScopeProjectionBinder declines outright and NativeProjectionBinder has no shaper for — so
        // for THIS query's shape Route is Fallback whether the Where arm succeeded or not, and Route can't be
        // this test's signal.
        //
        // NARROWED (final-review finding 7): this comment used to make the sweeping claim that "Route is
        // Fallback for EVERY join query today, Where success or not". That was true when Task 4 was written
        // and is no longer true — Task 5's wrapped scalar-only projection arm routes a confirmed join to
        // Projection, and the bare whole-entity-leaf arm routes one to WholeEntity (see
        // NativeJoinScopeProjectionBinderTests). The claim holds only for the whole-entity-leaf shape used
        // here.
        //
        // The PipelineOps assertion above is the correct, unambiguous signal either way: it's empty when the
        // Where arm declines (see the companion Inner-side test below) and populated only when
        // AddPredicateConjunct actually ran.
    }

    /// <summary>
    /// Task 5's own real green signal for the chained (depth &gt;= 2) case: drives a genuine two-join chain
    /// (<c>Owner.Join(Order).Join(OrderLine)</c>) through the real EF pipeline and proves the generalized
    /// <c>Where</c> arm's new <c>Levels.Count: &gt; 1</c> branch — <see cref="NativeJoinScopeTranslator.TryTranslateRootScopeOnly"/>
    /// — actually resolves a predicate reading the ROOT (outermost, "o") scope against the real,
    /// EF-generated nested <c>TransparentIdentifier&lt;TransparentIdentifier&lt;Owner,Order&gt;,OrderLine&gt;</c>
    /// shape, exactly as <see cref="Where_reading_outer_scope_after_join_populates_predicate_natively"/> does
    /// for depth 1. A populated <see cref="MongoMatchOp"/> on <c>PipelineOps</c> is the direct, unambiguous
    /// signal — Task 1's end-to-end functional test can't distinguish this from an unrelated Select-side
    /// decline (Task 6, not yet landed), so this narrower assertion is what actually proves this task's own
    /// code path ran and succeeded.
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
    /// Companion to the above for the <c>OrderBy</c> arm's new chained-scope branch — same two-join chain,
    /// same root-scope-only key selector, but asserting a <see cref="MongoSortOp"/> lands on
    /// <c>PipelineOps</c> instead of a <see cref="MongoMatchOp"/>. Proves
    /// <c>NativeSlotPopulator</c>'s <c>OrderBy</c>/<c>OrderByDescending</c> arm's new
    /// <c>Levels.Count: &gt; 1</c> fallback (added by this task; previously OrderBy had NO join-scope arm at
    /// all, even for depth 1) actually populates the sort slot for a chained scope.
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
    /// Final-review fix (M9a) — the chained <c>Where</c> and <c>OrderBy</c> arms above both have dedicated
    /// coverage; <c>ThenBy</c> (added by the same Task 5 five-branch structure — plain field, computed key,
    /// depth-1 join scope, chained join scope, decline) did not. Same two-join chain and root-scope-only key
    /// selectors as <see cref="OrderBy_reading_root_scope_after_chained_join_populates_sort_natively"/>, just
    /// with a second ordering key appended via <c>ThenBy</c> — proves <c>NativeSlotPopulator</c>'s
    /// <c>ThenBy</c>/<c>ThenByDescending</c> arm's <c>Levels.Count: &gt; 1</c> branch actually appends to the
    /// existing sort instead of declining or overwriting it.
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
        // The Where arm is deliberately Outer-only for now (ReferencesInnerScope gate) — PipelineOps ($match)
        // are always lowered before the $lookup stage that would materialize Inner, so this must still mark
        // the query non-native (Route == Fallback) rather than "succeed" with a $match on a not-yet-joined
        // field. This is the companion assertion to the Outer-side success above, proving the Outer-only
        // restriction survived the guard-2/field-vs-property fixes and still gates the Where call site.
        var mongoQ = TranslateJoinQuery((owners, orders) =>
            owners.Join(orders, o => o.Id, r => r.OwnerId, (o, r) => new { o, r })
                .Where(x => x.r.Total > 0));

        Assert.NotNull(mongoQ.Select.JoinScope);
        Assert.Empty(mongoQ.Select.PipelineOps);
        // Also Fallback for the (separate, expected) trailing-Select reason described in the companion test
        // above — both agree here, so this assertion doesn't distinguish anything on its own; the empty
        // PipelineOps assertion is what actually proves the Where arm declined.
        Assert.Equal(NativeRoute.Fallback, mongoQ.Select.Route);
    }
}
