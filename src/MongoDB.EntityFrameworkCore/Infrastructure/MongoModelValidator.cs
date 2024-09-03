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
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Serializers;
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

        ValidateMaximumOneRowVersionPerEntity(model);
        ValidateNoUnsupportedAttributesOrAnnotations(model);
        ValidateElementNames(model);
        ValidateNoShadowProperties(model);
        ValidateNoMutableKeys(model, logger);
        ValidatePrimaryKeys(model);

        SetupTypeDiscriminators(model);
    }

    private static readonly Dictionary<Type, IDiscriminatorConvention>? DiscriminatorConventionDictionary =
        typeof(BsonSerializer).GetField("__discriminatorConventions", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Dictionary<Type, IDiscriminatorConvention>;

    private static void SetupTypeDiscriminators(IModel model)
    {
        if (DiscriminatorConventionDictionary == null)
        {
            throw new InvalidOperationException("Unable to access MongoDB C# Driver discriminator conventions.");
        }

        var discriminatorProperties = new HashSet<IReadOnlyProperty>();
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var discriminatorProperty = entityType.FindDiscriminatorProperty();
            if (discriminatorProperty != null)
            {
                discriminatorProperties.Add(discriminatorProperty);
            }
        }

        foreach (var discriminatorProperty in discriminatorProperties)
        {
            var entityType = (IReadOnlyEntityType)discriminatorProperty.DeclaringType;
            DiscriminatorConventionDictionary.TryAdd(entityType.ClrType, new MongoEFDiscriminator(entityType));
        }
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
                    $"Entity '{entityType.DisplayName()}' has multiple properties '{propertyNames}' configured as row versions. Only one row version property per entity is supported.");
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
            if (property.FindAnnotation(MongoAnnotationNames.NotSupportedAttributes) is {Value: Attribute unsupported})
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
    /// <exception cref="InvalidOperationException">Throw when an entity requiring a key does not have one or it is not mapped to "_id".</exception>
    private static void ValidatePrimaryKeys(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            ValidateEntityPrimaryKey(entityType);
        }
    }

    /// <summary>
    /// Validate that element names meet the requirements of MongoDB and that there
    /// are no element names duplicated for an entity.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate primary key correctness.</param>
    /// <exception cref="NotSupportedException">Thrown when composite keys are encountered which are not supported.</exception>
    /// <exception cref="InvalidOperationException">Throw when an entity requiring a key does not have one or it is not mapped to "_id".</exception>
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
    /// <exception cref="InvalidOperationException">Thrown when a required primary key is not found or it is invalid.</exception>
    private static void ValidateEntityPrimaryKey(IEntityType entityType)
    {
        // We must have a primary key on root documents
        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey == null || primaryKey.Properties.Count == 0)
        {
            throw new InvalidOperationException(
                $"The entity type '{entityType.DisplayName()}' is a root document but does not have a primary key set.");
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
    /// <exception cref="InvalidOperationException">Throws when naming rules are violated or duplicate names exist.</exception>
    private static void ValidateEntityElementNames(IEntityType entityType)
    {
        var elementPropertyMap = new Dictionary<string, IPropertyBase>();

        foreach (var property in entityType.GetProperties())
        {
            var elementName = property.GetElementName();
            if (string.IsNullOrWhiteSpace(elementName)) continue;

            if (elementName.StartsWith("$"))
            {
                throw new InvalidOperationException(
                    $"Property '{property.Name}' on entity type '{entityType.DisplayName()}' may not map to element '{elementName
                    }' as it starts with the reserved character '$'.");
            }

            if (elementName.Contains('.'))
            {
                throw new InvalidOperationException(
                    $"Property '{property.Name}' on entity type '{entityType.DisplayName()}' may not map to element '{elementName
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
                if (elementName.StartsWith("$"))
                {
                    throw new InvalidOperationException(
                        $"Property '{navigation.Name}' on entity type '{entityType.DisplayName()}' may not map to element '{
                            elementName}' as it starts with the reserved character '$'.");
                }

                if (elementName.Contains('.'))
                {
                    throw new InvalidOperationException(
                        $"Property '{navigation.Name}' on entity type '{entityType.DisplayName()}' may not map to element '{
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
    /// Validate that no root entities have shadow properties.
    /// </summary>
    /// <param name="model">The <see cref="IModel"/> to validate for whether shadow properties are present.</param>
    /// <exception cref="NotSupportedException">Thrown when user-defined shadow properties are found on a root entity.</exception>
    private static void ValidateNoShadowProperties(IModel model)
    {
        foreach (var entityType in model.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            var shadowProperty = entityType.GetProperties().FirstOrDefault(p => p.IsShadowProperty());
            if (shadowProperty != null)
            {
                throw new NotSupportedException(
                    $"Unsupported shadow property '{shadowProperty.Name}' identified on entity type '{entityType.DisplayName()}'.");
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
}
