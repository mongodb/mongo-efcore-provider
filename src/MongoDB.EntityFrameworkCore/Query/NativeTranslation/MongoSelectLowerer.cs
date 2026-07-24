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
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Converts the native-translation IR on a <see cref="MongoQueryExpression"/> into a fully-typed
/// <see cref="MongoPipelineStage"/> list: the filter/sort/page ops (<see cref="MongoSelectDefinition.PipelineOps"/>)
/// are emitted verbatim in their recorded arrival order (EF-347 — no fixed canonical order), followed by
/// <c>$lookup</c>/<c>$unwind</c> and any terminal stage (<c>$unionWith</c>/<c>$group</c>/<c>$project</c>/aggregate).
/// </summary>
/// <remarks>
/// <para>
/// This lowerer is BSON-free. It produces typed stage IR objects only; BSON rendering is the
/// responsibility of the downstream pipeline renderer/factory. An empty <see cref="MongoSelectDefinition.PipelineOps"/>
/// list means no filter/sort/page ops are emitted at all.
/// </para>
/// <para>
/// Lookup eligibility is guarded here. If the query contains a lookup shape the native pipeline
/// cannot handle, a <see cref="NativeTranslationNotSupportedException"/> is thrown. The compile-time
/// gate catches this and falls back to the driver-LINQ path.
/// </para>
/// </remarks>
internal sealed class MongoSelectLowerer
{
    /// <summary>
    /// Lowers the native-translation IR of <paramref name="query"/> into typed pipeline stages.
    /// </summary>
    /// <param name="query">
    /// The <see cref="MongoQueryExpression"/> whose native IR (on its
    /// <see cref="MongoQueryExpression.Select"/>) and lookup state are lowered.
    /// </param>
    /// <returns>
    /// An ordered, read-only list of <see cref="MongoPipelineStage"/> values: the recorded
    /// <see cref="MongoSelectDefinition.PipelineOps"/> in arrival order, then lookups/terminal stages.
    /// Returns an empty list when no ops are populated.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown when the query contains a join or lookup shape that the native pipeline does not support.
    /// </exception>
    public IReadOnlyList<MongoPipelineStage> Lower(MongoQueryExpression query)
    {
        var select = query.Select;
        var stages = new List<MongoPipelineStage>();

        // 1. $match / $sort / $skip / $limit ops, emitted verbatim in the order they were recorded
        // (Select.PipelineOps — EF-347: no fixed canonical order; arrival order IS emission order).
        AppendSelectOpStages(select.PipelineOps, stages);

        // 2. $lookup/$unwind — cross-collection includes (group-3 lookup state stays on the query node).
        // A projected collection-navigation Count (NativeProjectionBinder.TryTranslateProjectedCollectionCount)
        // registers an IsNativeCollectionLookup $lookup here (InjectAfterRoot=true) so its _lookup_<Nav> array
        // is already present by the time the $project below reads it via $size — placing lookups after the
        // filter/sort/page block (but before $project) already satisfies that without any lowerer change.
        AppendLookupStages(query, stages);

        // Set operation terminal ($unionWith [+ dedup] or a set-difference shape for Intersect/Except).
        // Guaranteed whole-entity by the QMTEV guard (the operand is a plain whole-entity select — no
        // grouping/projection/cardinality/lookups), so the operand lowers to its own filter/sort/page ops only.
        if (select.SetOperation is { } setOp)
        {
            // EF-347 slice C1: projected operands. Each operand's own $project is part of ITS pipeline and
            // must be emitted BEFORE the combine — source1's ahead of the set-op stage (appended to `stages`
            // right after source1's PipelineOps above), the operand's inside the nested `operandStages`. The
            // dedup ($group{_id:$$ROOT}) and Intersect/Except source-tagging then operate over the PROJECTED
            // documents (correct: BCL dedups/compares the projected values). Contrast slice C2 (a trailing
            // projection over the COMBINED result), where OperandsProjected is false and select.Projection is
            // emitted AFTER the set-op stage by the fall-through Projection block below.
            if (setOp.OperandsProjected)
            {
                stages.Add(new MongoProjectStage(select.Projection));
            }

            var operandStages = new List<MongoPipelineStage>();
            AppendSelectOpStages(setOp.OperandSelect.PipelineOps, operandStages);
            if (setOp.OperandsProjected)
            {
                operandStages.Add(new MongoProjectStage(setOp.OperandSelect.Projection));
            }

            if (setOp.Kind is MongoSetOperationKind.Intersect or MongoSetOperationKind.Except)
            {
                stages.Add(new MongoSetDifferenceStage(setOp.Kind, operandStages, setOp.OperandCollectionName));
            }
            else
            {
                stages.Add(new MongoUnionWithStage(
                    operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
            }

            // EF-347 slice B: post-set-op composition. Trailing $match/$sort/$skip/$limit emit AFTER the
            // set-op stage (they operate on the COMBINED result), then fall through to the Projection block
            // (EF-347 slice C2: a trailing anonymous/DTO Select after a set op populates Select.Projection,
            // emitted here as a $project after the set-op stage and TrailingOps) and the Cardinality block
            // (post-set-op aggregate/reducer). UnwindSource/Grouping stay empty for a set-op query and their
            // blocks are skipped.
            AppendSelectOpStages(select.TrailingOps, stages);
            // NB: no early return — control continues to the Cardinality block.
        }

        // Terminal native SelectMany (EF-347 slices 3-5), then $project the result selector (populated in
        // Select.Projection by NativeSelectManyBinder). Terminal — nothing follows.
        // Owned (embedded, slice 3/4): $unwind the embedded array directly here.
        // Reference (cross-collection, slice 5): the $lookup + $unwind were already appended above by
        // AppendLookupStages (stage 5, ForceUnwind-collection branch) — nothing further to add here.
        if (select.UnwindSource is { } unwind)
        {
            if (unwind.Kind == MongoUnwindSourceKind.Owned)
                stages.Add(new MongoUnwindFieldStage(
                    unwind.InnerScopePath,
                    includeArrayIndex: unwind.WholeElement ? MongoReplaceRootStage.OrdinalField : null));

            // Inner-element-only user filter (o.Refs.Where(pred)): a $match on the unwound element, emitted
            // after the $unwind (owned: just above; reference: already emitted by AppendLookupStages) and
            // before the $replaceRoot (WholeElement) / $project (projected). Already scope-prefixed by the
            // binder (e.g. "_lookup_Refs.Total"). EF-347 filtered-inner slice — reference only for now.
            if (unwind.Filter is { } filter)
                stages.Add(new MongoMatchStage(filter));

            if (unwind.WholeElement)
            {
                // Bare whole-inner-element SelectMany: promote the unwound element to root.
                // Owned (embedded, shadow key): $mergeObjects the owner key + array ordinal in under sentinel
                // fields so the owned element's shadow key materializes non-null (EF-347 bare-owned spike).
                // Reference (cross-collection): the $lookup + $unwind were already appended by AppendLookupStages
                // above; a reference entity carries its own real stored key, so a PLAIN $replaceRoot suffices —
                // no sentinel merge (EF-347 ref-bare-entity slice).
                stages.Add(new MongoReplaceRootStage(
                    unwind.InnerScopePath,
                    mergeOwnerKeySentinels: unwind.Kind == MongoUnwindSourceKind.Owned));
                return stages;
            }

            if (select.Projection.Count > 0)
                stages.Add(new MongoProjectStage(select.Projection));
            return stages;
        }

        // 6b. Keyed $group terminal (GroupBy(key).Select(aggregate)). A GroupBy-route query has only
        // $match + $group by construction — the binder rejects orderings/paging alongside a grouping —
        // so no $sort/$skip/$limit precede it here. The $group is followed by a flattening $project
        // (Select.Projection) that lifts the grouped output — the _id (scalar key), each _id.<Name>
        // composite sub-key, and each accumulator output field — up to top-level result aliases the DOM
        // shaper reads by name (see NativeGroupByBinder / MongoQueryLanguageRenderer). Returning here is
        // safe: no further stages follow a grouping.
        if (select.Grouping is { } grouping)
        {
            stages.Add(new MongoGroupStage(grouping));
            if (select.Projection.Count > 0)
            {
                stages.Add(new MongoProjectStage(select.Projection));
            }

            return stages;
        }

        // 6. $project — server-side projection (terminal member-access anonymous/DTO Select). Emitted
        // last here: the projection is the final logical operation for the SP3 terminal slice, after
        // the filter/sort/page ops and any $lookup.
        // A projected-operand set op (slice C1) already emitted source1's $project above, ahead of the
        // set-op stage — don't re-emit it here. A trailing projection after a set op (slice C2, OperandsProjected
        // false) and a plain projected Select (no set op) both still emit here.
        if (select.Projection.Count > 0 && !(select.SetOperation?.OperandsProjected ?? false))
        {
            stages.Add(new MongoProjectStage(select.Projection));
        }

        // 7. Scalar aggregate terminal stage ($count / $group / $limit for Any/All).
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

        return stages;
    }

    /// <summary>
    /// Appends the ordered filter/sort/page stages ($match / $sort / $skip / $limit) for <paramref name="ops"/>
    /// in their recorded order. Shared by the outer query's own <see cref="MongoSelectDefinition.PipelineOps"/>,
    /// a set-operation operand's <see cref="MongoSelectDefinition.PipelineOps"/>
    /// (<see cref="MongoSetOperation.OperandSelect"/>, a plain whole-entity select), and the outer query's
    /// post-set-op <see cref="MongoSelectDefinition.TrailingOps"/> (EF-347 slice B).
    /// </summary>
    private static void AppendSelectOpStages(IReadOnlyList<MongoSelectOp> ops, List<MongoPipelineStage> stages)
    {
        foreach (var op in ops)
        {
            stages.Add(op switch
            {
                MongoMatchOp m => new MongoMatchStage(m.Predicate),
                MongoSortOp s => new MongoSortStage(s.Orderings),
                MongoSkipOp k => new MongoSkipStage(k.Count),
                MongoLimitOp l => new MongoLimitStage(l.Count),
                _ => throw new NativeTranslationNotSupportedException(
                    $"Unknown select op '{op.GetType().Name}'.")
            });
        }
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
            if (lookup.IsStreamableReference)
            {
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup));
            }
            else if (lookup.IsNativeCollectionLookup)
            {
                // Collection Include: keep the joined documents as an array under _lookup_<Nav>
                // (no $unwind). The DOM collection materializer reads the array back and runs the
                // IncludeCollection fixup, exactly as on the driver-LINQ path.
                stages.Add(new MongoLookupStage(lookup));
            }
            else if (lookup.Navigation.IsCollection && lookup.ForceUnwind)
            {
                // EF-347 slice 5: a cross-collection reference SelectMany flatten — $lookup the referenced
                // collection, then $unwind to one row per child with INNER-JOIN semantics (preserve:false):
                // a principal with no children drops out. (Include's reference $unwind uses preserve:true /
                // LEFT-join; this is the opposite.)
                stages.Add(new MongoLookupStage(lookup));
                stages.Add(new MongoUnwindStage(lookup, preserveNullAndEmptyArrays: false));
            }
            else
            {
                throw new NativeTranslationNotSupportedException(
                    $"Native pipeline does not support lookup for navigation '{lookup.Navigation.Name}' " +
                    "(only single-level reference and single-level collection includes).");
            }
        }
    }
}
