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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Metadata;

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
        if (name is {Length: 0})
            throw new ArgumentException("The string argument 'name' cannot be empty.");

        entityType.SetAnnotation(MongoAnnotationNames.CollectionName, name);
    }

    /// <summary>
    /// Returns the name of the collection to which the entity type is mapped
    /// or <see langword="null" /> if not mapped to a collection.
    /// </summary>
    /// <param name="entityType">The entity type to get the collection name for.</param>
    /// <returns>The name of the collection to which the entity type is mapped.</returns>
    public static string GetCollectionName(this IReadOnlyEntityType entityType)
    {
        var nameAnnotation = entityType.FindAnnotation(MongoAnnotationNames.CollectionName);
        if (nameAnnotation?.Value != null)
            return (string)nameAnnotation.Value;

        return GetDefaultCollectionName(entityType);
    }

    /// <summary>
    /// Returns the default collection name that would be used for this entity type.
    /// </summary>
    /// <param name="entityType">The entity type to get the collection name for.</param>
    /// <returns>The default name of the collection to which the entity type would be mapped.</returns>
    public static string GetDefaultCollectionName(this IReadOnlyEntityType entityType)
        => entityType.HasSharedClrType ? entityType.ShortName() : entityType.ClrType.ShortDisplayName();

    /// <summary>
    /// Determines if an entity is a root document or whether it is an owned entity/complex type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyEntityType"/> to check.</param>
    /// <returns><see langref="true"/> if the entity is a root, <see langref="false"/> if it is owned.</returns>
    public static bool IsDocumentRoot(this IReadOnlyEntityType entityType)
        => entityType.BaseType?.IsDocumentRoot()
           ?? (entityType.FindOwnership() == null
               || entityType[MongoAnnotationNames.CollectionName] != null);

    /// <summary>
    /// Get the element name where this entity type lives - namely the navigation element of the
    /// entity that contains it.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyEntityType"/> to obtain the property name for.</param>
    /// <returns>The string name of the property that owns this.</returns>
    public static string? GetContainingElementName(this IReadOnlyEntityType entityType)
        => entityType[MongoAnnotationNames.ElementName] as string
           ?? GetDefaultContainingElementName(entityType);

    private static string? GetDefaultContainingElementName(IReadOnlyEntityType entityType)
        => entityType.FindOwnership() is IReadOnlyForeignKey ownership
            ? ownership.PrincipalToDependent!.Name
            : null;
}
