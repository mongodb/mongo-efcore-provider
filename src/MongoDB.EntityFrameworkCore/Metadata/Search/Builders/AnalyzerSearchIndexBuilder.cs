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
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API called from <see cref="SearchIndexBuilder"/> that builds a custom analyzer defintion for use
/// in the search index.
/// </summary>
/// <remarks>
/// For more information about search index analyzers, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
/// </remarks>
/// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> defining the index being built.</param>
/// <param name="analyzerName">The name of the custom analyzer being defined.</param>
public class AnalyzerSearchIndexBuilder(SearchIndexDefinition indexDefinition, string analyzerName)
{
    private readonly SearchIndexAnalyzerDefinition _definition
        = indexDefinition
            .GetOrAddTopLevelDefinition<SearchIndexAnalyzersDefinition>("analyzers")
            .GetOrAddAnalyzerDefinition(analyzerName);

    /// <summary>
    /// Configures character filters for this analyzer. Character filters examine text one character at a time and perform
    /// filtering operations.
    /// </summary>
    /// <remarks>
    /// For more information about character filters, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/"/>.
    ///  </remarks>
    /// <returns>A <see cref="CharacterFilterSearchIndexBuilder"/> to configure character filters.</returns>
    public CharacterFilterSearchIndexBuilder WithCharacterFilters()
        => new(_definition);

