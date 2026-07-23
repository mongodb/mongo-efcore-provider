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

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.UnitTests.Query.NativeTranslation;

/// <summary>
/// Tests for <see cref="MongoSelectLowerer"/>, which turns the native-translation slots
/// on <see cref="MongoQueryExpression"/> into a typed <see cref="MongoPipelineStage"/> list
/// in canonical pipeline order ($match → $sort → $skip → $limit → $lookup/$unwind).
/// </summary>
public class MongoSelectLowererTests
{
    private class StubEntity
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
    }

    private static MongoQueryExpression TestSelect()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>();
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType);
    }

    // ── Reference-collection fixture (EF-347 slice 5, Task 3) ───────────────────
    // A genuine cross-collection reference nav (FK-based HasMany/WithOne), distinct from the owned
    // (embedded) Items fixture Test 15 uses — needed to build a ForceUnwind-collection LookupExpression.

    private class ReferenceChild
    {
        public ObjectId Id { get; set; }
        public ObjectId ParentId { get; set; }
        public decimal Total { get; set; }
    }

    private class ReferenceParent
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; } = "";
        public List<ReferenceChild> Children { get; set; } = [];
    }

    private static (MongoQueryExpression Query, INavigation Navigation) TestReferenceSelect()
    {
        using var db = SingleEntityDbContext.Create<ReferenceParent>(mb =>
        {
            mb.Entity<ReferenceChild>();
            mb.Entity<ReferenceParent>().HasMany(p => p.Children).WithOne().HasForeignKey(c => c.ParentId);
        });
        var entityType = db.Model.FindEntityType(typeof(ReferenceParent))!;
        var navigation = entityType.FindNavigation(nameof(ReferenceParent.Children))!;
        return (new MongoQueryExpression(entityType), navigation);
    }

    // ── Test 1: Empty slots → no stages ─────────────────────────────────────────

    [Fact]
    public void Empty_slots_lower_to_no_stages()
    {
        var select = TestSelect();
        var stages = new MongoSelectLowerer().Lower(select);
        Assert.Empty(stages);
    }

    // ── Test 2: All slots populated → canonical order ────────────────────────────

    [Fact]
    public void Predicate_ordering_offset_limit_lower_in_canonical_order()
    {
        var select = TestSelect();
        select.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        select.Select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, null), true));
        select.Select.AppendSkip(new MongoConstantExpression(5, null));
        select.Select.AppendLimit(new MongoConstantExpression(10, null));

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Equal(4, stages.Count);
        Assert.IsType<MongoMatchStage>(stages[0]);
        Assert.IsType<MongoSortStage>(stages[1]);
        Assert.IsType<MongoSkipStage>(stages[2]);
        Assert.IsType<MongoLimitStage>(stages[3]);
    }

    // ── Test 3: Only a predicate → exactly one MongoMatchStage ──────────────────

    [Fact]
    public void Only_predicate_lowers_to_single_match_stage()
    {
        var select = TestSelect();
        select.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        Assert.IsType<MongoMatchStage>(stages[0]);
    }

    // ── Test 4: Match stage carries the predicate expression ────────────────────

    [Fact]
    public void Match_stage_carries_the_predicate_expression()
    {
        var select = TestSelect();
        var predicate = new MongoConstantExpression(42, null);
        select.Select.AddPredicateConjunct(predicate);

        var stages = new MongoSelectLowerer().Lower(select);

        var matchStage = Assert.IsType<MongoMatchStage>(stages[0]);
        Assert.Same(predicate, matchStage.Predicate);
    }

    // ── Test 5: Only orderings → exactly one MongoSortStage ─────────────────────

    [Fact]
    public void Only_orderings_lower_to_single_sort_stage()
    {
        var select = TestSelect();
        select.Select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, null), true));
        select.Select.AppendThenBy(new MongoOrdering(new MongoConstantExpression(1, null), false));

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        var sortStage = Assert.IsType<MongoSortStage>(stages[0]);
        Assert.Equal(2, sortStage.Orderings.Count);
    }

    // ── Test 6: Only Offset → exactly one MongoSkipStage ────────────────────────

    [Fact]
    public void Only_offset_lowers_to_single_skip_stage()
    {
        var select = TestSelect();
        var offset = new MongoConstantExpression(10, null);
        select.Select.AppendSkip(offset);

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        var skipStage = Assert.IsType<MongoSkipStage>(stages[0]);
        Assert.Same(offset, skipStage.Offset);
    }

    // ── Test 7: Only Limit → exactly one MongoLimitStage ────────────────────────

    [Fact]
    public void Only_limit_lowers_to_single_limit_stage()
    {
        var select = TestSelect();
        var limit = new MongoConstantExpression(5, null);
        select.Select.AppendLimit(limit);

        var stages = new MongoSelectLowerer().Lower(select);

        Assert.Single(stages);
        var limitStage = Assert.IsType<MongoLimitStage>(stages[0]);
        Assert.Same(limit, limitStage.Limit);
    }

    // ── Test 8: Sort stage carries orderings from the slot ───────────────────────

    [Fact]
    public void Sort_stage_carries_orderings_from_the_slot()
    {
        var select = TestSelect();
        var keyExpr = new MongoConstantExpression(0, null);
        select.Select.StartOrReplaceSort(new MongoOrdering(keyExpr, Ascending: true));

        var stages = new MongoSelectLowerer().Lower(select);

        var sortStage = Assert.IsType<MongoSortStage>(stages[0]);
        var ordering = Assert.Single(sortStage.Orderings);
        Assert.Same(keyExpr, ordering.KeySelector);
        Assert.True(ordering.Ascending);
    }

    // ── Test 9: Projection lowers to a project stage last ──────────────────────────

    [Fact]
    public void Projection_lowers_to_a_project_stage_last()
    {
        var query = TestSelect();
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.AddProjection(new MongoProjection("Name", new MongoConstantExpression(0, null)));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.IsType<MongoMatchStage>(stages[0]);
        var project = Assert.IsType<MongoProjectStage>(stages[^1]);
        Assert.Single(project.Projections);
        Assert.Equal("Name", project.Projections[0].Alias);
    }

    [Fact]
    public void No_projection_produces_no_project_stage()
    {
        var query = TestSelect();
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.DoesNotContain(stages, s => s is MongoProjectStage);
    }

    // ── Test 11: Grouping lowers to a $match then $group ────────────────────────

    [Fact]
    public void Lowers_grouping_to_group_stage_after_match()
    {
        var query = TestSelect();
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.Grouping = new MongoGrouping(
            new[] { new MongoGroupingKeyPart(null, new MongoFieldExpression(property: null!, elementName: "country")) },
            new[] { new MongoGroupAccumulator("Count", "$sum", null) });

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),
            s => Assert.IsType<MongoGroupStage>(s));
    }

    // ── Test 12: Set-op select lowers to a single MongoUnionWithStage ───────────

    [Fact]
    public void SetOperation_appends_union_stage_after_canonical_stages()
    {
        var operand = new MongoSelectDefinition(); // empty operand → no inner stages
        var query = TestSelect();
        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "customers");
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        var union = Assert.IsType<MongoUnionWithStage>(Assert.Single(stages));
        Assert.Equal("customers", union.OperandCollectionName);
        Assert.False(union.Dedup);
        Assert.Empty(union.OperandStages);
    }

    // ── Test 13: Union sets Dedup and lowers the operand predicate to a $match ──

    [Fact]
    public void Union_sets_dedup_and_lowers_operand_predicate()
    {
        var operand = new MongoSelectDefinition();
        operand.AddPredicateConjunct(new MongoBinaryExpression(
            MongoBinaryOperator.Equal,
            new MongoFieldExpression(property: null!, elementName: "country"),
            new MongoConstantExpression("UK", null)));

        var query = TestSelect();
        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Union, operand, "customers");
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        var union = Assert.IsType<MongoUnionWithStage>(Assert.Single(stages));
        Assert.True(union.Dedup);
        Assert.IsType<MongoMatchStage>(Assert.Single(union.OperandStages));
    }

    // ── Test 14: Outer $match precedes the union stage ───────────────────────────

    [Fact]
    public void Outer_where_precedes_the_union_stage()
    {
        var operand = new MongoSelectDefinition();
        var query = TestSelect();
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "customers");
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),
            s => Assert.IsType<MongoUnionWithStage>(s));
    }

    // ── Test 15: Owned-collection SelectMany unwind lowers to $unwind then $project (EF-347 slice 3) ──

    [Fact]
    public void UnwindSource_lowers_to_unwind_then_project_stage_in_order()
    {
        var query = TestSelect();
        query.Select.UnwindSource = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        query.Select.AddProjection(new MongoProjection("Name", new MongoFieldExpression(property: null!, elementName: "Name")));
        query.Select.AddProjection(new MongoProjection("Price", new MongoFieldExpression(property: null!, elementName: "Items.Price")));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.Equal("Items", Assert.IsType<MongoUnwindFieldStage>(s).ElementPath),
            s =>
            {
                var project = Assert.IsType<MongoProjectStage>(s);
                Assert.Equal(2, project.Projections.Count);
                Assert.Equal("Name", project.Projections[0].Alias);
                Assert.Equal("Price", project.Projections[1].Alias);
            });
    }

    // ── Owned whole-element SelectMany lowers to $unwind then $replaceRoot (EF-347 bare-owned) ──
    // The naive $replaceRoot alone is insufficient (owned keys are shadow properties not in the
    // document), so WholeElement drives the $unwind to also carry the array ordinal via
    // includeArrayIndex (MongoReplaceRootStage.OrdinalField) for the following $replaceRoot to merge in.

    [Fact]
    public void WholeElement_UnwindSource_lowers_to_unwind_then_replaceRoot_stage_in_order()
    {
        var query = TestSelect();
        var unwind = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        unwind.WholeElement = true;
        query.Select.UnwindSource = unwind;

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s =>
            {
                var unwindField = Assert.IsType<MongoUnwindFieldStage>(s);
                Assert.Equal("Items", unwindField.ElementPath);
                Assert.Equal(MongoReplaceRootStage.OrdinalField, unwindField.IncludeArrayIndex);
            },
            s => Assert.Equal("Items", Assert.IsType<MongoReplaceRootStage>(s).NewRoot));
    }

    // ── Test 16: Reference-collection SelectMany unwind lowers to $lookup → $unwind → $project
    // (EF-347 slice 5, Task 3). Distinct from Test 15 (Owned): AppendLookupStages (stage 5) already
    // appends the $lookup+$unwind for a Reference UnwindSource BEFORE the UnwindSource block runs,
    // so no MongoUnwindFieldStage should ever appear for this Kind.

    [Fact]
    public void Reference_UnwindSource_lowers_to_lookup_then_unwind_then_project_stage_in_order()
    {
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation, forceUnwind: true);
        query.AddLookup(lookup);
        query.Select.UnwindSource = MongoUnwindSource.Reference(
            LookupExpression.GetLookupAlias(navigation), navigation.TargetEntityType, lookup);
        query.Select.AddProjection(new MongoProjection("Name", new MongoFieldExpression(property: null!, elementName: "Name")));
        query.Select.AddProjection(new MongoProjection("Total", new MongoFieldExpression(property: null!, elementName: "_lookup_Children.Total")));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup),
            s =>
            {
                var unwind = Assert.IsType<MongoUnwindStage>(s);
                Assert.Same(lookup, unwind.Lookup);
                Assert.False(unwind.PreserveNullAndEmptyArrays);
            },
            s =>
            {
                var project = Assert.IsType<MongoProjectStage>(s);
                Assert.Equal(2, project.Projections.Count);
                Assert.Equal("Name", project.Projections[0].Alias);
                Assert.Equal("Total", project.Projections[1].Alias);
            });

        Assert.DoesNotContain(stages, s => s is MongoUnwindFieldStage);
    }

    // Test 17: bare whole reference-ENTITY SelectMany (EF-347 ref-bare-entity slice). Like Test 16,
    // AppendLookupStages emits $lookup + $unwind(preserve:false) first; then WholeElement drives a PLAIN
    // $replaceRoot (no $mergeObjects — a reference entity has a real stored key), and there is NO trailing
    // $project (Projection is empty for a whole-entity result).
    [Fact]
    public void WholeElement_Reference_UnwindSource_lowers_to_lookup_then_unwind_then_plain_replaceRoot()
    {
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation, forceUnwind: true);
        query.AddLookup(lookup);
        var unwind = MongoUnwindSource.Reference(
            LookupExpression.GetLookupAlias(navigation), navigation.TargetEntityType, lookup);
        unwind.WholeElement = true;
        query.Select.UnwindSource = unwind;

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup),
            s =>
            {
                var u = Assert.IsType<MongoUnwindStage>(s);
                Assert.Same(lookup, u.Lookup);
                Assert.False(u.PreserveNullAndEmptyArrays);
            },
            s =>
            {
                var rr = Assert.IsType<MongoReplaceRootStage>(s);
                Assert.Equal(LookupExpression.GetLookupAlias(navigation), rr.NewRoot);
                Assert.False(rr.MergeOwnerKeySentinels);
            });
    }

    // ── EF-347 slice B: trailing ops emit AFTER the set-op stage; cardinality falls through ──

    [Fact]
    public void Trailing_ops_lower_after_the_set_op_stage()
    {
        var query = TestSelect();
        // source1's own pre-set-op predicate:
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "customers");
        query.Select.IsSetOp = true;
        // post-set-op (trailing) sort — routes to TrailingOps because SetOperation is now attached:
        query.Select.StartOrReplaceSort(new MongoOrdering(new MongoConstantExpression(0, null), true));

        var stages = new MongoSelectLowerer().Lower(query);

        // $match (pre-set-op) → $unionWith (set op) → $sort (trailing), in that order.
        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),
            s => Assert.IsType<MongoUnionWithStage>(s),
            s => Assert.IsType<MongoSortStage>(s));
    }

    [Fact]
    public void Trailing_cardinality_lowers_after_the_set_op_stage()
    {
        var query = TestSelect();
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Intersect, new MongoSelectDefinition(), "customers");
        query.Select.IsSetOp = true;
        query.Select.Cardinality = MongoCardinality.ForAggregate(
            MongoAggregateOperator.Count, selector: null, MongoEmptyAggregateBehavior.DefaultValue,
            emptyValue: 0, typeof(int), presenceOnly: false, presentValue: null);

        var stages = new MongoSelectLowerer().Lower(query);

        // set-difference stage (Intersect/Except) → $count. The lowerer must NOT early-return after the
        // set-op stage — it must fall through to the Cardinality block.
        Assert.Collection(stages,
            s => Assert.IsType<MongoSetDifferenceStage>(s),
            s => Assert.IsType<MongoCountStage>(s));
    }
}
