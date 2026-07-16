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
/// Converts the native-translation slots on a <see cref="MongoQueryExpression"/> into
/// a fully-typed <see cref="MongoPipelineStage"/> list in canonical aggregation pipeline order:
/// <c>$match → $sort → $skip → $limit → $lookup/$unwind</c>.
/// </summary>
/// <remarks>
/// <para>
/// This lowerer is BSON-free. It produces typed stage IR objects only; BSON rendering is the
/// responsibility of the downstream pipeline renderer/factory. Empty slots are dropped (no
/// predicate means no <see cref="MongoMatchStage"/>, and so on).
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
    /// Lowers the native-translation slots of <paramref name="query"/> into typed pipeline stages.
    /// </summary>
    /// <param name="query">
    /// The <see cref="MongoQueryExpression"/> whose native scalar slots (on its
    /// <see cref="MongoQueryExpression.Select"/>) and lookup state are lowered.
    /// </param>
    /// <returns>
    /// An ordered, read-only list of <see cref="MongoPipelineStage"/> values in canonical pipeline
    /// order. Returns an empty list when no slots are populated.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown when the query contains a join or lookup shape that the native pipeline does not support.
    /// </exception>
    public IReadOnlyList<MongoPipelineStage> Lower(MongoQueryExpression query)
    {
        var select = query.Select;
        var stages = new List<MongoPipelineStage>();

        // 1-4. $match → $sort → $skip → $limit — filter / sort / paging.
        AppendCanonicalStages(select, stages);

        // 5. $lookup/$unwind — cross-collection includes (group-3 lookup state stays on the query node).
        // A projected collection-navigation Count (NativeProjectionBinder.TryTranslateProjectedCollectionCount)
        // registers an IsNativeCollectionLookup $lookup here (InjectAfterRoot=true) so its _lookup_<Nav> array
        // is already present by the time stage 6's $project reads it via $size — this canonical ordering
        // ($lookup before $project) already satisfies that without any lowerer change.
        AppendLookupStages(query, stages);

        // Set operation terminal ($unionWith [+ dedup]). Guaranteed terminal and whole-entity by the QMTEV
        // guard (the operand is a plain whole-entity select — no grouping/projection/cardinality/lookups), so
        // nothing follows it and the operand lowers to canonical stages only.
        if (select.SetOperation is { } setOp)
        {
            var operandStages = new List<MongoPipelineStage>();
            AppendCanonicalStages(setOp.OperandSelect, operandStages);
            stages.Add(new MongoUnionWithStage(operandStages, setOp.OperandCollectionName, dedup: setOp.Kind == MongoSetOperationKind.Union));
            return stages;
        }

        // Owned-collection SelectMany (EF-347 slice 3): $unwind the embedded array, then $project the result
        // selector (populated in Select.Projection by NativeSelectManyBinder). Terminal — nothing follows.
        if (select.UnwindSource is { } unwind)
        {
            stages.Add(new MongoUnwindFieldStage(unwind.ElementPath));
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

        // 6. $project — server-side projection (terminal member-access anonymous/DTO Select). Last in
        // canonical order: the projection is the final logical operation for the SP3 terminal slice.
        if (select.Projection.Count > 0)
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
    /// Appends the canonical <c>$match → $sort → $skip → $limit</c> block for <paramref name="select"/>.
    /// Shared between the outer query and a set-operation operand (<see cref="MongoSetOperation.OperandSelect"/>),
    /// which is a plain whole-entity select and so only ever needs these four stages.
    /// </summary>
    private static void AppendCanonicalStages(MongoSelectDefinition select, List<MongoPipelineStage> stages)
    {
        // 1. $match — filter predicate.
        if (select.Predicate != null)
        {
            stages.Add(new MongoMatchStage(select.Predicate));
        }

        // 2. $sort — orderings.
        if (select.Orderings.Count > 0)
        {
            stages.Add(new MongoSortStage(select.Orderings));
        }

        // 3. $skip — offset (pagination start).
        if (select.Offset != null)
        {
            stages.Add(new MongoSkipStage(select.Offset));
        }

        // 4. $limit — result cap.
        if (select.Limit != null)
        {
            stages.Add(new MongoLimitStage(select.Limit));
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
            else
            {
                throw new NativeTranslationNotSupportedException(
                    $"Native pipeline does not support lookup for navigation '{lookup.Navigation.Name}' " +
                    "(only single-level reference and single-level collection includes).");
            }
        }
    }
}
