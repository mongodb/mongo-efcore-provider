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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

/// <summary>
/// The definition for a type set in a MongoDB search index.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class SearchIndexAnalyzerDefinition : SearchIndexDefinitionBase
{
    private readonly List<ISearchIndexDefinition> _characterFilterDefinitions = new();
    private readonly List<ISearchIndexDefinition> _tokenFilterDefinitions = new();

    /// <summary>
    /// The definition for the tokenizer to use.
    /// </summary>
    public SearchIndexTokenizerDefinition? SearchIndexTokenizerDefinition { get; set; }

    /// <summary>
    /// Finds the definition for the given character filter. If none is found, then a new instance is created and returned.
    /// </summary>
    /// <typeparam name="TDefinition">The type of definition to find or add.</typeparam>
    /// <param name="filterName">The name of the filter.</param>
    /// <returns>The existing definition, or a new one.</returns>
    public TDefinition GetOrAddCharacterFilterDefinition<TDefinition>(string filterName)
        where TDefinition : ISearchIndexDefinition, new()
        => _characterFilterDefinitions.GetOrAddDefinition<TDefinition>(filterName);

    /// <summary>
    /// Finds the definition for the given tokenfilter. If none is found, then a new instance is created and returned.
    /// </summary>
    /// <typeparam name="TDefinition">The type of definition to find or add.</typeparam>
    /// <param name="filterName">The name of the filter.</param>
    /// <returns>The existing definition, or a new one.</returns>
    public TDefinition GetOrAddTokenFilterDefinition<TDefinition>(string filterName)
        where TDefinition : ISearchIndexDefinition, new()
        => _tokenFilterDefinitions.GetOrAddDefinition<TDefinition>(filterName);

    /// <inheritdoc/>
    public override BsonValue ToBson()
    {
        if (SearchIndexTokenizerDefinition == null)
        {
            throw new InvalidOperationException(
                $"The MongoDB search index custom analyzer '{Name}' does not specify a tokenizer. All custom analyzers must configure a tokenizer.");
        }

        var document = new BsonDocument { { "name", Name }, { "tokenizer", SearchIndexTokenizerDefinition.ToBson() } };

        if (_characterFilterDefinitions.Count > 0)
        {
            var array = new BsonArray();
            foreach (var filterDefinition in _characterFilterDefinitions)
            {
                array.Add(filterDefinition.ToBson());
            }
            document.Add("charFilters", array);
        }

        if (_tokenFilterDefinitions.Count > 0)
        {
            var array = new BsonArray();
            foreach (var filterDefinition in _tokenFilterDefinitions)
            {
                array.Add(filterDefinition.ToBson());
            }
            document.Add("tokenFilters", array);
        }

        return document;
    }
}
