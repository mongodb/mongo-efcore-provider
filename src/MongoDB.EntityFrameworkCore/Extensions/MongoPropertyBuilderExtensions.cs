﻿/* Copyright 2023-present MongoDB Inc.
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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Metadata;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extension methods for <see cref="PropertyBuilder" />.
/// </summary>
public static class MongoPropertyBuilderExtensions
{
    /// <summary>
    /// Configures the document element that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The element name for the property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder HasElementName(
        this PropertyBuilder propertyBuilder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        propertyBuilder.Metadata.SetElementName(name);
        return propertyBuilder;
    }

    /// <summary>
    /// Configures the document element that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <typeparam name="TProperty">The type of the property being configured.</typeparam>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The element name for the property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<TProperty> HasElementName<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder,
        string name)
        => (PropertyBuilder<TProperty>)HasElementName((PropertyBuilder)propertyBuilder, name);

    /// <summary>
    /// Configures the document element that the property is mapped to when targeting MongoDB.
    /// If an empty string is supplied then the property will not be persisted.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The element name for the property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The same builder instance if the configuration was applied, <see langword="null" /> otherwise.</returns>
    public static IConventionPropertyBuilder? HasElementName(
        this IConventionPropertyBuilder propertyBuilder,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (!CanSetElementName(propertyBuilder, name, fromDataAnnotation))
        {
            return null;
        }

        propertyBuilder.Metadata.SetElementName(name, fromDataAnnotation);
        return propertyBuilder;
    }

    /// <summary>
    /// Returns a value indicating whether the given document element name can be set.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The element name for the property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the field name can be set, <see langword="false"/> if not.</returns>
    public static bool CanSetElementName(
        this IConventionPropertyBuilder propertyBuilder,
        string? name,
        bool fromDataAnnotation = false)
        => propertyBuilder.CanSetAnnotation(MongoAnnotationNames.ElementName, name, fromDataAnnotation);

    /// <summary>
    /// Configures the BSON representation that the property is stored as when targeting MongoDB.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> to store this property as
    /// or <see langword="null" /> to unset the configuration and use the default.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder HasBsonRepresentation(
        this PropertyBuilder propertyBuilder,
        BsonType? bsonType,
        bool? allowOverflow = null,
        bool? allowTruncation = null)
    {
        propertyBuilder.Metadata.SetBsonRepresentation(bsonType, allowOverflow, allowTruncation);
        return propertyBuilder;
    }

    /// <summary>
    /// Configures the BSON representation that the property is stored as when targeting MongoDB.
    /// </summary>
    /// <typeparam name="TProperty">The type of the property being configured.</typeparam>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> to store this property as
    /// or <see langword="null" /> to unset the configuration and use the default.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<TProperty> HasBsonRepresentation<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder,
        BsonType? bsonType,
        bool? allowOverflow = null,
        bool? allowTruncation = null)
        => (PropertyBuilder<TProperty>)HasBsonRepresentation((PropertyBuilder)propertyBuilder, bsonType, allowOverflow, allowTruncation);

    /// <summary>
    /// Configures the BSON representation that the property is stored as when targeting MongoDB.
    /// If a null <see cref="BsonType"/> is supplied then a default storage type based on the property type will be used.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> to store this property as.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>The same builder instance if the configuration was applied, <see langword="null" /> otherwise.</returns>
    public static IConventionPropertyBuilder? HasBsonRepresentation(
        this IConventionPropertyBuilder propertyBuilder,
        BsonType? bsonType,
        bool? allowOverflow = null,
        bool? allowTruncation = null,
        bool fromDataAnnotation = false)
    {
        if (!CanSetBsonRepresentation(propertyBuilder, bsonType, fromDataAnnotation))
        {
            return null;
        }

        propertyBuilder.Metadata.SetBsonRepresentation(bsonType, allowOverflow, allowTruncation, fromDataAnnotation);
        return propertyBuilder;
    }

    /// <summary>
    /// Returns a value indicating whether the given <see cref="BsonType"/> can be set.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> to store this property as.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the <see cref="BsonType"/> can be set, <see langword="false"/> if not.</returns>
    public static bool CanSetBsonRepresentation(
        this IConventionPropertyBuilder propertyBuilder,
        BsonType? bsonType,
        bool fromDataAnnotation = false)
        => propertyBuilder.CanSetAnnotation(MongoAnnotationNames.BsonRepresentation, bsonType, fromDataAnnotation);

    /// <summary>
    /// Configures the <see cref="DateTimeKind"/> for the property.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="dateTimeKind">The <see cref="DateTimeKind"/> to use for the property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder HasDateTimeKind(
        this PropertyBuilder propertyBuilder,
        DateTimeKind dateTimeKind)
    {
        propertyBuilder.Metadata.SetDateTimeKind(dateTimeKind);
        return propertyBuilder;
    }

    /// <summary>
    /// Configures the <see cref="DateTimeKind"/> for the property.
    /// </summary>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="dateTimeKind">The <see cref="DateTimeKind"/> to use for the property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static IConventionPropertyBuilder HasDateTimeKind(
        this IConventionPropertyBuilder propertyBuilder,
        DateTimeKind dateTimeKind)
    {
        propertyBuilder.Metadata.SetDateTimeKind(dateTimeKind);
        return propertyBuilder;
    }
}
