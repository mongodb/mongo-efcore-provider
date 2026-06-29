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
/// This lowerer is BSON-free. It produces typed stage IR objects only; BSON rendering is the
/// responsibility of the downstream pipeline renderer (Task 9). Empty slots are dropped (no
/// predicate means no <see cref="MongoMatchStage"/>, and so on).
///
/// Lookup eligibility is guarded here. If the query contains a lookup shape the native pipeline
/// cannot handle, a <see cref="NativeTranslationNotSupportedException"/> is thrown. The calling
/// gate (Task 14) catches this and falls back to the driver-LINQ path.
/// </remarks>
internal sealed class MongoSelectLowerer
{
    /// <summary>
    /// Lowers the native-translation slots of <paramref name="select"/> into typed pipeline stages.
    /// </summary>
    /// <param name="select">The <see cref="MongoQueryExpression"/> whose slots are lowered.</param>
    /// <returns>
    /// An ordered, read-only list of <see cref="MongoPipelineStage"/> values in canonical pipeline
    /// order. Returns an empty list when no slots are populated.
    /// </returns>
    /// <exception cref="NativeTranslationNotSupportedException">
    /// Thrown when the query contains a join or lookup shape that the native pipeline does not support.
    /// </exception>
    public IReadOnlyList<MongoPipelineStage> Lower(MongoQueryExpression select)
    {
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

        // 5. $lookup/$unwind — cross-collection includes.
        AppendLookupStages(select, stages);

        return stages;
    }

    /// <summary>
    /// Appends <see cref="MongoLookupStage"/> + <see cref="MongoUnwindStage"/> pairs for each lookup,
    /// after validating that the native pipeline can handle the lookup shape.
    /// </summary>
    private static void AppendLookupStages(MongoQueryExpression select, List<MongoPipelineStage> stages)
    {
        var lookups = select.Lookups;

        // Join-coverage guard: if this is a join query and there are fewer lookups than inner
        // collections, emitting a partial pipeline would silently drop a join and return wrong results.
        if (select.IsJoinQuery && lookups.Count < select.InnerCollections.Count)
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
