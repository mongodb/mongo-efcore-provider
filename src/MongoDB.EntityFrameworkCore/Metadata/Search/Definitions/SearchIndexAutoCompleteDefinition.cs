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

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

/// <summary>
/// A MongoDB search definition for the "autocomplete" type.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class SearchIndexAutoCompleteDefinition : SearchIndexDefinitionBase
{
    /// <summary>
    /// The similarity algorithms to use when scoring with the autocomplete operator.
    /// </summary>
    public SearchSimilarityAlgorithm? SimilarityAlgorithm { get; set; }

    /// <summary>
    /// The tokenization strategy to use when indexing the field for autocompletion.
    /// </summary>
    public SearchTokenization? Tokenization { get; set; }

    /// <summary>
    /// Flag to perform normalizations such as including or removing diacritics from the indexed text.
    /// </summary>
    public bool? FoldDiacritics { get; set; }

    /// <summary>
    /// The minimum number of characters per indexed sequence.
    /// </summary>
    public int? MinGrams { get; set; }

    /// <summary>
    /// The maximum number of characters per indexed sequence.
    /// </summary>
    public int? MaxGrams { get; set; }

    /// <summary>
    /// The name of the analyzer to use for auto-completion.
    /// </summary>
    public string? AnalyzerName { get; set; }

    /// <inheritdoc/>
    public override BsonValue ToBson()
    {
        var document = new BsonDocument { { "type", "autocomplete" } };
        document.Add("analyzer", AnalyzerName, AnalyzerName != null);
        document.Add("minGrams", MinGrams, MinGrams != null);
        document.Add("maxGrams", MaxGrams, MaxGrams != null);
        document.Add("tokenization", GetTokenizationString(), Tokenization != null);
        document.Add("foldDiacritics", FoldDiacritics, FoldDiacritics != null);

        if (SimilarityAlgorithm != null)
        {
            var asString = SimilarityAlgorithm.ToString()!;
            document["similarity"] =
                new BsonDocument { { "type", asString.Substring(0, 1).ToLowerInvariant() + asString.Substring(1) } };
        }

        return document;

        string? GetTokenizationString()
        {
            if (Tokenization == null)
            {
                return null;
            }

            var rawString = Tokenization.ToString()!;
            return rawString[0].ToString().ToLower() + rawString.Substring(1);
        }
    }
}
