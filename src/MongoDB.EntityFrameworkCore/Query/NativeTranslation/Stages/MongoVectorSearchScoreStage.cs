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
/// The <c>$addFields</c> companion emitted immediately after every <c>$vectorSearch</c>, surfacing the
/// per-document relevance score as the synthetic <see cref="ScoreField"/> element.
/// </summary>
/// <remarks>
/// <para>
/// A MARKER with no payload: <c>MongoPipelineFactory</c> renders it to the fixed
/// <c>{ "$addFields": { "__score": { "$meta": "vectorSearchScore" } } }</c>. Giving it a generic
/// "add these fields" BSON payload instead would put BSON into the lowerer, which this area's contract
/// forbids — the lowerer is BSON-free and all BSON construction belongs to the renderer/factory.
/// </para>
/// <para>
/// It is emitted UNCONDITIONALLY, exactly as the driver-LINQ bridge does, so the two paths' pipelines stay
/// byte-identical and no committed MQL baseline moves. The score can also be read straight out of a later
/// <c>$project</c> via <c>$meta</c>, so emitting the companion is a baseline-parity choice rather than a
/// correctness one.
/// </para>
/// </remarks>
internal sealed class MongoVectorSearchScoreStage : MongoPipelineStage
{
    /// <summary>
    /// The synthetic top-level element the score is written to. Not a mapped property of any entity type;
    /// a projection that reads it back does so by raw element name.
    /// </summary>
    internal const string ScoreField = "__score";
}
