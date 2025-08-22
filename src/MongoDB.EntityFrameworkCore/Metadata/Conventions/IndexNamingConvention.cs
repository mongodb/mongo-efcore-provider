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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

internal class IndexNamingConvention : IModelFinalizingConvention
{
    /// <summary>
    /// Creates a <see cref="CollectionNameFromDbSetConvention" /> with required dependencies.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this convention depends upon.</param>
    public IndexNamingConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
    }

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes().Where(e => e.IsDocumentRoot()))
        {
            ProcessEntityType(entityType, []);
        }
    }

    private static void ProcessEntityType(IConventionEntityType entityType, List<string> path)
    {
        foreach (var index in entityType.GetIndexes().Where(p => p.Name == null).ToList())
        {
            ReplaceIndex(entityType, index, path);
        }

        var ownedEntityTypes = entityType.GetReferencingForeignKeys()
            .Where(k => k.IsOwnership && k.PrincipalEntityType == entityType)
            .Select(k => k.DeclaringEntityType);

        foreach (var ownedEntityType in ownedEntityTypes)
        {
            var newPath = path.ToList();
            newPath.Add(ownedEntityType.GetContainingElementName()!);
            ProcessEntityType(ownedEntityType, newPath);
        }
    }

    private static void ReplaceIndex(IConventionEntityType entityType, IConventionIndex index, List<string> path)
    {
        entityType.RemoveIndex(index);

        var newIndex = entityType.AddIndex(
            index.Properties,
            index.MakeIndexName(path))!;

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
