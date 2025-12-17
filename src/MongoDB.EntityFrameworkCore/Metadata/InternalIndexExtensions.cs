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

using MongoDB.EntityFrameworkCore.Extensions;
using VectorSimilarity = MongoDB.Driver.VectorSimilarity;

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
        var entityType = index.DeclaringEntityType;
        var path = entityType.GetDocumentPath();

        var similarityValue = vectorIndexOptions.Similarity == VectorSimilarity.DotProduct
            ? "dotProduct" // Because neither "DotProduct" or "dotproduct" are allowed.
            : vectorIndexOptions.Similarity.ToString().ToLowerInvariant();

        var vectorField = new BsonDocument
        {
            { "type", BsonString.Create("vector") },
            { "path", BsonString.Create(string.Join('.', path.Append(index.Properties.Single().GetElementName()))) },
            { "numDimensions", BsonInt32.Create(vectorIndexOptions.Dimensions) },
            { "similarity", BsonString.Create(similarityValue) },
        };

        if (vectorIndexOptions.Quantization.HasValue)
        {
            vectorField.Add("quantization", BsonString.Create(vectorIndexOptions.Quantization.ToString()?.ToLower()));
        }

        if (vectorIndexOptions.HnswMaxEdges != null || vectorIndexOptions.HnswNumEdgeCandidates != null)
        {
            var hnswDocument = new BsonDocument
            {
                { "maxEdges", BsonInt32.Create(vectorIndexOptions.HnswMaxEdges ?? 16) },
                { "numEdgeCandidates", BsonInt32.Create(vectorIndexOptions.HnswNumEdgeCandidates ?? 100) }
            };
            vectorField.Add("hnswOptions", hnswDocument);
        }

        var fieldDocuments = new List<BsonDocument> { vectorField };
        if (vectorIndexOptions.FilterPaths != null)
        {
            BuildFilterPaths(entityType, path, vectorIndexOptions.FilterPaths, fieldDocuments);
        }

        var model = new CreateSearchIndexModel(
            index.Name!,
            SearchIndexType.VectorSearch,
            new BsonDocument { { "fields", BsonArray.Create(fieldDocuments) } });

        return model;
    }

    /// <summary>
    /// Filter paths each point to a single property on a document, but that document may be nested. For nested documents,
    /// the navigations must be followed from the root entity type down to the nested entity type to create the full path.
    /// However, the entity type on which the query is run may also be nested, in which case the first path of the path
    /// comes from here.
    /// </summary>
    private static void BuildFilterPaths(
        IEntityType entityType,
        IReadOnlyList<string> basePath,
        IReadOnlyList<string> specifiedPaths,
        List<BsonDocument> fieldDocuments)
    {
        foreach (var filterPath in specifiedPaths)
        {
            var currentEntityType = entityType;
            var pathRemaining = filterPath;
            var builtPath = basePath.ToList();
            while (true)
            {
                var dotIndex = pathRemaining.IndexOf('.');
                if (dotIndex < 0)
                {
                    // No more dots means this is the last part of the path, which is a property.
                    break;
                }

                // If we are not at the last part, then this part of the path is a navigation to an owned type
                var navigationName = pathRemaining.Substring(0, dotIndex);
                var navigation = currentEntityType?.FindNavigation(navigationName);
                if (navigation != null)
                {
                    currentEntityType = navigation.TargetEntityType;
                    builtPath.Add(currentEntityType.GetContainingElementName()!);
                }
                else
                {
                    // This part handles paths passed as strings for which the fields are not mapped.
                    builtPath.Add(navigationName); // Could be non-mapped but specified by string
                    currentEntityType = null;
                }

                pathRemaining = pathRemaining.Substring(dotIndex + 1);
            }

            var property = currentEntityType?.GetProperty(pathRemaining);
            builtPath.Add(property != null ? property.GetElementName() : pathRemaining);

            var fieldDocument = new BsonDocument
            {
                { "type", BsonString.Create("filter") },
                { "path", BsonString.Create(string.Join('.', builtPath)) }
            };

            fieldDocuments.Add(fieldDocument);
        }
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
