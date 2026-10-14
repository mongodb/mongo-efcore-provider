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

using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API that builds an "autocomplete" search index type. Called from
/// <see cref="SearchIndexBuilder"/> or <see cref="NestedSearchIndexBuilder"/>, or their generic
/// counterparts, <see cref="SearchIndexBuilder{TEntity}"/> or <see cref="NestedSearchIndexBuilder{TEntity}"/>
/// </summary>
/// <remarks>
/// For more information about the autocomplete index type, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>
/// </remarks>
/// <param name="definition">The <see cref="SearchIndexAutoCompleteDefinition"/> defining the index type being built.</param>
public sealed class AutoCompleteSearchIndexBuilder(SearchIndexAutoCompleteDefinition definition)
{
    /// <summary>
    /// Configures the name of the analyzer to use for auto-completion. This must be the name of a built-in analyzer (but
    /// consider using <see cref="UseAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined in this index
    /// with <see cref="SearchIndexBuilder.AddCustomAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder UseAnalyzer(string analyzerName)
    {
        definition.AnalyzerName = analyzerName;
        return this;
    }

    /// <summary>
    /// Configures the built-in analyzer (defined by <see cref="BuiltInSearchAnalyzer"/>) to use for auto-completion.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder UseAnalyzer(BuiltInSearchAnalyzer analyzer)
        => UseAnalyzer(SearchIndexBuilder.ToAnalyzerName(analyzer));

    /// <summary>
    /// Configures the minimum number of characters per indexed sequence.
    /// </summary>
    /// <remarks>
    /// We recommend 4 for the minimum value. A value that is less than 4 could impact performance because
    /// the size of the index can become very large. We recommend the default value of 2 for edgeGram only.
    /// </remarks>
    /// <param name="minGrams">The minimum number of characters per indexed sequence.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder WithMinGrams(int minGrams)
    {
        definition.MinGrams = minGrams;
        return this;
    }

    /// <summary>
    /// Configures the maximum number of characters per indexed sequence.
    /// </summary>
    /// <remarks>
    /// The value limits the character length of indexed tokens. When you search for terms longer than the maxGrams value,
    /// MongoDB Search truncates the tokens to the maxGrams length.
    /// </remarks>
    /// <param name="maxGrams">The maximum number of characters per indexed sequence.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder WithMaxGrams(int maxGrams)
    {
        definition.MaxGrams = maxGrams;
        return this;
    }

    /// <summary>
    /// Configures whether to perform normalizations such as including or removing diacritics from the indexed text.
    /// </summary>
    /// <param name="normalize">
    /// When <see langword="true"/>, normalizations are performed such as ignoring diacritic marks in the index and query text.
    /// For example, a search for cafè returns results with the characters cafè and cafe because MongoDB Search returns
    /// results with and without diacritics.
    /// When <see langword="false"/>these normalizations are not performed. MongoDB Search returns only results that match the
    /// strings with or without diacritics in the query. For example, a search for cafè returns results only with the
    /// characters cafè. A search for cafe returns results only with the characters cafe.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder FoldDiacritics(bool normalize)
    {
        definition.FoldDiacritics = normalize;
        return this;
    }

    /// <summary>
    /// Configures the tokenization strategy to use when indexing the field for autocompletion.
    /// </summary>
    /// <remarks>
    /// For more information about tokenization, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/"/>.
    /// </remarks>
    /// <param name="tokenization">The <see cref="SearchTokenization"/> to use.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder WithTokenization(SearchTokenization tokenization)
    {
        definition.Tokenization = tokenization;
        return this;
    }

    /// <summary>
    /// Configures the similarity algorithms to use when scoring with the autocomplete operator.
    /// </summary>
    /// <remarks>
    /// For more information about similarity algorithms, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/score/get-details/#std-label-fts-similarity-algorithms"/>
    /// </remarks>
    /// <param name="similarityAlgorithm">The <see cref="SearchSimilarityAlgorithm"/> to use.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public AutoCompleteSearchIndexBuilder UseSimilarity(SearchSimilarityAlgorithm similarityAlgorithm)
    {
        definition.SimilarityAlgorithm = similarityAlgorithm;
        return this;
    }
}
