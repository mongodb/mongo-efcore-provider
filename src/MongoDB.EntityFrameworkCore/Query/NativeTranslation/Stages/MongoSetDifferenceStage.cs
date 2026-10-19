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
/// LINQ <c>Intersect</c>/<c>Except</c> over the same collection. MongoDB has no such stage, so the renderer dedupes
/// and tags each side (<c>_a</c>/<c>_b</c>) via <c>$unionWith</c>, regroups by full document, then <c>$match</c>es
/// on the tags per <see cref="Kind"/> and unwraps with <c>$replaceRoot</c>.
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
