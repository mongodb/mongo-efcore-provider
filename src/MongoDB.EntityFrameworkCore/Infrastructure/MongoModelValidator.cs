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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Ensures rules required by the MongoDB provider are checked and enforced for a given <see cref="IModel"/>.
/// </summary>
public class MongoModelValidator : ModelValidator
{
    private static readonly Type[] UnsupportedConstructorAttributes = [typeof(BsonConstructorAttribute)];
    private static readonly Type[] UnsupportedMethodAttributes = [typeof(BsonFactoryMethodAttribute)];

    private static readonly Type[] UnsupportedClassAttributes =
    [
        typeof(BsonDictionaryOptionsAttribute),
        typeof(BsonDiscriminatorAttribute),
        typeof(BsonFactoryMethodAttribute),
        typeof(BsonKnownTypesAttribute),
        typeof(BsonMemberMapAttributeUsageAttribute),
        typeof(BsonNoIdAttribute),
        typeof(BsonSerializationOptionsAttribute),
        typeof(BsonSerializerAttribute)
    ];

    /// <summary>
    /// Create a <see cref="MongoModelValidator"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="ModelValidatorDependencies"/> required by this validator.</param>
    public MongoModelValidator(ModelValidatorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// Validate that the <paramref name="model"/> is correct and can be used by the
    /// MongoDB provider.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate for correctness.</param>
    /// <param name="logger">A logger to receive validation diagnostic information.</param>
    public override void Validate(IModel model, IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        base.Validate(model, logger);

        ValidateNoTablePerType(model);
        ValidateMaximumOneRowVersionPerEntity(model);
        ValidateNoUnsupportedAttributesOrAnnotations(model);
        ValidateElementNames(model);
        ValidateNoMutableKeys(model, logger);
        ValidatePrimaryKeys(model);
        ValidateQueryableEncryption(model, logger);
    }

    /// <summary>
    /// Validate that each entity has a maximum of one RowVersion.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate for correctness.</param>
    /// <exception cref="NotSupportedException">When an entity type has more than one RowVersion.</exception>
    private static void ValidateMaximumOneRowVersionPerEntity(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var rowVersionProperties = entityType.GetProperties().Where(RowVersion.IsRowVersion).ToArray();
            if (rowVersionProperties.Length > 1)
            {
                var propertyNames = string.Join(", ", rowVersionProperties.Select(p => p.Name));
                throw new NotSupportedException(
                    $"Entity '{entityType.DisplayName()}' has multiple properties '{propertyNames
                    }' configured as row versions. Only one row version property per entity is supported.");
            }
        }
    }

