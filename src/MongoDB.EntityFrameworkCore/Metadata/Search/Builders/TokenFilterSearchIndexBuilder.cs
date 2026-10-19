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
/// Model building fluent API called from <see cref="AnalyzerSearchIndexBuilder"/> that configures token filters for a custom
/// analyzer definition.
/// </summary>
/// <remarks>
/// For more information about token filters, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/"/>.
/// </remarks>
/// <param name="definition">The <see cref="SearchIndexAnalyzerDefinition"/> being built.</param>
public class TokenFilterSearchIndexBuilder(SearchIndexAnalyzerDefinition definition)
{
    /// <summary>
    /// Adds the "asciiFolding" token filter to the custom analyzer being defined. This filter converts alphabetic, numeric, and
    /// symbolic Unicode characters that are not in the Basic Latin Unicode block to their ASCII equivalents, if available.
    /// </summary>
    /// <remarks>
    /// For more information about the "asciiFolding" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-asciiFolding-tf-ref"/>.
    /// </remarks>
    /// <param name="includeOriginalTokens">
    /// If <see langword="false"/>, then original tokens are also included in the output, otherwise they are not.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddAsciiFoldingFilter(bool? includeOriginalTokens = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<IncludeOriginalTokensFilterDefinition>("asciiFolding");
        filterDefinition.IncludeOriginalTokens = includeOriginalTokens;
        return this;
    }

    /// <summary>
    /// Adds the "daitchMokotoffSoundex" token filter to the custom analyzer being defined. This filter creates tokens for words
    /// that sound the same based on the Daitch-Mokotoff Soundex phonetic algorithm. This filter can generate multiple encodings
    /// for each input, where each encoded token is a six-digit number.
    /// </summary>
    /// <remarks>
    /// For more information about the "daitchMokotoffSoundex" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-daitchmokotoffsoundex-tf-ref"/>.
    /// </remarks>
    /// <param name="includeOriginalTokens">
    /// If <see langword="false"/>, then original tokens are also included in the output, otherwise they are not.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddDaitchMokotoffSoundexFilter(bool? includeOriginalTokens = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<IncludeOriginalTokensFilterDefinition>("daitchMokotoffSoundex");
        filterDefinition.IncludeOriginalTokens = includeOriginalTokens;
        return this;
    }

    /// <summary>
    /// Adds the "edgeGram" token filter to the custom analyzer being defined. This filter tokenizes input from the left side, or
    /// "edge", of a text input into n-grams of configured sizes.
    /// </summary>
    /// <remarks>
    /// For more information about the "edgeGram" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-edgegram-tf-ref"/>.
    /// </remarks>
    /// <param name="minGram">The minimum length of generated n-grams.</param>
    /// <param name="maxGram">The maximum length of generated n-grams.</param>
    /// <param name="includeOutOfBoundsTerms">
    /// If <see langword="true"/>, then tokens shorter than "minGram" or longer than "maxGram" are
    /// indexed, otherwise they are not.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddEdgeGramFilter(int minGram, int maxGram, bool? includeOutOfBoundsTerms = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<GramFilterDefinition>("edgeGram");
        filterDefinition.IncludeOutOfBoundsTerms = includeOutOfBoundsTerms;
        filterDefinition.MinGram = minGram;
        filterDefinition.MaxGram = maxGram;
        return this;
    }

    /// <summary>
    /// Adds the "englishPossessive" token filter to the custom analyzer being defined. This filter removes possessives
    /// (trailing 's) from words.
    /// </summary>
    /// <remarks>
    /// For more information about the "englishPossessive" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-englishPossessive-tf-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddEnglishPossessiveFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("englishPossessive");
        return this;
    }

    /// <summary>
    /// Adds the "flattenGraph" token filter to the custom analyzer being defined. This filter transforms a token filter graph
    /// into a flat form suitable for indexing. If you use <see cref="AddWordDelimiterGraphFilter"/>, then add this filter after
    /// adding that filter.
    /// </summary>
    /// <remarks>
    /// For more information about the "flattenGraph" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-flattenGraph-tf-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddFlattenGraphFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("flattenGraph");
        return this;
    }

    /// <summary>
    /// Adds the "icuFolding" token filter to the custom analyzer being defined. This filter applies character folding from
    /// Unicode Technical Report #30 such as accent removal, case folding, canonical duplicates folding, and many others
    /// detailed in the report.
    /// </summary>
    /// <remarks>
    /// For more information about the "icuFolding" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-icufolding-tf-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddIcuFoldingFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("icuFolding");
        return this;
    }

    /// <summary>
    /// Adds the "icuNormalizer" token filter to the custom analyzer being defined. This filter normalizes tokens using a
    /// standard Unicode Normalization Mode.
    /// </summary>
    /// <remarks>
    /// For more information about the "icuNormalizer" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-icunormalizer-tf-ref"/>.
    /// </remarks>
    /// <param name="normalizationForm">The normalization form to apply, as defined by <see cref="IcuNormalizationForm"/></param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddIcuNormalizerFilter(IcuNormalizationForm? normalizationForm = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<IcuNormalizingFilterDefinition>("icuNormalizer");
        filterDefinition.NormalizationForm = normalizationForm;
        return this;
    }

    /// <summary>
    /// Adds the "keywordRepeat" token filter to the custom analyzer being defined. This filter emits each incoming token twice,
    /// as a keyword and as a non-keyword.
    /// </summary>
    /// <remarks>
    /// You can stem the non-keyword token using subsequent token filters and preserve the
    /// keyword token. This can be used to boost exact matches and retrieve stemmed matches. This is typically used in
    /// conjunction with a stemming filter, such as "porterStemming", followed by "removeDuplicates".
    /// For more information about the "keywordRepeat" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-keywordRepeat-tf-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddKeywordRepeatFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("keywordRepeat");
        return this;
    }

    /// <summary>
    /// Adds the "kStemming" token filter to the custom analyzer being defined. This filter combines algorithmic stemming with
    /// a built-in dictionary for the english language to stem words. It expects lowercase text and doesn't modify uppercase text.
    /// </summary>
    /// <remarks>
    /// For more information about the "kStemming" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-kStemming-tf-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddKStemmingFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("kStemming");
        return this;
    }

    /// <summary>
    /// Adds the "length" token filter to the custom analyzer being defined. This filter removes tokens that are too short or
    /// too long.
    /// </summary>
    /// <remarks>
    /// For more information about the "length" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-length-tf-ref"/>.
    /// </remarks>
    /// <param name="min">The minimum length of a token.</param>
    /// <param name="max">The maximum length of a token.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddLengthFilter(int? min = null, int? max = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<MinMaxFilterDefinition>("length");
        filterDefinition.Min = min;
        filterDefinition.Max = max;
        return this;
    }

    /// <summary>
    /// Adds the "lowercase" token filter to the custom analyzer being defined. This filter normalizes token text to lowercase.
    /// </summary>
    /// <remarks>
    /// For more information about the "lowercase" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-lowercase-tf-ref"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddLowercaseFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("lowercase");
        return this;
    }

    /// <summary>
    /// Adds the "nGram" token filter to the custom analyzer being defined. This filter tokenizes input into n-grams of
    /// configured sizes. You can't use the nGram token filter in synonym or autocomplete mapping definitions.
    /// </summary>
    /// <remarks>
    /// For more information about the "nGram" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#ngram"/>.
    /// </remarks>
    /// <param name="minGram">The minimum length of generated n-grams.</param>
    /// <param name="maxGram">The maximum length of generated n-grams.</param>
    /// <param name="includeOutOfBoundsTerms">
    /// If <see langword="true"/>, then tokens shorter than "minGram" or longer than maxGram" are
    /// indexed, otherwise they are not.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddNGramFilter(int minGram, int maxGram, bool? includeOutOfBoundsTerms = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<GramFilterDefinition>("nGram");
        filterDefinition.IncludeOutOfBoundsTerms = includeOutOfBoundsTerms;
        filterDefinition.MinGram = minGram;
        filterDefinition.MaxGram = maxGram;
        return this;
    }

    /// <summary>
    /// Adds the "porterStemming" token filter to the custom analyzer being defined. This filter uses the porter stemming
    /// algorithm to remove the common morphological and inflectional suffixes from words in English. It expects lowercase
    /// text and doesn't work as expected for uppercase text.
    /// </summary>
    /// <remarks>
    /// For more information about the "porterStemming" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#porterstemming"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddPorterStemmingFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("porterStemming");
        return this;
    }

    /// <summary>
    /// Adds the "regex" token filter to the custom analyzer being defined. This filter applies a regular expression with Java
    /// regex syntax to each token, replacing matches with a specified string.
    /// </summary>
    /// <remarks>
    /// For more information about the "regex" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-regex-tf-ref"/>.
    /// </remarks>
    /// <param name="pattern">The regular expression pattern to apply to each token.</param>
    /// <param name="replacement">The replacement string to substitute wherever a matching pattern occurs.</param>
    /// <param name="matches">Defines whether to replace all matching patterns, or only the first.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddRegexFilter(string pattern, string replacement, RegexTokenFilterMatches matches)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<RegexFilterDefinition>("regex");
        filterDefinition.Pattern = pattern;
        filterDefinition.Replacement = replacement;
        filterDefinition.Matches = matches;
        return this;
    }

    /// <summary>
    /// Adds the "removeDuplicates" token filter to the custom analyzer being defined. This filter removes consecutive duplicate
    /// tokens, which are tokens for the same term in the same position.
    /// </summary>
    /// <remarks>
    /// For more information about the "removeDuplicates" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#removeduplicates"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddRemoveDuplicatesFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("removeDuplicates");
        return this;
    }

    /// <summary>
    /// Adds the "reverse" token filter to the custom analyzer being defined. This filter reverses each string token.
    /// </summary>
    /// <remarks>
    /// For more information about the "reverse" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#reverse"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddReverseFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("reverse");
        return this;
    }

    /// <summary>
    /// Adds the "shingle" token filter to the custom analyzer being defined. This filter constructs shingles (token n-grams)
    /// from a series of tokens. You can't use the shingle token filter in synonym or autocomplete mapping definitions.
    /// </summary>
    /// <remarks>
    /// For more information about the "shingle" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#shingle"/>.
    /// </remarks>
    /// <param name="minShingleSize">The minimum number of tokens per shingle. Must be greater than or equal to two.</param>
    /// <param name="maxShingleSize">The maximum number of tokens per shingle.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddShingleFilter(int minShingleSize, int maxShingleSize)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<ShingleMinMaxFilterDefinition>("shingle");
        filterDefinition.Min = minShingleSize;
        filterDefinition.Max = maxShingleSize;
        return this;
    }

    /// <summary>
    /// Adds the "snowballStemming" token filter to the custom analyzer being defined. This filter filters Stems tokens using a
    /// Snowball-generated stemmer.
    /// </summary>
    /// <remarks>
    /// For more information about the "snowballStemming" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#snowballStemming"/>.
    /// </remarks>
    /// <param name="stemmerName">The stemmer to use, as defined by <see cref="SnowballStemmerName"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddSnowballStemmingFilter(SnowballStemmerName stemmerName)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<SnowballStemmingFilterDefinition>("snowballStemming");
        filterDefinition.StemmerName = stemmerName;
        return this;
    }

    /// <summary>
    /// Adds the "spanishPluralStemming" token filter to the custom analyzer being defined. This filter stems spanish plural words.
    /// It expects lowercase text.
    /// </summary>
    /// <remarks>
    /// For more information about the "spanishPluralStemming" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#spanishpluralstemming"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddSpanishPluralStemmingFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("spanishPluralStemming");
        return this;
    }

    /// <summary>
    /// Adds the "stempel" token filter to the custom analyzer being defined. This filter uses Lucene's default Polish stemmer
    /// table to stem words in the Polish language. It expects lowercase text.
    /// </summary>
    /// <remarks>
    /// For more information about the "stempel" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#stempel"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddStempelFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("stempel");
        return this;
    }

    /// <summary>
    /// Adds the "stopword" token filter to the custom analyzer being defined. This filter removes tokens that correspond to the
    /// specified stop words. This token filter doesn't analyze the specified stop words.
    /// </summary>
    /// <remarks>
    /// For more information about the "stopword" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#stopword"/>.
    /// </remarks>
    /// <param name="stopWordTokens">List of the stop words that correspond to the tokens to remove.</param>
    /// <param name="ignoreCase">
    /// If <see langword="false"/>, then token matching is case-sensitive, otherwise matching of tokens is case-insensitive.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddStopWordFilter(IEnumerable<string> stopWordTokens, bool? ignoreCase = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<StopWordFilterDefinition>("stopword");
        filterDefinition.Tokens = stopWordTokens.ToList();
        filterDefinition.IgnoreCase = ignoreCase;
        return this;
    }

    /// <summary>
    /// Adds the "trim" token filter to the custom analyzer being defined. This filter trims leading and trailing whitespace
    /// from tokens.
    /// </summary>
    /// <remarks>
    /// For more information about the "trim" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#trim"/>.
    /// </remarks>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddTrimFilter()
    {
        definition.GetOrAddTokenFilterDefinition<EmptySearchIndexFilterDefinition>("trim");
        return this;
    }

    /// <summary>
    /// Adds the "wordDelimiterGraph" token filter to the custom analyzer being defined. This filter splits tokens into
    /// sub-tokens based on configured rules.
    /// </summary>
    /// <remarks>
    /// We recommend that you don't use this token filter with the standard tokenizer because this tokenizer removes many of
    /// the intra-word delimiters that this token filter uses to determine boundaries.
    /// For more information about the "wordDelimiterGraph" filter, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#worddelimitergraph"/>.
    /// </remarks>
    /// <param name="options">A <see cref="WordDelimiterOptions"/> with options for the filter.</param>
    /// <param name="protectedWords">List of the tokens to protect from delimitation.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TokenFilterSearchIndexBuilder AddWordDelimiterGraphFilter(
        WordDelimiterOptions? options = null, IEnumerable<string>? protectedWords = null)
    {
        var filterDefinition = definition.GetOrAddTokenFilterDefinition<WordDelimiterGraphFilterDefinition>("wordDelimiterGraph");
        filterDefinition.Options = options;
        filterDefinition.ProtectedWords = protectedWords?.ToList();
        return this;
    }
}
