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

using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures store value generation as <see cref="ValueGenerated.OnAdd" /> on properties that are
/// part of the primary key and not part of any foreign keys or were configured to have a database default value.
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
    /// or <see langref="null"/> if no strategy should be used.
    /// </returns>
    protected override ValueGenerated? GetValueGenerated(IConventionProperty property)
    {
        if (property.DeclaringType is IConventionEntityType && property.ClrType == typeof(ObjectId))
        {
            var primaryKey = property.FindContainingPrimaryKey();
            if (primaryKey is {Properties.Count: 1})
            {
                return ValueGenerated.OnAdd;
            }
        }

        return base.GetValueGenerated(property);
    }
}
