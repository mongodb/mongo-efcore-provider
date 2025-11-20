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
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API for use in <see cref="DbContext.OnModelCreating"/> that builds a MongoDB full-text search index.
/// </summary>
/// <remarks>
/// For more information about search indexes, see <see href="https://www.mongodb.com/docs/atlas/atlas-search/index-definitions/"/>
/// </remarks>
/// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> defining the index being built.</param>
public class SearchIndexBuilder(SearchIndexDefinition indexDefinition)
{
    /// <summary>
    /// The <see cref="SearchIndexDefinition"/> defining the index being built.
    /// </summary>
    protected SearchIndexDefinition IndexDefinition  { get; } = indexDefinition;

    /// <summary>
    /// Configures this index as "dynamic", meaning that all properties will be indexed automatically using the default search
    /// index type set. When dynamic, properties are indexed as boolean, date, 32 and 64-bit integer, double precision floating
    /// point number, objectID, string, or UUID.
    /// </summary>
    /// <remarks>
    /// For more information about dynamic and static mappings, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#dynamic-and-static-mappings"/>
    /// </remarks>
    /// <param name="dynamic">
    /// <see langword="true" /> to create a dynamic index, <see langword="false" /> to create a static index. If this is not set
    /// explicitly, then the index will be static by default.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder IsDynamic(bool dynamic = true)
    {
        IndexDefinition.IsDynamic = dynamic;
        IndexDefinition.TypeSetName = null;
        return this;
    }

    /// <summary>
    /// Configures this index as "dynamic", meaning that all properties will be indexed automatically using the type set with
    /// the given name. The type set must be defined and added to the index using <see cref="AddTypeSet(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about dynamic and static mappings, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#dynamic-and-static-mappings"/>
    /// </remarks>
    /// <param name="typeSetName">
    /// The name of the type set to use, which must be added to the index using <see cref="AddTypeSet(string)"/>.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder IsDynamicWithTypeSet(string typeSetName)
    {
        IndexDefinition.IsDynamic = true;
        IndexDefinition.TypeSetName = typeSetName;
        return this;
    }

    /// <summary>
    /// Configures the name of the analyzer to use by default for this index. This must be the name of a built-in analyzer (but
    /// consider using <see cref="UseAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined in this index
    /// with <see cref="AddCustomAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder UseAnalyzer(string analyzerName)
    {
        IndexDefinition.AnalyzerName = analyzerName;
        return this;
    }

    /// <summary>
    /// Configures the built-in analyzer (defined by <see cref="BuiltInSearchAnalyzer"/>) to use by default for this index.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder UseAnalyzer(BuiltInSearchAnalyzer analyzer)
        => UseAnalyzer(ToAnalyzerName(analyzer));

    /// <summary>
    /// Configures the name of the search analyzer to use by default for this index. This must be the name of a built-in analyzer
    /// (but consider using <see cref="UseSearchAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined in this
    /// index with <see cref="AddCustomAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder UseSearchAnalyzer(string analyzerName)
    {
        IndexDefinition.SearchAnalyzerName = analyzerName;
        return this;
    }

    /// <summary>
    /// Configures the built-in search analyzer (defined by <see cref="BuiltInSearchAnalyzer"/>) to use by default for this index.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder UseSearchAnalyzer(BuiltInSearchAnalyzer analyzer)
        => UseSearchAnalyzer(ToAnalyzerName(analyzer));

    /// <summary>
    /// Specifies the number of sub-indexes to create if the document count exceeds two billion. The following values are valid:
    /// 1, 2, 4. If omitted, defaults to 1. To use index partitions, you must have search nodes deployed in your cluster.
    /// </summary>
    /// <remarks>
    /// For more information about search index partitions, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/index-partition/"/>.
    /// </remarks>
    /// <param name="numPartitions">the number of sub-indexes to create if the document count exceeds two billion.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder HasPartitions(int numPartitions)
    {
        IndexDefinition.NumPartitions = numPartitions;
        return this;
    }

