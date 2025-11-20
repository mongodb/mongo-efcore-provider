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
using System.Linq;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata.Search;
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Extensions;

/// <summary>
/// Entity type extension methods for MongoDB metadata.
/// </summary>
public static class MongoEntityTypeExtensions
{
    /// <summary>
    /// Sets the name of the collection to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The entity type to set the collection name for.</param>
    /// <param name="name">The name to set.</param>
    public static void SetCollectionName(this IMutableEntityType entityType, string? name)
    {
        if (name is { Length: 0 })
            throw new ArgumentException("The string argument 'name' cannot be empty.", name);

        entityType.SetAnnotation(MongoAnnotationNames.CollectionName, name);
    }

    /// <summary>
    /// Sets the name of the collection to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The entity type to set the collection name for.</param>
    /// <param name="name">The name to set.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured collection name.</returns>
    public static string? SetCollectionName(
        this IConventionEntityType entityType,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (name is {Length: 0})
            throw new ArgumentException($"The string argument '{nameof(name)}' cannot be empty.", name);

        return (string?)entityType.SetAnnotation(
            MongoAnnotationNames.CollectionName,
            name,
            fromDataAnnotation)?.Value;
    }

    /// <summary>
    /// Returns the name of the collection to which the entity type is mapped
    /// or <see langword="null" /> if not mapped to a collection.
    /// </summary>
    /// <param name="entityType">The entity type to get the collection name for.</param>
    /// <returns>The name of the collection to which the entity type is mapped.</returns>
    public static string GetCollectionName(this IReadOnlyEntityType entityType)
        => entityType.BaseType != null
            ? entityType.GetRootType().GetCollectionName()
            : (string?)entityType[MongoAnnotationNames.CollectionName]
              ?? GetDefaultCollectionName(entityType);

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> for the name of the collection to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The entity type to find configuration source for.</param>
    /// <returns>The <see cref="ConfigurationSource" /> for the name of the collection to which the entity type is mapped</returns>
    public static ConfigurationSource? GetCollectionNameConfigurationSource(this IConventionEntityType entityType)
        => entityType.FindAnnotation(MongoAnnotationNames.CollectionName)
            ?.GetConfigurationSource();

    /// <summary>
    /// Returns the default collection name that would be used for this entity type.
    /// </summary>
    /// <param name="entityType">The entity type to get the collection name for.</param>
    /// <returns>The default name of the collection to which the entity type would be mapped.</returns>
    public static string GetDefaultCollectionName(this IReadOnlyEntityType entityType)
    {
        var documentRoot = entityType.GetDocumentRoot();

        return documentRoot != entityType
            ? documentRoot.GetCollectionName()
            : entityType.HasSharedClrType
                ? entityType.ShortName()
                : entityType.ClrType.ShortDisplayName();
    }

    /// <summary>
    /// Determines if an entity is a root document or whether it is an owned entity/complex type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyEntityType"/> to check.</param>
    /// <returns><see langword="true"/> if the entity is a root, <see langword="false"/> if it is owned.</returns>
    public static bool IsDocumentRoot(this IReadOnlyEntityType entityType)
        => entityType.BaseType?.IsDocumentRoot()
           ?? entityType.FindOwnership() == null
           || entityType[MongoAnnotationNames.CollectionName] != null;

    /// <summary>
    /// Get the name of the parent element to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyEntityType"/> to obtain the property name for.</param>
    /// <returns>The string name of the property that owns this.</returns>
    public static string? GetContainingElementName(this IReadOnlyEntityType entityType)
        => entityType[MongoAnnotationNames.ElementName] as string
           ?? GetDefaultContainingElementName(entityType);

    private static string? GetDefaultContainingElementName(IReadOnlyEntityType entityType)
        => entityType.FindOwnership() is { } ownership
            ? ownership.PrincipalToDependent!.Name
            : null;

