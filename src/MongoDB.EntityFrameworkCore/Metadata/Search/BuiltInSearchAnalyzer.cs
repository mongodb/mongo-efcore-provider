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
/// Built-in MongoDB Search analyzers backed by Apache Lucene.
/// </summary>
/// <remarks>
/// These values correspond to MongoDB Atlas Search built-in analyzers as documented by MongoDB.
/// Each analyzer controls how text is normalized and tokenized for indexing and queries
/// (e.g., lowercasing, stop-word removal, stemming, language-specific rules).
/// Choose the analyzer that matches the language and tokenization behavior you need.
/// For more information about search index analyzers, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
/// </remarks>
public enum BuiltInSearchAnalyzer
{
    /// <summary>
    /// The Lucene Standard analyzer: general-purpose tokenizer with Unicode-aware tokenization,
    /// lowercasing, and optional stop word removal.
    /// </summary>
    LuceneStandard = 0,

    /// <summary>
    /// The Lucene Simple analyzer: lowercases text and splits on any non-letter characters; no stemming.
    /// </summary>
    LuceneSimple = 1,

    /// <summary>
    /// The Lucene Whitespace analyzer: splits tokens only on whitespace; preserves case and punctuation.
    /// </summary>
    LuceneWhitespace = 2,

    /// <summary>
    /// The Lucene Keyword analyzer: treats the entire input as a single token (no tokenization).
    /// </summary>
    LuceneKeyword = 3,

    /// <summary>
    /// Arabic language analyzer: lowercasing, Arabic-specific normalization, stop words, and stemming.
    /// </summary>
    LuceneArabic = 4,

    /// <summary>
    /// Armenian language analyzer: lowercasing, Armenian stop words, and stemming.
    /// </summary>
    LuceneArmenian = 5,

    /// <summary>
    /// Basque language analyzer: lowercasing, Basque stop words, and stemming.
    /// </summary>
    LuceneBasque = 6,

    /// <summary>
    /// Bengali language analyzer: lowercasing, normalization, Bengali stop words, and stemming.
    /// </summary>
    LuceneBengali = 7,

    /// <summary>
    /// Brazilian Portuguese analyzer: lowercasing, Brazilian Portuguese stop words, and stemming.
    /// </summary>
    LuceneBrazilian = 8,

    /// <summary>
    /// Bulgarian language analyzer: lowercasing, stop words, and stemming for Bulgarian.
    /// </summary>
    LuceneBulgarian = 9,

    /// <summary>
    /// Catalan language analyzer: lowercasing, Catalan stop words, and stemming.
    /// </summary>
    LuceneCatalan = 10,

    /// <summary>
    /// Chinese analyzer (deprecated in Lucene but exposed by Atlas Search): tokenization for Chinese text.
    /// Prefer SmartCN where appropriate.
    /// </summary>
    LuceneChinese = 11,

    /// <summary>
    /// CJK analyzer: bigram tokenization suitable for Chinese, Japanese, and Korean text.
    /// </summary>
    LuceneCjk = 12,

    /// <summary>
    /// Czech language analyzer: lowercasing, stop words, and stemming for Czech.
    /// </summary>
    LuceneCzech = 13,

    /// <summary>
    /// Danish language analyzer: lowercasing, Danish stop words, and stemming.
    /// </summary>
    LuceneDanish = 14,

    /// <summary>
    /// Dutch language analyzer: lowercasing, Dutch stop words, and stemming.
    /// </summary>
    LuceneDutch = 15,

    /// <summary>
    /// English language analyzer: lowercasing, English stop words, and stemming.
    /// </summary>
    LuceneEnglish = 16,

    /// <summary>
    /// Finnish language analyzer: lowercasing, Finnish stop words, and stemming.
    /// </summary>
    LuceneFinnish = 17,

    /// <summary>
    /// French language analyzer: lowercasing, French stop words, and stemming.
    /// </summary>
    LuceneFrench = 18,

    /// <summary>
    /// Galician language analyzer: lowercasing, Galician stop words, and stemming.
    /// </summary>
    LuceneGalician = 19,

    /// <summary>
    /// German language analyzer: lowercasing, German stop words, and stemming.
    /// </summary>
    LuceneGerman = 20,

