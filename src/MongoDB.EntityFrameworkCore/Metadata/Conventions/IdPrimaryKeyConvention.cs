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
public class IdPrimaryKeyConvention : KeyDiscoveryConvention
{
    /// <summary>
    /// Creates a <see cref="IdPrimaryKeyConvention"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this service relies upon.</param>
    public IdPrimaryKeyConvention(
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

        var candidates = entityType.GetProperties().Where(
            p => !p.IsImplicitlyCreated() || !ConfigurationSource.Convention.Overrides(p.GetConfigurationSource()));

        foreach (var candidate in candidates)
        {
            if (MongoPropertyExtensions.GetElementName(candidate) == "_id")
            {
                entityTypeBuilder.PrimaryKey(new[] {candidate});
                return;
            }
        }
    }
}
