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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata;

internal static class InternalIndexExtensions
{
    public static string MakeIndexName(this IReadOnlyIndex index)
    {
        // There is no server naming convention for vector indexes, and they cannot be composite, so use a simple name:
        if (index.GetVectorIndexOptions().HasValue)
        {
            return index.Properties.FirstOrDefault()?.Name + "VectorIndex";
        }

        // For normal indexes, mimic the servers index naming convention using the property names and directions
        var parts = new string[index.Properties.Count * 2];

        var propertyIndex = 0;
        var partsIndex = 0;
        foreach (var property in index.Properties)
        {
            parts[partsIndex++] = property.GetElementName();
            parts[partsIndex++] = GetDescending(index, propertyIndex++) ? "-1" : "1";
        }

        return string.Join('_', index.DeclaringEntityType.GetDocumentPath().Concat(parts));
    }

    public static string MakeIndexName(this IKey key)
    {
        var parts = new string[key.Properties.Count];

        var partsIndex = 0;
        foreach (var property in key.Properties)
        {
            parts[partsIndex++] = property.GetElementName() + "_1";
        }

        return string.Join('_', key.DeclaringEntityType.GetDocumentPath().Concat(parts));
    }

    public static bool GetDescending(this IReadOnlyIndex index, int propertyIndex)
        => index.IsDescending switch
        {
            null => false,
            { Count: 0 } => true,
            { } i when i.Count < propertyIndex => false,
            { } i => i.ElementAtOrDefault(propertyIndex)
        };

    public static CreateSearchIndexModel CreateVectorIndexDocument(
        this IIndex index, VectorIndexOptions vectorIndexOptions)
    {
        var path = index.DeclaringEntityType.GetDocumentPath();

        var similarityValue = vectorIndexOptions.Similarity == VectorSimilarity.DotProduct
            ? "dotProduct" // Because neither "DotProduct" or "dotproduct" are allowed.
            : vectorIndexOptions.Similarity.ToString().ToLowerInvariant();

        var pathString = $", path: '{string.Join('.', path.Append(index.Properties.Single().GetElementName()))}'";
        var dimensions = $", numDimensions: {vectorIndexOptions.Dimensions}";
        var similarity = $", similarity: '{similarityValue}'";

        var quantization = vectorIndexOptions.Quantization.HasValue
            ? $", quantization: '{vectorIndexOptions.Quantization.ToString()?.ToLower()}'"
            : "";

        var hnswOptions = vectorIndexOptions.HnswMaxEdges != null || vectorIndexOptions.HnswNumEdgeCandidates != null
            ? $"'hnswOptions': {{ 'maxEdges': {vectorIndexOptions.HnswMaxEdges ?? 16}, 'numEdgeCandidates': {vectorIndexOptions.HnswNumEdgeCandidates ?? 100} }}"
            : "";

        var filters = "";
        if (vectorIndexOptions.FilterPaths != null)
        {
            foreach (var filterPath in vectorIndexOptions.FilterPaths)
            {
                var fullPath = path.Count > 0 ? string.Join('.', path) + '.' + filterPath : filterPath;
                filters += $", {{ type: 'filter', path: '{fullPath}' }}";
            }

        }

        var model = new CreateSearchIndexModel(
            index.Name!,
            SearchIndexType.VectorSearch,
            BsonDocument.Parse(
                $"{{ fields: [ {{ type: 'vector'{pathString}{dimensions}{similarity}{quantization}{hnswOptions} }}{filters} ] }}"));

        return model;
    }

    public static CreateIndexModel<BsonDocument> CreateIndexDocument(this IIndex index)
    {
        var path = index.DeclaringEntityType.GetDocumentPath();

        var doc = new BsonDocument();
        var propertyIndex = 0;

        foreach (var property in index.Properties)
        {
            doc.Add(string.Join('.', path.Append(property.GetElementName())), index.GetDescending(propertyIndex++) ? -1 : 1);
        }

        var options = index.GetCreateIndexOptions() ?? new CreateIndexOptions<BsonDocument>();
        options.Name ??= index.Name!;
        options.Unique ??= index.IsUnique;

        return new CreateIndexModel<BsonDocument>(doc, options);
    }
}
