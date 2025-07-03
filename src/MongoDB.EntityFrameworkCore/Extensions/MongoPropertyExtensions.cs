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
using MongoDB.EntityFrameworkCore;
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
    /// Returns the <see cref="BsonRepresentationConfiguration"/> the property is stored as when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the element name for.</param>
    /// <returns>Returns the <see cref="BsonRepresentationConfiguration"/> the property is stored as.</returns>
    public static BsonRepresentationConfiguration? GetBsonRepresentation(this IReadOnlyProperty property)
        => property[MongoAnnotationNames.BsonRepresentation] is IDictionary<string, object> value
            ? BsonRepresentationConfiguration.CreateFrom(value)
            : null;

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
    /// Gets the <see cref="DateTimeKind"/> of the <see cref="DateTime"/> property is mapped to when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the <see cref="DateTimeKind"/> for.</param>
    public static DateTimeKind GetDateTimeKind(this IReadOnlyProperty property)
    {
        var dateTimeKindAnnotation = property.FindAnnotation(MongoAnnotationNames.DateTimeKind);
        return dateTimeKindAnnotation?.Value == null ? DateTimeKind.Unspecified : (DateTimeKind)dateTimeKindAnnotation.Value;
    }

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
    /// Gets the <see cref="ConfigurationSource" /> of the <see cref="DateTimeKind"/> for the property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the source of <see cref="DateTimeKind"/> for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the <see cref="DateTimeKind"/> was specified by for this property.
    /// </returns>
    public static ConfigurationSource? GetDateKindConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.DateTimeKind)?.GetConfigurationSource();


    /// <summary>
    /// Returns the encryption data key id used to encrypt the property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the encryption data key id for.</param>
    /// <returns>The encryption data key id used to encrypt the property, or <see langword="null"/> if not set.</returns>
    public static Guid? GetEncryptionDataKeyId(this IReadOnlyProperty property)
        => property[MongoAnnotationNames.EncryptionDataKeyId] as Guid?;

    /// <summary>
    /// Sets the encryption data key id used to encrypt the property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the encryption data key id for.</param>
    /// <param name="dataKeyId">The encryption data key id to set, or <see langword="null" /> to unset the value.</param>
    public static void SetEncryptionDataKeyId(
        this IMutableProperty property,
        Guid? dataKeyId)
        => property.SetOrRemoveAnnotation(MongoAnnotationNames.EncryptionDataKeyId, Check.NotEmpty(dataKeyId));

    /// <summary>
    /// Sets the encryption data key id used to encrypt the property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the BsonType for.</param>
    /// <param name="dataKeyId">The encryption data key id to set, or <see langword="null" /> to unset the value.</param>
    /// <returns>The <see cref="Guid"/> encryption data key id for the property if set, or <see langref="null"/> if no value is set.</returns>
    public static Guid? SetEncryptionDataKeyId(
        this IConventionProperty property,
        Guid? dataKeyId)
        => (Guid?)property.SetOrRemoveAnnotation(MongoAnnotationNames.EncryptionDataKeyId, Check.NotEmpty(dataKeyId))?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> of the encryption data key id when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the encryption data key id for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the encryption data key id was specified by for this property.
    /// </returns>
    public static ConfigurationSource? GetEncryptionDataKeyIdConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.EncryptionDataKeyId)?.GetConfigurationSource();


    /// <summary>
    /// Returns the <see cref="QueryableEncryptionType"/> indicating the type of Queryable Encryption for this property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the Queryable Encryption type for.</param>
    /// <returns>The <see cref="QueryableEncryptionType"/>, or <see langword="null"/> if not set.</returns>
    public static QueryableEncryptionType? GetQueryableEncryptionType(this IReadOnlyProperty property)
        => property[MongoAnnotationNames.QueryableEncryptionType] as QueryableEncryptionType?;

    /// <summary>
    /// Sets the <see cref="QueryableEncryptionType"/> indicating the type of Queryable Encryption for this property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the Queryable Encryption type for.</param>
    /// <param name="queryableEncryptionType">The <see cref="QueryableEncryptionType"/> specifying the type of Queryable Encryption to use, or <see langword="null" /> to unset the value.</param>
    public static void SetQueryableEncryptionType(
        this IMutableProperty property,
        QueryableEncryptionType? queryableEncryptionType)
        => property.SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionType, Check.IsDefinedOrNull(queryableEncryptionType));

    /// <summary>
    /// Sets the <see cref="QueryableEncryptionType"/> indicating the type of Queryable Encryption for this property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the Queryable Encryption type for.</param>
    /// <param name="queryableEncryptionType">The <see cref="QueryableEncryptionType"/> specifying the type of Queryable Encryption to use, or <see langword="null" /> to unset the value.</param>
    /// <returns>The <see cref="QueryableEncryptionType"/>, or <see langword="null"/> if not set.</returns>
    public static QueryableEncryptionType? SetQueryableEncryptionType(
        this IConventionProperty property,
        QueryableEncryptionType? queryableEncryptionType)
        => (QueryableEncryptionType?)property
            .SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionType, Check.IsDefinedOrNull(queryableEncryptionType))?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> of the Queryable Encryption type for the property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the Queryable Encryption type for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the Queryable Encryption type was specified for this property.
    /// </returns>
    public static ConfigurationSource? GetQueryableEncryptionTypeConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionType)?.GetConfigurationSource();


    /// <summary>
    /// Returns the specified maximum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the maximum value for.</param>
    /// <returns>The specified maximum value, or <see langword="null"/> if not set.</returns>
    public static object? GetQueryableEncryptionRangeMax(this IReadOnlyProperty property)
        => property[MongoAnnotationNames.QueryableEncryptionRangeMax];

    /// <summary>
    /// Sets the maximum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the maximum value for.</param>
    /// <param name="maxValue">The maximum value for the Queryable Encryption range property, or <see langword="null" /> to unset the value.</param>
    public static void SetQueryableEncryptionRangeMax(
        this IMutableProperty property,
        object? maxValue)
        => property.SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax, maxValue);

    /// <summary>
    /// Sets the maximum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the maximum value for.</param>
    /// <param name="maxValue">The maximum value for the Queryable Encryption range property, or <see langword="null" /> to unset the value.</param>
    /// <returns>The maximum value for the Queryable Encryption range property, or <see langword="null"/> if not set.</returns>
    public static object? SetQueryableEncryptionRangeMax(
        this IConventionProperty property,
        object? maxValue)
        => (QueryableEncryptionType?)property
            .SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax, maxValue)?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> of the maximum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the maximum value for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the maximum value that was specified for this property.
    /// </returns>
    public static ConfigurationSource? GetQueryableEncryptionRangeMaxConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax)?.GetConfigurationSource();


    /// <summary>
    /// Returns the specified minimum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IReadOnlyProperty"/> to obtain the minimum value for.</param>
    /// <returns>The specified minimum value, or <see langword="null"/> if not set.</returns>
    public static object? GetQueryableEncryptionRangeMin(this IReadOnlyProperty property)
        => property[MongoAnnotationNames.QueryableEncryptionRangeMin];

    /// <summary>
    /// Sets the minimum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IMutableProperty"/> to set the minimum value for.</param>
    /// <param name="minValue">The minimum value for the Queryable Encryption range property, or <see langword="null" /> to unset the value.</param>
    public static void SetQueryableEncryptionRangeMin(
        this IMutableProperty property,
        object? minValue)
        => property.SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin, minValue);

    /// <summary>
    /// Sets the minimum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to set the minimum value for.</param>
    /// <param name="minValue">The minimum value for the Queryable Encryption range property, or <see langword="null" /> to unset the value.</param>
    /// <returns>The minimum value for the Queryable Encryption range property, or <see langword="null"/> if not set.</returns>
    public static object? SetQueryableEncryptionRangeMin(
        this IConventionProperty property,
        object? minValue)
        => (QueryableEncryptionType?)property
            .SetOrRemoveAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin, minValue)?.Value;

    /// <summary>
    /// Gets the <see cref="ConfigurationSource" /> of the minimum value of a Queryable Encryption range property when targeting MongoDB.
    /// </summary>
    /// <param name="property">The <see cref="IConventionProperty"/> to obtain the minimum value for.</param>
    /// <returns>
    /// The <see cref="ConfigurationSource" /> the minimum value that was specified for this property.
    /// </returns>
    public static ConfigurationSource? GetQueryableEncryptionRangeMinConfigurationSource(this IConventionProperty property)
        => property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin)?.GetConfigurationSource();

    private static string GetDefaultElementName(IReadOnlyProperty property) =>
        property switch
        {
            _ when property.IsOwnedTypeKey() => "",
            _ when property.IsRowVersion() => RowVersion.DefaultElementName,
            _ when property.IsTypeDiscriminator() => "_t",
            _ => property.Name
        };

    private static bool IsTypeDiscriminator(this IReadOnlyProperty property)
    {
        if (property.DeclaringType is not IEntityType entityType) return false;
        var discriminatorProperty = entityType.FindDiscriminatorProperty();
        return property == discriminatorProperty;
    }

    internal static bool IsOwnedTypeKey(this IReadOnlyProperty property)
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

    internal static bool IsOwnedCollectionShadowKey(this IReadOnlyProperty property) =>
        property.FindContainingPrimaryKey()
            is { Properties.Count: > 1 } && !property.IsForeignKey()
                                         && property.ClrType == typeof(int)
                                         && (property.ValueGenerated & ValueGenerated.OnAdd) != 0
                                         && property.GetElementName().Length == 0;
}
