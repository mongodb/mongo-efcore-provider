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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// Runs when the model has been built to clear the build-time <see cref="Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType"/>
/// reference from every search index definition. The containing-element name was already captured into the definition
/// at configuration time, so this reference is no longer needed; nulling it ensures the persisted
/// <c>Mongo:SearchIndexDefinitions</c> annotation payload does not carry a mutable build-time reference across model
/// finalization (which would otherwise be retained by compiled models).
/// </summary>
internal sealed class SearchIndexDefinitionFinalizingConvention : IModelFinalizingConvention
{
    /// <summary>
    /// Creates a <see cref="SearchIndexDefinitionFinalizingConvention" /> with required dependencies.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this convention depends upon.</param>
    public SearchIndexDefinitionFinalizingConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
    }

    /// <inheritdoc/>
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            ProcessEntityType(entityType);
        }
    }

    private static void ProcessEntityType(IConventionEntityType entityType)
    {
        foreach (var definition in entityType.GetSearchIndexDefinitions())
        {
            definition.ClearEntityType();
        }

        var ownedEntityTypes = entityType.GetReferencingForeignKeys()
            .Where(k => k.IsOwnership && k.PrincipalEntityType == entityType)
            .Select(k => k.DeclaringEntityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            ProcessEntityType(ownedEntityType);
        }
    }
}
