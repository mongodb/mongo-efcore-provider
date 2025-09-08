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
/// Runs when the model has been built to find all indexes and give a MongoDB-specific default name to any index that does
/// not already have a name.
/// </summary>
public class IndexNamingConvention : IModelFinalizingConvention
{
    /// <summary>
    /// Creates a <see cref="IndexNamingConvention" /> with required dependencies.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this convention depends upon.</param>
    public IndexNamingConvention(ProviderConventionSetBuilderDependencies dependencies)
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

    private void ProcessEntityType(IConventionEntityType entityType)
    {
        foreach (var index in entityType.GetIndexes().Where(p => p.Name == null).ToList())
        {
            ReplaceIndex(index);
        }

        var ownedEntityTypes = entityType.GetReferencingForeignKeys()
            .Where(k => k.IsOwnership && k.PrincipalEntityType == entityType)
            .Select(k => k.DeclaringEntityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            ProcessEntityType(ownedEntityType);
        }
    }

    /// <summary>
    /// Replaces the given index with a new one which is the same but with options changed. Override this method to
    /// change the default naming pattern.
    /// </summary>
    /// <param name="index">The <see cref="IIndex"/> that is being named.</param>
    protected virtual void ReplaceIndex(IConventionIndex index)
    {
        var entityType = index.DeclaringEntityType;
        entityType.RemoveIndex(index);

        var newIndex = entityType.AddIndex(
            index.Properties,
            index.MakeIndexName())!;

        newIndex.SetIsUnique(index.IsUnique);
        newIndex.SetIsDescending(index.IsDescending);

        var creationOptions = index.GetCreateIndexOptions();
        if (creationOptions != null)
        {
            newIndex.SetCreateIndexOptions(creationOptions);
        }

        var vectorOptions = index.GetVectorIndexOptions();
        if (vectorOptions.HasValue)
        {
            newIndex.SetVectorIndexOptions(vectorOptions);
        }
    }
}