    /// <summary>
    /// Greek language analyzer: lowercasing, Greek stop words, and stemming with Greek-specific normalization.
    /// </summary>
    LuceneGreek = 21,

    /// <summary>
    /// Hindi language analyzer: lowercasing, Devanagari normalization, Hindi stop words, and stemming.
    /// </summary>
    LuceneHindi = 22,

    /// <summary>
    /// Hungarian language analyzer: lowercasing, Hungarian stop words, and stemming.
    /// </summary>
    LuceneHungarian = 23,

    /// <summary>
    /// Indonesian language analyzer: lowercasing, Indonesian stop words, and stemming.
    /// </summary>
    LuceneIndonesian = 24,

    /// <summary>
    /// Irish language analyzer: lowercasing with Irish-specific normalization, stop words, and stemming.
    /// </summary>
    LuceneIrish = 25,

    /// <summary>
    /// Italian language analyzer: lowercasing, Italian stop words, and stemming.
    /// </summary>
    LuceneItalian = 26,

    /// <summary>
    /// Japanese analyzer using morphological analysis (Kuromoji): tokenization, reading forms, stop words, and stemming.
    /// </summary>
    LuceneJapanese = 27,

    /// <summary>
    /// Korean language analyzer with morphological analysis (old): tokenization and normalization for Korean.
    /// Prefer Nori where appropriate.
    /// </summary>
    LuceneKorean = 28,

    /// <summary>
    /// Kuromoji Japanese analyzer: morphological tokenization for Japanese with normalization and stop words.
    /// </summary>
    LuceneKuromoji = 29,

    /// <summary>
    /// Latvian language analyzer: lowercasing, Latvian stop words, and stemming.
    /// </summary>
    LuceneLatvian = 30,

    /// <summary>
    /// Lithuanian language analyzer: lowercasing, Lithuanian stop words, and stemming.
    /// </summary>
    LuceneLithuanian = 31,

    /// <summary>
    /// Morfologik analyzer for Polish: dictionary-based lemmatization/stemming with Polish stop words.
    /// </summary>
    LuceneMorfologik = 32,

    /// <summary>
    /// Nori Korean analyzer: modern Korean morphological analyzer with normalization and stop words.
    /// </summary>
    LuceneNori = 33,

    /// <summary>
    /// Norwegian language analyzer: lowercasing, Norwegian stop words, and stemming.
    /// </summary>
    LuceneNorwegian = 34,

    /// <summary>
    /// Persian language analyzer: Persian-specific normalization, stop words, and tokenization.
    /// </summary>
    LucenePersian = 35,

    /// <summary>
    /// Polish language analyzer: lowercasing, Polish stop words, and stemming.
    /// </summary>
    LucenePolish = 36,

    /// <summary>
    /// Portuguese language analyzer: lowercasing, Portuguese stop words, and stemming.
    /// </summary>
    LucenePortuguese = 37,

    /// <summary>
    /// Romanian language analyzer: lowercasing, Romanian stop words, and stemming.
    /// </summary>
    LuceneRomanian = 38,

    /// <summary>
    /// Russian language analyzer: lowercasing, Russian stop words, and stemming.
    /// </summary>
    LuceneRussian = 39,

    /// <summary>
    /// SmartCN Chinese analyzer: sentence segmentation and tokenization for Simplified Chinese.
    /// </summary>
    LuceneSmartcn = 40,

    /// <summary>
    /// Sorani (Kurdish) analyzer: Sorani-specific normalization, stop words, and stemming.
    /// </summary>
    LuceneSorani = 41,

    /// <summary>
    /// Spanish language analyzer: lowercasing, Spanish stop words, and stemming.
    /// </summary>
    LuceneSpanish = 42,

    /// <summary>
    /// Swedish language analyzer: lowercasing, Swedish stop words, and stemming.
    /// </summary>
    LuceneSwedish = 43,

    /// <summary>
    /// Thai language analyzer: tokenization using Thai dictionary segmentation with normalization and stop words.
    /// </summary>
    LuceneThai = 44,

    /// <summary>
    /// Turkish language analyzer: lowercasing with Turkish-specific casing rules, stop words, and stemming.
    /// </summary>
    LuceneTurkish = 45,

    /// <summary>
    /// Ukrainian language analyzer: lowercasing, Ukrainian stop words, and stemming.
    /// </summary>
    LuceneUkrainian = 46
}
