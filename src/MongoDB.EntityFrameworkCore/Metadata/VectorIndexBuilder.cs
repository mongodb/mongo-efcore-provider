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
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// A fluent builder API for configuring MongoDB vector index options.
/// </summary>
public class VectorIndexBuilder(IMutableIndex index, VectorIndexOptions indexOptions)
{
    /// <summary>
    /// The index being configured.
    /// </summary>
    protected IMutableIndex Index { get; } = index;

    /// <summary>
    /// The <see cref="VectorIndexOptions"/> being used.
    /// </summary>
    protected VectorIndexOptions IndexOptions { get; set; }  = indexOptions;

    /// <summary>
    /// Configures Type of automatic vector quantization for your vectors. Use this setting only if your embeddings are float or double vectors.
    /// </summary>
    /// <param name="quantization">Type of automatic vector quantization for your vectors.</param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public VectorIndexBuilder HasQuantization(VectorQuantization? quantization)
    {
        Index.SetVectorIndexOptions(IndexOptions = IndexOptions with { Quantization = quantization });
        return this;
    }

    /// <summary>
    /// Configures Hierarchical Navigable Small Worlds options for ANN searches.
    /// </summary>
    /// <param name="maxEdges">Maximum number of edges (or connections) that a node can have in the Hierarchical Navigable Small Worlds graph.</param>
    /// <param name="numEdgeCandidates">Analogous to numCandidates at query-time, this parameter controls the maximum number of nodes to evaluate to find the closest neighbors to connect to a new node.</param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public VectorIndexBuilder HasEdgeOptions(int maxEdges, int numEdgeCandidates)
    {
        Index.SetVectorIndexOptions(IndexOptions =
            IndexOptions with { HnswMaxEdges = maxEdges, HnswNumEdgeCandidates = numEdgeCandidates });
        return this;
    }

    /// <summary>
    /// Adds the given property to those that can be used to filter in vector queries with this index.
    /// </summary>
    /// <param name="propertyPath">The name of the property that may be used for filters.</param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public VectorIndexBuilder AllowsFiltersOn(string propertyPath)
    {
        var newPaths = IndexOptions.FilterPaths?.ToList() ?? new List<string>();
        newPaths.Add(propertyPath);
        Index.SetVectorIndexOptions(IndexOptions = IndexOptions with { FilterPaths = newPaths });
        return this;
    }
}
