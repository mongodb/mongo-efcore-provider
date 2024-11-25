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

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
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
    /// <param name="indexBuilder">The builder for the index being configured.</param>
    /// <param name="options">The <see cref="CreateIndexOptions"/> for this index.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>
    /// The same builder instance if the configuration was applied, <see langword="null" /> otherwise.
    /// </returns>
    public static IConventionIndexBuilder? HasCreateIndexOptions(
        this IConventionIndexBuilder indexBuilder,
        CreateIndexOptions<BsonDocument>? options,
        bool fromDataAnnotation = false)
    {
        if (!indexBuilder.CanSetCreateIndexOptions(options, fromDataAnnotation))
        {
            return null;
        }

        indexBuilder.Metadata.SetCreateIndexOptions(options, fromDataAnnotation);
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
}