    /// <summary>
    /// Sets the name of the parent element to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The entity type to set the containing element name for.</param>
    /// <param name="name">The name to set.</param>
    public static void SetContainingElementName(this IMutableEntityType entityType, string? name)
    {
        if (name is not null && name.Trim().Length == 0)
            throw new ArgumentException($"The string argument '{nameof(name)}' cannot be empty.", name);

        entityType.SetOrRemoveAnnotation(MongoAnnotationNames.ElementName, name);
    }

    /// <summary>
    /// Sets the name of the parent element to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The entity type to set the containing element name for.</param>
    /// <param name="name">The name to set.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The new annotation or <see langword="null" /> if it was removed.</returns>
    public static string? SetContainingElementName(
        this IConventionEntityType entityType,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (name is not null && name.Trim().Length == 0)
            throw new ArgumentException($"The string argument '{nameof(name)}' cannot be empty.", name);

        return (string?)entityType.SetOrRemoveAnnotation(MongoAnnotationNames.ElementName, name, fromDataAnnotation)?.Value;
    }

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> for the parent element to which the entity type is mapped.
    /// </summary>
    /// <param name="entityType">The entity type to find configuration source for.</param>
    /// <returns>The <see cref="ConfigurationSource" /> for the parent element to which the entity type is mapped.</returns>
    public static ConfigurationSource? GetContainingElementNameConfigurationSource(this IConventionEntityType entityType)
        => entityType.FindAnnotation(MongoAnnotationNames.ElementName)
            ?.GetConfigurationSource();