    /// <summary>
    /// Configures storing all indexed fields in the document. Note that storing full documents might significantly impact
    /// performance during indexing and querying. Consider only storing some fields using <see cref="StoreSourceFor"/>.
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
    public SearchIndexBuilder StoreAllSource(bool store = true)
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
    public SearchIndexBuilder StoreSourceFor(string memberName, bool store = true)
    {
        var property = GetSearchIndexProperty(IndexDefinition.EntityType, memberName);
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsAutoComplete(string memberName, Action<AutoCompleteSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsBoolean(string memberName, Action<BooleanSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsDate(string memberName, Action<DateSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsGeo(string memberName, Action<GeoSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsNumber(string memberName, Action<NumberSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsObjectId(string memberName, Action<ObjectIdSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsString(string memberName, Action<StringSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsToken(string memberName, Action<TokenSearchIndexBuilder> nestedBuilder)
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
            GetSearchIndexProperty(IndexDefinition.EntityType, memberName)));

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
    public SearchIndexBuilder IndexAsUuid(string memberName, Action<UuidSearchIndexBuilder> nestedBuilder)
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
    public SearchIndexBuilder ExcludeFromIndex(string memberName)
    {
        IndexDefinition.GetOrAddFieldDefinition<SearchIndexExcludeDefinition>(
            GetSearchIndexMember(IndexDefinition.EntityType, memberName));
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
        => CreateNestedBuilder<EmbeddedSearchIndexDefinition>(memberName, IndexDefinition, collection: false);

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
    public SearchIndexBuilder IndexAsEmbedded(string memberName, Action<NestedSearchIndexBuilder> nestedBuilder)
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
        => CreateNestedBuilder<EmbeddedArraySearchIndexDefinition>(memberName, IndexDefinition, collection: true);

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
    public SearchIndexBuilder IndexAsEmbeddedArray(string memberName, Action<NestedSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbeddedArray(memberName));
        return this;
    }

    /// <summary>
    /// Adds a custom type set definition to this index. Type sets define how to dynamically index members. To use the type
    /// set, pass the defined type set name to <see cref="IsDynamicWithTypeSet"/> at the top level or on an embedded (nested)
    /// document or array of documents.
    /// </summary>
    /// <remarks>
    /// For more information about type sets, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/"/>.
    /// </remarks>
    /// <param name="typeSetName">The name of the type set to define. </param>
    /// <returns>A <see cref="TypeSetSearchIndexBuilder"/> to configure the type set.</returns>
    public TypeSetSearchIndexBuilder AddTypeSet(string typeSetName)
        => new(IndexDefinition, typeSetName);

    /// <summary>
    /// Adds a custom type set definition to this index. Type sets define how to dynamically index members. To use the type
    /// set, pass the defined type set name to <see cref="IsDynamicWithTypeSet"/> at the top level or on an embedded (nested)
    /// document or array of documents.
    /// </summary>
    /// <remarks>
    /// For more information about type sets, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/"/>.
    /// </remarks>
    /// <param name="typeSetName">The name of the type set to define.</param>
    /// <param name="nestedBuilder">A <see cref="TypeSetSearchIndexBuilder"/> delegate to configure the type set.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder AddTypeSet(string typeSetName, Action<TypeSetSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(AddTypeSet(typeSetName));
        return this;
    }

    /// <summary>
    /// Adds a collection to the index that contains synonym definitions.
    /// </summary>
    /// <remarks>
    /// For more information about synonym mapping, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/synonyms/"/>.
    /// </remarks>
    /// <param name="name">The name that defines this synonym mapping which must be passed to the query.</param>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <param name="synonymsCollectionName">The name of the collection containing the synonyms.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder AddSynonyms(
        string name, string analyzerName, string synonymsCollectionName)
    {
        var synonymDefinition = IndexDefinition.GetOrAddTopLevelDefinition<SearchIndexSynonymsDefinition>("synonyms")
            .GetOrAddSearchIndexSynonymDefinition(name);

        synonymDefinition.AnalyzerName = analyzerName;
        synonymDefinition.CollectionName = synonymsCollectionName;
        return this;
    }

    /// <summary>
    /// Adds a collection to the index that contains synonym definitions.
    /// </summary>
    /// <remarks>
    /// For more information about synonym mapping, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/synonyms/"/>.
    /// </remarks>
    /// <param name="name">The name that defines this synonym mapping which must be passed to the query.</param>
    /// <param name="analyzer">The well-known analyzer to use, as defined by <see cref="BuiltInSearchAnalyzer"/>.</param>
    /// <param name="synonymsCollectionName">The name of the collection containing the synonyms.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder AddSynonyms(string name, BuiltInSearchAnalyzer analyzer, string synonymsCollectionName)
        => AddSynonyms(name, ToAnalyzerName(analyzer), synonymsCollectionName);

    /// <summary>
    /// Adds and defines a custom search analyzer to the index. The custom analyzer is used by passing its name to any of the
    /// methods that accept an analyzer name, such as <see cref="UseAnalyzer(string)"/> and <see cref="UseSearchAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of the analyzer to define and add to the index.</param>
    /// <returns>A <see cref="AnalyzerSearchIndexBuilder"/> to configure the analyzer.</returns>
    public AnalyzerSearchIndexBuilder AddCustomAnalyzer(string analyzerName)
        => new(IndexDefinition, analyzerName);

    /// <summary>
    /// Adds and defines a custom search analyzer to the index. The custom analyzer is used by passing its name to any of the
    /// methods that accept an analyzer name, such as <see cref="UseAnalyzer(string)"/> and <see cref="UseSearchAnalyzer(string)"/>.
    /// </summary>
    /// <remarks>
    /// For more information about analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of the analyzer to define and add to the index.</param>
    /// <param name="nestedBuilder">A <see cref="AnalyzerSearchIndexBuilder"/> delegate to configure the analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder AddCustomAnalyzer(string analyzerName, Action<AnalyzerSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(AddCustomAnalyzer(analyzerName));
        return this;
    }

    internal static NestedSearchIndexBuilder CreateNestedBuilder<TDefinition>(
        string memberName, SearchIndexDefinitionWithFields indexDefinition, bool collection)
        where TDefinition : SearchIndexDefinitionWithFields, new()
        => new(GetNestedType<TDefinition>(indexDefinition.EntityType, memberName, indexDefinition, collection));

    internal static NestedSearchIndexBuilder<TEntity> CreateNestedBuilder<TEntity, TDefinition>(
        string memberName, SearchIndexDefinitionWithFields indexDefinition, bool collection)
        where TDefinition : SearchIndexDefinitionWithFields, new()
        => new(GetNestedType<TDefinition>(indexDefinition.EntityType, memberName, indexDefinition, collection));

    private static SearchIndexDefinitionWithFields GetNestedType<TDefinition>(
        IMutableEntityType entityType, string memberName, SearchIndexDefinitionWithFields indexDefinition, bool collection)
        where TDefinition : SearchIndexDefinitionWithFields, new()
    {
        var member = GetSearchIndexMember(entityType, memberName);
        if (member is INavigationBase navigationBase)
        {
            if (collection != navigationBase.IsCollection)
            {
                const string secondBit =
                    "References to a single object can be indexed as 'embedded', while references to a collection of objects can be indexed as 'embedded array'.";

                if (collection)
                {
                    throw new InvalidOperationException(
                        $"The member '{entityType.DisplayName()}.{memberName}' cannot be indexed as an embedded array because it is not a collection of embedded objects. {secondBit}");
                }
                throw new InvalidOperationException(
                    $"The member '{entityType.DisplayName()}.{memberName}' cannot be indexed as an embedded object because it is a collection of objects. {secondBit}");
            }

            var nestedEntityType = navigationBase.TargetEntityType;
            var embeddedDefinition = indexDefinition.GetOrAddFieldDefinition<TDefinition>(nestedEntityType.GetContainingElementName()!);
            embeddedDefinition.EntityType = (IMutableEntityType)nestedEntityType;
            return embeddedDefinition;
        }

        throw new InvalidOperationException(
            $"The member '{entityType.DisplayName()}.{memberName}' does not point to a nested owned entity. Make sure that the referenced type is mapped as a nested/embedded document.");
    }

    internal static IMutablePropertyBase GetSearchIndexMember(IMutableEntityType entityType, string memberName)
    {
        var propertyBase = entityType.FindMember(memberName);
        if (propertyBase == null)
        {
            throw new InvalidOperationException(
                $"Member '{memberName}' was not found or not mapped to a property or navigation in entity type '{entityType.DisplayName()}'. Make sure all properties in search indexes are mapped in the entity framework model.");
        }
        return propertyBase;
    }

    internal static IMutableProperty GetSearchIndexProperty(IMutableEntityType entityType, string memberName)
    {
        var member = GetSearchIndexMember(entityType, memberName);
        if (member is IMutableProperty property)
        {
            return property;
        }

        {
            throw new InvalidOperationException(
                $"Member '{memberName}' was not found or not mapped to a property in entity type '{entityType.DisplayName()}'. Make sure all properties in search indexes are mapped in the entity framework model.");
        }
    }

    internal static string ToAnalyzerName(BuiltInSearchAnalyzer analyzer)
        => "lucene." + analyzer.ToString().Substring(6).ToLowerInvariant();
}
