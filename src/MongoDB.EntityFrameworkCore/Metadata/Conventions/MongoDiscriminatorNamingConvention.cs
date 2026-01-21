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

using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// Runs when the model has been built to find all discriminators and map to "_t" unless they are explicitly configured to
/// something else.
/// </summary>
public class MongoDiscriminatorNamingConvention : IModelFinalizingConvention
{
    /// <summary>
    /// Creates a <see cref="MongoDiscriminatorNamingConvention" /> with required dependencies.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this convention depends upon.</param>
    public MongoDiscriminatorNamingConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
    }

    /// <inheritdoc/>
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var discriminator in modelBuilder.Metadata.GetEntityTypes().Select(e => e.FindDiscriminatorProperty()))
        {
            if (discriminator != null)
            {
                var oldAnnotation = discriminator.FindAnnotation(MongoAnnotationNames.ElementName);
                if (oldAnnotation is null || oldAnnotation.GetConfigurationSource() == ConfigurationSource.Convention)
                {
                    discriminator.SetElementName("_t");
                }
            }
        }
    }
}
