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
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata.Search;
using MongoDB.EntityFrameworkCore.Metadata.Search.Builders;
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// Mongo specific extension methods for <see cref="EntityTypeBuilder" />.
/// </summary>
public static class MongoEntityTypeBuilderExtensions
{
    const string ArgumentNameCannotBeEmptyExceptionMessage = "The string argument 'name' cannot be empty.";

    /// <summary>
    /// Configures the collection that the entity type maps to when targeting a MongoDB database.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the collection in MongoDB.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static EntityTypeBuilder ToCollection(
        this EntityTypeBuilder entityTypeBuilder,
        string? name)
    {
        if (name is {Length: 0})
            throw new ArgumentException(ArgumentNameCannotBeEmptyExceptionMessage, name);

        entityTypeBuilder.Metadata.SetCollectionName(name);

        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures the collection that the entity type maps to when targeting a MongoDB database.
    /// </summary>
    /// <typeparam name="TEntity">The entity type being configured.</typeparam>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the collection in MongoDB.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static EntityTypeBuilder<TEntity> ToCollection<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string? name)
        where TEntity : class
        => (EntityTypeBuilder<TEntity>)((EntityTypeBuilder)entityTypeBuilder).ToCollection(name);

    /// <summary>
    /// Configures the collection that the entity type maps to when targeting a MongoDB database.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the collection.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The same builder instance if the configuration was applied, <see langword="null" /> otherwise.</returns>
    public static IConventionEntityTypeBuilder? ToCollection(
        this IConventionEntityTypeBuilder entityTypeBuilder,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (!entityTypeBuilder.CanSetCollection(name, fromDataAnnotation))
            return null;

        entityTypeBuilder.Metadata.SetCollectionName(name, fromDataAnnotation);
        return entityTypeBuilder;
    }

    /// <summary>
    ///  Returns whether the collection name can be set for this entity type using the specified configuration source.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the collection.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the configuration can be applied.</returns>
    public static bool CanSetCollection(
        this IConventionEntityTypeBuilder entityTypeBuilder,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (name is {Length: 0})
            throw new ArgumentException(ArgumentNameCannotBeEmptyExceptionMessage, name);

        return entityTypeBuilder.CanSetAnnotation(MongoAnnotationNames.CollectionName, name, fromDataAnnotation);
    }

    /// <summary>
    /// Configures the element name that the entity is mapped to when stored as an embedded document.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the parent element.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static OwnedNavigationBuilder HasElementName(
        this OwnedNavigationBuilder entityTypeBuilder,
        string? name)
    {
        entityTypeBuilder.OwnedEntityType.SetContainingElementName(name);

        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures the element name that the entity is mapped to when stored as an embedded document.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the parent element.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static OwnedNavigationBuilder<TOwnerEntity, TDependentEntity> HasElementName<TOwnerEntity, TDependentEntity>(
        this OwnedNavigationBuilder<TOwnerEntity, TDependentEntity> entityTypeBuilder,
        string? name)
        where TOwnerEntity : class
        where TDependentEntity : class
    {
        entityTypeBuilder.OwnedEntityType.SetContainingElementName(name);

        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures the element name that the entity is mapped to when stored as an embedded document.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the parent element.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The same builder instance if the configuration was applied, <see langword="null" /> otherwise.</returns>
    public static IConventionEntityTypeBuilder? HasElementName(
        this IConventionEntityTypeBuilder entityTypeBuilder,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (!entityTypeBuilder.CanSetElementName(name, fromDataAnnotation))
        {
            return null;
        }

        entityTypeBuilder.Metadata.SetContainingElementName(name, fromDataAnnotation);

        return entityTypeBuilder;
    }

    /// <summary>
    /// Returns a value indicating whether the parent element name to which the entity type is mapped to can be set
    /// from the current configuration source.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="name">The name of the element property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the configuration can be applied.</returns>
    public static bool CanSetElementName(
        this IConventionEntityTypeBuilder entityTypeBuilder,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (name is not null && name.Trim().Length == 0)
            throw new ArgumentException(ArgumentNameCannotBeEmptyExceptionMessage, name);

        return entityTypeBuilder.CanSetAnnotation(MongoAnnotationNames.ElementName, name, fromDataAnnotation);
    }

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexName">The index name, or <see langword="null" /> to use "default".</param>
    /// <returns>A builder to further configure the search index.</returns>
    public static SearchIndexBuilder HasSearchIndex(
        this EntityTypeBuilder entityTypeBuilder,
        string? indexName = null)
        => new(SearchIndexDefinition(entityTypeBuilder.Metadata, indexName));

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="buildAction">A nested builder to configure the search index.</param>
    /// <returns>A builder to further configure the entity type.</returns>
    public static EntityTypeBuilder HasSearchIndex(
        this EntityTypeBuilder entityTypeBuilder,
        Action<SearchIndexBuilder> buildAction)
        => entityTypeBuilder.HasSearchIndex(null, buildAction);

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexName">The index name, or <see langword="null" /> to use "default".</param>
    /// <param name="buildAction">A nested builder to configure the search index.</param>
    /// <returns>A builder to further configure the entity type.</returns>
    public static EntityTypeBuilder HasSearchIndex(
        this EntityTypeBuilder entityTypeBuilder,
        string? indexName,
        Action<SearchIndexBuilder> buildAction)
    {
        buildAction(entityTypeBuilder.HasSearchIndex(indexName));
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexName">The index name, or <see langword="null" /> to use "default".</param>
    /// <returns>A builder to further configure the search index.</returns>
    public static SearchIndexBuilder<TEntity> HasSearchIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string? indexName = null) where TEntity : class
        => new(SearchIndexDefinition(entityTypeBuilder.Metadata, indexName));

    private static SearchIndexDefinition SearchIndexDefinition(IMutableEntityType entityType, string? indexName)
    {
        indexName ??= "default";
        var indexDefinition = entityType.GetSearchIndexDefinition(indexName);

        if (indexDefinition is null)
        {
            indexDefinition = new SearchIndexDefinition(entityType, indexName);
            entityType.SetSearchIndexDefinition(indexDefinition);
        }

        return indexDefinition;
    }

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="buildAction">A nested builder to configure the search index.</param>
    /// <returns>A builder to further configure the entity type.</returns>
    public static EntityTypeBuilder<TEntity> HasSearchIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Action<SearchIndexBuilder<TEntity>> buildAction) where TEntity : class
        => entityTypeBuilder.HasSearchIndex(null, buildAction);

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="buildAction">A nested builder to configure the search index.</param>
    /// <returns>A builder to further configure the entity type.</returns>
    public static EntityTypeBuilder<TEntity> HasSearchIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Action<SearchIndexBuilder> buildAction) where TEntity : class
        => entityTypeBuilder.HasSearchIndex(null, buildAction);

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexName">The index name, or <see langword="null" /> to use "default".</param>
    /// <param name="buildAction">A nested builder to configure the search index.</param>
    /// <returns>A builder to further configure the entity type.</returns>
    public static EntityTypeBuilder<TEntity> HasSearchIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string? indexName,
        Action<SearchIndexBuilder> buildAction) where TEntity : class
    {
        buildAction(entityTypeBuilder.HasSearchIndex(indexName));
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures MongoDB search indexing for members of this type.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexName">The index name, or <see langword="null" /> to use "default".</param>
    /// <param name="buildAction">A nested builder to configure the search index.</param>
    /// <returns>A builder to further configure the entity type.</returns>
    public static EntityTypeBuilder<TEntity> HasSearchIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string? indexName,
        Action<SearchIndexBuilder<TEntity>> buildAction) where TEntity : class
    {
        buildAction(entityTypeBuilder.HasSearchIndex(indexName));
        return entityTypeBuilder;
    }

    /// <summary>
    /// Configures MongoDB search indexes.
    /// </summary>
    /// <remarks>
    /// See <see href="https://www.mongodb.com/docs/atlas/atlas-search"/> for more information about MongoDB search indexes.
    /// </remarks>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexDefinitions">The <see cref="SearchIndexDefinition"/> to use for the MongoDB search Indexes.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The same builder instance if the configuration was applied, <see langword="null" /> otherwise.
    /// </returns>
    public static IConventionEntityTypeBuilder? HasSearchIndexes(
        this IConventionEntityTypeBuilder entityTypeBuilder,
        IReadOnlyList<SearchIndexDefinition>? indexDefinitions,
        bool fromDataAnnotation = false)
    {
        if (entityTypeBuilder.CanHaveSearchIndexes(indexDefinitions, fromDataAnnotation))
        {
            entityTypeBuilder.Metadata.SetSearchIndexDefinition(indexDefinitions, fromDataAnnotation);
            return entityTypeBuilder;
        }

        return null;
    }

    /// <summary>
    /// Returns a value indicating whether the search indexes can be configured.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type being configured.</param>
    /// <param name="indexDefinitions">The <see cref="SearchIndexDefinition"/> to use for the search indexes.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the search index definition can be set.</returns>
    public static bool CanHaveSearchIndexes(
        this IConventionEntityTypeBuilder entityTypeBuilder,
        IReadOnlyList<SearchIndexDefinition>? indexDefinitions,
        bool fromDataAnnotation = false)
        => entityTypeBuilder.CanSetAnnotation(MongoAnnotationNames.SearchIndexDefinitions, indexDefinitions, fromDataAnnotation);
}
