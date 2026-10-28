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

using System.Linq;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Truth-table tests for <see cref="MongoSelectDefinition"/>'s gate predicates:
/// <see cref="MongoSelectDefinition.IsFilterOnlyCollectionScan"/>, <see cref="MongoSelectDefinition.HasNonCommutingPostJoinOp"/> and
/// <see cref="MongoSelectDefinition.TryFlipTrailingSortDirection"/>.
/// </summary>
public class MongoSelectDefinitionGateTests
{
    private static MongoConstantExpression Const(int v) => new(v, forSerialization: null);

    private static MongoOrdering Order(int key, bool ascending) => new(Const(key), ascending);

    // ---- IsFilterOnlyCollectionScan ----

    [Fact]
    public void IsFilterOnlyCollectionScan_is_false_for_an_empty_select()
    {
        var s = new MongoSelectDefinition();

        Assert.False(s.IsFilterOnlyCollectionScan);
        Assert.True(s.IsBareCollectionScan);
    }

    [Fact]
    public void IsFilterOnlyCollectionScan_is_true_for_matches_only()
    {
        var s = new MongoSelectDefinition();
        s.AddPredicateConjunct(Const(1));
        s.AddPredicateConjunct(Const(2));

        Assert.True(s.IsFilterOnlyCollectionScan);
        Assert.False(s.IsBareCollectionScan);
    }

    [Fact]
    public void IsFilterOnlyCollectionScan_is_false_when_any_other_op_is_recorded()
    {
        var withSort = new MongoSelectDefinition();
        withSort.AddPredicateConjunct(Const(1));
        withSort.StartOrReplaceSort(Order(1, true));
        Assert.False(withSort.IsFilterOnlyCollectionScan);

        var withSkip = new MongoSelectDefinition();
        withSkip.AddPredicateConjunct(Const(1));
        withSkip.AppendSkip(Const(1));
        Assert.False(withSkip.IsFilterOnlyCollectionScan);

        var withLimit = new MongoSelectDefinition();
        withLimit.AddPredicateConjunct(Const(1));
        withLimit.AppendLimit(Const(1));
        Assert.False(withLimit.IsFilterOnlyCollectionScan);

        var withDistinct = new MongoSelectDefinition();
        withDistinct.AddPredicateConjunct(Const(1));
        withDistinct.AppendDistinct();
        Assert.False(withDistinct.IsFilterOnlyCollectionScan);
    }

    [Fact]
    public void IsFilterOnlyCollectionScan_is_false_after_a_declined_operator_or_non_bare_join_inner()
    {
        var declined = new MongoSelectDefinition();
        declined.AddPredicateConjunct(Const(1));
        declined.MarkNotNativelyRepresentable();
        Assert.False(declined.IsFilterOnlyCollectionScan);

        var nonBare = new MongoSelectDefinition();
        nonBare.AddPredicateConjunct(Const(1));
        nonBare.MarkSawNonBareJoinInner();
        Assert.False(nonBare.IsFilterOnlyCollectionScan);
    }

    [Fact]
    public void IsFilterOnlyCollectionScan_is_false_when_post_join_ops_are_recorded()
    {
        var s = new MongoSelectDefinition();
        s.AddPredicateConjunct(Const(1));
        s.MarkJoinInnerAccessConfirmed();
        s.AddPredicateConjunct(Const(2));

        Assert.NotEmpty(s.PostJoinOps);
        Assert.False(s.IsFilterOnlyCollectionScan);
    }

    // ---- HasNonCommutingPostJoinOp ----

    [Fact]
    public void HasNonCommutingPostJoinOp_is_false_with_no_post_join_ops()
    {
        var s = new MongoSelectDefinition();
        s.AppendLimit(Const(1)); // a pre-join op

        Assert.False(s.HasNonCommutingPostJoinOp);
    }

