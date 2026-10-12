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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// A <c>$unionWith</c> aggregation stage: unions the current pipeline output with the result of running
/// <see cref="OperandStages"/> against <see cref="OperandCollectionName"/>. When <see cref="Dedup"/> is
/// set (LINQ <c>Union</c>), the renderer follows it with a full-document (<c>$$ROOT</c>) de-duplication.
/// </summary>
internal sealed class MongoUnionWithStage : MongoPipelineStage
{
    public MongoUnionWithStage(IReadOnlyList<MongoPipelineStage> operandStages, string operandCollectionName, bool dedup)
    {
        OperandStages = operandStages;
        OperandCollectionName = operandCollectionName;
        Dedup = dedup;
    }

    public IReadOnlyList<MongoPipelineStage> OperandStages { get; }
    public string OperandCollectionName { get; }
    public bool Dedup { get; }
}