    /// <summary>
    /// Gets the path from the root of the document to this, possibly nested, entity type.
    /// </summary>
    /// <param name="entityType">The entity type, which may be top-level or nested.</param>
    /// <returns>The path, which will be empty for top-level (root) entities.</returns>
    public static IReadOnlyList<string> GetDocumentPath(this IReadOnlyEntityType entityType)
    {
        var owner = entityType.FindOwnership();
        if (owner == null)
        {
            return [];
        }

        var path = new List<string>();
        do
        {
            path.Add(owner.DeclaringEntityType.GetContainingElementName()!);
            owner = owner.PrincipalEntityType.FindOwnership();
        } while (owner != null);

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Gets the entity type that maps directly to the MongoDB document for either the root itself or any nested entity types.
    /// </summary>
    /// <param name="entityType">The entity type, which may be top-level or nested.</param>
    /// <returns>The <see cref="IEntityType"/> mapped directly to the MongoDB document.</returns>
    public static IReadOnlyEntityType GetDocumentRoot(this IReadOnlyEntityType entityType)
    {
        while (entityType.FindOwnership() != null)
        {
            entityType = entityType.FindOwnership()!.PrincipalEntityType;
        }

        return entityType;
    }

    /// <summary>
    /// Gets the entity type that maps directly to the MongoDB document for either the root itself or any nested entity types.
    /// </summary>
    /// <param name="entityType">The entity type, which may be top-level or nested.</param>
    /// <returns>The <see cref="IEntityType"/> mapped directly to the MongoDB document.</returns>
    public static IMutableEntityType GetDocumentRoot(this IMutableEntityType entityType)
        => (IMutableEntityType)GetDocumentRoot((IReadOnlyEntityType)entityType);

    /// <summary>
    /// Gets the entity type that maps directly to the MongoDB document for either the root itself or any nested entity types.
    /// </summary>
    /// <param name="entityType">The entity type, which may be top-level or nested.</param>
    /// <returns>The <see cref="IEntityType"/> mapped directly to the MongoDB document.</returns>
    public static IConventionEntityType GetDocumentRoot(this IConventionEntityType entityType)
        => (IConventionEntityType)GetDocumentRoot((IMutableEntityType)entityType);

    /// <summary>
    /// Returns all the <see cref="SearchIndexDefinition"/> for a given entity type.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns>The search index definitions, which may be empty if none are set.</returns>
    public static IReadOnlyList<SearchIndexDefinition> GetSearchIndexDefinitions(this IReadOnlyEntityType entityType)
        => (IReadOnlyList<SearchIndexDefinition>?)entityType[MongoAnnotationNames.SearchIndexDefinitions] ?? [];

    /// <summary>
    /// Returns the <see cref="SearchIndexDefinition"/> with the given name.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="name">The index name.</param>
    /// <returns>The search index definition, or <see langword="null" /> if none is set.</returns>
    public static SearchIndexDefinition? GetSearchIndexDefinition(this IReadOnlyEntityType entityType, string name)
        => entityType.GetSearchIndexDefinitions().FirstOrDefault(e => e.Name == name);

    /// <summary>
    /// Sets the <see cref="SearchIndexDefinition"/> with the given name for a given entity type.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> to use.</param>
    public static void SetSearchIndexDefinition(this IMutableEntityType entityType, SearchIndexDefinition indexDefinition)
    {
        var indexDefinitions = entityType.GetSearchIndexDefinitions().ToList();
        for (var i = 0; i < indexDefinitions.Count; i++)
        {
            if (indexDefinitions[i].Name == indexDefinition.Name)
            {
                indexDefinitions[i] = indexDefinition;
                goto done;
            }
        }

        indexDefinitions.Add(indexDefinition);

        done:

        entityType.SetAnnotation(MongoAnnotationNames.SearchIndexDefinitions, indexDefinitions);
    }

    /// <summary>
    /// Removes the <see cref="SearchIndexDefinition"/> for the given name for a given entity type.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="indexName">The name of the index to remove.</param>
    public static string? RemoveSearchIndexDefinition(this IMutableEntityType entityType, string indexName)
    {
        var indexDefinitions = entityType.GetSearchIndexDefinitions().ToList();
        for (var i = 0; i < indexDefinitions.Count; i++)
        {
            if (indexDefinitions[i].Name == indexName)
            {
                indexDefinitions.Remove(indexDefinitions[i]);
                entityType.SetAnnotation(MongoAnnotationNames.SearchIndexDefinitions, indexDefinitions);
                return indexName;
            }
        }

        return null;
    }

    /// <summary>
    /// Removes the <see cref="SearchIndexDefinition"/> for the given name for a given entity type.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="indexName">The name of the index to remove.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static string? RemoveSearchIndexDefinition(
        this IConventionEntityType entityType,
        string indexName,
        bool fromDataAnnotation = false)
        => (fromDataAnnotation
                ? ConfigurationSource.Convention
                : ConfigurationSource.DataAnnotation)
            .Overrides(GetSearchIndexDefinitionConfigurationSource(entityType))
                ? RemoveSearchIndexDefinition((IMutableEntityType)entityType, indexName)
                : null;

    /// <summary>
    /// Sets all the <see cref="SearchIndexDefinition"/> for a given entity type.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> to use.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The configured value.</returns>
    public static IReadOnlyList<SearchIndexDefinition>? SetSearchIndexDefinition(
        this IConventionEntityType entityType,
        IReadOnlyList<SearchIndexDefinition>? indexDefinition,
        bool fromDataAnnotation = false)
        => (IReadOnlyList<SearchIndexDefinition>?)entityType.SetAnnotation(
            MongoAnnotationNames.SearchIndexDefinitions, indexDefinition, fromDataAnnotation)?.Value;

    /// <summary>
    /// Returns the <see cref="ConfigurationSource" /> for <see cref="GetSearchIndexDefinitions" />.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns>The <see cref="ConfigurationSource" /> for <see cref="GetSearchIndexDefinitions" />.</returns>
    public static ConfigurationSource? GetSearchIndexDefinitionConfigurationSource(this IConventionEntityType entityType)
        => entityType.FindAnnotation(MongoAnnotationNames.SearchIndexDefinitions)?.GetConfigurationSource();
}
