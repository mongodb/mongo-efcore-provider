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
