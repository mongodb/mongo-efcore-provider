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
/// Unicode normalization forms supported by the ICU Normalization token filter in MongoDB search.
/// </summary>
/// <remarks>
/// ICU normalization converts text into a canonical representation so that visually equivalent strings
/// compare and sort consistently. Use these forms to control whether text is decomposed or composed and
/// whether compatibility mappings are applied.
/// For background on Unicode normalization, see Unicode Standard Annex #15.
/// <see href="https://unicode.org/reports/tr15/"/>
/// </remarks>
public enum IcuNormalizationForm
{
    /// <summary>
    /// Normalization Form Decomposed (NFD): canonical decomposition only.
    /// Characters are expanded into base characters and combining marks; no compatibility folding.
    /// </summary>
    Nfd,

    /// <summary>
    /// Normalization Form Composed (NFC): canonical decomposition followed by canonical composition.
    /// Produces a preferred composed form where available; no compatibility folding.
    /// </summary>
    Nfc,

    /// <summary>
    /// Normalization Form Compatibility Decomposed (NFKD): applies compatibility mappings and then decomposes.
    /// Useful when you want to remove distinctions such as font variants and ligatures.
    /// </summary>
    Nfkd,

    /// <summary>
    /// Normalization Form Compatibility Composed (NFKC): applies compatibility mappings, then composes.
    /// Produces a canonical, compatibility-folded composed form suitable for search and comparison.
    /// </summary>
    Nfkc
}
