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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions
{
    /// <summary>
    /// The top-level definition for a MongoDB search index.
    /// </summary>
    /// <remarks>
    /// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
    /// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
    /// configure a MongoDB search index.
    /// </remarks>
    public class SearchIndexDefinition : SearchIndexDefinitionWithFields
    {
        private readonly List<ISearchIndexDefinition> _topLevelDefinitions = new();

        /// <summary>
        /// Creates a top-level <see cref="SearchIndexDefinition"/> for the given entity type with the given name.
        /// </summary>
        /// <param name="entityType">The top-level entity type for which the search index is being defined.</param>
        /// <param name="name">The name of the index to create.</param>
        public SearchIndexDefinition(IMutableEntityType entityType, string name)
        {
            EntityType = entityType;

            // ReSharper disable once VirtualMemberCallInConstructor
            Name = name;
        }

        /// <summary>
        /// Specifies the number of sub-indexes to create if the document count exceeds two billion.
        /// </summary>
        public int? NumPartitions { get; set; }

        /// <summary>
        /// The name of the analyzer to use by default for this index.
        /// </summary>
        public string? AnalyzerName { get; set; }

        /// <summary>
        /// The name of the analyzer to use by default for searches with this index.
        /// </summary>
        public string? SearchAnalyzerName { get; set; }

        /// <summary>
        /// Finds the top-level definition for the given name and type. If none is found, then a new instance is created and
        /// returned.
        /// </summary>
        /// <typeparam name="TDefinition">The type of field definition to find or add.</typeparam>
        /// <param name="elementName">The element to which the definition applies.</param>
        /// <returns>The existing definition, or a new one.</returns>
        public TDefinition GetOrAddTopLevelDefinition<TDefinition>(string elementName)
            where TDefinition : ISearchIndexDefinition, new()
            => _topLevelDefinitions.GetOrAddDefinition<TDefinition>(elementName);

        /// <inheritdoc/>
        public override BsonValue ToBson()
        {
            var indexDocument = new BsonDocument();

            indexDocument.Add("analyzer", AnalyzerName, AnalyzerName != null);
            indexDocument.Add("searchAnalyzer", SearchAnalyzerName, SearchAnalyzerName != null);
            indexDocument.Add("numPartitions", NumPartitions, NumPartitions != null);

            var fieldsDocument = new BsonDocument();
            var mappingsDocument = new BsonDocument { { "dynamic", ToDynamicBson() }, { "fields", fieldsDocument } };

            AddFieldsToDocument(fieldsDocument);

            indexDocument.Add("mappings", mappingsDocument);

            foreach (var subDocument in _topLevelDefinitions)
            {
                indexDocument.Add(subDocument.Name, subDocument.ToBson());
            }

            var storedSourceValue = ToStoredSource();
            indexDocument.Add("storedSource", storedSourceValue, storedSourceValue != null);

            return indexDocument;
        }
    }
}
