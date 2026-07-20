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
}
