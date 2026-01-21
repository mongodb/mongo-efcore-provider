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
    LuceneStandard,

    /// <summary>
    /// The Lucene Simple analyzer: lowercases text and splits on any non-letter characters; no stemming.
    /// </summary>
    LuceneSimple,

    /// <summary>
    /// The Lucene Whitespace analyzer: splits tokens only on whitespace; preserves case and punctuation.
    /// </summary>
    LuceneWhitespace,

    /// <summary>
    /// The Lucene Keyword analyzer: treats the entire input as a single token (no tokenization).
    /// </summary>
    LuceneKeyword,

    /// <summary>
    /// Arabic language analyzer: lowercasing, Arabic-specific normalization, stop words, and stemming.
    /// </summary>
    LuceneArabic,

    /// <summary>
    /// Armenian language analyzer: lowercasing, Armenian stop words, and stemming.
    /// </summary>
    LuceneArmenian,

    /// <summary>
    /// Basque language analyzer: lowercasing, Basque stop words, and stemming.
    /// </summary>
    LuceneBasque,

    /// <summary>
    /// Bengali language analyzer: lowercasing, normalization, Bengali stop words, and stemming.
    /// </summary>
    LuceneBengali,

    /// <summary>
    /// Brazilian Portuguese analyzer: lowercasing, Brazilian Portuguese stop words, and stemming.
    /// </summary>
    LuceneBrazilian,

    /// <summary>
    /// Bulgarian language analyzer: lowercasing, stop words, and stemming for Bulgarian.
    /// </summary>
    LuceneBulgarian,

    /// <summary>
    /// Catalan language analyzer: lowercasing, Catalan stop words, and stemming.
    /// </summary>
    LuceneCatalan,

    /// <summary>
    /// Chinese analyzer (deprecated in Lucene but exposed by Atlas Search): tokenization for Chinese text.
    /// Prefer SmartCN where appropriate.
    /// </summary>
    LuceneChinese,

    /// <summary>
    /// CJK analyzer: bigram tokenization suitable for Chinese, Japanese, and Korean text.
    /// </summary>
    LuceneCjk,

    /// <summary>
    /// Czech language analyzer: lowercasing, stop words, and stemming for Czech.
    /// </summary>
    LuceneCzech,

    /// <summary>
    /// Danish language analyzer: lowercasing, Danish stop words, and stemming.
    /// </summary>
    LuceneDanish,

    /// <summary>
    /// Dutch language analyzer: lowercasing, Dutch stop words, and stemming.
    /// </summary>
    LuceneDutch,

    /// <summary>
    /// English language analyzer: lowercasing, English stop words, and stemming.
    /// </summary>
    LuceneEnglish,

    /// <summary>
    /// Finnish language analyzer: lowercasing, Finnish stop words, and stemming.
    /// </summary>
    LuceneFinnish,

    /// <summary>
    /// French language analyzer: lowercasing, French stop words, and stemming.
    /// </summary>
    LuceneFrench,

    /// <summary>
    /// Galician language analyzer: lowercasing, Galician stop words, and stemming.
    /// </summary>
    LuceneGalician,

    /// <summary>
    /// German language analyzer: lowercasing, German stop words, and stemming.
    /// </summary>
    LuceneGerman,

    /// <summary>
    /// Greek language analyzer: lowercasing, Greek stop words, and stemming with Greek-specific normalization.
    /// </summary>
    LuceneGreek,

    /// <summary>
    /// Hindi language analyzer: lowercasing, Devanagari normalization, Hindi stop words, and stemming.
    /// </summary>
    LuceneHindi,

    /// <summary>
    /// Hungarian language analyzer: lowercasing, Hungarian stop words, and stemming.
    /// </summary>
    LuceneHungarian,

    /// <summary>
    /// Indonesian language analyzer: lowercasing, Indonesian stop words, and stemming.
    /// </summary>
    LuceneIndonesian,

    /// <summary>
    /// Irish language analyzer: lowercasing with Irish-specific normalization, stop words, and stemming.
    /// </summary>
    LuceneIrish,

    /// <summary>
    /// Italian language analyzer: lowercasing, Italian stop words, and stemming.
    /// </summary>
    LuceneItalian,

    /// <summary>
    /// Japanese analyzer using morphological analysis (Kuromoji): tokenization, reading forms, stop words, and stemming.
    /// </summary>
    LuceneJapanese,

    /// <summary>
    /// Korean language analyzer with morphological analysis (old): tokenization and normalization for Korean.
    /// Prefer Nori where appropriate.
    /// </summary>
    LuceneKorean,

    /// <summary>
    /// Kuromoji Japanese analyzer: morphological tokenization for Japanese with normalization and stop words.
    /// </summary>
    LuceneKuromoji,

    /// <summary>
    /// Latvian language analyzer: lowercasing, Latvian stop words, and stemming.
    /// </summary>
    LuceneLatvian,

    /// <summary>
    /// Lithuanian language analyzer: lowercasing, Lithuanian stop words, and stemming.
    /// </summary>
    LuceneLithuanian,

    /// <summary>
    /// Morfologik analyzer for Polish: dictionary-based lemmatization/stemming with Polish stop words.
    /// </summary>
    LuceneMorfologik,

    /// <summary>
    /// Nori Korean analyzer: modern Korean morphological analyzer with normalization and stop words.
    /// </summary>
    LuceneNori,

    /// <summary>
    /// Norwegian language analyzer: lowercasing, Norwegian stop words, and stemming.
    /// </summary>
    LuceneNorwegian,

    /// <summary>
    /// Persian language analyzer: Persian-specific normalization, stop words, and tokenization.
    /// </summary>
    LucenePersian,

    /// <summary>
    /// Polish language analyzer: lowercasing, Polish stop words, and stemming.
    /// </summary>
    LucenePolish,

    /// <summary>
    /// Portuguese language analyzer: lowercasing, Portuguese stop words, and stemming.
    /// </summary>
    LucenePortuguese,

    /// <summary>
    /// Romanian language analyzer: lowercasing, Romanian stop words, and stemming.
    /// </summary>
    LuceneRomanian,

    /// <summary>
    /// Russian language analyzer: lowercasing, Russian stop words, and stemming.
    /// </summary>
    LuceneRussian,

    /// <summary>
    /// SmartCN Chinese analyzer: sentence segmentation and tokenization for Simplified Chinese.
    /// </summary>
    LuceneSmartcn,

    /// <summary>
    /// Sorani (Kurdish) analyzer: Sorani-specific normalization, stop words, and stemming.
    /// </summary>
    LuceneSorani,

    /// <summary>
    /// Spanish language analyzer: lowercasing, Spanish stop words, and stemming.
    /// </summary>
    LuceneSpanish,

    /// <summary>
    /// Swedish language analyzer: lowercasing, Swedish stop words, and stemming.
    /// </summary>
    LuceneSwedish,

    /// <summary>
    /// Thai language analyzer: tokenization using Thai dictionary segmentation with normalization and stop words.
    /// </summary>
    LuceneThai,

    /// <summary>
    /// Turkish language analyzer: lowercasing with Turkish-specific casing rules, stop words, and stemming.
    /// </summary>
    LuceneTurkish,

    /// <summary>
    /// Ukrainian language analyzer: lowercasing, Ukrainian stop words, and stemming.
    /// </summary>
    LuceneUkrainian
}
