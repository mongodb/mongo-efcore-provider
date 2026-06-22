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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

/// <summary>
/// The definition for the collection of analyzers added to an index.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class SearchIndexAnalyzersDefinition : SearchIndexDefinitionBase
{
    private readonly List<ISearchIndexDefinition> _analyzerDefinitions = new();

    /// <summary>
    /// Finds the definition for the given analyzer name. If none is found, then a new instance is created and returned.
    /// </summary>
    /// <param name="analyzerName">The name of the analyzer.</param>
    /// <returns>The existing definition, or a new one.</returns>
    public SearchIndexAnalyzerDefinition GetOrAddAnalyzerDefinition(string analyzerName)
        => _analyzerDefinitions.GetOrAddDefinition<SearchIndexAnalyzerDefinition>(analyzerName);

    /// <inheritdoc/>
    public override BsonValue ToBson()
    {
        var array = new BsonArray();
        foreach (var analyzerDefinition in _analyzerDefinitions)
        {
            array.Add(analyzerDefinition.ToBson());
        }

        return array;
    }
}
