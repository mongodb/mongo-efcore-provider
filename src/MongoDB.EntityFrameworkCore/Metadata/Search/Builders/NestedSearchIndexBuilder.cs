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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API called from <see cref="SearchIndexBuilder"/> that builds a MongoDB full-text search
/// index definition for embedded (nested) documents or arrays of documents.
/// </summary>
/// <remarks>
/// For more information about search indexes, see <see href="https://www.mongodb.com/docs/atlas/atlas-search/index-definitions/"/>
/// </remarks>
/// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> defining the index being built.</param>
public class NestedSearchIndexBuilder(SearchIndexDefinitionWithFields indexDefinition)
{
    /// <summary>
    /// The <see cref="SearchIndexDefinition"/> defining the index being built.
    /// </summary>
    protected SearchIndexDefinitionWithFields IndexDefinition  { get; } = indexDefinition;

    /// <summary>
    /// Configures the indexing of the embedded document(s) as "dynamic", meaning that all properties will be indexed
    /// automatically using the default search index type set. When dynamic, properties are indexed as boolean, date,
    /// 32 and 64-bit integer, double precision floating point number, objectID, string, or UUID.
    /// </summary>
    /// <remarks>
    /// For more information about dynamic and static mappings, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#dynamic-and-static-mappings"/>
    /// </remarks>
    /// <param name="dynamic">
    /// <see langword="true" /> to index dynamically, <see langword="false" /> to index statically. If this is not set
    /// explicitly, then the index will be static by default.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IsDynamic(bool dynamic = true)
    {
        IndexDefinition.IsDynamic = dynamic;
        IndexDefinition.TypeSetName = null;
        return this;
    }

    /// <summary>
    /// Configures the indexing of the embedded document(s) as "dynamic", meaning that all properties will be indexed
    /// automatically using the type set with the given name. The type set must be defined and added to the index
    /// using <see cref="SearchIndexBuilder.AddTypeSet(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about dynamic and static mappings, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#dynamic-and-static-mappings"/>
    /// </remarks>
    /// <param name="typeSetName">
    /// The name of the type set to use, which must be added to the index using <see cref="SearchIndexBuilder.AddTypeSet(string)"/>.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IsDynamicWithTypeSet(string typeSetName)
    {
        IndexDefinition.IsDynamic = true;
        IndexDefinition.TypeSetName = typeSetName;
        return this;
    }

    /// <summary>
    /// Configures storing all indexed fields in the embedded document(s). Note that storing full documents might
    /// significantly impact performance during indexing and querying. Consider only storing some fields using
    /// <see cref="StoreSourceFor"/>.
    /// </summary>
    /// <remarks>
    /// If not specified, then MongoDB will not store source for all fields.
    /// For more information about stored source, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/stored-source-definition/"/>.
    /// </remarks>
    /// <param name="store">
    /// <see langword="true" /> to store all source. <see langword="false" /> to explicitly not store all source.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder StoreAllSource(bool store = true)
    {
        IndexDefinition.IncludeAll = store;
        IndexDefinition.IncludedFieldNames.Clear();
        IndexDefinition.ExcludedFieldNames.Clear();
        return this;
    }

    /// <summary>
    /// Configures storing or excluding source for the given field or property.
    /// </summary>
    /// <remarks>
    /// For more information about stored source, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/stored-source-definition/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the member to store or exclude source for.</param>
    /// <param name="store"><see langword="true" /> to store source, or <see langword="false" /> to exclude storing source.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder StoreSourceFor(string memberName, bool store = true)
    {
        var property = SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName);
        var fieldName = property.GetElementName();

