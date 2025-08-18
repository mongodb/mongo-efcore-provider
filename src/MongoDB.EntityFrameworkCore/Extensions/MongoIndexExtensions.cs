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

using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// Index extension methods for MongoDB database metadata.
/// </summary>
public static class MongoIndexExtensions
{
    /// <summary>
    /// Sets the  <see cref="CreateIndexOptions"/> for the index.
    /// </summary>
    /// <param name="index">The <see cref="IConventionIndex"/> to set the options for.</param>
    /// <param name="createIndexOptions">The <see cref="CreateIndexOptions"/> containing the options for creating this index.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static CreateIndexOptions? SetCreateIndexOptions(
        this IConventionIndex index,
        CreateIndexOptions? createIndexOptions,
        bool fromDataAnnotation = false)
        => (CreateIndexOptions?)index
            .SetOrRemoveAnnotation(MongoAnnotationNames.CreateIndexOptions, createIndexOptions, fromDataAnnotation)?.Value;

    /// <summary>
    /// Sets the  <see cref="CreateIndexOptions"/> for the index.
    /// </summary>
    /// <param name="index">The <see cref="IMutableIndex"/> to set the options for.</param>
    /// <param name="createIndexOptions">The <see cref="CreateIndexOptions"/> containing the options for creating this index.</param>
    public static void SetCreateIndexOptions(this IMutableIndex index, CreateIndexOptions? createIndexOptions)
        => index.SetOrRemoveAnnotation(MongoAnnotationNames.CreateIndexOptions, createIndexOptions);

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> for the create index options of the index.
    /// </summary>
    /// <param name="index">The <see cref="IConventionIndex"/> to get the options for.</param>
    /// <returns>The <see cref="ConfigurationSource" /> for the name of the index in the database.</returns>
    public static ConfigurationSource? GetCreateIndexOptionsConfigurationSource(this IConventionIndex index)
        => index.FindAnnotation(MongoAnnotationNames.CreateIndexOptions)?.GetConfigurationSource();

    /// <summary>
    /// Gets the <see cref="CreateIndexOptions"/> options for the index if one is set.
    /// </summary>
    /// <param name="index">The <see cref="IConventionIndex"/> to set the options for.</param>
    /// <returns>The <see cref="CreateIndexModel{TDocument}"/> with the configured index options, or <see langword="null" /> if one is not set.</returns>
    public static CreateIndexOptions? GetCreateIndexOptions(this IConventionIndex index)
        => (CreateIndexOptions?)index.FindAnnotation(MongoAnnotationNames.CreateIndexOptions)?.Value;

    internal static CreateIndexOptions? GetCreateIndexOptions(this IIndex index)
        => (CreateIndexOptions?)index.FindAnnotation(MongoAnnotationNames.CreateIndexOptions)?.Value;

    /// <summary>
    ///     Returns the <see cref="VectorIndexOptions"/> for an Atlas Search Vector Index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns>The index options, or <see langword="null" /> if none is set.</returns>
    public static VectorIndexOptions? GetVectorIndexOptions(this IReadOnlyIndex index)
        => (VectorIndexOptions?)index[MongoAnnotationNames.VectorIndexOptions];

    /// <summary>
    ///     Sets the <see cref="VectorIndexOptions"/> for an Atlas Search Vector Index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="indexOptions">The options to use for an Atlas Search Vector Index.</param>
    public static void SetVectorIndexOptions(this IMutableIndex index, VectorIndexOptions? indexOptions)
        => index.SetAnnotation(MongoAnnotationNames.VectorIndexOptions, indexOptions);

    /// <summary>
    ///     Sets the vector index type to use an Atlas Search Vector Index.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="indexOptions">The options to use for the Atlas Search Vector Index.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static VectorIndexOptions? SetVectorIndexOptions(
        this IConventionIndex index,
        VectorIndexOptions? indexOptions,
        bool fromDataAnnotation = false)
        => (VectorIndexOptions?)index.SetAnnotation(MongoAnnotationNames.VectorIndexOptions, indexOptions, fromDataAnnotation)?.Value;

    /// <summary>
    ///     Returns the <see cref="ConfigurationSource" /> for <see cref="GetVectorIndexOptions" />.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The <see cref="ConfigurationSource" /> for <see cref="GetVectorIndexOptions" />.</returns>
    public static ConfigurationSource? GetVectorIndexOptionsConfigurationSource(this IConventionIndex property)
        => property.FindAnnotation(MongoAnnotationNames.VectorIndexOptions)?.GetConfigurationSource();
}