    /// <summary>
    /// Validate that no unsupported attributes or annotations are applied anywhere on the entities.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate for correctness.</param>
    /// <exception cref="NotSupportedException">When an unsupported attribute or annotation is encountered.</exception>
    private static void ValidateNoUnsupportedAttributesOrAnnotations(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            ValidateNoUnsupportedClassAttributes(entityType);
            ValidateNoUnsupportedConstructorAttributes(entityType);
            ValidateNoUnsupportedMethodAttributes(entityType);
            ValidateNoUnsupportedPropertyAnnotations(entityType);
        }
    }

    /// <summary>
    /// Validate that no unsupported attributes are defined on the entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyTypeBase"/> being validated.</param>
    /// <exception cref="NotSupportedException">When an unsupported attribute is encountered on the type.</exception>
    private static void ValidateNoUnsupportedClassAttributes(IReadOnlyTypeBase entityType)
    {
        var unsupported = FindUndefinedAttribute(entityType.ClrType.GetCustomAttributes(), UnsupportedClassAttributes);
        if (unsupported == null) return;

        var attributeTypeName = unsupported.GetType().ShortDisplayName();
        throw new NotSupportedException($"Entity '{entityType.DisplayName()}' is annotated with unsupported attribute '{
            attributeTypeName}'.");
    }

    /// <summary>
    /// Validate that no unsupported attributes are defined on the constructors of an entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyTypeBase"/> being validated.</param>
    /// <exception cref="NotSupportedException">When an unsupported attribute is encountered on a constructor of the type.</exception>
    private static void ValidateNoUnsupportedConstructorAttributes(IReadOnlyTypeBase entityType)
    {
        foreach (var constructor in entityType.ClrType.GetConstructors())
        {
            var unsupported = FindUndefinedAttribute(constructor.GetCustomAttributes(), UnsupportedConstructorAttributes);
            if (unsupported == null) continue;

            var attributeTypeName = unsupported.GetType().ShortDisplayName();
            throw new NotSupportedException(
                $"Entity '{entityType.DisplayName()}' has a constructor annotated with unsupported attribute '{attributeTypeName
                }'.");
        }
    }

    /// <summary>
    /// Validate that no unsupported attributes are defined on the methods of an entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyTypeBase"/> being validated.</param>
    /// <exception cref="NotSupportedException">When an unsupported attribute is encountered on a method of the type.</exception>
    private static void ValidateNoUnsupportedMethodAttributes(IReadOnlyTypeBase entityType)
    {
        foreach (var method in entityType.ClrType.GetMethods())
        {
            var unsupported = FindUndefinedAttribute(method.GetCustomAttributes(), UnsupportedMethodAttributes);
            if (unsupported == null) continue;

            var attributeTypeName = unsupported.GetType().ShortDisplayName();
            throw new NotSupportedException(
                $"Method '{entityType.DisplayName()}.{method.Name}' is annotated with unsupported attribute '{attributeTypeName
                }'.");
        }
    }

    private static Attribute? FindUndefinedAttribute(IEnumerable<Attribute> attributes, Type[] unsupported)
        => attributes.FirstOrDefault(a => unsupported.Any(u => u == a.GetType() || a.GetType().IsSubclassOf(u)));

    /// <summary>
    /// Validate that no unsupported annotations are defined on the properties of an entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IReadOnlyTypeBase"/> being validated.</param>
    /// <exception cref="NotSupportedException">When an unsupported attribute is encountered on a property of the type.</exception>
    private static void ValidateNoUnsupportedPropertyAnnotations(IReadOnlyTypeBase entityType)
    {
        // Properties use conventions and annotations instead of direct attribute checking to ensure flow through EF
        // mappings and configuration.
        foreach (var property in entityType.GetProperties().Where(p => !p.IsShadowProperty()))
        {
            if (property.FindAnnotation(MongoAnnotationNames.NotSupportedAttributes) is { Value: Attribute unsupported })
            {
                var attributeTypeName = unsupported.GetType().ShortDisplayName();
                throw new NotSupportedException(
                    $"Property '{entityType.DisplayName()}.{property.Name}' is annotated with unsupported attribute '{
                        attributeTypeName}'.");
            }
        }
    }

    /// <summary>
    /// Validate that required primary keys exist and that they are aligned with the
    /// requirement of MongoDB that they be named "_id".
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate primary key correctness.</param>
    /// <exception cref="NotSupportedException">Thrown when composite keys are encountered which are not supported yet.</exception>
    /// <exception cref="InvalidOperationException">Thrown when an entity requiring a key does not have one, or it is not mapped to "_id".</exception>
    private static void ValidatePrimaryKeys(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            ValidateEntityPrimaryKey(entityType);
        }
    }

    /// <summary>
    /// Validate that MongoDB Queryable Encryption is correctly configured for a model if being used.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate Queryable Encryption correctness.</param>
    /// <param name="logger">A logger to receive validation diagnostic information.</param>
    /// <exception cref="InvalidOperationException">Thrown when the model uses Queryable Encryption but the encryption configuration is not valid.</exception>
    private static void ValidateQueryableEncryption(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            ValidateEntityQueryableEncryption(entityType, false, false, [], logger);
        }
    }

    /// <summary>
    /// Validate that MongoDB Queryable Encryption is correctly configured for an entity if being used.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> being validated.</param>
    /// <param name="insideCollectionNavigation">Whether this entity is contained within a collection navigation somewhere in its hierarchy.</param>
    /// <param name="insideEncryptedOwnedEntity">Whether this entity is contained within an owned entity somewhere in its hierarchy.</param>
    /// <param name="usedDataKeys">The encryption data key ids already used to proactively validate re-use.</param>
    /// <param name="logger">A logger to receive validation diagnostic information.</param>
    /// <exception cref="InvalidOperationException">Thrown when the entity uses Queryable Encryption but the encryption configuration is not valid.</exception>
    private static void ValidateEntityQueryableEncryption(
        IEntityType entityType,
        bool insideCollectionNavigation,
        bool insideEncryptedOwnedEntity,
        HashSet<Guid> usedDataKeys,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var property in entityType.GetProperties())
        {
            var queryableEncryptionType = property.GetQueryableEncryptionType();
            if (queryableEncryptionType == null) continue;

            if (insideCollectionNavigation)
            {
                throw new InvalidOperationException(
                    PropertyOnEntity(property) +
                    " is to be stored inside an array as it is an owned entity in a collection navigation." +
                    " Queryable Encryption does not support encryption of elements within a BSON array.");
            }

            if (insideEncryptedOwnedEntity && queryableEncryptionType != QueryableEncryptionType.NotQueryable)
            {
                throw new InvalidOperationException(
                    PropertyOnEntity(property) +
                    " is to be stored inside an encrypted object as it is a property on an owned entity." +
                    " Queryable Encryption does not support alternative encryption of elements within an encrypted object.");
            }

            var dataKeyId = property.GetEncryptionDataKeyId();
            if (dataKeyId == null)
            {
                throw new InvalidOperationException(
                    PropertyOnEntity(property) + " is to be encrypted but no data key id has been specified.");
            }

            if (!usedDataKeys.Add(dataKeyId.Value))
            {
                throw new InvalidOperationException(
                    PropertyOnEntity(property) +
                    " specifies a data key id that has already been used on a different property or navigation.");
            }

            ValidatePropertyQueryableEncryptionType(property, queryableEncryptionType.Value, logger);
        }

        foreach (var navigation in entityType.GetNavigations())
        {
            if (navigation.IsEmbedded())
            {
                var isEncryptedOwnedEntity =
                    navigation.ForeignKey.GetQueryableEncryptionType() == QueryableEncryptionType.NotQueryable;

                if (isEncryptedOwnedEntity)
                {
                    var dataKeyId = navigation.ForeignKey.GetEncryptionDataKeyId();
                    if (dataKeyId == null)
                    {
                        throw new InvalidOperationException(
                            NavigationOnEntity(navigation) + " is to be encrypted but no data key id has been specified.");
                    }

                    if (!usedDataKeys.Add(dataKeyId.Value))
                    {
                        throw new InvalidOperationException(
                            NavigationOnEntity(navigation) +
                            " specifies a data key id that has already been used on a different property or navigation.");
                    }
                }

                ValidateEntityQueryableEncryption(navigation.TargetEntityType,
                    insideCollectionNavigation || navigation.IsCollection,
                    insideEncryptedOwnedEntity || isEncryptedOwnedEntity,
                    usedDataKeys,
                    logger);
            }
        }
    }

    /// <summary>
    /// Validate that MongoDB Queryable Encryption type is correctly configured for a property if being used.
    /// </summary>
    /// <param name="property">The <see cref="IProperty"/> being validated.</param>
    /// <param name="queryableEncryptionType">The <see cref="QueryableEncryptionType"/> configuration of the property.</param>
    /// <param name="logger">A logger to receive validation diagnostic information.</param>
    /// <exception cref="InvalidOperationException">Thrown when the property uses Queryable Encryption but the encryption configuration is not valid.</exception>
    private static void ValidatePropertyQueryableEncryptionType(
        IProperty property,
        QueryableEncryptionType queryableEncryptionType,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var storageType = BsonTypeHelper.GetBsonType(property);
        if (storageType == BsonType.Array)
        {
            throw new InvalidOperationException(
                PropertyOnEntity(property)
                + " is to be stored as an array."
                + " Queryable Encryption does not support encryption of elements within a BSON array.");
        }

        if (property.IsNullable)
        {
            logger.EncryptedNullablePropertyEncountered(property);
        }

        switch (queryableEncryptionType)
        {
            case QueryableEncryptionType.NotQueryable:
                break;

            case QueryableEncryptionType.Equality:
                ValidatePropertyForEqualityQueryableEncryption(property);
                break;

            case QueryableEncryptionType.Range:
                ValidatePropertyForRangeQueryableEncryption(property, logger);
                break;

            default:
                throw new InvalidOperationException(
                    PropertyOnEntity(property) + $" has unsupported query type '{queryableEncryptionType}'.");
        }
    }

    /// <summary>
    /// Validate that MongoDB Queryable Encryption is correctly configured for a property being used for equality queries.
    /// </summary>
    /// <param name="property">The <see cref="IProperty"/> being validated.</param>
    /// <exception cref="InvalidOperationException">Thrown when the equality-query property uses Queryable Encryption but the configuration is not valid.</exception>
    private static void ValidatePropertyForEqualityQueryableEncryption(IProperty property)
    {
        var bsonType = BsonTypeHelper.GetBsonType(property);
        switch (bsonType)
        {
            case BsonType.Decimal128:
            case BsonType.Double:
            case BsonType.Document:
                {
                    throw CannotBeEncryptedForEqualityException($"BsonType.{bsonType} is not a supported type.");
                }

            default:
                return;
        }

        Exception CannotBeEncryptedForEqualityException(string reason)
            => new InvalidOperationException(PropertyOnEntity(property)
                                             + $" cannot be encrypted for equality as {reason}.");
    }

    /// <summary>
    /// Validate that MongoDB Queryable Encryption is correctly configured for a property being used for range queries.
    /// </summary>
    /// <param name="property">The <see cref="IProperty"/> being validated.</param>
    /// <param name="logger">A logger to receive validation diagnostic information.</param>
    /// <exception cref="InvalidOperationException">Thrown when the range-query property uses Queryable Encryption but the configuration is not valid.</exception>
    private static void ValidatePropertyForRangeQueryableEncryption(
        IProperty property,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        var bsonType = BsonTypeHelper.GetBsonType(property);
        switch (bsonType)
        {
            case BsonType.Decimal128:
            case BsonType.Double:
                {
                    // Just test required values are present, leave validation to QE schema checker
                    _ = property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin)?.Value ??
                        throw CannotBeEncryptedForRangeException("no min value has been specified.");

                    _ = property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax)?.Value ??
                        throw CannotBeEncryptedForRangeException("no max value has been specified.");

                    break;
                }

            case BsonType.Int32:
            case BsonType.Int64:
            case BsonType.DateTime:
                {
                    var min = property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMin);
                    var max = property.FindAnnotation(MongoAnnotationNames.QueryableEncryptionRangeMax);

                    if (min?.Value == null || max?.Value == null)
                    {
                        logger.RecommendedMinMaxRangeMissing(property);
                    }

                    break;
                }

            default:
                throw CannotBeEncryptedForRangeException(
                    "only Int32, Int64, DateTime, Decimal128 and Double BsonTypes are supported.");
        }

        Exception CannotBeEncryptedForRangeException(string reason)
            => new InvalidOperationException(PropertyOnEntity(property)
                                             + $" (BsonType.{bsonType}) cannot be used for Queryable Encryption range queries as {reason}.");
    }

    /// <summary>
    /// Validate that element names meet the requirements of MongoDB and that there
    /// are no element names duplicated for an entity.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate primary key correctness.</param>
    /// <exception cref="NotSupportedException">Thrown when composite keys are encountered which are not supported.</exception>
    /// <exception cref="InvalidOperationException">Thrown when an entity requiring a key does not have one, or it is not mapped to "_id".</exception>
    private static void ValidateElementNames(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            ValidateEntityElementNames(entityType);
        }
    }

    /// <summary>
    /// Validate that an entity has a valid primary key.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> to validate the primary key of.</param>
    /// <exception cref="InvalidOperationException">Thrown when a required primary key is not found, or it is invalid.</exception>
    private static void ValidateEntityPrimaryKey(IEntityType entityType)
    {
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey == null)
        {
            // This is either a keyless type, or will be validated to have a key in ValidateNonNullPrimaryKeys
            return;
        }

        if (primaryKey.Properties.Count == 1)
        {
            // The primary key must map to "_id"
            var primaryKeyProperty = primaryKey.Properties[0];
            var primaryKeyElementName = primaryKeyProperty.GetElementName();
            if (primaryKeyElementName != "_id")
            {
                throw new InvalidOperationException(
                    $"The entity type '{entityType.DisplayName()}' primary key property '{primaryKeyProperty.Name
                    }' must be mapped to element '_id'.");
            }
        }
    }

    /// <summary>
    /// Validate the mapped BSON element names of a given <see cref="IEntityType"/>.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> to validate the element names of.</param>
    /// <exception cref="InvalidOperationException">Thrown when naming rules are violated or duplicate names exist.</exception>
    private static void ValidateEntityElementNames(IEntityType entityType)
    {
        var elementPropertyMap = new Dictionary<string, IPropertyBase>();

        foreach (var property in entityType.GetProperties())
        {
            var elementName = property.GetElementName();
            if (string.IsNullOrWhiteSpace(elementName)) continue;

            if (elementName.StartsWith("$"))
            {
                throw new InvalidOperationException(PropertyOnEntity(property) + $" may not map to element '{elementName
                }' as it starts with the reserved character '$'.");
            }

            if (elementName.Contains('.'))
            {
                throw new InvalidOperationException(PropertyOnEntity(property) + $" may not map to element '{elementName
                }' as it contains the reserved character '.'.");
            }

            if (elementPropertyMap.TryGetValue(elementName, out var otherProperty))
            {
                throw new InvalidOperationException(
                    $"Properties '{property.Name}' and '{otherProperty.Name}' on entity type '{entityType.DisplayName()
                    }' are mapped to element '{elementName}'. Map one of them to a different BSON element.");
            }

            elementPropertyMap[elementName] = property;
        }


        var elementNavigationMap = new Dictionary<string, INavigation>();

        foreach (var navigation in entityType.GetNavigations().Where(n => n.IsEmbedded()))
        {
            var elementName = navigation.TargetEntityType.GetContainingElementName();
            if (elementName != null)
            {
                if (elementName.StartsWith('$'))
                {
                    throw new InvalidOperationException(NavigationOnEntity(navigation) + $" may not map to element '{
                        elementName}' as it starts with the reserved character '$'.");
                }

                if (elementName.Contains('.'))
                {
                    throw new InvalidOperationException(NavigationOnEntity(navigation) + $" may not map to element '{
                        elementName}' as it contains the reserved character '.'.");
                }

                if (elementPropertyMap.TryGetValue(elementName, out var otherProperty))
                {
                    throw new InvalidOperationException(
                        $"Navigation '{navigation.Name}' and Property '{otherProperty.Name}' on entity type '{
                            entityType.DisplayName()}' are mapped to element '{elementName
                            }'. Map one of them to a different BSON element.");
                }

                if (elementNavigationMap.TryGetValue(elementName, out var otherNavigation))
                {
                    throw new InvalidOperationException(
                        $"Navigations '{navigation.Name}' and '{otherNavigation.Name}' on entity type '{entityType.DisplayName()
                        }' are mapped to element '{elementName}'. Map one of them to a different BSON element.");
                }

                elementNavigationMap[elementName] = navigation;
            }
        }
    }

    /// <summary>
    /// Validates that the only keys that can actually be changed are shadow keys used by owned entities.
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <param name="logger">The logger to use.</param>
    /// <exception cref="InvalidOperationException">Thrown if mutable keys exist that aren't part of the shadow indexer on owned entities.</exception>
    protected override void ValidateNoMutableKeys(
        IModel model,
        IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            foreach (var key in entityType.GetDeclaredKeys())
            {
                var mutableProperty = key.Properties.FirstOrDefault(p => p.ValueGenerated.HasFlag(ValueGenerated.OnUpdate));
                if (mutableProperty != null && !mutableProperty.IsOwnedCollectionShadowKey())
                {
                    throw new InvalidOperationException(CoreStrings.MutableKeyProperty(mutableProperty.Name));
                }
            }
        }
    }

    /// <summary>
    /// Validate that no entities are mapped with anything except table-per-hierarchy (TPH).
    /// </summary>
    /// <param name="model">The model to validate.</param>
    /// <exception cref="NotSupportedException">Thrown if entities have any mappings except table-per-hierarchy, e.g. table-per-type.</exception>
    private static void ValidateNoTablePerType(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes())
        {
            var mappingStrategy = (string?)entityType.FindAnnotation("Relational:MappingStrategy")?.Value;
            if (mappingStrategy != null && mappingStrategy != "TPH")
            {
                throw new NotSupportedException(
                    $"Entity '{entityType.DisplayName()}' is mapped with a {mappingStrategy
                    } strategy. Only TPH (the default) is supported by the MongoDB provider.");
            }
        }
    }

    private static string PropertyOnEntity(IProperty property)
        => $"Property '{property.Name}' on entity type '{property.DeclaringType.DisplayName()}'";

    private static string NavigationOnEntity(INavigation navigation)
        => $"Navigation '{navigation.Name}' on entity type '{navigation.DeclaringType.DisplayName()}'";
}
