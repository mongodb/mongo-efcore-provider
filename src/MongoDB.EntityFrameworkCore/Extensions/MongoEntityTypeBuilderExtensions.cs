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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.EntityFrameworkCore.Metadata;

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
            throw new ArgumentException(ArgumentNameCannotBeEmptyExceptionMessage);

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
            throw new ArgumentException(ArgumentNameCannotBeEmptyExceptionMessage);

        return (string?)entityType.SetAnnotation(
            MongoAnnotationNames.CollectionName,
            name,
            fromDataAnnotation)?.Value;
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
            throw new ArgumentException(ArgumentNameCannotBeEmptyExceptionMessage);

        return entityTypeBuilder.CanSetAnnotation(MongoAnnotationNames.CollectionName, name, fromDataAnnotation);
    }
}