    [Fact]
    public void HasNonCommutingPostJoinOp_is_false_for_match_and_sort_which_commute_with_a_later_join()
    {
        var s = new MongoSelectDefinition();
        s.MarkJoinInnerAccessConfirmed();
        s.AddPredicateConjunct(Const(1));
        s.StartOrReplaceSort(Order(1, true));

        Assert.Equal(2, s.PostJoinOps.Count);
        Assert.False(s.HasNonCommutingPostJoinOp);
    }

    [Fact]
    public void HasNonCommutingPostJoinOp_is_true_for_skip_limit_and_distinct()
    {
        var skip = new MongoSelectDefinition();
        skip.MarkJoinInnerAccessConfirmed();
        skip.AppendSkip(Const(1));
        Assert.True(skip.HasNonCommutingPostJoinOp);

        var limit = new MongoSelectDefinition();
        limit.MarkJoinInnerAccessConfirmed();
        limit.AppendLimit(Const(1));
        Assert.True(limit.HasNonCommutingPostJoinOp);

        var distinct = new MongoSelectDefinition();
        distinct.MarkJoinInnerAccessConfirmed();
        distinct.AppendDistinct();
        Assert.True(distinct.HasNonCommutingPostJoinOp);

        var postJoinDistinct = new MongoSelectDefinition();
        postJoinDistinct.AppendPostJoinDistinct(new MongoDistinctOp());
        Assert.True(postJoinDistinct.HasNonCommutingPostJoinOp);
    }

    [Fact]
    public void HasNonCommutingPostJoinOp_is_true_when_any_of_several_post_join_ops_does_not_commute()
    {
        var s = new MongoSelectDefinition();
        s.MarkJoinInnerAccessConfirmed();
        s.AddPredicateConjunct(Const(1));
        s.AppendLimit(Const(1));

        Assert.True(s.HasNonCommutingPostJoinOp);
    }

    // ---- TryFlipTrailingSortDirection ----

    [Fact]
    public void TryFlipTrailingSortDirection_returns_false_without_mutating_when_there_are_no_ops()
    {
        var s = new MongoSelectDefinition();

        Assert.False(s.TryFlipTrailingSortDirection());
        Assert.Empty(s.PipelineOps);
    }

    [Fact]
    public void TryFlipTrailingSortDirection_returns_false_without_mutating_when_the_tail_is_not_a_sort()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Order(1, true));
        s.AppendLimit(Const(1));
        var before = s.PipelineOps.ToList();

        Assert.False(s.TryFlipTrailingSortDirection());
        Assert.Equal(before, s.PipelineOps);
    }

    [Fact]
    public void TryFlipTrailingSortDirection_flips_every_ordering_of_the_tail_sort()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Order(1, true));
        s.AppendThenBy(Order(2, false));

        Assert.True(s.TryFlipTrailingSortDirection());

        var sort = Assert.IsType<MongoSortOp>(Assert.Single(s.PipelineOps));
        Assert.Equal([false, true], sort.Orderings.Select(o => o.Ascending));
        Assert.Equal([1, 2], sort.Orderings.Select(o => ((MongoConstantExpression)o.KeySelector).Value));
    }

    [Fact]
    public void TryFlipTrailingSortDirection_flips_only_the_tail_sort_and_twice_restores_it()
    {
        var s = new MongoSelectDefinition();
        s.StartOrReplaceSort(Order(1, true));
        s.AppendSkip(Const(1));
        s.StartOrReplaceSort(Order(2, true));

        Assert.True(s.TryFlipTrailingSortDirection());
        Assert.Equal([true], ((MongoSortOp)s.PipelineOps[0]).Orderings.Select(o => o.Ascending));
        Assert.Equal([false], ((MongoSortOp)s.PipelineOps[2]).Orderings.Select(o => o.Ascending));

        Assert.True(s.TryFlipTrailingSortDirection());
        Assert.Equal([true], ((MongoSortOp)s.PipelineOps[2]).Orderings.Select(o => o.Ascending));
    }
}
