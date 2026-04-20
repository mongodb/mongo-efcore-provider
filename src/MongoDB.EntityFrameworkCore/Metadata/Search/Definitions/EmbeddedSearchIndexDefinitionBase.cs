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
/// Defines a MongoDB search index part that contains field mappings for an embedded (nested) document, or an array of
/// embedded (nested) documents.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public abstract class EmbeddedSearchIndexDefinitionBase : SearchIndexDefinitionWithFields
{
    /// <summary>
    /// Returns "document" for a single embedded (nested) document, or "embeddedDocuments" for an embedded (nested) array of
    /// documents.
    /// </summary>
    protected abstract string EmbeddingType { get; }

    /// <summary>
    /// Generates BSON for the part of the index definition representing the embedded document(s).
    /// </summary>
    /// <returns>The generate BSON.</returns>
    public override BsonValue ToBson()
    {
        var fieldsDocument = new BsonDocument();
        AddFieldsToDocument(fieldsDocument);

        var indexDocument = new BsonDocument
        {
            { "type", EmbeddingType },
            { "dynamic", ToDynamicBson() },
            { "fields", fieldsDocument }
        };

        var storedSourceValue = ToStoredSource();
        indexDocument.Add("storedSource", storedSourceValue, storedSourceValue != null);

        return indexDocument;
    }
}
