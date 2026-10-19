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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API called from <see cref="SearchIndexBuilder"/> that configures indexing of types in the custom
/// type set.
/// </summary>
/// <remarks>
/// For more information about dynamic and static mappings, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/define-field-mappings/#dynamic-and-static-mappings"/>
/// </remarks>
/// <param name="indexDefinition">The <see cref="SearchIndexDefinition"/> being defined.</param>
/// <param name="typeSetName">The name of the type set being configured.</param>
public class TypeSetSearchIndexBuilder(SearchIndexDefinition indexDefinition, string typeSetName)
{
    private readonly SearchIndexTypeSetDefinition _definition
        = indexDefinition
            .GetOrAddTopLevelDefinition<SearchIndexTypeSetsDefinition>("typeSets")
            .GetOrAddTypeSetDefinition(typeSetName);

    /// <summary>
    /// Indexes the type set for auto-completion. That is, tokens are generated for prefix substrings such that the results
    /// can be used to show all possible auto-completions for that substring.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for auto-completion, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="AutoCompleteSearchIndexBuilder"/> to configure this indexing.</returns>
    public AutoCompleteSearchIndexBuilder IndexAsAutoComplete()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexAutoCompleteDefinition>("autocomplete"));

    /// <summary>
    /// Indexes the type set for auto-completion. That is, tokens are generated for prefix substrings such that the results
    /// can be used to show all possible auto-completions for that substring.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for auto-completion, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/autocomplete-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="AutoCompleteSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsAutoComplete(Action<AutoCompleteSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsAutoComplete());
        return this;
    }

    /// <summary>
    /// Indexes the type set for boolean values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for boolean values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="BooleanSearchIndexBuilder"/> to configure this indexing.</returns>
    public BooleanSearchIndexBuilder IndexAsBoolean()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexBooleanDefinition>("boolean"));

    /// <summary>
    /// Indexes the type set for boolean values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for boolean values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/boolean-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="BooleanSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsBoolean(Action<BooleanSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsBoolean());
        return this;
    }

    /// <summary>
    /// Indexes the type set for date values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for date values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="DateSearchIndexBuilder"/> to configure this indexing.</returns>
    public DateSearchIndexBuilder IndexAsDate()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexDateDefinition>("date"));

    /// <summary>
    /// Indexes the type set for date values.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for date values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="DateSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsDate(Action<DateSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsDate());
        return this;
    }

    /// <summary>
    /// Indexes the type set for geographic points and shape coordinates.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for geographic points and shape coordinates, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="GeoSearchIndexBuilder"/> to configure this indexing.</returns>
    public GeoSearchIndexBuilder IndexAsGeo()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexGeoDefinition>("geo"));

    /// <summary>
    /// Indexes the type set for geographic points and shape coordinates.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for geographic points and shape coordinates, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="GeoSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsGeo(Action<GeoSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsGeo());
        return this;
    }

    /// <summary>
    /// Indexes the type set for numbers.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for numbers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="NumberSearchIndexBuilder"/> to configure this indexing.</returns>
    public NumberSearchIndexBuilder IndexAsNumber()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexNumberDefinition>("number"));

    /// <summary>
    /// Indexes the type set for numbers.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for numbers, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="NumberSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsNumber(Action<NumberSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsNumber());
        return this;
    }

    /// <summary>
    /// Indexes the type set for <see cref="ObjectId"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for ObjectIDs, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/object-id-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="ObjectIdSearchIndexBuilder"/> to configure this indexing.</returns>
    public ObjectIdSearchIndexBuilder IndexAsObjectId()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexObjectIdDefinition>("objectId"));

    /// <summary>
    /// Indexes the type set for <see cref="ObjectId"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for ObjectIds, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/object-id-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="ObjectIdSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsObjectId(Action<ObjectIdSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsObjectId());
        return this;
    }

    /// <summary>
    /// Indexes the type set for strings.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for string values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="StringSearchIndexBuilder"/> to configure this indexing.</returns>
    public StringSearchIndexBuilder IndexAsString()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexStringDefinition>("string"));

    /// <summary>
    /// Indexes the type set for strings.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for string values, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/string-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="StringSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsString(Action<StringSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsString());
        return this;
    }

    /// <summary>
    /// Indexes the type set as tokens. That is, the entire value is included as a searchable token. This kind of index
    /// is required for sorting.
    /// </summary>
    /// <remarks>
    /// For more information about indexing as tokens, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/token-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="TokenSearchIndexBuilder"/> to configure this indexing.</returns>
    public TokenSearchIndexBuilder IndexAsToken()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexTokenDefinition>("token"));

    /// <summary>
    /// Indexes the type set as tokens. That is, the entire value is included as a searchable token. This kind of index
    /// is required for sorting.
    /// </summary>
    /// <remarks>
    /// For more information about indexing as tokens, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/token-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="TokenSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsToken(Action<TokenSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsToken());
        return this;
    }

    /// <summary>
    /// Indexes the type set for UUIDs, including <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for UUIDs, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/uuid-type/"/>.
    /// </remarks>
    /// <returns>A <see cref="UuidSearchIndexBuilder"/> to configure this indexing.</returns>
    public UuidSearchIndexBuilder IndexAsUuid()
        => new(_definition.GetOrAddTypeDefinition<SearchIndexUuidDefinition>("uuid"));

    /// <summary>
    /// Indexes the type set for UUIDs, including <see cref="Guid"/>.
    /// </summary>
    /// <remarks>
    /// For more information about indexing for UUIDs, see
    /// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/uuid-type/"/>.
    /// </remarks>
    /// <param name="nestedBuilder">A <see cref="UuidSearchIndexBuilder"/> delegate to configure this indexing.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public TypeSetSearchIndexBuilder IndexAsUuid(Action<UuidSearchIndexBuilder> nestedBuilder)
    {
        nestedBuilder(IndexAsUuid());
        return this;
    }
}
