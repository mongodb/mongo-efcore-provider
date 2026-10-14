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
/// Model building fluent API that builds an "string" search index type. Called from
/// <see cref="SearchIndexBuilder"/> or <see cref="SearchIndexBuilder{TEntity}"/>, or their generic
/// counterparts, <see cref="SearchIndexBuilder{TEntity}"/> or <see cref="NestedSearchIndexBuilder{TEntity}"/>
/// </summary>
/// <remarks>
/// For more information about the autocomplete index type, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>
/// </remarks>
/// <param name="definition">The <see cref="NestedSearchIndexBuilder"/> defining the index type being built.</param>
public class StringSearchIndexBuilder(SearchIndexStringDefinition definition)
{
    /// <summary>
    /// Configures the name of the analyzer to use for string indexing. This must be the name of a built-in analyzer (but
    /// consider using <see cref="UseAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined in this index
    /// with <see cref="SearchIndexBuilder.AddCustomAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder UseAnalyzer(string analyzerName)
    {
        definition.AnalyzerName = analyzerName;
        return this;
    }

    /// <summary>
    /// Configures the built-in analyzer (defined by <see cref="BuiltInSearchAnalyzer"/>) to use for string indexing.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder UseAnalyzer(BuiltInSearchAnalyzer analyzer)
        => UseAnalyzer(SearchIndexBuilder.ToAnalyzerName(analyzer));

    /// <summary>
    /// Adds the built-in analyzer (defined by <see cref="BuiltInSearchAnalyzer"/>) to use for string indexing.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="name">
    /// The name for this alternate analyzer in the index. This must be referenced from the search to use this analyzer.
    /// </param>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder AddAlternateAnalyzer(string name, string analyzerName)
    {
        definition.AlternateAnalyzers[name] = analyzerName;
        return this;
    }

    /// <summary>
    /// Adds the name of an alternate analyzer to use for string indexing. This must be the name of a built-in analyzer (but
    /// consider using <see cref="AddAlternateAnalyzer(string, BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined
    /// in this index with <see cref="SearchIndexBuilder.AddCustomAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="name">
    /// The name for this alternate analyzer in the index. This must be referenced from the search to use this analyzer.
    /// </param>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder AddAlternateAnalyzer(string name, BuiltInSearchAnalyzer analyzer)
        => AddAlternateAnalyzer(name, SearchIndexBuilder.ToAnalyzerName(analyzer));

    /// <summary>
    /// Adds an alternate similarity algorithms to use when scoring with the string index.
    /// </summary>
    /// <remarks>
    /// For more information about similarity algorithms, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/score/get-details/#std-label-fts-similarity-algorithms"/>
    /// </remarks>
    /// <param name="name">
    /// The name for this alternate similarity algorithm in the index. This must be referenced from the search to use this
    /// algorithm.
    /// </param>
    /// <param name="similarityAlgorithm">The <see cref="SearchSimilarityAlgorithm"/> to use.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder AddAlternateSimilarity(string name, SearchSimilarityAlgorithm similarityAlgorithm)
    {
        definition.AlternateSimilarityAlgorithms[name] = similarityAlgorithm;
        return this;
    }

    /// <summary>
    /// Configures the name of the analyzer to use for string searches with this index. This must be the name of a built-in
    /// analyzer (but consider using <see cref="UseSearchAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined
    /// in this index with <see cref="SearchIndexBuilder.AddCustomAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// If you don't specify a value, then analyzer used is analyzer defined for this field, if specified, followed by the
    /// default search analyzer for the index, if specified, followed by the default analyzer for the index, if specified,
    /// followed by the <see cref="BuiltInSearchAnalyzer.LuceneStandard"/> analyzer.
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder UseSearchAnalyzer(string analyzerName)
    {
        definition.SearchAnalyzerName = analyzerName;
        return this;
    }

    /// <summary>
    /// Configures the built-in analyzer (defined by <see cref="BuiltInSearchAnalyzer"/>) to use for string searches with this
    /// index.
    /// </summary>
    /// <remarks>
    /// If you don't specify a value, then analyzer used is analyzer defined for this field, if specified, followed by the
    /// default search analyzer for the index, if specified, followed by the default analyzer for the index, if specified,
    /// followed by the <see cref="BuiltInSearchAnalyzer.LuceneStandard"/> analyzer.
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder UseSearchAnalyzer(BuiltInSearchAnalyzer analyzer)
        => UseSearchAnalyzer(SearchIndexBuilder.ToAnalyzerName(analyzer));

    /// <summary>
    /// Configured the amount of information to store for the indexed field, as defined by <see cref="StringSearchIndexAmount"/>.
    /// </summary>
    /// <param name="indexAmount">Information to store, defined by <see cref="StringSearchIndexAmount"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder WithIndexAmount(StringSearchIndexAmount indexAmount)
    {
        definition.IndexAmount = indexAmount;
        return this;
    }

    /// <summary>
    /// Configured a flag that indicates whether to store the exact document text as well as the analyzed values in the index.
    /// By default, document text is stored.
    /// </summary>
    /// <remarks>
    /// The value for this option must be <see langword="true"/> for "highlight" searches.
    /// To reduce the index size and performance footprint, we recommend setting this flag to <see langword="false"/>. To learn
    /// more, see <see href="https://www.mongodb.com/docs/atlas/atlas-search/performance/index-performance/#std-label-index-perf"/>
    /// </remarks>
    /// <param name="store">If <see langword="false"/>, then document text is not stored, otherwise it is.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder StoreDocumentText(bool store = true)
    {
        definition.StoreDocumentText = store;
        return this;
    }

    /// <summary>
    /// Configures the maximum number of characters in the value of the field to index. MongoDB Search doesn't index if the
    /// field value is greater than the specified number of characters.
    /// </summary>
    /// <param name="characterCount">The maximum number of characters in the value of the field to index.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder IgnoreAbove(int characterCount)
    {
        definition.IgnoreAbove = characterCount;
        return this;
    }

    /// <summary>
    /// Configures the similarity algorithms to use when scoring for string indexes.
    /// </summary>
    /// <remarks>
    /// For more information about similarity algorithms, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/score/get-details/#std-label-fts-similarity-algorithms"/>
    /// </remarks>
    /// <param name="similarityAlgorithm">The <see cref="SearchSimilarityAlgorithm"/> to use.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder UseSimilarity(SearchSimilarityAlgorithm similarityAlgorithm)
    {
        definition.SimilarityAlgorithm = similarityAlgorithm;
        return this;
    }

    /// <summary>
    /// Configures a flag that specifies whether to include or omit the field length in the result when scoring.
    /// By default, the field length is included.
    /// </summary>
    /// <remarks>
    /// The length of the field is determined by the number of tokens produced by the analyzer for the field.
    /// When the field length is included, then MongoDB Search uses the length of the field to determine the higher score when
    /// scoring. For example, if two documents match a MongoDB Search query, the document with the shorter field length scores
    /// higher than the document with the longer field length.
    /// </remarks>
    /// <param name="include">If <see langword="false"/>, then the field length is omitted, otherwise it is included.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public StringSearchIndexBuilder IncludeFieldLength(bool include = true)
    {
        definition.IncludeFieldLength = include;
        return this;
    }
}
