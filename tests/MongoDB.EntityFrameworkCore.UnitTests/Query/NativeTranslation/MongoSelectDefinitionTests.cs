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
    public void HasPagingAnywhere_is_false_with_no_paging()
        => Assert.False(new MongoSelectDefinition().HasPagingAnywhere);

    [Fact]
    public void HasPagingAnywhere_sees_pipeline_ops()
    {
        var s = new MongoSelectDefinition();
        s.AppendSkip(Const(5));

        Assert.True(s.HasPagingAnywhere);
    }

    [Fact]
    public void HasPagingAnywhere_sees_trailing_ops_after_a_set_op()
    {
        // A Take composed AFTER a set operation records into _trailingOps, which HasPaging deliberately does
        // not scan (its consumer gates a PRE-terminal GroupBy). The CSHARP-6017 join guard must still see it.
        var s = new MongoSelectDefinition();
        s.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "OtherCollection");
        s.AppendLimit(Const(3));

        Assert.Empty(s.PipelineOps);
        Assert.False(s.HasPaging);
        Assert.True(s.HasPagingAnywhere);
    }

    [Fact]
    public void HasPagingAnywhere_sees_declined_unrecorded_paging()
    {
        // TODO(CSHARP-6017): delete with the rest of the paging guard (this test AND the two HasPagingAnywhere_*
        // tests above; keep the Fallback_wrong_data_* / PropagateFallbackWrongDataFrom_* tests below, which pin
        // the permanent EF-344 mechanism).
        // A Skip/Take composed after a NON-set-op terminal (e.g. a natively-bound projected Distinct) is DECLINED
        // by NativeSlotPopulator's post-terminal early return rather than recorded, so it lands in NEITHER op
        // list — yet it is still in the captured method chain the driver-LINQ fallback executes, where the driver
        // folds it into the correlated $lookup sub-pipeline. HasPagingAnywhere must therefore see it too; without
        // this, EF-366's join guard missed the shape and it returned silently wrong rows under default Native.
        var s = new MongoSelectDefinition();
        s.MarkSawUnrecordedPaging();

        Assert.Empty(s.PipelineOps);
        Assert.Empty(s.TrailingOps);
        Assert.False(s.HasPaging);
        Assert.True(s.HasPagingAnywhere);
    }

    [Fact]
    public void Fallback_wrong_data_is_false_by_default()
    {
        var s = new MongoSelectDefinition();

        Assert.False(s.IsFallbackWrongData);
        Assert.False(s.IsGroupByFallbackUnsafe);
        Assert.False(s.IsPagedJoinInnerFallbackUnsafe);
    }

    [Fact]
    public void MarkPagedJoinInnerFallbackUnsafe_sets_the_flag_and_forces_fallback_route()
    {
        var s = new MongoSelectDefinition();
        s.MarkPagedJoinInnerFallbackUnsafe();

        Assert.True(s.IsPagedJoinInnerFallbackUnsafe);
        Assert.True(s.IsFallbackWrongData);
        Assert.False(s.IsGroupByFallbackUnsafe);
        Assert.Equal(NativeRoute.Fallback, s.Route);
    }

    [Fact]
    public void PropagateFallbackWrongDataFrom_copies_both_provenances_independently()
    {
        var groupByInner = new MongoSelectDefinition();
        groupByInner.MarkGroupByFallbackUnsafe();
        var outer1 = new MongoSelectDefinition();
        outer1.PropagateFallbackWrongDataFrom(groupByInner);

        Assert.True(outer1.IsGroupByFallbackUnsafe);
        Assert.False(outer1.IsPagedJoinInnerFallbackUnsafe);

        var pagedInner = new MongoSelectDefinition();
        pagedInner.MarkPagedJoinInnerFallbackUnsafe();
        var outer2 = new MongoSelectDefinition();
        outer2.PropagateFallbackWrongDataFrom(pagedInner);

        Assert.True(outer2.IsPagedJoinInnerFallbackUnsafe);
        Assert.False(outer2.IsGroupByFallbackUnsafe);
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
}
