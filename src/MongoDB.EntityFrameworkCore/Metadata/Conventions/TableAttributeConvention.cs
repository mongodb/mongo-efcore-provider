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
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures the collection name for entity types based on an applied <see cref="TableAttribute" /> for
/// familiarity/compatibility with other EF Core providers.
/// </summary>
public class TableAttributeConvention : TypeAttributeConventionBase<TableAttribute>
{
    /// <summary>
    /// Creates a <see cref="CollectionAttributeConvention" />.
    /// </summary>
    /// <param name="dependencies">Parameter object containing dependencies for this convention.</param>
    public TableAttributeConvention(
        ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <summary>
    /// Called after an entity type is added to the model if it has an attribute.
    /// </summary>
    /// <param name="entityTypeBuilder">The builder for the entity type.</param>
    /// <param name="attribute">The attribute.</param>
    /// <param name="context">Additional information associated with convention execution.</param>
    protected override void ProcessEntityTypeAdded(
        IConventionEntityTypeBuilder entityTypeBuilder,
        TableAttribute attribute,
        IConventionContext<IConventionEntityTypeBuilder> context)
    {
        if (!string.IsNullOrWhiteSpace(attribute.Name))
        {
            entityTypeBuilder.ToCollection(attribute.Name, fromDataAnnotation: true);
        }

        if (!string.IsNullOrWhiteSpace(attribute.Schema))
        {
            var meta = entityTypeBuilder.Metadata;
            throw new NotSupportedException($"Entity '{meta.ShortName()}' specifies a "
                                            + $"{nameof(TableAttribute)}.{nameof(TableAttribute.Schema)} which is not supported by "
                                            + $"MongoDB.");
        }
    }
}
