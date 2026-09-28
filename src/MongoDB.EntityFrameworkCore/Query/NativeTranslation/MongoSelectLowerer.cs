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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Converts the native IR on a <see cref="MongoQueryExpression"/> into typed <see cref="MongoPipelineStage"/>s:
/// <see cref="MongoSelectDefinition.PipelineOps"/> in recorded arrival order, then <c>$lookup</c>/<c>$unwind</c>
/// and any terminal stage.
/// </summary>
/// <remarks>
/// BSON-free; rendering belongs to the renderer/factory. Throws <see cref="NativeTranslationNotSupportedException"/>
/// for an unsupported lookup shape, which the compile-time gate turns into a driver-LINQ fallback.
/// </remarks>
internal sealed class MongoSelectLowerer
{
    /// <summary>
    /// Lowers the native IR of <paramref name="query"/> into an ordered list of pipeline stages.
    /// </summary>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// The query contains a join or lookup shape the native pipeline does not support.
    /// </exception>
    public IReadOnlyList<MongoPipelineStage> Lower(MongoQueryExpression query)
    {
        var select = query.Select;
        var stages = new List<MongoPipelineStage>();
        var sortFields = new SyntheticSortFieldAllocator(ReservedElementNames(query));

        // $vectorSearch must be the first stage (server error Location40602 otherwise), so it has a dedicated
        // slot rather than living in PipelineOps. The score $addFields follows it, matching the driver-LINQ MQL.
        if (select.VectorSearch is { } vectorSearch)
        {
            stages.Add(new MongoVectorSearchStage(vectorSearch));
            stages.Add(new MongoVectorSearchScoreStage());
        }

        AppendSelectOpStages(select.PipelineOps, stages, sortFields);

        // Lookups follow the filter/sort/page ops and precede $project, so a projected collection-nav Count's
        // _lookup_<Nav> array exists when $project reads it via $size.
        //
        // A set-op query defers lookups until after the combine (see below): the operand's nested pipeline
        // carries no lookups, so joining here would leave every operand row with an empty joined array.
        if (select.SetOperation == null)
        {
            AppendLookupStages(query, stages);

            // A reference-Include null check confirmed from a bare Where must run after the $lookup/$unwind;
            // see MongoSelectDefinition.PostJoinOps.
            AppendSelectOpStages(select.PostJoinOps, stages, sortFields);
            // Paging deferred past a join whose $unwind may change row count; see
            // MongoSelectDefinition.PostLookupPagingOps. Can't co-occur with a set op: set-op operand gates
            // exclude join queries.
            AppendSelectOpStages(select.PostLookupPagingOps, stages, sortFields);
        }

        // Set-op terminal: $unionWith (+ dedup) or a set-difference shape for Intersect/Except.
        if (select.SetOperation is { } setOp)
        {
            // Projected operands: each operand's own $group (projected Distinct only) and $project run before
            // the combine, so dedup/Intersect/Except compare projected values. A trailing projection over the
            // combined result instead has OperandsProjected false and is emitted by the Projection block below.
            if (setOp.OperandsProjected)
            {
                // source1's own pre-combine lookup (projected collection-nav Count) must precede its $project
                // and must not apply to the other operand's rows, so it's emitted here rather than deferred.
                AppendLookupStages(query, stages);

                // A GroupBy composed after this set op over a Grouping-bearing source1 (projected Distinct or
                // GroupBy.Select(aggregate)) had SnapshotPriorGroupingForNestedGroupBy move source1's own
                // $group/$project into the Prior* slots; Grouping/Projection are then the outer GroupBy's, which
                // must run over the combined rows and are emitted by the Grouping block below.
                if (select.PriorGrouping is { } source1PriorGrouping)
                {
                    AppendPriorGroupingStages(select, source1PriorGrouping, stages, sortFields);
                }
                else
                {
                    if (select.Grouping is { } source1Grouping)
                        stages.Add(new MongoGroupStage(source1Grouping));
                    stages.Add(new MongoProjectStage(select.Projection));

                    // Ops composed on source1's projected Distinct/GroupBy output before the set op
                    // (Distinct().Where(..).Union(..)) belong to source1, ahead of the combine.
                    AppendSelectOpStages(select.PostGroupOps, stages, sortFields);
                }
            }

            AppendSetOpChainStages(select, stages, sortFields);

            // Post-set-op ops act on the combined result; control then falls through to the Projection,
            // Grouping and Cardinality blocks below.
            AppendSelectOpStages(select.TrailingOps, stages, sortFields);

            // Deferred lookups run over the combined stream, after dedup/source tagging: those compare whole
            // documents, so a joined array present then would change the comparison key. Skipped when
            // OperandsProjected (source1's lookup was emitted above). The operand (OperandSelect) has no
            // lookup plumbing, so a right-hand operand with its own lookup is unsupported.
            if (!setOp.OperandsProjected)
            {
                AppendLookupStages(query, stages);
            }
            // No early return: control continues to the blocks below.
        }

        // Terminal native SelectMany, then $project the result selector. Owned: $unwind the embedded array
        // here. Reference: the $lookup + $unwind were already appended by AppendLookupStages.
        if (select.UnwindSource is { } unwind)
        {
            if (unwind.Kind == MongoUnwindSourceKind.Owned)
                stages.Add(new MongoUnwindFieldStage(
                    unwind.InnerScopePath,
                    includeArrayIndex: unwind.WholeElement ? MongoReplaceRootStage.OrdinalField : null));

            // Inner-element filter (o.Refs.Where(pred)): after the $unwind, before $replaceRoot/$project.
            // The binder already scope-prefixed its paths.
            if (unwind.Filter is { } filter)
                stages.Add(new MongoMatchStage(filter));

            if (unwind.WholeElement)
            {
                // Promote the unwound element to root. Owned elements also merge in the owner key and array
                // ordinal as sentinel fields so their shadow key materializes non-null; a reference entity has
                // its own stored key.
                stages.Add(new MongoReplaceRootStage(
                    unwind.InnerScopePath,
                    mergeOwnerKeySentinels: unwind.Kind == MongoUnwindSourceKind.Owned));
                return stages;
            }

            if (select.Projection.Count > 0)
                stages.Add(new MongoProjectStage(select.Projection));
            return stages;
        }

        // A GroupBy nested on a finalized prior grouping (projected Distinct or GroupBy.Select(aggregate)): the
        // prior $group/$project (snapshotted by SnapshotPriorGroupingForNestedGroupBy) must run first, or the
        // two collapse into one and silently aggregate pre-dedup rows. Ops composed between the two GroupBys
        // (PostGroupOps) follow it.
        //
        // Skipped for a projected-operand set op: there the prior grouping is source1's own, already emitted
        // before the set-op stage.
        if (select.PriorGrouping is { } priorGrouping && select.SetOperation is not { OperandsProjected: true })
        {
            AppendPriorGroupingStages(select, priorGrouping, stages, sortFields);
        }

        // Keyed $group terminal, followed by a flattening $project that lifts _id, _id.<Name> sub-keys and
        // accumulator outputs to the top-level aliases the shaper reads. Pre-group PipelineOps are fine ahead
        // of it; see MongoSelectDefinition.HasOrdering.
        //
        // Skipped for a projected-operand set op unless PriorGrouping is set: otherwise that Grouping is source1's
        // own Distinct/GroupBy, already emitted before the set-op stage, and re-emitting would add a spurious
        // $group over the combined result. With PriorGrouping set, Grouping is an outer GroupBy over the combined
        // rows. A GroupBy after a whole-entity Union/Concat (OperandsProjected false) is also emitted here, after
        // TrailingOps.
        if (select.Grouping is { } grouping
            && (select.SetOperation is not { OperandsProjected: true } || select.PriorGrouping != null))
        {
            stages.Add(new MongoGroupStage(grouping));

            // A scalar aggregate directly on a bare GroupBy(key) (no Select) also sets Cardinality; its
            // post-group predicate on the accumulator output emits here, then control falls through to the
            // aggregate-terminal switch below.
            if (select.PostGroupPredicate is { } postGroupPredicate)
            {
                stages.Add(new MongoMatchStage(postGroupPredicate));
            }

            // HAVING: before the group sort (it decides which groups exist) and before the flatten $project
            // (it may reference a field the Select doesn't project).
            if (select.GroupHavingPredicate is { } havingPredicate)
            {
                stages.Add(new MongoMatchStage(havingPredicate));
            }

            // Group ordering must precede the flatten $project, which may drop an ordering aggregate the Select
            // doesn't project.
            if (select.GroupOrderOp is { } groupOrderOp)
            {
                AppendSortStages(groupOrderOp, stages, sortFields);
            }

            // Group paging: after the group sort, before the flatten $project. $limit: 0 is normalized later
            // by MongoPipelineFactory.NormalizePagingStages.
            if (select.GroupPagingOps.Count > 0)
            {
                AppendSelectOpStages(select.GroupPagingOps, stages, sortFields);
            }

            if (select.Projection.Count > 0)
            {
                stages.Add(new MongoProjectStage(select.Projection));
            }

            // Ops composed after a projected Distinct act on its output. Emitted regardless of Cardinality: a
            // trailing aggregate falls through to the terminal switch, and these must precede it or a Where
            // is silently dropped. When PriorGrouping is set, PostGroupOps was already emitted above.
            if (select.PriorGrouping == null)
                AppendSelectOpStages(select.PostGroupOps, stages, sortFields);

            if (select.Cardinality?.Aggregate is null)
            {
                return stages;
            }
        }

        // Terminal $project, after ops and lookups. Not re-emitted when a projected-operand set op or the
        // Grouping block above already emitted it.
        if (select.Grouping == null
            && select.Projection.Count > 0 && !(select.SetOperation?.OperandsProjected ?? false))
        {
            stages.Add(new MongoProjectStage(select.Projection));
        }

        // Scalar aggregate terminal stage ($count / $group / $limit for Any/All).
        var cardinality = select.Cardinality;
        if (cardinality?.Aggregate is { } aggregate)
        {
            stages.Add(aggregate switch
            {
                MongoAggregateOperator.Count or MongoAggregateOperator.LongCount
                    => new MongoCountStage(BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Sum
                    => new MongoGroupAccumulatorStage("$sum", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Min
                    => new MongoGroupAccumulatorStage("$min", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Max
                    => new MongoGroupAccumulatorStage("$max", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Average
                    => new MongoGroupAccumulatorStage("$avg", cardinality.Selector!, BsonValueSerializer.ScalarField),
                MongoAggregateOperator.Any or MongoAggregateOperator.All
                    => new MongoLimitStage(new MongoConstantExpression(1, forSerialization: null)),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unsupported aggregate operator '{aggregate}'.")
            });
        }

        // Last()/LastOrDefault() with no prior sort: a $group collapses the whole input to its last document,
        // so it must follow $lookup/$project for Included/projected fields to be captured.
        if (cardinality?.Reducer is not null && cardinality.UnorderedLastRow)
        {
            stages.Add(new MongoLastRowStage());
            stages.Add(new MongoReplaceRootStage("_last", mergeOwnerKeySentinels: false));
        }

        return stages;
    }

    // The snapshotted prior grouping's $group, its own HAVING (after the $group, before the flattening
    // $project), the flattening $project, then the ops composed on its output (PostGroupOps).
    private static void AppendPriorGroupingStages(
        MongoSelectDefinition select,
        MongoGrouping priorGrouping,
        List<MongoPipelineStage> stages,
        SyntheticSortFieldAllocator sortFields)
    {
        stages.Add(new MongoGroupStage(priorGrouping));

        if (select.PriorGroupHavingPredicate is { } priorHavingPredicate)
        {
            stages.Add(new MongoMatchStage(priorHavingPredicate));
        }

        if (select.PriorGroupingProjection.Count > 0)
            stages.Add(new MongoProjectStage(select.PriorGroupingProjection));
        AppendSelectOpStages(select.PostGroupOps, stages, sortFields);
    }

    // One stage per set-op link, in LINQ source order (a left-nested chain has several). Each Union dedups
    // right after its own $unionWith: Concat(Union(A,B),C) must dedup A,B before C joins, and hoisting the
    // dedup to the end would silently drop rows. Mutually recursive with AppendSetOpOperandStages, which
    // nests a right-nested operand's $unionWith inside the outer one's pipeline.
    private static void AppendSetOpChainStages(
        MongoSelectDefinition select,
        List<MongoPipelineStage> stages,
        SyntheticSortFieldAllocator sortFields)
    {
        foreach (var link in select.SetOperations)
        {
            // Ops recorded between the previous link and this one act on the result combined so far.
            AppendSelectOpStages(link.PrecedingOps, stages, sortFields);

            var operandStages = AppendSetOpOperandStages(link, sortFields);

            if (link.Kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
            {
                stages.Add(new MongoSetDifferenceStage(link.Kind, operandStages, link.OperandCollectionName));
            }
            else
            {
                stages.Add(new MongoUnionWithStage(
                    operandStages, link.OperandCollectionName, dedup: link.Kind == MongoSetOperationKind.Union));
            }
        }
    }

    // The operand's sub-pipeline: its ops, then either its pre-combine $group/$project (projected operand) or
    // its own set-op chain (right-nested whole-entity operand). Mutually exclusive: the QMTEV admits nesting
    // only via IsWholeEntitySetOpOperandSelect, which requires no projection.
    private static List<MongoPipelineStage> AppendSetOpOperandStages(
        MongoSetOperation link,
        SyntheticSortFieldAllocator sortFields)
    {
        var operandStages = new List<MongoPipelineStage>();
        AppendSelectOpStages(link.OperandSelect.PipelineOps, operandStages, sortFields);

        if (link.OperandsProjected)
        {
            if (link.OperandSelect.Grouping is { } operandGrouping)
                operandStages.Add(new MongoGroupStage(operandGrouping));
            operandStages.Add(new MongoProjectStage(link.OperandSelect.Projection));

            // The operand's ops composed on its projected Distinct/GroupBy output.
            AppendSelectOpStages(link.OperandSelect.PostGroupOps, operandStages, sortFields);
        }
        else
        {
            AppendSetOpChainStages(link.OperandSelect, operandStages, sortFields);

            // The operand's own post-combine ops (B.Union(C).Take(1)) close its sub-pipeline.
            AppendSelectOpStages(link.OperandSelect.TrailingOps, operandStages, sortFields);
        }

        return operandStages;
    }

    // Emits $match/$sort/$skip/$limit (and Distinct) ops in recorded order.
    private static void AppendSelectOpStages(
        IReadOnlyList<MongoSelectOp> ops,
        List<MongoPipelineStage> stages,
        SyntheticSortFieldAllocator sortFields)
    {
        foreach (var op in ops)
        {
            if (op is MongoSortOp sortOp)
            {
                AppendSortStages(sortOp, stages, sortFields);
                continue;
            }

            // Whole-entity Distinct: $group{_id:"$$ROOT"} dedup, then $replaceRoot from "$_id". Over a bare join leaf,
            // first narrow the flattened join document to the selected entity (see MongoDistinctOp). A left join's
            // unmatched inner rows all narrow to {} and so dedup to a single null entity.
            if (op is MongoDistinctOp distinctOp)
            {
                if (distinctOp.KeepOnlyField is { } keepOnly)
                {
                    stages.Add(new MongoProjectStage(
                        [new MongoProjection(keepOnly, new MongoElementRefExpression(keepOnly, typeof(object)))]));
                }

                if (distinctOp.ExcludeField is { } exclude)
                {
                    stages.Add(new MongoUnsetStage([exclude]));
                }

                stages.Add(new MongoGroupByRootStage());
                stages.Add(new MongoReplaceRootStage("_id", mergeOwnerKeySentinels: false));
                continue;
            }

            stages.Add(op switch
            {
                MongoMatchOp m => new MongoMatchStage(m.Predicate),
                MongoSkipOp k => new MongoSkipStage(k.Count),
                MongoLimitOp l => new MongoLimitStage(l.Count),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unknown select op '{op.GetType().Name}'.")
            });
        }
    }

    /// <summary>
    /// Emits one <see cref="MongoSortOp"/>. Field-path keys are emitted as-is; computed keys are materialized
    /// into synthetic fields by a preceding <c>$set</c> and removed by a following <c>$unset</c>, since
    /// <c>$sort</c> accepts field paths only.
    /// </summary>
    /// <remarks>
    /// The no-computed-key path must emit no <c>$set</c>: a <c>$set</c> ahead of an indexed sort turns an
    /// <c>IXSCAN</c> into a <c>COLLSCAN</c>. A mixed sort accepts that cost.
    /// </remarks>
    private static void AppendSortStages(
        MongoSortOp sortOp, List<MongoPipelineStage> stages, SyntheticSortFieldAllocator sortFields)
    {
        List<MongoProjection>? computed = null;
        var orderings = new List<MongoOrdering>(sortOp.Orderings.Count);

        foreach (var ordering in sortOp.Orderings)
        {
            if (ordering.KeySelector is MongoFieldExpression or MongoElementRefExpression)
            {
                orderings.Add(ordering);
                continue;
            }

            var name = sortFields.Allocate();
            (computed ??= []).Add(new MongoProjection(name, ordering.KeySelector));
            orderings.Add(new MongoOrdering(
                new MongoElementRefExpression(name, ordering.KeySelector.Type), ordering.Ascending));
        }

        if (computed is null)
        {
            stages.Add(new MongoSortStage(sortOp.Orderings));
            return;
        }

        stages.Add(new MongoAddFieldsStage(computed));
        stages.Add(new MongoSortStage(orderings));
        stages.Add(new MongoUnsetStage(computed.Select(f => f.Alias).ToList()));
    }

    /// <summary>
    /// Appends <see cref="MongoLookupStage"/> + <see cref="MongoUnwindStage"/> pairs for each lookup,
    /// after validating that the native pipeline can handle the lookup shape.
    /// </summary>
    private static void AppendLookupStages(MongoQueryExpression query, List<MongoPipelineStage> stages)
    {
        var lookups = query.Lookups;

        // Join-coverage guard: if this is a join query and there are fewer lookups than inner
        // collections, emitting a partial pipeline would silently drop a join and return wrong results.
        if (query.IsJoinQuery && lookups.Count < query.InnerCollections.Count)
        {
            throw new NativeTranslationNotSupportedException(
                "Native pipeline does not support this join shape (only single-level reference includes).");
        }

        foreach (var lookup in lookups)
        {
            if (lookup.IsReference && !lookup.HasPipeline)
            {
                // $unwind follows the navigation's requiredness via PreserveNullAndEmptyArrays (inner for
                // required, so a dangling FK drops the row; left-outer for optional). A fixed default would
                // silently get one of them wrong.
                //
                // Deliberately broader than IsStreamableReference: also admits transitive hops (localField
                // prefixed by a prior lookup's alias), which render identically. IsStreamableReference still
                // excludes them to keep such queries off the streaming materializer.
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, lookup.PreserveNullAndEmptyArrays));
            }
            else if (lookup.IsNativeCollectionLookup
                     // A renamed lookup must still be non-ForceUnwind and not FallbackOnly, or a TPH-derived
                     // collection Include target that collided with a join would render natively and leak
                     // sibling-subtype rows.
                     || (lookup.RenamedToAvoidJoinCollision && !lookup.ForceUnwind
                         && lookup.PipelineKind is LookupPipelineKind.None or LookupPipelineKind.NestedInclude or LookupPipelineKind.FilteredInclude)
                     || (lookup.Navigation is { IsCollection: true } pipelinedNav
                         && lookup.PipelineKind is LookupPipelineKind.NestedInclude or LookupPipelineKind.FilteredInclude
                         && !lookup.ForceUnwind
                         && lookup.As == LookupExpression.GetLookupAlias(pipelinedNav))
                     || lookup.IsTransitiveCollectionLookup)
            {
                // Collection Include: the joined documents stay an array under _lookup_<Nav> (no $unwind),
                // read back by the DOM collection materializer.
                //
                // The pipelined disjunct admits nested ThenInclude and filtered Include sub-pipelines. It's
                // keyed on PipelineKind, not HasPipeline, so a CorrelatedReducer lookup (also a collection
                // nav) reaches its own branch below instead of being mis-rendered with no $unwind.
                //
                // IsTransitiveCollectionLookup: a collection ThenInclude off a reference Include
                // (Orders.Include(o => o.Customer.Orders)) — a plain lookup with LocalField/As prefixed by
                // the reference lookup's alias.
                stages.Add(new MongoLookupStage(lookup));
            }
            else if (lookup.Navigation is { IsCollection: true } && lookup.ForceUnwind)
            {
                // Collection-nav Join/LeftJoin/GroupJoin or reference SelectMany flatten. PreserveNullAndEmptyArrays
                // is false for Join/SelectMany (inner) and true for LeftJoin/GroupJoin (left-outer).
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, lookup.PreserveNullAndEmptyArrays));
            }
            else if (lookup.PipelineKind == LookupPipelineKind.CorrelatedReducer)
            {
                // Collection-nav First/FirstOrDefault projection leaf: the sub-pipeline yields 0 or 1 documents.
                // Always left-outer; First's throw-on-empty is handled on read (MongoCorrelatedReducerLeaf).
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: true));
            }
            else
            {
                // Navigation is null for a navigation-less Join hop; name the target entity type instead.
                throw new NativeTranslationNotSupportedException(
                    "Native pipeline does not support lookup for "
                    + (lookup.Navigation is { } nav
                        ? $"navigation '{nav.Name}' "
                        : $"navigation-less join onto '{lookup.TargetEntityType.DisplayName()}' ")
                    + "(only single-level reference and single-level collection includes).");
            }
        }
    }

    // Double-underscore prefix, matching the other sentinel fields (__score, __ownerKey, __ord).
    private const string SyntheticSortFieldPrefix = "__sort";

    /// <summary>
    /// Allocates synthetic sort-field names for one <see cref="Lower"/> invocation, skipping reserved names.
    /// </summary>
    /// <remarks>
    /// Per-invocation so emitted names (and <c>AssertMql</c> baselines) don't depend on execution order. The
    /// reserved set matters because <c>$set</c> silently overwrites a same-named field a model may map via
    /// <c>HasElementName</c>; see <see cref="ReservedElementNames"/> and <c>NativeComputedSortTests</c>.
    /// </remarks>
    private sealed class SyntheticSortFieldAllocator(IReadOnlyCollection<string> reservedElementNames)
    {
        private int _next;

        public string Allocate()
        {
            while (true)
            {
                var name = SyntheticSortFieldPrefix + _next++;
                if (!reservedElementNames.Contains(name))
                    return name;
            }
        }
    }

    // Every top-level element name a synthetic $set could collide with in this pipeline: the root entity
    // type's, plus every set-op operand's, since operands may be of a different entity type and their ops use
    // the same allocator.
    private static IReadOnlyCollection<string> ReservedElementNames(MongoQueryExpression query)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddTopLevelElementNames(query.CollectionExpression.EntityType, names);

        AddSetOpOperandElementNames(query.Select, names);

        return names;
    }

    // Recursive: a right-nested operand carries its own chain, lowered through the same allocator.
    private static void AddSetOpOperandElementNames(MongoSelectDefinition select, HashSet<string> names)
    {
        foreach (var link in select.SetOperations)
        {
            AddTopLevelElementNames(link.OperandEntityType, names);
            AddSetOpOperandElementNames(link.OperandSelect, names);
        }
    }

    // Top-level element names of an entity type: mapped properties, owned-navigation containing elements, and
    // complex properties (which GetProperties() doesn't return).
    //
    // Walks GetDerivedTypesInclusive(): TPH derived types share the top-level namespace, but a base type's
    // Get*() methods omit derived members, so a derived element could be clobbered by $set and removed by $unset.
    private static void AddTopLevelElementNames(IEntityType entityType, HashSet<string> names)
    {
        foreach (var type in entityType.GetDerivedTypesInclusive())
        {
            foreach (var property in type.GetProperties())
                names.Add(property.GetElementName());

            foreach (var navigation in type.GetNavigations())
            {
                if (navigation.IsEmbedded() && navigation.TargetEntityType.GetContainingElementName() is { } elementName)
                    names.Add(elementName);
            }

            foreach (var complexProperty in type.GetComplexProperties())
                names.Add(GetComplexPropertyElementName(complexProperty));
        }
    }

    // No GetElementName overload exists for complex properties, so read the annotation with the same CLR-name
    // fallback. Mirrors MongoQueryableMethodTranslatingExpressionVisitor.GetComplexPropertyElementName.
    private static string GetComplexPropertyElementName(IReadOnlyComplexProperty complexProperty)
        => (string?)complexProperty[MongoDB.EntityFrameworkCore.Metadata.MongoAnnotationNames.ElementName]
           ?? complexProperty.Name;
}
