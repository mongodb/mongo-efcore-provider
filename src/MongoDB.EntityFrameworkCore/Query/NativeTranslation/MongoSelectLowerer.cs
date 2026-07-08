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

        // 5. $lookup/$unwind — cross-collection includes (group-3 lookup state stays on the query node).
        AppendLookupStages(query, stages);

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
            // Per-lookup guard: only single-level reference includes with no sub-pipeline and
            // no transitive _lookup_ local field are supported.
            if (!lookup.IsStreamableReference)
            {
                throw new NativeTranslationNotSupportedException(
                    $"Native pipeline does not support lookup for navigation '{lookup.Navigation.Name}' " +
                    "(only single-level reference includes).");
            }

            stages.Add(new MongoLookupStage(lookup));
            stages.Add(new MongoUnwindStage(lookup));
        }
    }
}
