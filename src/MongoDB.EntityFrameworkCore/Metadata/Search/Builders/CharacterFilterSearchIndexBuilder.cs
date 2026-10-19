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
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API called from <see cref="AnalyzerSearchIndexBuilder"/> that configures character filters for a custom
/// analyzer definition.
/// </summary>
/// <remarks>
/// For more information about character filters, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/"/>.
/// </remarks>
/// <param name="definition">The <see cref="SearchIndexAnalyzerDefinition"/> being built.</param>
public class CharacterFilterSearchIndexBuilder(SearchIndexAnalyzerDefinition definition)
{
    /// <summary>
    /// Adds the "htmlStrip" character filter to the custom analyzer being defined. This filter strips out HTML constructs.
    /// </summary>
    /// <remarks>
    /// For more information about the "htmlStrip" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/#htmlstrip"/>.
    /// </remarks>
    /// <param name="ignoredTags">The list of HTML tags to exclude from filtering.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public CharacterFilterSearchIndexBuilder AddHtmlStripFilter(IEnumerable<string>? ignoredTags = null)
    {
        var filterDefinition = definition.GetOrAddCharacterFilterDefinition<HtmlStripFilterDefinition>("htmlStrip");
        filterDefinition.IgnoredTags = ignoredTags?.ToList();
        return this;
    }

    /// <summary>
    /// Adds the "icuNormalize" character filter to the custom analyzer being defined. This filter normalizes text with the ICU
    /// Normalizer. It is based on Lucene's "ICUNormalizer2CharFilter".
    /// </summary>
    /// <remarks>
    /// For more information about the "icuNormalize" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/#icunormalize"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public CharacterFilterSearchIndexBuilder AddIcuNormalizeFilter()
    {
        definition.GetOrAddCharacterFilterDefinition<EmptySearchIndexFilterDefinition>("icuNormalize");
        return this;
    }

    /// <summary>
    /// Adds the "mapping" character filter to the custom analyzer being defined. This filter applies user-specified
    /// normalization mappings to characters. It is based on Lucene's "MappingCharFilter".
    /// A mapping indicates that one character or group of characters should be substituted for another
    /// </summary>
    /// <remarks>
    /// For more information about the "mapping" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/#std-label-mapping-ref"/>.
    /// </remarks>
    /// <param name="mappings">A list of mappings from original string to replacement string.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public CharacterFilterSearchIndexBuilder AddMappingFilter(IEnumerable<(string Original, string Replacement)> mappings)
    {
        var filterDefinition = definition.GetOrAddCharacterFilterDefinition<MappingsFilterDefinition>("mapping");
        filterDefinition.Mappings = mappings.ToDictionary(e => e.Original, e => e.Replacement);
        return this;
    }

    /// <summary>
    /// Adds the "persian" character filter to the custom analyzer being defined. This filter replaces instances of zero-width
    /// non-joiner with the space character. This character filter is based on Lucene's "PersianCharFilter".
    /// </summary>
    /// <remarks>
    /// For more information about the "persian" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/#std-label-persian-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public CharacterFilterSearchIndexBuilder AddPersianFilter()
    {
        definition.GetOrAddCharacterFilterDefinition<EmptySearchIndexFilterDefinition>("persian");
        return this;
    }
}
