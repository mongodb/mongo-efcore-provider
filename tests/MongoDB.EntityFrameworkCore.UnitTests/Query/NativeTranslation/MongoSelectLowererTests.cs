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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
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
        public int A { get; set; }
    }

    private static MongoQueryExpression TestSelect()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>();
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType);
    }

    // A query over the same StubEntity, but with a property mapped onto the FIRST synthetic sort field
    // name the allocator would otherwise hand out — EF-401 slice B collision-guard fixture.
    private static MongoQueryExpression TestSelectWithReservedElementName()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>(
            mb => mb.Entity<StubEntity>().Property(x => x.Name).HasElementName("__sort0"));
        var entityType = db.Model.GetEntityTypes().First();
        return new MongoQueryExpression(entityType);
    }

    // ── Fix round 1, Minor 1 / IMPORTANT: fixtures pinning the OTHER two arms of TopLevelElementNames ──

    private class OwnedThing
    {
        public int Value { get; set; }
    }

    private class StubEntityWithOwned
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public OwnedThing Owned { get; set; } = new();
    }

    // A query whose entity type has an OWNED navigation whose containing element name is the FIRST
    // synthetic sort field name the allocator would otherwise hand out — pins the EMBEDDED-NAVIGATION arm
    // of TopLevelElementNames specifically (distinct from the scalar-property arm the fixture above pins).
    private static MongoQueryExpression TestSelectWithReservedEmbeddedElementName()
    {
        using var db = SingleEntityDbContext.Create<StubEntityWithOwned>(
            mb => mb.Entity<StubEntityWithOwned>().OwnsOne(x => x.Owned).HasElementName("__sort0"));
        var entityType = db.Model.FindEntityType(typeof(StubEntityWithOwned))!;
        return new MongoQueryExpression(entityType);
    }

    private class ComplexThing
    {
        public int Value { get; set; }
    }

    private class StubEntityWithComplex
    {
        public ObjectId Id { get; set; }
        public int A { get; set; }
        public ComplexThing Complex { get; set; } = new();
    }

    // A query whose entity type has a COMPLEX property renamed to the FIRST synthetic sort field name the
    // allocator would otherwise hand out — pins the COMPLEX-PROPERTY arm of TopLevelElementNames (the
    // IMPORTANT fix-round-1 finding: GetProperties() does not see a ComplexProperty's own top-level
    // document slot, mirroring the precedent at IsWholeElementRepresentable's third guard arm).
    private static MongoQueryExpression TestSelectWithReservedComplexElementName()
    {
        using var db = SingleEntityDbContext.Create<StubEntityWithComplex>(mb =>
            mb.Entity<StubEntityWithComplex>().ComplexProperty(x => x.Complex)
                .Metadata.SetAnnotation(MongoAnnotationNames.ElementName, "__sort0"));
        var entityType = db.Model.FindEntityType(typeof(StubEntityWithComplex))!;
        return new MongoQueryExpression(entityType);
    }

    // Resolves a real mapped property of the given query's entity type into a MongoFieldExpression —
    // EF-401 slice B: a genuine field key, as opposed to the MongoConstantExpression placeholder several
    // pre-existing tests in this file use.
    private static MongoFieldExpression FieldRef(MongoQueryExpression query, string name)
    {
        var property = query.CollectionExpression.EntityType.FindProperty(name)!;
        return new MongoFieldExpression(property, property.GetElementName());
    }

    private static MongoBinaryExpression Sum() => new(
        MongoBinaryOperator.Add,
        new MongoElementRefExpression("A", typeof(int)),
        new MongoElementRefExpression("B", typeof(int)));

    private static MongoBinaryExpression Product() => new(
        MongoBinaryOperator.Multiply,
        new MongoElementRefExpression("A", typeof(int)),
        new MongoElementRefExpression("B", typeof(int)));

    private static MongoQueryExpression BuildComputedSortQuery()
    {
        var query = TestSelect();
        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));
        return query;
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
        select.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(select, "A"), true));
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
        select.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(select, "A"), true));
        select.Select.AppendThenBy(new MongoOrdering(FieldRef(select, "Name"), false));

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
        var keyExpr = FieldRef(select, "A");
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
        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "customers", query.CollectionExpression.EntityType);
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
        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Union, operand, "customers", query.CollectionExpression.EntityType);
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
        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Concat, operand, "customers", query.CollectionExpression.EntityType);
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),
            s => Assert.IsType<MongoUnionWithStage>(s));
    }

    // ── EF-397: a collection-Include $lookup on a SET-OP query is emitted AFTER the set-op stage ──
    //
    // The stage-order fact the feature rests on, pinned here in isolation from the end-to-end tests in
    // NativeSetOpsTests. Emitted at the ordinary step-2 position it would precede the $unionWith and so join
    // only source1's rows — the operand pipeline nested inside the $unionWith lowers from
    // OperandSelect.PipelineOps alone and never carries a lookup.

    [Fact]
    public void SetOp_defers_the_collection_lookup_until_after_the_union_stage()
    {
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation); // collection nav, no ForceUnwind => IsNativeCollectionLookup
        query.AddLookup(lookup);
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "others", query.CollectionExpression.EntityType);
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoMatchStage>(s),                                    // source1's own ops
            s => Assert.True(Assert.IsType<MongoUnionWithStage>(s).Dedup),             // the combine + dedup
            s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup));      // then, and only then, the join
    }

    [Fact]
    public void SetOp_emits_the_deferred_lookup_after_trailing_ops_and_before_the_projection()
    {
        // The deferred block keeps the SAME relative slot the non-set-op path gives it: ops -> $lookup ->
        // $project. A trailing Take must therefore page the combined stream BEFORE the join runs (so only
        // surviving rows are joined), and the $project must still see the joined array.
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation);
        query.AddLookup(lookup);
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Concat, new MongoSelectDefinition(), "others", query.CollectionExpression.EntityType);
        query.Select.IsSetOp = true;
        query.Select.AppendLimit(new MongoConstantExpression(2, null)); // lands in TrailingOps (post-set-op)
        query.Select.AddProjection(new MongoProjection("Name", new MongoFieldExpression(property: null!, elementName: "Name")));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoUnionWithStage>(s),
            s => Assert.IsType<MongoLimitStage>(s),
            s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup),
            s => Assert.IsType<MongoProjectStage>(s));
    }

    [Fact]
    public void Non_set_op_lookup_position_is_unchanged()
    {
        // The complement of the two tests above: with no set op the lookup still lands at step 2, right
        // after the ops and before the projection. Pins that the deferral is scoped to set-op queries only.
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation);
        query.AddLookup(lookup);
        query.Select.AppendLimit(new MongoConstantExpression(2, null));
        query.Select.AddProjection(new MongoProjection("Name", new MongoFieldExpression(property: null!, elementName: "Name")));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.IsType<MongoLimitStage>(s),
            s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup),
            s => Assert.IsType<MongoProjectStage>(s));
    }

    // ── Test 15: Owned-collection SelectMany unwind lowers to $unwind then $project (EF-347 slice 3) ──

    [Fact]
    public void UnwindSource_lowers_to_unwind_then_project_stage_in_order()
    {
        var query = TestSelect();
        query.Select.AddUnwindSource(MongoUnwindSource.Owned("Items", innerEntityType: null!));
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
        query.Select.AddUnwindSource(unwind);

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
        query.Select.AddUnwindSource(MongoUnwindSource.Reference(
            LookupExpression.GetLookupAlias(navigation), navigation.TargetEntityType, lookup));
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
        query.Select.AddUnwindSource(unwind);

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

    // Task 4: filtered-inner reference SelectMany (o.Refs.Where(r => r.Total > 100)). Mirrors the
    // WholeElement_Reference_UnwindSource test immediately above, plus a Filter set on the unwind — the
    // lowerer must emit a $match for it after the reference $unwind (already appended by AppendLookupStages)
    // and before the $replaceRoot.
    [Fact]
    public void Reference_unwind_with_filter_emits_match_after_unwind_before_terminal()
    {
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation, forceUnwind: true);
        query.AddLookup(lookup);
        var unwind = MongoUnwindSource.Reference(
            LookupExpression.GetLookupAlias(navigation), navigation.TargetEntityType, lookup);
        unwind.WholeElement = true;
        unwind.Filter = new MongoBinaryExpression(
            MongoBinaryOperator.GreaterThan,
            new MongoFieldExpression(property: null!, elementName: "_lookup_Children.Total"),
            new MongoConstantExpression(100, forSerialization: null));
        query.Select.AddUnwindSource(unwind);

        var stages = new MongoSelectLowerer().Lower(query).ToList();

        var unwindIndex = stages.FindIndex(s => s is MongoUnwindStage);
        var matchIndex = stages.FindIndex(s => s is MongoMatchStage);
        var replaceRootIndex = stages.FindIndex(s => s is MongoReplaceRootStage);
        Assert.True(unwindIndex >= 0 && matchIndex > unwindIndex, "filter $match must follow the reference $unwind");
        Assert.True(replaceRootIndex > matchIndex, "filter $match must precede the $replaceRoot");
    }

    // EF-449: a reference-collection-nav First/FirstOrDefault projection leaf tags its LookupExpression
    // with PipelineKind == CorrelatedReducer (the $lookup's own sub-pipeline already narrows to 0-or-1
    // matches). AppendLookupStages must emit $lookup + a LEFT-OUTER $unwind for it, rather than falling
    // through to the final else's NativeTranslationNotSupportedException.
    [Fact]
    public void AppendLookupStages_emits_lookup_and_left_outer_unwind_for_CorrelatedReducer()
    {
        var (query, navigation) = TestReferenceSelect();
        var lookup = new LookupExpression(navigation) { PipelineKind = LookupPipelineKind.CorrelatedReducer };
        lookup.PipelineStages.Add(new BsonDocument("$limit", 1));
        query.AddLookup(lookup);

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.Collection(stages,
            s => Assert.Same(lookup, Assert.IsType<MongoLookupStage>(s).Lookup),
            s =>
            {
                var unwind = Assert.IsType<MongoUnwindStage>(s);
                Assert.Same(lookup, unwind.Lookup);
                Assert.True(unwind.PreserveNullAndEmptyArrays);
            });
    }

    // EF-347 filtered-inner OWNED SelectMany. Mirrors the reference filter test above but for an owned
    // $unwind: the lowerer's Filter $match block is kind-agnostic, so it must emit the $match after the
    // owned $unwind and before the $project (projected form) / $replaceRoot (whole-element form) with NO
    // production change — proving the owned reuse claim.
    [Fact]
    public void Owned_UnwindSource_with_filter_lowers_match_after_unwind_before_project()
    {
        var query = TestSelect();
        var unwind = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        unwind.Filter = new MongoConstantExpression(true, null);
        query.Select.AddUnwindSource(unwind);
        query.Select.AddProjection(new MongoProjection("X", new MongoConstantExpression(1, null)));

        var stages = new MongoSelectLowerer().Lower(query).ToList();

        var unwindIndex = stages.FindIndex(s => s is MongoUnwindFieldStage);
        var matchIndex = stages.FindIndex(s => s is MongoMatchStage);
        var projectIndex = stages.FindIndex(s => s is MongoProjectStage);
        Assert.True(unwindIndex >= 0 && matchIndex > unwindIndex, "filter $match must follow the owned $unwind");
        Assert.True(projectIndex > matchIndex, "filter $match must precede the $project");
    }

    [Fact]
    public void Owned_WholeElement_UnwindSource_with_filter_lowers_match_after_unwind_before_replaceRoot()
    {
        var query = TestSelect();
        var unwind = MongoUnwindSource.Owned("Items", innerEntityType: null!);
        unwind.WholeElement = true;
        unwind.Filter = new MongoConstantExpression(true, null);
        query.Select.AddUnwindSource(unwind);

        var stages = new MongoSelectLowerer().Lower(query).ToList();

        var unwindIndex = stages.FindIndex(s => s is MongoUnwindFieldStage);
        var matchIndex = stages.FindIndex(s => s is MongoMatchStage);
        var replaceRootIndex = stages.FindIndex(s => s is MongoReplaceRootStage);
        Assert.True(unwindIndex >= 0 && matchIndex > unwindIndex, "filter $match must follow the owned $unwind");
        Assert.True(replaceRootIndex > matchIndex, "filter $match must precede the $replaceRoot");
    }

    // ── EF-347 slice B: trailing ops emit AFTER the set-op stage; cardinality falls through ──

    [Fact]
    public void Trailing_ops_lower_after_the_set_op_stage()
    {
        var query = TestSelect();
        // source1's own pre-set-op predicate:
        query.Select.AddPredicateConjunct(new MongoConstantExpression(true, null));
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "customers", query.CollectionExpression.EntityType);
        query.Select.IsSetOp = true;
        // post-set-op (trailing) sort — routes to TrailingOps because SetOperation is now attached:
        query.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(query, "A"), true));

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
            MongoSetOperationKind.Intersect, new MongoSelectDefinition(), "customers", query.CollectionExpression.EntityType);
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

    // ── EF-401 (stream 1, slice B): a computed sort key lowers to $set → $sort → $unset ─────────

    [Fact]
    public void Computed_sort_key_lowers_to_set_sort_unset()
    {
        var query = TestSelect();
        var sum = new MongoBinaryExpression(
            MongoBinaryOperator.Add,
            new MongoElementRefExpression("A", typeof(int)),
            new MongoElementRefExpression("B", typeof(int)));
        query.Select.StartOrReplaceSort(new MongoOrdering(sum, Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        var addFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
        var sort = Assert.IsType<MongoSortStage>(stages[1]);
        var unset = Assert.IsType<MongoUnsetStage>(stages[2]);

        var synthetic = Assert.Single(addFields.Fields).Alias;
        Assert.Same(sum, Assert.Single(addFields.Fields).Expression);
        Assert.Equal(synthetic, Assert.IsType<MongoElementRefExpression>(Assert.Single(sort.Orderings).KeySelector).Path);
        Assert.Equal(synthetic, Assert.Single(unset.FieldNames));
        Assert.StartsWith("__sort", synthetic);
    }

    [Fact]
    public void A_sort_with_only_field_keys_emits_a_bare_sort_and_no_set()
    {
        // LOAD-BEARING, not tidiness. MEASURED (spike §6.2): a $set in front of a $sort disqualifies
        // index-backed sorting EVEN WHEN every sort key is a plain indexed field path — {$sort:{A:1}} is
        // IXSCAN A_1, and the identical sort preceded by an unrelated $set is a COLLSCAN.
        var query = TestSelect();
        query.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(query, "A"), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.IsType<MongoSortStage>(Assert.Single(stages));
        Assert.DoesNotContain(stages, s => s is MongoAddFieldsStage or MongoUnsetStage);
    }

    [Fact]
    public void A_mixed_sort_computes_only_the_computed_key_and_leaves_the_field_key_a_plain_path()
    {
        var query = TestSelect();
        var product = new MongoBinaryExpression(
            MongoBinaryOperator.Multiply,
            new MongoElementRefExpression("A", typeof(int)),
            new MongoElementRefExpression("B", typeof(int)));
        query.Select.StartOrReplaceSort(new MongoOrdering(FieldRef(query, "A"), Ascending: true));
        query.Select.AppendThenBy(new MongoOrdering(product, Ascending: false));

        var stages = new MongoSelectLowerer().Lower(query);

        var addFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
        var sort = Assert.IsType<MongoSortStage>(stages[1]);
        Assert.Single(addFields.Fields);                                   // only the computed key is materialized
        Assert.Equal(2, sort.Orderings.Count);
        Assert.IsType<MongoFieldExpression>(sort.Orderings[0].KeySelector);  // the field key stays a plain path
        Assert.IsType<MongoElementRefExpression>(sort.Orderings[1].KeySelector);
        Assert.False(sort.Orderings[1].Ascending);                          // direction is preserved per ordering
    }

    [Fact]
    public void Two_computed_keys_in_one_sort_get_distinct_names_and_one_set_and_one_unset()
    {
        var query = TestSelect();
        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));
        query.Select.AppendThenBy(new MongoOrdering(Product(), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        var addFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
        Assert.Equal(2, addFields.Fields.Count);
        Assert.Equal(2, addFields.Fields.Select(f => f.Alias).Distinct().Count());
        Assert.IsType<MongoSortStage>(stages[1]);
        Assert.Equal(2, Assert.IsType<MongoUnsetStage>(stages[2]).FieldNames.Count);
        Assert.Equal(3, stages.Count);   // ONE $set and ONE $unset per sort stage, not one per ordering
    }

    [Fact]
    public void Synthetic_names_are_stable_across_repeated_lowering_of_the_same_query()
    {
        // The prototype used a process-global counter and emitted __sort3 on one spec case and __sort4 on its
        // async twin (MEASURED, spike §2.1) — which would make every committed AssertMql baseline unstable.
        var first = new MongoSelectLowerer().Lower(BuildComputedSortQuery());
        var second = new MongoSelectLowerer().Lower(BuildComputedSortQuery());

        Assert.Equal(
            Assert.IsType<MongoAddFieldsStage>(first[0]).Fields[0].Alias,
            Assert.IsType<MongoAddFieldsStage>(second[0]).Fields[0].Alias);

        // Minor 4 (fix round 1): the assertion above uses two DIFFERENT lowerer instances, so it would also
        // pass for a counter promoted to an INSTANCE field on MongoSelectLowerer — it only pins per-run
        // stability, not per-INVOCATION allocation. Calling Lower TWICE on the SAME instance pins the
        // stronger, actually-intended property: the allocator is rebuilt fresh every Lower call.
        var lowerer = new MongoSelectLowerer();
        var third = lowerer.Lower(BuildComputedSortQuery());
        var fourth = lowerer.Lower(BuildComputedSortQuery());

        Assert.Equal(
            Assert.IsType<MongoAddFieldsStage>(third[0]).Fields[0].Alias,
            Assert.IsType<MongoAddFieldsStage>(fourth[0]).Fields[0].Alias);
    }

    [Fact]
    public void A_synthetic_name_colliding_with_a_mapped_element_name_is_skipped()
    {
        // $set OVERWRITES a same-named field silently — the same hazard IsWholeElementRepresentable's
        // sentinel-collision guard exists for on the owned bare-element path ($mergeObjects).
        var query = TestSelectWithReservedElementName();   // maps a property to element name "__sort0"

        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.NotEqual("__sort0", Assert.Single(Assert.IsType<MongoAddFieldsStage>(stages[0]).Fields).Alias);
    }

    [Fact]
    public void A_synthetic_name_colliding_with_an_owned_navigations_containing_element_name_is_skipped()
    {
        // Minor 1 (fix round 1): pins the EMBEDDED-NAVIGATION arm of TopLevelElementNames specifically.
        // The sibling scalar-property collision test's own mutation (ignore the WHOLE reserved set) does
        // not discriminate WHICH arm actually populates it — deleting only the navigation loop leaves this
        // test red while the scalar-property test stays green, and vice versa.
        var query = TestSelectWithReservedEmbeddedElementName();   // owned nav's containing element name is "__sort0"

        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.NotEqual("__sort0", Assert.Single(Assert.IsType<MongoAddFieldsStage>(stages[0]).Fields).Alias);
    }

    [Fact]
    public void A_synthetic_name_colliding_with_a_complex_propertys_element_name_is_skipped()
    {
        // IMPORTANT (fix round 1): TopLevelElementNames originally omitted complex properties entirely —
        // IEntityType.GetProperties() does not see a ComplexProperty's own top-level document slot, the
        // same hazard IsWholeElementRepresentable's third guard arm
        // (MongoQueryableMethodTranslatingExpressionVisitor) exists for on the owned $mergeObjects path.
        // Not reachable today (the populator declines every computed key), but becomes reachable the moment
        // Task 3 lands.
        var query = TestSelectWithReservedComplexElementName();   // complex property's element name is "__sort0"

        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.NotEqual("__sort0", Assert.Single(Assert.IsType<MongoAddFieldsStage>(stages[0]).Fields).Alias);
    }

    // ── EF-408: the two gaps the collision guard used to carry as "accepted but unverified" ──────────

    private class StubEntityDerived : StubEntity
    {
        public int Special { get; set; }
    }

    // A query over the TPH BASE type whose DERIVED sibling maps a property onto the first synthetic sort
    // field name. Every TPH type shares one collection and one top-level document namespace, but
    // IEntityType.GetProperties() on the base never returns a derived type's own declared members — so the
    // reserved set has to walk GetDerivedTypesInclusive() (EF-408 gap 2).
    private static MongoQueryExpression TestSelectWithReservedDerivedElementName()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>(
            mb => mb.Entity<StubEntityDerived>().Property(x => x.Special).HasElementName("__sort0"));
        var entityType = db.Model.FindEntityType(typeof(StubEntity))!;
        return new MongoQueryExpression(entityType);
    }

    private class OtherStubEntity
    {
        public ObjectId Id { get; set; }
        public string Other { get; set; } = "";
    }

    // An outer query whose OWN entity type reserves nothing, paired with a set-op operand entity type that
    // maps a property onto the first synthetic sort field name (EF-408 gap 1). A projected-operand set op
    // does not require the operands to share an entity type, and the operand's ops lower through the SAME
    // allocator into the nested $unionWith pipeline.
    private static (MongoQueryExpression Query, IEntityType OperandEntityType) TestSelectWithReservedOperandElementName()
    {
        using var db = SingleEntityDbContext.Create<StubEntity>(
            mb => mb.Entity<OtherStubEntity>().Property(x => x.Other).HasElementName("__sort0"));
        return (new MongoQueryExpression(db.Model.FindEntityType(typeof(StubEntity))!),
            db.Model.FindEntityType(typeof(OtherStubEntity))!);
    }

    [Fact]
    public void A_synthetic_name_colliding_with_a_TPH_derived_types_element_name_is_skipped()
    {
        // EF-408 gap 2, MEASURED reachable end-to-end (NativeComputedSortTests
        // .Synthetic_sort_field_does_not_clobber_a_TPH_derived_types_own_element): the $set clobbered the
        // derived type's real element and the trailing $unset then removed it from the document entirely,
        // so the derived row failed to materialize under the default Native mode.
        var query = TestSelectWithReservedDerivedElementName();

        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        Assert.NotEqual("__sort0", Assert.Single(Assert.IsType<MongoAddFieldsStage>(stages[0]).Fields).Alias);
    }

    [Fact]
    public void A_synthetic_name_colliding_with_a_set_op_operands_own_element_name_is_skipped()
    {
        // EF-408 gap 1, MEASURED reachable end-to-end (NativeComputedSortTests
        // .Synthetic_sort_field_does_not_clobber_a_set_op_operands_own_element). The OUTER type reserves
        // nothing here, so only reading the OPERAND's entity type can keep this allocation off "__sort0".
        var (query, operandEntityType) = TestSelectWithReservedOperandElementName();

        var operand = new MongoSelectDefinition();
        operand.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));
        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, operand, "others", operandEntityType);
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        var union = Assert.IsType<MongoUnionWithStage>(Assert.Single(stages));
        Assert.NotEqual("__sort0",
            Assert.Single(Assert.IsType<MongoAddFieldsStage>(union.OperandStages[0]).Fields).Alias);
    }

    // ── Minor 3 (fix round 1): the allocator must be SHARED across all three AppendSelectOpStages call
    // sites — a regression passing a fresh allocator at either the set-op operand's PipelineOps or the
    // post-set-op TrailingOps would produce a DUPLICATE "__sort0" that no existing test would catch (all
    // six pre-round-1 slice-B tests drive only the outer query's own PipelineOps). ─────────────────────

    [Fact]
    public void Computed_sort_in_a_set_op_operand_gets_a_distinct_synthetic_name_from_the_outer_querys()
    {
        var query = TestSelect();
        // Outer query's own PipelineOps (recorded BEFORE the set op is attached):
        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

        // The operand's own select, populated independently of the outer query:
        var operand = new MongoSelectDefinition();
        operand.StartOrReplaceSort(new MongoOrdering(Product(), Ascending: true));

        query.Select.SetOperation = new MongoSetOperation(MongoSetOperationKind.Union, operand, "customers", query.CollectionExpression.EntityType);
        query.Select.IsSetOp = true;

        var stages = new MongoSelectLowerer().Lower(query);

        // Outer: $set, $sort, $unset, then $unionWith (whose own OperandStages are $set, $sort, $unset).
        var outerAddFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
        var union = Assert.IsType<MongoUnionWithStage>(stages[3]);
        var operandAddFields = Assert.IsType<MongoAddFieldsStage>(union.OperandStages[0]);

        // The $unset must be INSIDE the operand's nested pipeline, and this is the one test that reaches a
        // set-op operand at all — so it is the only place that can pin it. The $unset exists specifically for
        // set-op hygiene: a synthetic field left on the operand's documents would fold into Union's own
        // $group{_id:"$$ROOT"} dedup key and change set semantics.
        Assert.IsType<MongoUnsetStage>(union.OperandStages[2]);

        var outerName = Assert.Single(outerAddFields.Fields).Alias;
        var operandName = Assert.Single(operandAddFields.Fields).Alias;
        Assert.NotEqual(outerName, operandName);
    }

    [Fact]
    public void Computed_sort_in_TrailingOps_gets_a_distinct_synthetic_name_from_the_outer_querys()
    {
        var query = TestSelect();
        // Outer query's own PipelineOps (recorded BEFORE the set op is attached):
        query.Select.StartOrReplaceSort(new MongoOrdering(Sum(), Ascending: true));

        query.Select.SetOperation = new MongoSetOperation(
            MongoSetOperationKind.Union, new MongoSelectDefinition(), "customers", query.CollectionExpression.EntityType);
        query.Select.IsSetOp = true;

        // Post-set-op (trailing) sort — routes to TrailingOps because SetOperation is now attached:
        query.Select.StartOrReplaceSort(new MongoOrdering(Product(), Ascending: true));

        var stages = new MongoSelectLowerer().Lower(query);

        // $set/$sort/$unset (outer) → $unionWith → $set/$sort/$unset (trailing).
        var outerAddFields = Assert.IsType<MongoAddFieldsStage>(stages[0]);
        Assert.IsType<MongoUnionWithStage>(stages[3]);
        var trailingAddFields = Assert.IsType<MongoAddFieldsStage>(stages[4]);

        var outerName = Assert.Single(outerAddFields.Fields).Alias;
        var trailingName = Assert.Single(trailingAddFields.Fields).Alias;
        Assert.NotEqual(outerName, trailingName);
    }
}
