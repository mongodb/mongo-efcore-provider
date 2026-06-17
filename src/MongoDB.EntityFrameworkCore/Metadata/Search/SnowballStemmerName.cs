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
/// Names of Snowball stemmer algorithms supported by the MongoDB search Snowball stemming token filter.
/// </summary>
/// <remarks>
/// Snowball stemming reduces words to their stems according to language‑specific morphological rules
/// (e.g., "running" → "run"). Choose the stemmer that matches the language of the text you index to
/// improve recall while keeping relevant terms grouped together.
/// For details, see MongoDB Search Snowball stemming token filter and the Snowball project:
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/token-filters/snowball-stemming/"/>,
/// <see href="https://snowballstem.org/"/>.
/// </remarks>
public enum SnowballStemmerName
{
    /// <summary>
    /// Arabic Snowball stemmer: light stemming with Arabic‑specific normalization.
    /// </summary>
    Arabic,
    /// <summary>
    /// Armenian Snowball stemmer: suffix stripping for Armenian.
    /// </summary>
    Armenian,
    /// <summary>
    /// Basque Snowball stemmer: Basque morphology handling with conservative suffix removal.
    /// </summary>
    Basque,
    /// <summary>
    /// Catalan Snowball stemmer: reduces common Catalan inflections.
    /// </summary>
    Catalan,
    /// <summary>
    /// Danish Snowball stemmer: Danish diacritics and inflectional endings.
    /// </summary>
    Danish,
    /// <summary>
    /// Dutch Snowball stemmer: handles Dutch plural and verb endings.
    /// </summary>
    Dutch,
    /// <summary>
    /// English Snowball stemmer (Porter2): improved Porter algorithm for English.
    /// </summary>
    English,
    /// <summary>
    /// Estonian Snowball stemmer: Estonian inflectional endings.
    /// </summary>
    Estonian,
    /// <summary>
    /// Finnish Snowball stemmer: strong morphological reduction for Finnish.
    /// </summary>
    Finnish,
    /// <summary>
    /// French Snowball stemmer: accents handling and common French suffixes.
    /// </summary>
    French,
    /// <summary>
    /// German Snowball stemmer: standard German stemming rules.
    /// </summary>
    German,
    /// <summary>
    /// German2 Snowball stemmer: alternative German algorithm with different suffix rules.
    /// </summary>
    German2,
    /// <summary>
    /// Hungarian Snowball stemmer: removes frequent Hungarian suffixes and case endings.
    /// </summary>
    Hungarian,
    /// <summary>
    /// Irish Snowball stemmer: Irish‑specific normalization and suffix stripping.
    /// </summary>
    Irish,
    /// <summary>
    /// Italian Snowball stemmer: reduces common Italian inflections.
    /// </summary>
    Italian,
    /// <summary>
    /// Lithuanian Snowball stemmer: Lithuanian inflectional endings.
    /// </summary>
    Lithuanian,
    /// <summary>
    /// Norwegian Snowball stemmer: stemming for Norwegian (Bokmål/Nynorsk).
    /// </summary>
    Norwegian,
    /// <summary>
    /// Porter stemmer (classic English): original Porter algorithm; more aggressive and older than Porter2.
    /// </summary>
    Porter,
    /// <summary>
    /// Portuguese Snowball stemmer: handles Portuguese plural, gender, and verb endings.
    /// </summary>
    Portuguese,
    /// <summary>
    /// Romanian Snowball stemmer: Romanian diacritics and suffix removal.
    /// </summary>
    Romanian,
    /// <summary>
    /// Russian Snowball stemmer: Cyrillic support with Russian inflectional endings.
    /// </summary>
    Russian,
    /// <summary>
    /// Spanish Snowball stemmer: reduces common Spanish suffixes and verb conjugations.
    /// </summary>
    Spanish,
    /// <summary>
    /// Swedish Snowball stemmer: Swedish plural and inflectional endings.
    /// </summary>
    Swedish,
    /// <summary>
    /// Turkish Snowball stemmer: stemming with Turkish‑specific casing and vowel harmony considerations.
    /// </summary>
    Turkish,
}
