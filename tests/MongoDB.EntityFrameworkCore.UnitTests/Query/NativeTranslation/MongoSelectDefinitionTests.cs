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

using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Tests for the native-translation logical query IR, <see cref="MongoSelectDefinition"/> (the
/// "MongoSelectExpression" of the EF-323 design), composed into <see cref="MongoQueryExpression"/>
/// via its <see cref="MongoQueryExpression.Select"/> property.
/// </summary>
public class MongoSelectDefinitionTests
{
    private class StubEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static MongoSelectDefinition TestSelect()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>();
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType).Select;
    }

    [Fact]
    public void AddPredicateConjunct_ANDs_into_a_single_predicate()
    {
        var select = TestSelect();
        var a = new MongoConstantExpression(true, null);
        var b = new MongoConstantExpression(true, null);

        select.AddPredicateConjunct(a);
        select.AddPredicateConjunct(b);

        var op = Assert.IsType<MongoMatchOp>(Assert.Single(select.PipelineOps));
        var binary = Assert.IsType<MongoBinaryExpression>(op.Predicate);
        Assert.Equal(MongoBinaryOperator.AndAlso, binary.Operator);
    }

    // ── Ordered select-op pipeline merge rules (EF-347 Task 1) ──────────────────────

    private static MongoConstantExpression Const(int v) => new(v, forSerialization: null);
    private static MongoOrdering Asc() => new(Const(1), Ascending: true);

    [Fact]
    public void Consecutive_predicates_merge_into_one_match_op()
    {
        var s = new MongoSelectDefinition();
        s.AddPredicateConjunct(Const(1));
        s.AddPredicateConjunct(Const(2));

        var op = Assert.IsType<MongoMatchOp>(Assert.Single(s.PipelineOps));
        Assert.IsType<MongoBinaryExpression>(op.Predicate); // AndAlso of the two conjuncts
    }

    [Fact]
    public void Predicate_after_sort_appends_a_separate_match_op()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Asc());
        s.AddPredicateConjunct(Const(1));

        Assert.Collection(s.PipelineOps,
            o => Assert.IsType<MongoSortOp>(o),
            o => Assert.IsType<MongoMatchOp>(o));
    }

    [Fact]
    public void Consecutive_order_by_replaces_the_sort_op()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Asc());
        s.StartOrReplaceSort(new MongoOrdering(Const(2), Ascending: false));

        var op = Assert.IsType<MongoSortOp>(Assert.Single(s.PipelineOps));
        Assert.False(Assert.Single(op.Orderings).Ascending);
    }

    [Fact]
    public void Then_by_extends_the_current_sort_op()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Asc());
        s.AppendThenBy(new MongoOrdering(Const(2), Ascending: false));

        var op = Assert.IsType<MongoSortOp>(Assert.Single(s.PipelineOps));
        Assert.Equal(2, op.Orderings.Count);
    }

    [Fact]
    public void Take_before_skip_records_both_ops_in_arrival_order()
    {
        var s = new MongoSelectDefinition();
        s.AppendLimit(Const(10));
        s.AppendSkip(Const(5));

        Assert.Collection(s.PipelineOps,
            o => Assert.IsType<MongoLimitOp>(o),
            o => Assert.IsType<MongoSkipOp>(o));
        Assert.True(s.HasPaging);
        Assert.True(s.HasLimit);
    }

    [Fact]
    public void Fallback_wrong_data_is_false_by_default()
    {
        var s = new MongoSelectDefinition();

        Assert.False(s.IsFallbackWrongData);
        Assert.False(s.IsGroupByFallbackUnsafe);
    }

    [Fact]
    public void MarkGroupByFallbackUnsafe_sets_the_flag_and_forces_fallback_route()
    {
        var s = new MongoSelectDefinition();
        s.MarkGroupByFallbackUnsafe();

        Assert.True(s.IsGroupByFallbackUnsafe);
        Assert.True(s.IsFallbackWrongData);
        Assert.Equal(NativeRoute.Fallback, s.Route);
    }

    [Fact]
    public void PropagateFallbackWrongDataFrom_copies_the_GroupBy_provenance()
    {
        var groupByInner = new MongoSelectDefinition();
        groupByInner.MarkGroupByFallbackUnsafe();
        var outer = new MongoSelectDefinition();
        outer.PropagateFallbackWrongDataFrom(groupByInner);

        Assert.True(outer.IsGroupByFallbackUnsafe);
        Assert.True(outer.IsFallbackWrongData);
        Assert.Equal(NativeRoute.Fallback, outer.Route);
    }

    [Fact]
    public void PropagateFallbackWrongDataFrom_a_clean_inner_is_a_no_op()
    {
        var outer = new MongoSelectDefinition();
        outer.PropagateFallbackWrongDataFrom(new MongoSelectDefinition());

        Assert.False(outer.IsFallbackWrongData);
        Assert.Equal(NativeRoute.WholeEntity, outer.Route);
    }

    [Fact]
    public void Route_defaults_to_whole_entity()
        => Assert.Equal(NativeRoute.WholeEntity, TestSelect().Route);

    [Fact]
    public void Route_is_projection_when_a_projection_is_added()
    {
        var select = TestSelect();
        select.AddProjection(new MongoProjection("a", new MongoConstantExpression(1, null)));

        Assert.Equal(NativeRoute.Projection, select.Route);
    }

    [Fact]
    public void Route_is_fallback_after_MarkNotNativelyRepresentable()
    {
        var select = TestSelect();
        select.AddProjection(new MongoProjection("a", new MongoConstantExpression(1, null)));
        select.MarkNotNativelyRepresentable();

        Assert.Equal(NativeRoute.Fallback, select.Route);
    }

    [Fact]
    public void AddProjection_appends_in_order_to_Projection()
    {
        var select = TestSelect();
        var a = new MongoProjection("Name", new MongoConstantExpression(1, null));
        var b = new MongoProjection("Age", new MongoConstantExpression(2, null));

        select.AddProjection(a);
        select.AddProjection(b);

        Assert.Equal(2, select.Projection.Count);
        Assert.Equal("Name", select.Projection[0].Alias);
        Assert.Equal("Age", select.Projection[1].Alias);
    }

    [Fact]
    public void New_select_has_empty_projection()
        => Assert.Empty(TestSelect().Projection);

    [Fact]
    public void Route_is_GroupBy_when_grouping_set()
    {
        var select = new MongoSelectDefinition();
        select.Grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });

        Assert.Equal(NativeRoute.GroupBy, select.Route);
    }

    [Fact]
    public void Route_is_Fallback_even_when_grouping_set_if_marked_not_native()
    {
        var select = new MongoSelectDefinition();
        select.Grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });
        select.MarkNotNativelyRepresentable();

        Assert.Equal(NativeRoute.Fallback, select.Route);
    }

    // ── UnwindSources chain (EF-347 nested-reference slice) ─────────────────────────

    [Fact]
    public void New_select_has_no_unwind_sources_and_null_UnwindSource_shim()
    {
        var select = new MongoSelectDefinition();

        Assert.Empty(select.UnwindSources);
        Assert.Null(select.UnwindSource);
        Assert.False(select.HasTerminalOperator);
    }

    [Fact]
    public void AddUnwindSource_appends_and_UnwindSource_shim_reads_the_last_one()
    {
        var select = new MongoSelectDefinition();
        var first = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        var second = MongoUnwindSource.Reference("_lookup_Leaves", innerEntityType: null!, lookup: null!);

        select.AddUnwindSource(first);
        Assert.Same(first, select.UnwindSource); // single source: shim == that source
        Assert.True(select.HasTerminalOperator);

        select.AddUnwindSource(second);
        Assert.Equal(2, select.UnwindSources.Count);
        Assert.Same(first, select.UnwindSources[0]);
        Assert.Same(second, select.UnwindSources[1]);
        Assert.Same(second, select.UnwindSource); // shim now reads the LAST source
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_true_for_exactly_one_reference_source()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Mids", innerEntityType: null!, lookup: null!));

        Assert.True(select.IsSingleReferenceUnwindTerminalOnly);
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_false_for_owned_source()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Owned("Items", innerEntityType: null!));

        Assert.False(select.IsSingleReferenceUnwindTerminalOnly);
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_false_once_two_sources_are_chained()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Mids", innerEntityType: null!, lookup: null!));
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Leaves", innerEntityType: null!, lookup: null!));

        Assert.False(select.IsSingleReferenceUnwindTerminalOnly);
    }

    [Fact]
    public void IsSingleReferenceUnwindTerminalOnly_false_when_a_group_is_also_set()
    {
        var select = new MongoSelectDefinition();
        select.AddUnwindSource(MongoUnwindSource.Reference("_lookup_Mids", innerEntityType: null!, lookup: null!));
        select.IsGroupBy = true;

        Assert.False(select.IsSingleReferenceUnwindTerminalOnly);
    }

    // ── Reference-Include candidate join counting (EF-368, fix round 1) ────────────
    //
    // These exercise MarkSawCandidateReferenceIncludeJoin/MarkReferenceIncludeConfirmed directly at the IR
    // level.
    //
    // STALE-COMMENT CORRECTION: this note used to say "Task 4 wires the candidate-recording call site
    // (NativeSlotPopulator) but NOT the confirming one (that is Task 5's job) — so there is no LINQ shape yet
    // that reaches MarkReferenceIncludeConfirmed ... a functional/end-to-end test would be vacuous today
    // (nothing can confirm)". THAT IS NO LONGER TRUE. Task 5 shipped (EF-368): single-level reference Include
    // goes native, and MongoQueryableMethodTranslatingExpressionVisitor.TryConfirmReferenceInclude calls
    // MarkReferenceIncludeConfirmed on an ordinary LINQ shape. End-to-end coverage exists in
    // NativeReferenceIncludeTests and is NOT vacuous.
    //
    // These IR-level tests are kept anyway, for the reason that still holds: they pin the
    // two-candidate-joins-one-confirmation counter arithmetic directly, which no single end-to-end query
    // shape isolates.

    [Fact]
    public void HasUnconfirmedCandidateJoin_false_with_no_candidates()
        => Assert.False(new MongoSelectDefinition().HasUnconfirmedCandidateJoin);

    [Fact]
    public void HasUnconfirmedCandidateJoin_true_for_a_single_unconfirmed_candidate()
    {
        var select = new MongoSelectDefinition();
        select.MarkSawCandidateReferenceIncludeJoin();

        Assert.True(select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Fallback, select.Route);
    }

    [Fact]
    public void HasUnconfirmedCandidateJoin_false_when_the_single_candidate_is_confirmed()
    {
        var select = new MongoSelectDefinition();
        select.MarkSawCandidateReferenceIncludeJoin();
        select.MarkReferenceIncludeConfirmed();

        Assert.False(select.HasUnconfirmedCandidateJoin);
    }

    [Fact]
    public void HasUnconfirmedCandidateJoin_true_when_only_one_of_two_candidates_is_confirmed()
    {
        // The fix-round-1 regression pin: two candidate joins, only one confirmed. A flat boolean pair would
        // go "all confirmed" the moment ANY join confirms, wrongly admitting the second, untouched candidate
        // and defeating default-deny. The count form must still report unconfirmed here.
        var select = new MongoSelectDefinition();
        select.MarkSawCandidateReferenceIncludeJoin();
        select.MarkSawCandidateReferenceIncludeJoin();
        select.MarkReferenceIncludeConfirmed();

        Assert.True(select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Fallback, select.Route);
    }

    [Fact]
    public void HasUnconfirmedCandidateJoin_false_when_two_candidates_are_both_confirmed()
    {
        var select = new MongoSelectDefinition();
        select.MarkSawCandidateReferenceIncludeJoin();
        select.MarkSawCandidateReferenceIncludeJoin();
        select.MarkReferenceIncludeConfirmed();
        select.MarkReferenceIncludeConfirmed();

        Assert.False(select.HasUnconfirmedCandidateJoin);
    }

    [Fact]
    public void HasUnconfirmedCandidateJoin_true_when_confirmations_exceed_candidates()
    {
        // A confirmation arriving without a matching candidate join is a broken invariant. The strict
        // inequality (!=, not >) means this fails closed (still routes to Fallback) rather than being
        // silently read as "all confirmed" — see the doc comment on HasUnconfirmedCandidateJoin.
        var select = new MongoSelectDefinition();
        select.MarkSawCandidateReferenceIncludeJoin();
        select.MarkReferenceIncludeConfirmed();
        select.MarkReferenceIncludeConfirmed();

        Assert.True(select.HasUnconfirmedCandidateJoin);
        Assert.Equal(NativeRoute.Fallback, select.Route);
    }
}
