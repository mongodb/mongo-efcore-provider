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

namespace MongoDB.EntityFrameworkCore.Metadata.Search;

/// <summary>
/// Tokenization strategies for MongoDB search string fields.
/// </summary>
/// <remarks>
/// Tokenization controls how text is split into smaller units (tokens) for indexing and matching.
/// Use edge-gram tokenization for efficient prefix/suffix autocomplete, and n-gram tokenization for
/// infix matching within words. Actual token lengths are controlled by tokenizer options such as
/// <c>minGram</c> and <c>maxGram</c> in the analyzer configuration.
/// See MongoDB search tokenizer docs for details:
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/edge-gram/"/>,
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/tokenizers/n-gram/"/>.
/// </remarks>
public enum SearchTokenization
{
    /// <summary>
    /// Edge-gram tokenization from the left edge (prefix grams). Produces tokens that start at the
    /// beginning of the term and extend up to the configured <c>maxGram</c>. Typically used for
    /// autocomplete and prefix matching (e.g., "mon", "mong", "mongo").
    /// </summary>
    EdgeGram,
    /// <summary>
    /// Edge-gram tokenization from the right edge (suffix grams). Produces tokens that end at the
    /// end of the term and extend backward, enabling suffix matching (e.g., "bson", "son", "on").
    /// Useful when you need to match endings such as file extensions or suffix-based queries.
    /// </summary>
    RightEdgeGram,
    /// <summary>
    /// N-gram tokenization using a sliding window across the entire term (infix grams). Produces
    /// overlapping tokens of length between <c>minGram</c> and <c>maxGram</c>, enabling substring
    /// matching anywhere within a word. Useful for partial/infix search, with increased index size.
    /// </summary>
    NGram
}
