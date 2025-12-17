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

using System.Reflection;
using VectorQuantization = MongoDB.Driver.VectorQuantization;

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// A fluent builder API for configuring MongoDB vector index options.
/// </summary>
public class VectorIndexBuilder<TEntity>(IMutableIndex index, VectorIndexOptions indexOptions)
    : VectorIndexBuilder(index, indexOptions)
{
    /// <summary>
    /// Configures Type of automatic vector quantization for your vectors. Use this setting only if your embeddings are float or double vectors.
    /// </summary>
    /// <param name="quantization">Type of automatic vector quantization for your vectors.</param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public new VectorIndexBuilder<TEntity> HasQuantization(VectorQuantization? quantization)
        => (VectorIndexBuilder<TEntity>)base.HasQuantization(quantization);

    /// <summary>
    /// Configures Hierarchical Navigable Small Worlds options for ANN searches.
    /// </summary>
    /// <param name="maxEdges">Maximum number of edges (or connections) that a node can have in the Hierarchical Navigable Small Worlds graph.</param>
    /// <param name="numEdgeCandidates">Analogous to numCandidates at query-time, this parameter controls the maximum number of nodes to evaluate to find the closest neighbors to connect to a new node.</param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public new VectorIndexBuilder<TEntity> HasEdgeOptions(int maxEdges, int numEdgeCandidates)
        => (VectorIndexBuilder<TEntity>)base.HasEdgeOptions(maxEdges, numEdgeCandidates);

    /// <summary>
    /// Adds the given property to those that can be used to filter in vector queries with this index.
    /// </summary>
    /// <param name="propertyPath">The name of the property that may be used for filters.</param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public new VectorIndexBuilder<TEntity> AllowsFiltersOn(string propertyPath)
        => (VectorIndexBuilder<TEntity>)base.AllowsFiltersOn(propertyPath);

    /// <summary>
    /// Adds the given property to those that can be used to filter in vector queries with this index.
    /// </summary>
    /// <param name="propertyExpression">A lambda expression representing the property on this or a nested type. </param>
    /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
    public VectorIndexBuilder<TEntity> AllowsFiltersOn<TProperty>(Expression<Func<TEntity, TProperty>> propertyExpression)
        => AllowsFiltersOn(string.Join('.', propertyExpression.GetMemberAccess<MemberInfo>().Select(m => m.Name)));
}
