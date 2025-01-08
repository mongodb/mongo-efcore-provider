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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures store value generation for <see cref="ObjectId"/> and <see cref="Guid"/> keys
/// as well as the synthesized keys used internally by EF to index owned collection navigations.
/// </summary>
internal class MongoValueGenerationConvention : ValueGenerationConvention, IEntityTypeAnnotationChangedConvention
{
    /// <summary>
    /// Creates a <see cref="MongoValueGenerationConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public MongoValueGenerationConvention(ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public virtual void ProcessEntityTypeAnnotationChanged(
        IConventionEntityTypeBuilder entityTypeBuilder,
        string name,
        IConventionAnnotation? annotation,
        IConventionAnnotation? oldAnnotation,
        IConventionContext<IConventionAnnotation> context)
    {
        // We only want to deal with top-level entities
        if (name != MongoAnnotationNames.CollectionName || annotation == null == (oldAnnotation == null))
        {
            return;
        }

        var primaryKey = entityTypeBuilder.Metadata.FindPrimaryKey();
        if (primaryKey == null)
        {
            return;
        }

        foreach (var property in primaryKey.Properties)
        {
            property.Builder.ValueGenerated(GetValueGenerated(property));
        }
    }

    /// <summary>
    /// Returns the store <see cref="ValueGenerated"/> strategy to set for a given property.
    /// </summary>
    /// <param name="property">The property determine a generation strategy for.</param>
    /// <returns>
    /// The store <see cref="ValueGenerated"/> strategy to set for the <paramref name="property"/>
    /// or <see langword="null"/> if no strategy should be used.
    /// </returns>
    protected override ValueGenerated? GetValueGenerated(IConventionProperty property)
    {
        var entityType = property.DeclaringType as IConventionEntityType;
        var propertyType = property.ClrType.UnwrapNullableType();

        // Auto-generate synthesized keys for owned collections
        if (propertyType == typeof(int) && entityType != null)
        {
            var ownership = entityType.FindOwnership();
            if (ownership is { IsUnique: false } && !entityType.IsDocumentRoot())
            {
                var pk = property.FindContainingPrimaryKey();
                if (pk != null
                    && !property.IsForeignKey()
                    && pk.Properties.Count == ownership.Properties.Count + 1
                    && property.IsShadowProperty()
                    && ownership.Properties.All(fkProperty => pk.Properties.Contains(fkProperty)))
                {
                    return ValueGenerated.OnAddOrUpdate;
                }
            }
        }

        // Auto-generate ObjectId keys
        if (propertyType == typeof(ObjectId))
        {
            var primaryKey = property.FindContainingPrimaryKey();
            if (primaryKey is {Properties.Count: 1})
            {
                return ValueGenerated.OnAdd;
            }
        }

        // Do not auto-generate anything else except Guid
        return propertyType != typeof(Guid)
            ? null
            : base.GetValueGenerated(property);
    }
}
