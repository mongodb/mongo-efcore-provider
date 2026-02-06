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
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

/// <summary>
/// Definition for the "wordDelimiterGraph" token filter.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class WordDelimiterGraphFilterDefinition : SearchIndexDefinitionBase
{
    /// <summary>
    /// A <see cref="WordDelimiterOptions"/> with options for the filter.
    /// </summary>
    public WordDelimiterOptions? Options { get; set; }

    /// <summary>
    /// List of the tokens to protect from delimitation.
    /// </summary>
    public IReadOnlyList<string>? ProtectedWords { get; set; }

    /// <inheritdoc/>
    public override BsonValue ToBson()
    {
        var document = new BsonDocument { { "type", Name } };
        if (ProtectedWords != null)
        {
            var words = new BsonDocument { { "words", new BsonArray(ProtectedWords) }};
            words.Add("ignoreCase", Options?.IgnoreCaseForProtectedWords, Options?.IgnoreCaseForProtectedWords != null);
            document.Add("protectedWords", words);
        }

        if (Options != null)
        {
            var options = new BsonDocument();
            var optionsValue = Options.Value;
            options.Add("generateWordParts", optionsValue.GenerateWordParts, optionsValue.GenerateWordParts != null);
            options.Add("generateNumberParts", optionsValue.GenerateNumberParts, optionsValue.GenerateNumberParts != null);
            options.Add("concatenateWords", optionsValue.ConcatenateWords, optionsValue.ConcatenateWords != null);
            options.Add("concatenateNumbers", optionsValue.ConcatenateNumbers, optionsValue.ConcatenateNumbers != null);
            options.Add("concatenateAll", optionsValue.ConcatenateAll, optionsValue.ConcatenateAll != null);
            options.Add("preserveOriginal", optionsValue.PreserveOriginal, optionsValue.PreserveOriginal != null);
            options.Add("splitOnCaseChange", optionsValue.SplitOnCaseChange, optionsValue.SplitOnCaseChange != null);
            options.Add("splitOnNumerics", optionsValue.SplitOnNumerics, optionsValue.SplitOnNumerics != null);
            options.Add("stemEnglishPossessive", optionsValue.StemEnglishPossessive, optionsValue.StemEnglishPossessive != null);
            options.Add("ignoreKeywords", optionsValue.IgnoreKeywords, optionsValue.IgnoreKeywords != null);
            document.Add("delimiterOptions", options);
        }

        return document;
    }
}
