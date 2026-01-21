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
/// A MongoDB search definition for the "string" type.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class SearchIndexStringDefinition : SearchIndexDefinitionBase
{
    /// <summary>
    /// The name of the analyzer to use for string indexing.
    /// </summary>
    public string? AnalyzerName { get; set; }

    /// <summary>
    /// The names of alternate analyzer to use for string indexing.
    /// </summary>
    public IDictionary<string, string> AlternateAnalyzers { get; } = new Dictionary<string, string>();

    /// <summary>
    /// The names of alternate similarity algorithms to use for string indexing.
    /// </summary>
    public IDictionary<string, SearchSimilarityAlgorithm> AlternateSimilarityAlgorithms { get; } =
        new Dictionary<string, SearchSimilarityAlgorithm>();

    /// <summary>
    /// Configures the name of the analyzer to use for string searches with this index.
    /// </summary>
    public string? SearchAnalyzerName { get; set; }

    /// <summary>
    /// The amount of information to store for the indexed field.
    /// </summary>
    public StringSearchIndexAmount? IndexAmount { get; set; }

    /// <summary>
    /// A flag that indicates whether to store the exact document text as well as the analyzed values in the index.
    /// </summary>
    public bool? StoreDocumentText { get; set; }

    /// <summary>
    /// The maximum number of characters in the value of the field to index.
    /// </summary>
    public int? IgnoreAbove { get; set; }

    /// <summary>
    /// The similarity algorithms to use when scoring for string indexes.
    /// </summary>
    public SearchSimilarityAlgorithm? SimilarityAlgorithm { get; set; }

    /// <summary>
    /// A flag that specifies whether to include or omit the field length in the result when scoring.
    /// </summary>
    public bool? IncludeFieldLength { get; set; }

    /// <inheritdoc/>
    public override BsonValue ToBson()
    {
        var document = new BsonDocument { { "type", "string" } };
        document.Add("analyzer", AnalyzerName, AnalyzerName != null);

        if (AlternateAnalyzers.Count > 0 || AlternateSimilarityAlgorithms.Count > 0)
        {
            var multiDocument = new BsonDocument();

            foreach (var alternateAnalyzer in AlternateAnalyzers)
            {
                multiDocument.Add(alternateAnalyzer.Key,
                    new BsonDocument { { "type", "string" }, { "analyzer", alternateAnalyzer.Value } });
            }

            foreach (var alternateSimilarityAlgorithm in AlternateSimilarityAlgorithms)
            {
                var asString = alternateSimilarityAlgorithm.Value.ToString();
                multiDocument.Add(alternateSimilarityAlgorithm.Key,
                    new BsonDocument
                    {
                        { "type", "string" },
                        {
                            "similarity",
                            new BsonDocument { { "type", asString.Substring(0, 1).ToLowerInvariant() + asString.Substring(1) } }
                        }
                    });
            }

            document.Add("multi", multiDocument);
        }

        document.Add("searchAnalyzer", SearchAnalyzerName, SearchAnalyzerName != null);
        document.Add("indexOptions", IndexAmount?.ToString().ToLowerInvariant(), IndexAmount != null);
        document.Add("store", StoreDocumentText, StoreDocumentText != null);
        document.Add("ignoreAbove", IgnoreAbove, IgnoreAbove != null);

        if (SimilarityAlgorithm != null)
        {
            var asString = SimilarityAlgorithm.ToString()!;
            document["similarity"] =
                new BsonDocument { { "type", asString.Substring(0, 1).ToLowerInvariant() + asString.Substring(1) } };
        }

        document.Add("norms", IncludeFieldLength == true ? "include" : "omit", IncludeFieldLength != null);
        return document;
    }
}
