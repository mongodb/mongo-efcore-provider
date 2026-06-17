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
/// Controls how much term information MongoDB search stores for a string field.
/// </summary>
/// <remarks>
/// This setting maps to the underlying Lucene postings detail (also known as <c>indexOptions</c>),
/// trading index size for richer query features and scoring. Higher levels include the data from
/// lower levels:
/// <list type="bullet">
/// <item><description><see cref="Docs"/>: documents that contain each term.</description></item>
/// <item><description><see cref="Freqs"/>: + term frequencies for better scoring.</description></item>
/// <item><description><see cref="Positions"/>: + token positions for phrase and proximity queries.</description></item>
/// <item><description><see cref="Offsets"/>: + character offsets for precise highlighting/snippets.</description></item>
/// </list>
/// Choose the smallest amount that enables the queries you need to reduce index size and build time.
/// For background and recommended defaults, see Atlas Search string field options:
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string/"/>.
/// </remarks>
public enum StringSearchIndexAmount
{
    /// <summary>
    /// Store only the list of documents that contain each term (no frequencies, positions, or offsets).
    /// Use for basic term existence queries and filtering with minimal index size; limits scoring and
    /// disables phrase/proximity queries and accurate highlighting.
    /// </summary>
    Docs,
    /// <summary>
    /// Store document lists plus per‑document term frequencies. Enables better relevance scoring
    /// based on term frequency while keeping index overhead lower than positions/offsets.
    /// </summary>
    Freqs,
    /// <summary>
    /// Store document lists, term frequencies, and token positions. Required for phrase and proximity
    /// queries (e.g., slop) and improves certain analyzers’ behaviors; increases index size.
    /// </summary>
    Positions,
    /// <summary>
    /// Store document lists, term frequencies, positions, and character offsets. Required for
    /// precise text highlighting/snippet generation and some span queries; largest index footprint.
    /// </summary>
    Offsets
}