    /// <summary>
    /// Configures character filters for this analyzer. Character filters examine text one character at a time and perform
    /// filtering operations.
    /// </summary>
    /// <remarks>
    /// For more information about character filters, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/character-filters/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">
    /// A <see cref="CharacterFilterSearchIndexBuilder"/> delegate to configure character filters.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder WithCharacterFilters(Action<CharacterFilterSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(WithCharacterFilters());
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the edge-gram tokenizer. Edge-gram tokenization from the left edge (prefix grams).
    /// It produces tokens that start at the beginning of the term and extend up to the configured "maxGram".
    /// Typically used for autocomplete and prefix matching (e.g., "mon", "mong", "mongo").
    /// </summary>
    /// <remarks>
    /// For more information about edge-gram tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/edge-gram/"/>.
    /// </remarks>
    /// <param name="minGram">Number of characters to include in the shortest token created.</param>
    /// <param name="maxGram">Number of characters to include in the longest token created.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseEdgeGramTokenizer(int minGram, int maxGram)
    {
        _definition.SearchIndexTokenizerDefinition = new SearchIndexGramTokenizerDefinition("edgeGram", minGram, maxGram);
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the "keyword" tokenizer. The keyword tokenizer tokenizes the entire input as a single
    /// token.
    /// </summary>
    /// <remarks>
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseKeywordTokenizer()
    {
        _definition.SearchIndexTokenizerDefinition = new SearchIndexTokenizerDefinition { Name = "keyword" };
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the n-gram tokenizer. N-gram tokenization using a sliding window across the entire
    /// term (infix grams). Produces overlapping tokens of length between "minGram" and "maxGram",
    /// enabling substring matching anywhere within a word. Useful for partial/infix search, with increased index size.
    /// </summary>
    /// <remarks>
    /// For more information about n-gram tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/n-gram/"/>.
    /// </remarks>
    /// <param name="minGram">Number of characters to include in the shortest token created.</param>
    /// <param name="maxGram">Number of characters to include in the longest token created.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseNGramTokenizer(int minGram, int maxGram)
    {
        _definition.SearchIndexTokenizerDefinition = new SearchIndexGramTokenizerDefinition("nGram", minGram, maxGram);
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the "regexCaptureGroup" tokenizer. The regexCaptureGroup tokenizer matches a Java
    /// regular expression pattern to extract tokens.
    /// </summary>
    /// <remarks>
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <param name="pattern">The regular expression to match against.</param>
    /// <param name="characterGroup">
    /// Index of the character group within the matching expression to extract into tokens. Use <c>0</c> to extract all character
    /// groups.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseRegexCaptureGroupTokenizer(string pattern, int characterGroup)
    {
        _definition.SearchIndexTokenizerDefinition = new RegexCaptureGroupTokenizerDefinition(pattern, characterGroup);
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the "regexSplit" tokenizer. The regexSplit tokenizer splits tokens with a Java
    /// regular-expression based delimiter.
    /// </summary>
    /// <remarks>
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <param name="pattern">Regular expression to match against.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseRegexSplitTokenizer(string pattern)
    {
        _definition.SearchIndexTokenizerDefinition = new RegexSplitTokenizerDefinition(pattern);
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the "standard" tokenizer. The standard tokenizer tokenizes based on word break rules from
    /// the Unicode Text Segmentation algorithm: <see href="https://www.unicode.org/L2/L2019/19034-uax29-34-draft.pdf"/>.
    /// </summary>
    /// <remarks>
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <param name="maxTokenLength">
    /// Maximum length for a single token. Tokens greater than this length are split at this length into multiple tokens. If not
    /// specified, then the default is 255.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseStandardTokenizer(int? maxTokenLength = null)
    {
        _definition.SearchIndexTokenizerDefinition = new MaxTokenLengthTokenizerDefinition("standard", maxTokenLength);
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the "uaxUrlEmail" tokenizer. The uaxUrlEmail tokenizer tokenizes URLs and email addresses.
    /// </summary>
    /// <remarks>
    /// Although uaxUrlEmail tokenizer tokenizes based on word break rules from the Unicode Text Segmentation algorithm, we
    /// recommend using uaxUrlEmail tokenizer only when the indexed field value includes URLs and email addresses. For fields
    /// that don't include URLs or email addresses, use the standard tokenizer to create tokens based on word break rules.
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <param name="maxTokenLength">
    /// Maximum length for a single token. Tokens greater than this length are split at this length into multiple tokens. If not
    /// specified, then the default is 255.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseUaxUrlEmailTokenizer(int? maxTokenLength = null)
    {
        _definition.SearchIndexTokenizerDefinition = new MaxTokenLengthTokenizerDefinition("uaxUrlEmail", maxTokenLength);
        return this;
    }

    /// <summary>
    /// Configures this analyzer to use the "whitespace" tokenizer. The whitespace tokenizer tokenizes based on occurrences of
    /// whitespace between words.
    /// </summary>
    /// <remarks>
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <param name="maxTokenLength">
    /// Maximum length for a single token. Tokens greater than this length are split at this length into multiple tokens. If not
    /// specified, then the default is 255.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder UseWhitespaceTokenizer(int? maxTokenLength = null)
    {
        _definition.SearchIndexTokenizerDefinition = new MaxTokenLengthTokenizerDefinition("whitespace", maxTokenLength);
        return this;
    }

    /// <summary>
    /// Configures token filters for this analyzer. A token filter performs operations such as stemming, which reduces related
    /// words, such as "talking", "talked", and "talks" to their root word "talk", and redaction, the removal of sensitive
    /// information from public documents.
    /// </summary>
    /// <remarks>
    /// For more information about token filters, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/"/>.
    /// </remarks>
    /// <returns>A <see cref="TokenFilterSearchIndexBuilder"/> to configure token filters.</returns>
    public TokenFilterSearchIndexBuilder WithTokenFilters()
        => new(_definition);

    /// <summary>
    /// Configures token filters for this analyzer. A token filter performs operations such as stemming, which reduces related
    /// words, such as "talking", "talked", and "talks" to their root word "talk", and redaction, the removal of sensitive
    /// information from public documents.
    /// </summary>
    /// <remarks>
    /// For more information about token filters, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="TokenFilterSearchIndexBuilder"/> delegate to configure token filters.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AnalyzerSearchIndexBuilder WithTokenFilters(Action<TokenFilterSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(WithTokenFilters());
        return this;
    }
}
