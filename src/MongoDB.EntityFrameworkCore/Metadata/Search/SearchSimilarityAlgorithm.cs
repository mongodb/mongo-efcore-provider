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
/// Relevance scoring algorithms for MongoDB search string fields.
/// </summary>
/// <remarks>
/// Similarity determines how MongoDB search computes the score of a document for a given
/// query, typically based on term frequency, inverse document frequency, and field length.
/// Choose an algorithm that matches your scoring needs (e.g., full relevance ranking vs.
/// presence-only matching). For background, see Atlas Search string field options.
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string/"/>
/// </remarks>
public enum SearchSimilarityAlgorithm
{
    /// <summary>
    /// Okapi BM25 (default): probabilistic relevance model that balances term frequency (with
    /// saturation), inverse document frequency, and field-length normalization for robust ranking.
    /// </summary>
    Bm25,
    /// <summary>
    /// Boolean model: ignores term frequency and length normalization; focuses on presence/absence
    /// of query terms for deterministic matching with minimal scoring variation.
    /// </summary>
    Boolean,
    /// <summary>
    /// Stable TF-L: stable term-frequency and length-based scoring designed to keep scores
    /// consistent across index updates and corpus changes, trading off nuanced BM25-style ranking.
    /// Useful when predictable, repeatable scores are preferred over aggressive relevance tuning.
    /// </summary>
    StableTfl
}
