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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

/// <summary>
/// Defines a MongoDB search index part that contains field mappings. This could be for the top level document, or for
/// embedded (nested) documents.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public abstract class SearchIndexDefinitionWithFields : SearchIndexDefinitionBase
{
    /// <summary>
    /// The entity type for which the search index is being defined. May be an owned entity type when defining indexes on
    /// properties of embedded (nested) entity types.
    /// </summary>
    public IMutableEntityType EntityType { get; set; } = null!;

    /// <summary>
    /// Definitions for each field included in the index.
    /// </summary>
    public List<ISearchIndexDefinition> FieldDefinitions { get; } = new();

    /// <summary>
    /// Marks this definition as dynamic or static.
    /// </summary>
    public bool? IsDynamic { get; set; }

    /// <summary>
    /// The type set name to use for dynamic mappings. If this is not set and <see cref="IsDynamic"/> is true, then the
    /// default type set is used.
    /// </summary>
    public string? TypeSetName { get; set; }

    /// <summary>
    /// Indicates whether to include source for all fields. If this property is set, then
    /// <see cref="IncludedFieldNames"/> and <see cref="ExcludedFieldNames"/> are not used.
    /// </summary>
    public bool? IncludeAll { get; set; }

    /// <summary>
    /// The fields to store source for. Not used if <see cref="IncludeAll"/> is set.
    /// </summary>
    public IList<string> IncludedFieldNames { get; } = new List<string>();

    /// <summary>
    /// The fields to exclude storing source for. Not used if <see cref="IncludeAll"/> is set.
    /// </summary>
    public IList<string> ExcludedFieldNames { get; } = new List<string>();

    /// <summary>
    /// Creates a BSON fragment for the "dynamic: " element in a top-level or nested definition.
    /// </summary>
    /// <returns>The BSON to use.</returns>
    protected BsonValue ToDynamicBson()
        => IsDynamic == true
            ? TypeSetName != null
                ? new BsonDocument { { "typeSet", TypeSetName } }
                : true
            : false;

    /// <summary>
    /// Finds the definition for the field mapped to a given property of the given type. If none is found,
    /// then a new instance is created and returned.
    /// </summary>
    /// <typeparam name="TDefinition">The type of field definition to find or add.</typeparam>
    /// <param name="member">The member to which the definition applies.</param>
    /// <returns>The existing definition, or a new one.</returns>
    public TDefinition GetOrAddFieldDefinition<TDefinition>(IMutablePropertyBase member)
        where TDefinition : ISearchIndexDefinition, new()
    {
        var elementName = member switch
        {
            IProperty property => property.GetElementName(),
            INavigationBase navigationBase => navigationBase.TargetEntityType.GetContainingElementName()!,
            _ => throw new NotSupportedException($"Member {member.GetType()} is not supported.")
        };

        return GetOrAddFieldDefinition<TDefinition>(elementName);
    }

    /// <summary>
    /// Finds the definition for the field with the given name If none is found, then a new instance is created and returned.
    /// </summary>
    /// <typeparam name="TDefinition">The type of field definition to find or add.</typeparam>
    /// <param name="fieldName">The name of the field to which the definition applies.</param>
    /// <returns>The existing definition, or a new one.</returns>
    public TDefinition GetOrAddFieldDefinition<TDefinition>(string fieldName)
        where TDefinition : ISearchIndexDefinition, new()
        => FieldDefinitions.GetOrAddDefinition<TDefinition>(fieldName);

    /// <summary>
    /// Creates a BSON fragment for the "storedSource: " element in a top-level or nested definition.
    /// </summary>
    /// <returns>The BSON to use, or <see langword="null"/> if stored-source processing should stop.</returns>
    protected virtual BsonValue? ToStoredSource()
    {
        if (IncludeAll != null)
        {
            return IncludeAll.Value;
        }

        var includedFieldNames = IncludedFieldNames.ToList();
        var excludedFieldNames = ExcludedFieldNames.ToList();

        foreach (var definition in FieldDefinitions.OfType<SearchIndexDefinitionWithFields>())
        {
            AddFieldNames("", definition);
        }

        if (includedFieldNames.Any())
        {
            if (excludedFieldNames.Any())
            {
                throw new InvalidOperationException(
                    $"Stored source for '{Name}' has both excluded and included field names. Stored source can be configured to exclude or include field names, but not both.");
            }

            return new BsonDocument { { "include", new BsonArray(includedFieldNames) } };
        }

        if (excludedFieldNames.Any())
        {
            return new BsonDocument { { "exclude", new BsonArray(excludedFieldNames) } };
        }

        return null;

        void AddFieldNames(string prefix, SearchIndexDefinitionWithFields definition)
        {
            if (definition is EmbeddedArraySearchIndexDefinition)
            {
                return;
            }

            prefix += definition.EntityType.GetContainingElementName() + ".";

            foreach (var fieldName in definition.IncludedFieldNames)
            {
                includedFieldNames.Add(prefix + fieldName);
            }

            foreach (var fieldName in definition.ExcludedFieldNames)
            {
                excludedFieldNames.Add(prefix + fieldName);
            }

            foreach (var nestedDefinition in definition.FieldDefinitions.OfType<SearchIndexDefinitionWithFields>())
            {
                AddFieldNames(prefix, nestedDefinition);
            }
        }
    }

    /// <summary>
    /// Goes through each field definition and adds an entry for it to the given document.
    /// </summary>
    /// <param name="fieldsDocument">The document to which fields should be added.</param>
    protected void AddFieldsToDocument(BsonDocument fieldsDocument)
    {
        var sortedByName = new Dictionary<string, List<BsonValue>>();
        foreach (var fieldDocument in FieldDefinitions)
        {
            if (!sortedByName.TryGetValue(fieldDocument.Name, out var definitions))
            {
                definitions = new();
                sortedByName[fieldDocument.Name] = definitions;
            }
            definitions.Add(fieldDocument.ToBson());
        }

        foreach (var fieldName in sortedByName.Keys)
        {
            var definitions = sortedByName[fieldName];
            if (definitions.Count == 1)
            {
                fieldsDocument.Add(fieldName, definitions[0]);
            }
            else
            {
                var array = new BsonArray();
                foreach (var definition in definitions)
                {
                    array.Add(definition);
                }
                fieldsDocument.Add(fieldName, array);
            }
        }
    }
}
