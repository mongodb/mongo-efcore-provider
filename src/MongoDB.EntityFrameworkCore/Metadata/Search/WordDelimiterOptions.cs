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

using MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

namespace MongoDB.EntityFrameworkCore.Metadata.Search;

/// <summary>
/// Options for the MongoDB Search "wordDelimiterGraph" token filter, as used by
/// <see cref="TokenFilterSearchIndexBuilder.AddWordDelimiterGraphFilter"/>.
/// </summary>
/// <remarks>
/// The <c>wordDelimiterGraph</c> token filter splits and/or concatenates tokens around case
/// changes, numeric boundaries, and non‑alphanumeric delimiters (such as underscores and hyphens).
/// These options control whether subword parts are generated, whether catenated (joined) forms are
/// added, whether to preserve the original token, and how to treat English possessives and protected
/// words. Use these flags to tailor tokenization of identifiers and compound words such as
/// <c>PowerShot-500D</c>, <c>camelCase</c>, or <c>snake_case</c>.
/// For details, see the Atlas Search documentation for the token filter:
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#worddelimitergraph"/>.
/// </remarks>
/// <param name="GenerateWordParts">
/// If <see langword="true"/>, generate tokens for alphabetic subword parts split at delimiters and
/// case changes (e.g., <c>PowerShot</c> → <c>Power</c>, <c>Shot</c>).
/// </param>
/// <param name="GenerateNumberParts">
/// If <see langword="true"/>, generate tokens for numeric subword parts (e.g., <c>500D</c> → <c>500</c>, <c>D</c>).
/// </param>
/// <param name="ConcatenateWords">
/// If <see langword="true"/>, add a token that is the concatenation of adjacent alphabetic subwords
/// with delimiters removed (e.g., <c>power_shot</c> → <c>powershot</c> in addition to parts).
/// </param>
/// <param name="ConcatenateNumbers">
/// If <see langword="true"/>, add a token that is the concatenation of adjacent numeric subwords
/// (e.g., <c>500-20</c> → <c>50020</c> in addition to parts).
/// </param>
/// <param name="ConcatenateAll">
/// If <see langword="true"/>, add a token that is the concatenation of all subwords (letters and numbers)
/// with delimiters removed (e.g., <c>PowerShot-500D</c> → <c>PowerShot500D</c>).
/// </param>
/// <param name="PreserveOriginal">
/// If <see langword="true"/>, keep the input token as‑is in addition to any generated parts or concatenations.
/// Useful to match both split and unsplit forms.
/// </param>
/// <param name="SplitOnCaseChange">
/// If <see langword="true"/>, split tokens at case transitions (e.g., <c>camelCase</c> → <c>camel</c>, <c>Case</c>).
/// </param>
/// <param name="SplitOnNumerics">
/// If <see langword="true"/>, split tokens between alphabetic and numeric boundaries (e.g., <c>500D</c> → <c>500</c>, <c>D</c>).
/// </param>
/// <param name="StemEnglishPossessive">
/// If <see langword="true"/>, remove English possessive <c>'s</c> from the end of words before further processing
/// (e.g., <c>children's</c> → <c>children</c>).
/// </param>
/// <param name="IgnoreKeywords">
/// If <see langword="true"/>, do not modify tokens that have been marked as keywords by an upstream
/// <c>keywordMarker</c> filter.
/// </param>
/// <param name="IgnoreCaseForProtectedWords">
/// If <see langword="true"/>, treat entries in the protected words list as case‑insensitive when matching.
/// Has an effect only when protected words are configured on the filter.
/// </param>
public readonly record struct WordDelimiterOptions(
    bool? GenerateWordParts = null,
    bool? GenerateNumberParts = null,
    bool? ConcatenateWords = null,
    bool? ConcatenateNumbers = null,
    bool? ConcatenateAll = null,
    bool? PreserveOriginal = null,
    bool? SplitOnCaseChange = null,
    bool? SplitOnNumerics = null,
    bool? StemEnglishPossessive = null,
    bool? IgnoreKeywords = null,
    bool? IgnoreCaseForProtectedWords = null);
