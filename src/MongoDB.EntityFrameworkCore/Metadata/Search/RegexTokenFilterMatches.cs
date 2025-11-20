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
/// Controls how many occurrences of a regular expression are replaced per token by the MongoDB search "regex" token filter.
/// </summary>
/// <remarks>
/// Used with the custom analyzer "regex" token filter to decide whether to replace only the first
/// match in each token or all non-overlapping matches. For background, see the MongoDB Search
/// regex token filter documentation:
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/#std-label-regex-tf-ref"/>.
/// </remarks>
public enum RegexTokenFilterMatches
{
    /// <summary>
    /// Replace all non-overlapping matches of the pattern within each token.
    /// </summary>
    All,

    /// <summary>
    /// Replace only the first match of the pattern within each token; subsequent matches are left unchanged.
    /// </summary>
    None,
}
