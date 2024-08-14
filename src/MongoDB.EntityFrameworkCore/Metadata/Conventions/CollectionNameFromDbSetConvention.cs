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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// A convention that configures the collection name based on the <see cref="DbSet{TEntity}" /> property name.
/// </summary>
public sealed class CollectionNameFromDbSetConvention : IEntityTypeAddedConvention
{
    private readonly Dictionary<Type, string> _sets = new();

    /// <summary>
    /// Creates a <see cref="CollectionNameFromDbSetConvention" /> with required dependencies.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this convention depends upon.</param>
    public CollectionNameFromDbSetConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
        var ambiguousTypes = new List<Type>();

        foreach (var set in dependencies.SetFinder.FindSets(dependencies.ContextType))
        {
            if (!_sets.ContainsKey(set.Type))
            {
                _sets.Add(set.Type, set.Name);
            }
            else
            {
                ambiguousTypes.Add(set.Type);
            }
        }

        foreach (var type in ambiguousTypes)
        {
            _sets.Remove(type);
        }
    }

    /// <inheritdoc />
    public void ProcessEntityTypeAdded(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionContext<IConventionEntityTypeBuilder> context)
    {
        var entityType = entityTypeBuilder.Metadata;
        if (!entityType.HasSharedClrType
            && _sets.TryGetValue(entityType.ClrType, out var setName))
        {
            entityTypeBuilder.ToCollection(setName);
        }
    }
}
