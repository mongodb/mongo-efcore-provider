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
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// Atlas Vector Search index options.
/// See <see href="https://www.mongodb.com/docs/atlas/atlas-vector-search/vector-search-type">How to Index Fields for Vector Search</see>
/// for more information.
/// </summary>
/// <param name="Similarity">The <see cref="VectorSimilarity"/> to use to search for top K-nearest neighbors.</param>
/// <param name="Dimensions">Number of vector dimensions that Atlas Vector Search enforces at index-time and query-time.</param>
/// <param name="Quantization">Type of automatic vector quantization for your vectors.</param>
/// <param name="HnswMaxEdges">Maximum number of edges (or connections) that a node can have in the Hierarchical Navigable Small Worlds graph.</param>
/// <param name="HnswNumEdgeCandidates">Analogous to numCandidates at query-time, this parameter controls the maximum number of nodes to evaluate to find the closest neighbors to connect to a new node.</param>
/// <param name="FilterPaths">Paths to properties that may be used as filters on the entity type or its nested types.</param>
public readonly record struct VectorIndexOptions(
    VectorSimilarity Similarity,
    int Dimensions,
    VectorQuantization? Quantization = null,
    int? HnswMaxEdges = null,
    int? HnswNumEdgeCandidates = null,
    IReadOnlyList<string>? FilterPaths = null)
{
    /// <summary>
    /// Creates an uninitialized Atlas Vector Search index options object.
    /// </summary>
    public VectorIndexOptions()
        : this((VectorSimilarity)(-1), 0)
    {
    }
}
