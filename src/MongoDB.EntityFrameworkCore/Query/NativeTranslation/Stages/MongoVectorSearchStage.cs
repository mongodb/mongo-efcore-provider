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

using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents an Atlas <c>$vectorSearch</c> stage. Always the FIRST stage of the pipeline — the server
/// rejects it anywhere else (<c>Location40602</c>) — which is why the lowerer emits it from its own
/// dedicated slot rather than from the ordered op list.
/// </summary>
/// <remarks>
/// Unlike every other stage, this one is rendered by a DEFERRED <c>MongoPipelineFactory</c> slot: the BSON
/// SHAPE of a <c>$vectorSearch</c> body — whether the <c>exact</c> or <c>numCandidates</c> key is present at
/// all, and which <c>index</c> is used — depends on a runtime <c>VectorQueryOptions</c>, so it cannot be
/// baked at compile time and a value sentinel cannot stand in for it.
/// </remarks>
internal sealed class MongoVectorSearchStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoVectorSearchStage"/> class.
    /// </summary>
    /// <param name="search">The native IR for the vector search.</param>
    public MongoVectorSearchStage(MongoVectorSearch search) => Search = search;

    /// <summary>The native IR for the vector search this stage renders.</summary>
    public MongoVectorSearch Search { get; }
}
