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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

/// <summary>
/// Extension methods for search index definitions.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
internal static class SearchIndexDefinitionExtensions
{
    /// <summary>
    /// Finds the definition for the given type. If none is found, then a new instance is created and returned.
    /// </summary>
    /// <typeparam name="TDefinition">The type of definition to find or add.</typeparam>
    /// <param name="definitions">The list of definitions to look at.</param>
    /// <param name="name">The name of the definition.</param>
    /// <returns>The existing definition, or a new one.</returns>
    public static TDefinition GetOrAddDefinition<TDefinition>(this List<ISearchIndexDefinition> definitions, string name)
        where TDefinition : ISearchIndexDefinition, new()
    {
        foreach (var definition in definitions)
        {
            if (definition.Name == name && definition is TDefinition typedDefinition)
            {
                return typedDefinition;
            }
        }

        var newDefinition = new TDefinition { Name = name };
        definitions.Add(newDefinition);

        return newDefinition;
    }
}