        IndexDefinition.IncludeAll = null;
        var names = store ? IndexDefinition.IncludedFieldNames : IndexDefinition.ExcludedFieldNames;
        if (!names.Contains(fieldName))
        {
            names.Add(fieldName);
        }
        return this;
    }

    /// <summary>
    /// Indexes the given member for auto-completion. That is, tokens are generated for prefix substrings such that the results
    /// can be used to show all possible auto-completions for that substring.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for auto-completion, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="AutoCompleteSearchIndexBuilder"/> to configure this indexing.</returns>
    public AutoCompleteSearchIndexBuilder IndexAsAutoComplete(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexAutoCompleteDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for auto-completion. That is, tokens are generated for prefix substrings such that the results
    /// can be used to show all possible auto-completions for that substring.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for auto-completion, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="AutoCompleteSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsAutoComplete(string memberName, Action<AutoCompleteSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsAutoComplete(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for boolean values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for boolean values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="BooleanSearchIndexBuilder"/> to configure this indexing.</returns>
    public BooleanSearchIndexBuilder IndexAsBoolean(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexBooleanDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for boolean values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for boolean values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="BooleanSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsBoolean(string memberName, Action<BooleanSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsBoolean(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for date values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for date values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="DateSearchIndexBuilder"/> to configure this indexing.</returns>
    public DateSearchIndexBuilder IndexAsDate(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexDateDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for date values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for date values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="DateSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsDate(string memberName, Action<DateSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsDate(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for geographic points and shape coordinates.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for geographic points and shape coordinates, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="GeoSearchIndexBuilder"/> to configure this indexing.</returns>
    public GeoSearchIndexBuilder IndexAsGeo(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexGeoDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for geographic points and shape coordinates.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for geographic points and shape coordinates, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="GeoSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsGeo(string memberName, Action<GeoSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsGeo(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for numbers.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for numbers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="NumberSearchIndexBuilder"/> to configure this indexing.</returns>
    public NumberSearchIndexBuilder IndexAsNumber(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexNumberDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for numbers.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for numbers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="NumberSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsNumber(string memberName, Action<NumberSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsNumber(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for <see cref="ObjectId"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for ObjectIDs, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/object-id-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="ObjectIdSearchIndexBuilder"/> to configure this indexing.</returns>
    public ObjectIdSearchIndexBuilder IndexAsObjectId(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexObjectIdDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for <see cref="ObjectId"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for ObjectIds, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/object-id-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="ObjectIdSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsObjectId(string memberName, Action<ObjectIdSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsObjectId(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for strings.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for string values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="StringSearchIndexBuilder"/> to configure this indexing.</returns>
    public StringSearchIndexBuilder IndexAsString(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexStringDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for strings.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for string values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="StringSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsString(string memberName, Action<StringSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsString(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member as tokens. That is, the entire value is included as a searchable token. This kind of index
    /// is required for sorting.
    /// </summary>
    /// <remarks>
    /// For more information about indexing as tokens, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/token-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="TokenSearchIndexBuilder"/> to configure this indexing.</returns>
    public TokenSearchIndexBuilder IndexAsToken(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexTokenDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member as tokens. That is, the entire value is included as a searchable token. This kind of index
    /// is required for sorting.
    /// </summary>
    /// <remarks>
    /// For more information about indexing as tokens, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/token-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="TokenSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsToken(string memberName, Action<TokenSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsToken(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member for UUIDs, including <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for UUIDs, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/uuid-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="UuidSearchIndexBuilder"/> to configure this indexing.</returns>
    public UuidSearchIndexBuilder IndexAsUuid(string memberName)
        => new(IndexDefinition.GetOrAddFieldDefinition<SearchIndexUuidDefinition>(
            SearchIndexBuilder.GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

    /// <summary>
    /// Indexes the given member for UUIDs, including <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for UUIDs, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/uuid-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="UuidSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsUuid(string memberName, Action<UuidSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsUuid(memberName));
        return this;
    }

    /// <summary>
    /// Excludes the given member from being indexed.
    /// </summary>
    /// <remarks>
    /// For more information about excluding members, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#exclude-fields-example"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to be excluded from the index.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder ExcludeFromIndex(string memberName)
    {
        IndexDefinition.GetOrAddFieldDefinition<SearchIndexExcludeDefinition>(
            SearchIndexBuilder.GetSearchIndexMember(IndexDefinition.EntityType, memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member as an embedded (nested) document. Further indexing can then be configured on members of this
    /// embedded document.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/document-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="NestedSearchIndexBuilder"/> to configure members of the embedded document.</returns>
    public NestedSearchIndexBuilder IndexAsEmbedded(string memberName)
        => SearchIndexBuilder.CreateNestedBuilder<EmbeddedSearchIndexDefinition>(memberName, IndexDefinition, collection: false);

    /// <summary>
    /// Indexes the given member as an embedded (nested) document. Further indexing can then be configured on members of this
    /// embedded document.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/document-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">
    /// A <see cref="NestedSearchIndexBuilder"/> delegate to configure members of the embedded document.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsEmbedded(string memberName, Action<NestedSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbedded(memberName));
        return this;
    }

    /// <summary>
    /// Indexes the given member as an embedded (nested) array of documents. Further indexing can then be configured on members of
    /// this documents in the embedded array.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) arrays of documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="NestedSearchIndexBuilder"/> to configure members of the embedded documents.</returns>
    public NestedSearchIndexBuilder IndexAsEmbeddedArray(string memberName)
        => SearchIndexBuilder.CreateNestedBuilder<EmbeddedArraySearchIndexDefinition>(
            memberName, IndexDefinition, collection: true);

    /// <summary>
    /// Indexes the given member as an embedded (nested) array of documents. Further indexing can then be configured on members of
    /// this documents in the embedded array.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) arrays of documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/"/>.
    /// </remarks>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">
    /// A <see cref="NestedSearchIndexBuilder"/> delegate to configure members of the embedded documents.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NestedSearchIndexBuilder IndexAsEmbeddedArray(string memberName, Action<NestedSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbeddedArray(memberName));
        return this;
    }
}
