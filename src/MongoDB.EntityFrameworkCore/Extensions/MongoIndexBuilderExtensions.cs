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

using System;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// MongoDB specific extension methods for <see cref="IndexBuilder" />.
/// </summary>
public static class MongoIndexBuilderExtensions
{
    /// <summary>
    /// Configures the <see cref="CreateIndexOptions"/> for the index.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="options">The <see cref="CreateIndexOptions"/> for this index.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder HasCreateIndexOptions(
        this IndexBuilder indexBuilder,
        CreateIndexOptions? options)
    {
        indexBuilder.Metadata.SetCreateIndexOptions(options);

        return indexBuilder;
    }

    /// <summary>
    /// Configures the <see cref="CreateIndexOptions"/> for the index.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being configured.</typeparam>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="options">The <see cref="CreateIndexOptions"/> for this index.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder<TEntity> HasCreateIndexOptions<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        CreateIndexOptions options)
        => (IndexBuilder<TEntity>)HasCreateIndexOptions((IndexBuilder)indexBuilder, options);

    /// <summary>
    /// Returns a value indicating whether the given <see cref="CreateIndexOptions"/> can be set for the index.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="options">The <see cref="CreateIndexOptions"/> for the index.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the given name can be set for the index.</returns>
    public static bool CanSetCreateIndexOptions(
        this IConventionIndexBuilder indexBuilder,
        CreateIndexOptions? options,
        bool fromDataAnnotation = false)
        => indexBuilder.CanSetAnnotation(MongoAnnotationNames.CreateIndexOptions, options, fromDataAnnotation);

    /// <summary>
    /// Configures the index as an Atlas Vector Search index with the given similarity function and dimensions.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="similarity">The <see cref="VectorSimilarity"/> to use to search for top K-nearest neighbors.</param>
    /// <param name="dimensions">Number of vector dimensions, between 1 and 8192.</param>
    /// <returns>A builder to further configure the vector index options.</returns>
    public static VectorIndexBuilder IsVectorIndex(
        this IndexBuilder indexBuilder,
        VectorSimilarity similarity,
        int dimensions)
    {
        var indexOptions = new VectorIndexOptions(similarity, dimensions);
        indexBuilder.Metadata.SetVectorIndexOptions(indexOptions);

        return new VectorIndexBuilder(indexBuilder.Metadata, indexOptions);
    }

    /// <summary>
    /// Configures the index as an Atlas Vector Search index with the given similarity function and dimensions.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="similarity">The <see cref="VectorSimilarity"/> to use to search for top K-nearest neighbors.</param>
    /// <param name="dimensions">Number of vector dimensions, between 1 and 8192.</param>
    /// <param name="buildAction">A nested builder to configure vector index options.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder IsVectorIndex(
        this IndexBuilder indexBuilder,
        VectorSimilarity similarity,
        int dimensions,
        Action<VectorIndexBuilder> buildAction)
    {
        buildAction(indexBuilder.IsVectorIndex(similarity, dimensions));
        return indexBuilder;
    }

    /// <summary>
    /// Configures the index as an Atlas Vector Search index with the given similarity function and dimensions.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="similarity">The <see cref="VectorSimilarity"/> to use to search for top K-nearest neighbors.</param>
    /// <param name="dimensions">Number of vector dimensions, between 1 and 8192.</param>
    /// <param name="buildAction">A nested builder to configure vector index options.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder<TEntity> IsVectorIndex<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        VectorSimilarity similarity,
        int dimensions,
        Action<VectorIndexBuilder> buildAction)
        => (IndexBuilder<TEntity>)((IndexBuilder)indexBuilder).IsVectorIndex(similarity, dimensions, buildAction);

    /// <summary>
    /// Configures the index as an Atlas Vector Search index with the given similarity function and dimensions.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="similarity">The <see cref="VectorSimilarity"/> to use to search for top K-nearest neighbors.</param>
    /// <param name="dimensions">Number of vector dimensions, between 1 and 8192.</param>
    /// <returns>A builder to further configure the vector index options.</returns>
    public static VectorIndexBuilder<TEntity> IsVectorIndex<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        VectorSimilarity  similarity,
        int dimensions)
    {
        var indexOptions = new VectorIndexOptions(similarity, dimensions);
        indexBuilder.Metadata.SetVectorIndexOptions(indexOptions);

        return new VectorIndexBuilder<TEntity>(indexBuilder.Metadata, indexOptions);
    }

    /// <summary>
    /// Configures the index as an Atlas Vector Search index with the given similarity function and dimensions.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="similarity">The <see cref="VectorSimilarity"/> to use to search for top K-nearest neighbors.</param>
    /// <param name="dimensions">Number of vector dimensions, between 1 and 8192.</param>
    /// <param name="buildAction">A nested builder to configure vector index options.</param>
    /// <returns>A builder to further configure the index.</returns>
    public static IndexBuilder<TEntity> IsVectorIndex<TEntity>(
        this IndexBuilder<TEntity> indexBuilder,
        VectorSimilarity  similarity,
        int dimensions,
        Action<VectorIndexBuilder<TEntity>> buildAction)
    {
        buildAction(indexBuilder.IsVectorIndex(similarity, dimensions));
        return indexBuilder;
    }

    /// <summary>
    /// Configures the index as an Atlas Vector Search index with the given similarity function and dimensions.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="indexOptions">The <see cref="VectorIndexOptions"/> options to use for the Atlas Search Vector Index.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The same builder instance if the configuration was applied, <see langword="null" /> otherwise.
    /// </returns>
    public static IConventionIndexBuilder? IsVectorIndex(
        this IConventionIndexBuilder indexBuilder,
        VectorIndexOptions? indexOptions,
        bool fromDataAnnotation = false)
    {
        if (indexBuilder.CanSetIsVectorIndex(indexOptions, fromDataAnnotation))
        {
            indexBuilder.Metadata.SetVectorIndexOptions(indexOptions, fromDataAnnotation);
            return indexBuilder;
        }

        return null;
    }

    /// <summary>
    /// Returns a value indicating whether the vector index can be configured.
    /// </summary>
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="indexOptions">The <see cref="VectorIndexOptions"/> options to use for the Atlas Search Vector Index.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the index can be configured for vectors.</returns>
    public static bool CanSetIsVectorIndex(
        this IConventionIndexBuilder indexBuilder,
        VectorIndexOptions? indexOptions,
        bool fromDataAnnotation = false)
        => indexBuilder.CanSetAnnotation(MongoAnnotationNames.VectorIndexOptions, indexOptions, fromDataAnnotation);
}
