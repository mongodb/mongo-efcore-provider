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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Storage;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Property extension methods for MongoDB metadata.
/// </summary>
public static class MongoPropertyExtensions
{
    /// <summary>
    /// Returns the document element name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the element name for.</param>
    /// <returns>Returns the element name that the property is mapped to.</returns>
    public static string GetElementName(this IReadOnlyProperty property)
        => (string?)property[MongoAnnotationNames.ElementName]
           ?? GetDefaultElementName(property);

    private static string GetDefaultElementName(IReadOnlyProperty property)
    {
        var entityType = (IReadOnlyEntityType)property.DeclaringType;
        var ownership = entityType.FindOwnership();

        if (ownership != null && !entityType.IsDocumentRoot())
        {
            var pk = property.FindContainingPrimaryKey();
            if (pk != null
                && (property.ClrType == typeof(int) || ownership.Properties.Contains(property))
                && pk.Properties.Count == ownership.Properties.Count + (ownership.IsUnique ? 0 : 1)
                && ownership.Properties.All(fkProperty => pk.Properties.Contains(fkProperty)))
            {
                return "";
            }
        }

        return property.IsRowVersion() ? RowVersion.DefaultElementName : property.Name;
    }

    /// <summary>
    /// Returns the <see cref="BsonRepresentationConfiguration"/> the property is stored as when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the element name for.</param>
    /// <returns>Returns the <see cref="BsonRepresentationConfiguration"/> the property is stored as.</returns>
    public static BsonRepresentationConfiguration? GetBsonRepresentation(this IReadOnlyProperty property)
        => property[MongoAnnotationNames.BsonRepresentation] is IDictionary<string, object> value
            ? BsonRepresentationConfiguration.CreateFrom(value)
            : null;

    internal static bool IsOwnedTypeKey(this IProperty property)
    {
        var entityType = (IReadOnlyEntityType)property.DeclaringType;
        if (entityType.IsDocumentRoot())
        {
            return false;
        }

        var ownership = entityType.FindOwnership();
        if (ownership == null)
        {
            return false;
        }

        var pk = property.FindContainingPrimaryKey();
        return pk != null
               && (property.ClrType == typeof(int) || ownership.Properties.Contains(property))
               && pk.Properties.Count == ownership.Properties.Count + (ownership.IsUnique ? 0 : 1)
               && ownership.Properties.All(fkProperty => pk.Properties.Contains(fkProperty));
    }

    /// <summary>
    ///  Sets the document element name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the element name for.</param>
    /// <param name="name">The name of the element that should be used.</param>
    public static void SetElementName(this IMutableProperty property, string? name)
        => property.SetOrRemoveAnnotation(MongoAnnotationNames.ElementName, name);

