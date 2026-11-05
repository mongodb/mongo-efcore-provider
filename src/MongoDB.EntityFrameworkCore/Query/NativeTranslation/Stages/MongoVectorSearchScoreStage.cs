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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Marker rendered by <c>MongoPipelineFactory</c> to <c>{ "$addFields": { "__score": { "$meta": "vectorSearchScore" } } }</c>,
/// emitted unconditionally after every <c>$vectorSearch</c> (as the driver-LINQ bridge does, so both paths produce
/// identical pipelines).
/// </summary>
internal sealed class MongoVectorSearchScoreStage : MongoPipelineStage
{
    /// <summary>
    /// The synthetic top-level element holding the score; not a mapped property, so read back by raw name.
    /// </summary>
    internal const string ScoreField = "__score";
}
