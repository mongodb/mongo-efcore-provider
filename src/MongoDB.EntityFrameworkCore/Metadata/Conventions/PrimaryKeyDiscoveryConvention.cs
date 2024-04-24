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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that identifies the primary key for entity types based on the underlying element name being "_id".
/// </summary>
public class PrimaryKeyDiscoveryConvention : KeyDiscoveryConvention
{
    /// <summary>
    /// Creates a <see cref="PrimaryKeyDiscoveryConvention"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this service relies upon.</param>
    public PrimaryKeyDiscoveryConvention(
        ProviderConventionSetBuilderDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc/>
    protected override void TryConfigurePrimaryKey(IConventionEntityTypeBuilder entityTypeBuilder)
    {
        var entityType = entityTypeBuilder.Metadata;
        if (entityType.BaseType != null
            || (entityType.IsKeyless && entityType.GetIsKeylessConfigurationSource() != ConfigurationSource.Convention)
            || !entityTypeBuilder.CanSetPrimaryKey((IReadOnlyList<IConventionProperty>?)null))
        {
            return;
        }

        // Owned entities don't have real keys but need non-persisted shadow properties synthesized from their parent
        var ownership = entityType.FindOwnership();
        if (ownership != null)
        {
            if (entityType.FindPrimaryKey() == null)
            {
                var keyProperties = ownership.Properties.ToList();

                // Ones in a collection need their parent Id + an index
                if (!ownership.IsUnique)
                {
                    var uniqueShadowKeyProperty = entityTypeBuilder
                        .CreateUniqueProperty(typeof(int), "_unique", required: true)!
                        .Metadata;
                    keyProperties.Add(uniqueShadowKeyProperty);
                }

                entityTypeBuilder.PrimaryKey(keyProperties);
            }

            return;
        }

        // Look for anything mapped to element "_id" or property named as "_id"
        var entityProperties = entityType.GetProperties().ToArray();
        var underscoreIdElementProperty = entityProperties.FirstOrDefault(p => p.GetElementName() == "_id") ??
                                          entityProperties.FirstOrDefault(p => p.Name == "_id");
        if (underscoreIdElementProperty != null)
        {
            entityTypeBuilder.PrimaryKey(new[]
            {
                underscoreIdElementProperty
            });
            return;
        }

        // Try the standard provider to look for "Id", "EntityId" etc.
        base.TryConfigurePrimaryKey(entityTypeBuilder);

        var keys = entityType.GetKeys().ToArray();
        switch (keys.Length)
        {
            case > 1:
                throw new NotSupportedException("Alternate keys not supported at this time.");
            case 1:
                keys[0].Properties[0].SetElementName("_id");
                break;
        }
    }
}
