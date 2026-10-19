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
/// An Atlas <c>$vectorSearch</c> stage. Must be the first stage (the server rejects it elsewhere,
/// <c>Location40602</c>), so the lowerer emits it from a dedicated slot rather than the ordered op list.
/// </summary>
/// <remarks>
/// Rendered by a deferred <c>MongoPipelineFactory</c> slot: the body's shape (whether <c>exact</c> or
/// <c>numCandidates</c> is present, and which <c>index</c>) depends on runtime <c>VectorQueryOptions</c>, so a value
/// sentinel can't stand in for it.
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