    /// <summary>
    ///  Sets the document element name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the element name for.</param>
    /// <param name="name">The name of the element that should be used.</param>
    /// <param name="fromDataAnnotation"><see langword="true"/> if the configuration was specified using a data annotation, <see langword="false"/> if not.</param>
    /// <returns>The configured element name.</returns>
    public static string? SetElementName(
        this IConventionProperty property,
        string? name,
        bool fromDataAnnotation = false)
        => (string?)property.SetOrRemoveAnnotation(MongoAnnotationNames.ElementName, name, fromDataAnnotation)?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> the document element name that the property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the element name for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the element name was specified by for this property.
    /// </returns>
    public static ConfigurationSource? GetElementNameConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.ElementName)?.GetConfigurationSource();

    /// <summary>
    /// Sets the BSON representation for the property to configure how it is stored within MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the BsonType for.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> this property should be stored as
    /// or <see langword="null" /> to unset the value and use the default.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    public static void SetBsonRepresentation(
        this IMutableProperty property,
        BsonType? bsonType,
        bool? allowOverflow,
        bool? allowTruncation)
    {
        if (bsonType == null)
        {
            property.RemoveAnnotation(MongoAnnotationNames.BsonRepresentation);
            return;
        }

        var representation = new BsonRepresentationConfiguration(bsonType.Value, allowOverflow, allowTruncation);
        property.SetAnnotation(MongoAnnotationNames.BsonRepresentation, representation.ToDictionary());
    }

    /// <summary>
    /// Sets the BSON representation for the property to configure how it is stored within MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the BsonType for.</param>
    /// <param name="bsonType">The <see cref="BsonType"/> this property should be stored as
    /// or <see langword="null" /> to unset the value and use the default.</param>
    /// <param name="allowOverflow">Whether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    /// <param name="fromDataAnnotation"><see langword="true"/> if the configuration was specified using a data annotation, <see langword="false"/> if not.</param>
    /// <returns>The <see cref="BsonRepresentationConfiguration"/> configured how data on the property will be stored within MongoDB.</returns>
    public static BsonRepresentationConfiguration? SetBsonRepresentation(
        this IConventionProperty property,
        BsonType? bsonType,
        bool? allowOverflow,
        bool? allowTruncation,
        bool fromDataAnnotation = false)
    {
        if (bsonType == null)
        {
            property.RemoveAnnotation(MongoAnnotationNames.BsonRepresentation);
            return null;
        }

        var representation = new BsonRepresentationConfiguration(bsonType.Value, allowOverflow, allowTruncation);
        property.SetAnnotation(MongoAnnotationNames.BsonRepresentation, representation.ToDictionary(), fromDataAnnotation);
        return representation;
    }

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> of the <see cref="BsonType"/> for the property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the storage <see cref="BsonType"/> for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the <see cref="BsonType"/> was specified by for this property.
    /// </returns>
    public static ConfigurationSource? GetBsonRepresentationConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.BsonRepresentation)?.GetConfigurationSource();

    /// <summary>
    /// Sets the <see cref="DateTimeKind"/> of the DateTime property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the DateTimeKind for.</param>
    /// <param name="dateTimeKind">The <see cref="DateTimeKind"/> that should be used.</param>
    /// <exception cref="InvalidOperationException"></exception>
    public static void SetDateTimeKind(this IMutableProperty property, DateTimeKind dateTimeKind)
    {
        if (property.ClrType != typeof(DateTime) && property.ClrType != typeof(DateTime?))
        {
            throw new InvalidOperationException($"Cannot apply DateTimeKind annotation for non-DateTime field {property.Name} of {
                property.DeclaringType.Name} entity.");
        }

        property.SetAnnotation(MongoAnnotationNames.DateTimeKind, dateTimeKind);
    }

    /// <summary>
    /// Sets the <see cref="DateTimeKind"/> of the <see cref="DateTime"/> property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the DateTimeKind for.</param>
    /// <param name="dateTimeKind">The <see cref="DateTimeKind"/> that should be used.</param>
    /// <exception cref="InvalidOperationException"></exception>
    public static void SetDateTimeKind(this IConventionProperty property, DateTimeKind dateTimeKind)
    {
        if (property.ClrType != typeof(DateTime) && property.ClrType != typeof(DateTime?))
        {
            throw new InvalidOperationException($"Cannot apply DateTimeKind annotation for non-DateTime field {property.Name} of {
                property.DeclaringType.Name} entity.");
        }

        property.SetAnnotation(MongoAnnotationNames.DateTimeKind, dateTimeKind);
    }

    /// <summary>
    /// Gets the <see cref="DateTimeKind"/> of the <see cref="DateTime"/> property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the <see cref="DateTimeKind"/> for.</param>
    public static DateTimeKind GetDateTimeKind(this IReadOnlyProperty property)
    {
        var dateTimeKindAnnotation = property.FindAnnotation(MongoAnnotationNames.DateTimeKind);
        return dateTimeKindAnnotation?.Value == null ? DateTimeKind.Unspecified : (DateTimeKind)dateTimeKindAnnotation.Value;
    }

    internal static bool IsOwnedCollectionShadowKey(this IReadOnlyProperty property)
    {
        return property.FindContainingPrimaryKey()
                is {Properties.Count: > 1} && !property.IsForeignKey()
                                           && property.ClrType == typeof(int)
                                           && (property.ValueGenerated & ValueGenerated.OnAdd) != 0
                                           && property.GetElementName().Length == 0;
    }
}
