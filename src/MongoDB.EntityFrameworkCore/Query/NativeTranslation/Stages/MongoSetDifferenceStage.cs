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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A synthesized set-difference / set-intersection terminal (LINQ <c>Intersect</c>/<c>Except</c>). MongoDB
/// has no direct intersect/except stage; because both operands are the SAME collection (same entity type),
/// the renderer emits a source-tagging pipeline: each side is deduped (<c>$group{_id:"$$ROOT"}</c>) and
/// tagged (<c>_a</c>/<c>_b</c>) via <c>$unionWith</c>, re-unified by full document (<c>$group{$max}</c>),
/// discriminated (<c>$match</c>), and unwrapped (<c>$replaceRoot</c>). <see cref="Kind"/> selects the final
/// <c>$match</c> (Intersect: in both; Except: in the first, not the second). BSON-free, like every stage.
/// </summary>
internal sealed class MongoSetDifferenceStage : MongoPipelineStage
{
    public MongoSetDifferenceStage(
        MongoSetOperationKind kind, IReadOnlyList<MongoPipelineStage> operandStages, string operandCollectionName)
    {
        Kind = kind;
        OperandStages = operandStages;
        OperandCollectionName = operandCollectionName;
    }

    public MongoSetOperationKind Kind { get; }
    public IReadOnlyList<MongoPipelineStage> OperandStages { get; }
    public string OperandCollectionName { get; }
}
