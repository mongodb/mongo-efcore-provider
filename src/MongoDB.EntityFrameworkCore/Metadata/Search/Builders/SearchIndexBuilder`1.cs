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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API for use in <see cref="DbContext.OnModelCreating"/> that builds aMongoDB full-text search index
/// for the given entity type.
/// </summary>
/// <remarks>
/// For more information about search indexes, see <see href="https://www.mongodb.com/docs/atlas/atlas-search/index-definitions/"/>
/// </remarks>
/// <typeparam name="TEntity">The entity type the search index is being configured for.</typeparam>
/// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> defining the index being built.</param>
public class SearchIndexBuilder<TEntity>(SearchIndexDefinition indexDefinition)
    : SearchIndexBuilder(indexDefinition)
{
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
    public new SearchIndexBuilder<TEntity> IsDynamic(bool dynamic = true)
    {
        IndexDefinition.IsDynamic = dynamic;
        IndexDefinition.TypeSetName = null;
        return this;
    }

    /// <summary>
    /// Configures this index as "dynamic", meaning that all properties will be indexed automatically using the type set with
    /// the given name. The type set must be defined and added to the index using
    /// <see cref="AddTypeSet(string, Action{TypeSetSearchIndexBuilder})"/>.
    /// </summary>
    /// <remarks>
    /// For more information about dynamic and static mappings, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#dynamic-and-static-mappings"/>
    /// </remarks>
    /// <param name="typeSetName">
    /// The name of the type set to use, which must be added to the index using
    /// <see cref="AddTypeSet(string, Action{TypeSetSearchIndexBuilder})"/>.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> IsDynamicWithTypeSet(string typeSetName)
    {
        IndexDefinition.IsDynamic = true;
        IndexDefinition.TypeSetName = typeSetName;
        return this;
    }

    /// <summary>
    /// Configures the name of the analyzer to use by default for this index. This must be the name of a built-in analyzer (but
    /// consider using <see cref="UseAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined in this index
    /// with <see cref="AddCustomAnalyzer(string, Action{AnalyzerSearchIndexBuilder})"/>.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> UseAnalyzer(string analyzerName)
    {
        base.UseAnalyzer(analyzerName);
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
    public new SearchIndexBuilder<TEntity> UseAnalyzer(BuiltInSearchAnalyzer analyzer)
        => UseAnalyzer(ToAnalyzerName(analyzer));

    /// <summary>
    /// Configures the name of the search analyzer to use by default for this index. This must be the name of a built-in analyzer
    /// (but consider using <see cref="UseSearchAnalyzer(BuiltInSearchAnalyzer)"/> instead) or a custom analyzer defined in this
    /// index with <see cref="AddCustomAnalyzer(string, Action{AnalyzerSearchIndexBuilder})"/>
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of a well-known or custom analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> UseSearchAnalyzer(string analyzerName)
    {
        base.UseSearchAnalyzer(analyzerName);
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
    public new SearchIndexBuilder<TEntity> UseSearchAnalyzer(BuiltInSearchAnalyzer analyzer)
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
    public new SearchIndexBuilder<TEntity> HasPartitions(int numPartitions)
        => (SearchIndexBuilder<TEntity>)base.HasPartitions(numPartitions);

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
    public new SearchIndexBuilder<TEntity> StoreAllSource(bool store = true)
    {
        base.StoreAllSource(store);
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
    public new SearchIndexBuilder<TEntity> StoreSourceFor(string memberName, bool store = true)
    {
        base.StoreSourceFor(memberName, store);
        return this;
    }

    /// <summary>
    /// Configures storing or excluding source for the given field or property.
    /// </summary>
    /// <remarks>
    /// For more information about stored source, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/stored-source-definition/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to store or exclude source for.</param>
    /// <param name="store"><see langword="true" /> to store source, or <see langword="false" /> to exclude storing source.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> StoreSourceFor<TMember>(Expression<Func<TEntity, TMember>> member, bool store = true)
        => StoreSourceFor(member.GetMemberAccess().Name, store);

    /// <summary>
    /// Indexes the given member for auto-completion. That is, tokens are generated for prefix substrings such that the results
    /// can be used to show all possible auto-completions for that substring.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for auto-completion, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="AutoCompleteSearchIndexBuilder"/> to configure this indexing.</returns>
    public AutoCompleteSearchIndexBuilder IndexAsAutoComplete<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsAutoComplete(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for auto-completion. That is, tokens are generated for prefix substrings such that the results
    /// can be used to show all possible auto-completions for that substring.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for auto-completion, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="AutoCompleteSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsAutoComplete<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<AutoCompleteSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsAutoComplete(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for boolean values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for boolean values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="BooleanSearchIndexBuilder"/> to configure this indexing.</returns>
    public BooleanSearchIndexBuilder IndexAsBoolean<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsBoolean(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for boolean values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for boolean values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="BooleanSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsBoolean<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<BooleanSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsBoolean(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for date values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for date values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="DateSearchIndexBuilder"/> to configure this indexing.</returns>
    public DateSearchIndexBuilder IndexAsDate<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsDate(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for date values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for date values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="DateSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsDate<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<DateSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsDate(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for geographic points and shape coordinates.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for geographic points and shape coordinates, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="GeoSearchIndexBuilder"/> to configure this indexing.</returns>
    public GeoSearchIndexBuilder IndexAsGeo<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsGeo(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for geographic points and shape coordinates.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for geographic points and shape coordinates, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="GeoSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsGeo<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<GeoSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsGeo(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for numeric values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for numeric values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="NumberSearchIndexBuilder"/> to configure this indexing.</returns>
    public NumberSearchIndexBuilder IndexAsNumber<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsNumber(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for numeric values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for numeric values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="NumberSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsNumber<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<NumberSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsNumber(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for BSON ObjectId values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for ObjectId values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/objectId-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>An <see cref="ObjectIdSearchIndexBuilder"/> to configure this indexing.</returns>
    public ObjectIdSearchIndexBuilder IndexAsObjectId<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsObjectId(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for BSON ObjectId values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for ObjectId values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/objectId-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">An <see cref="ObjectIdSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsObjectId<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<ObjectIdSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsObjectId(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for string values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for string values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="StringSearchIndexBuilder"/> to configure this indexing.</returns>
    public StringSearchIndexBuilder IndexAsString<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsString(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for string values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for string values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="StringSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsString<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<StringSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsString(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for string values where each token represents a word or set of words.
    /// </summary>
    /// <remarks>
    /// For more information about indexing tokenized text, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/> and analyzers documentation.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="TokenSearchIndexBuilder"/> to configure this indexing.</returns>
    public TokenSearchIndexBuilder IndexAsToken<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsToken(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for string values where each token represents a word or set of words.
    /// </summary>
    /// <remarks>
    /// For more information about indexing tokenized text, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/> and analyzers documentation.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="TokenSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsToken<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<TokenSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsToken(member));
        return this;
    }

    /// <summary>
    /// Indexes the given member for UUID values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for UUID values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/uuid-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="UuidSearchIndexBuilder"/> to configure this indexing.</returns>
    public UuidSearchIndexBuilder IndexAsUuid<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsUuid(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member for UUID values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for UUID values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/uuid-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="UuidSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsUuid<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<UuidSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsUuid(member));
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
    public new SearchIndexBuilder<TEntity> ExcludeFromIndex(string memberName)
    {
        base.ExcludeFromIndex(memberName);
        return this;
    }

    /// <summary>
    /// Excludes the given member from being indexed.
    /// </summary>
    /// <remarks>
    /// For more information about excluding members, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#exclude-fields-example"/>.
    /// </remarks>
    /// <typeparam name="TMember">The type of the selected member.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to be excluded from the index.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> ExcludeFromIndex<TMember>(Expression<Func<TEntity, TMember>> member)
        => ExcludeFromIndex(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member as an embedded (nested) document. Further indexing can then be configured on members of this
    /// embedded document.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/document-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The CLR type of the embedded document.</typeparam>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="NestedSearchIndexBuilder{TMember}"/> to configure members of the embedded document.</returns>
    public NestedSearchIndexBuilder<TMember> IndexAsEmbedded<TMember>(string memberName)
        => CreateNestedBuilder<TMember, EmbeddedSearchIndexDefinition>(memberName, IndexDefinition, collection: false);

    /// <summary>
    /// Indexes the given member as an embedded (nested) array of documents. Further indexing can then be configured on members of
    /// this documents in the embedded array.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) arrays of documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The CLR type of the embedded documents in the array.</typeparam>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <returns>A <see cref="NestedSearchIndexBuilder{TMember}"/> to configure members of the embedded documents.</returns>
    public NestedSearchIndexBuilder<TMember> IndexAsEmbeddedArray<TMember>(string memberName)
        => CreateNestedBuilder<TMember, EmbeddedArraySearchIndexDefinition>(memberName, IndexDefinition, collection: true);

    /// <summary>
    /// Indexes the given member as an embedded (nested) document. Further indexing can then be configured on members of this
    /// embedded document.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/document-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The CLR type of the embedded document.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="NestedSearchIndexBuilder{TMember}"/> to configure members of the embedded document.</returns>
    public NestedSearchIndexBuilder<TMember> IndexAsEmbedded<TMember>(Expression<Func<TEntity, TMember>> member)
        => IndexAsEmbedded<TMember>(member.GetMemberAccess().Name);

    /// <summary>
    /// Indexes the given member as an embedded (nested) array of documents. Further indexing can then be configured on members of
    /// this documents in the embedded array.
    /// </summary>
    /// <remarks>
    /// For more information about indexing embedded (nested) arrays of documents, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/embedded-documents-type/"/>.
    /// </remarks>
    /// <typeparam name="TMember">The CLR type of the embedded documents in the array.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <returns>A <see cref="NestedSearchIndexBuilder{TMember}"/> to configure members of the embedded documents.</returns>
    public NestedSearchIndexBuilder<TMember> IndexAsEmbeddedArray<TMember>(Expression<Func<TEntity, IEnumerable<TMember>>> member)
        => IndexAsEmbeddedArray<TMember>(member.GetMemberAccess().Name);

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
    public new SearchIndexBuilder<TEntity> IndexAsEmbedded(string memberName, Action<NestedSearchIndexBuilder> nestedBuilder)
    {
        base.IndexAsEmbedded(memberName, nestedBuilder);
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
    /// <param name="nestedBuilder">
    /// A <see cref="NestedSearchIndexBuilder"/> delegate to configure members of the embedded documents.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> IndexAsEmbeddedArray(string memberName, Action<NestedSearchIndexBuilder> nestedBuilder)
    {
        base.IndexAsEmbeddedArray(memberName, nestedBuilder);
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
    /// <typeparam name="TMember">The CLR type of the embedded document.</typeparam>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="NestedSearchIndexBuilder{TMember}"/> delegate to configure the embedded document.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsEmbedded<TMember>(string memberName,
        Action<NestedSearchIndexBuilder<TMember>> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbedded<TMember>(memberName));
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
    /// <typeparam name="TMember">The CLR type of the embedded documents in the array.</typeparam>
    /// <param name="memberName">The name of the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="NestedSearchIndexBuilder{TMember}"/> delegate to configure the embedded documents.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsEmbeddedArray<TMember>(string memberName,
        Action<NestedSearchIndexBuilder<TMember>> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbeddedArray<TMember>(memberName));
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
    /// <typeparam name="TMember">The CLR type of the embedded document.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="NestedSearchIndexBuilder{TMember}"/> delegate to configure the embedded document.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsEmbedded<TMember>(
        Expression<Func<TEntity, TMember>> member, Action<NestedSearchIndexBuilder<TMember>> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbedded(member));
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
    /// <typeparam name="TMember">The CLR type of the embedded documents in the array.</typeparam>
    /// <param name="member">A lambda expression selecting the field or property to index.</param>
    /// <param name="nestedBuilder">A <see cref="NestedSearchIndexBuilder{TMember}"/> delegate to configure the embedded documents.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public SearchIndexBuilder<TEntity> IndexAsEmbeddedArray<TMember>(
        Expression<Func<TEntity, IEnumerable<TMember>>> member, Action<NestedSearchIndexBuilder<TMember>> nestedBuilder)
    {
        nestedBuilder(IndexAsEmbeddedArray(member));
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
    /// <param name="typeSetName">The name of the type set to define.</param>
    /// <param name="nestedBuilder">A <see cref="TypeSetSearchIndexBuilder"/> delegate to configure the type set.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> AddTypeSet(string typeSetName, Action<TypeSetSearchIndexBuilder> nestedBuilder)
    {
        base.AddTypeSet(typeSetName, nestedBuilder);
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
    /// <param name="synonymsCollectionName">The name of the collection that contains the synonym definitions.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> AddSynonyms(
        string name, string analyzerName, string synonymsCollectionName)
    {
        base.AddSynonyms(name, analyzerName, synonymsCollectionName);
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
    /// <param name="synonymsCollectionName">The name of the collection that contains the synonym definitions.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> AddSynonyms(string name, BuiltInSearchAnalyzer analyzer, string synonymsCollectionName)
        => AddSynonyms(name, ToAnalyzerName(analyzer), synonymsCollectionName);

    /// <summary>
    /// Adds a custom analyzer definition to this index. The analyzer name can then be used when configuring fields for string or
    /// token indexing.
    /// </summary>
    /// <remarks>
    /// For more information about search index analyzers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/analyzers/"/>.
    /// </remarks>
    /// <param name="analyzerName">The name of the analyzer to define.</param>
    /// <param name="nestedBuilder">An <see cref="AnalyzerSearchIndexBuilder"/> delegate to configure the analyzer.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public new SearchIndexBuilder<TEntity> AddCustomAnalyzer(string analyzerName, Action<AnalyzerSearchIndexBuilder> nestedBuilder)
    {
        base.AddCustomAnalyzer(analyzerName, nestedBuilder);
        return this;
    }
}
