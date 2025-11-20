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

using MongoDB.EntityFrameworkCore.Metadata.Search.Definitions;

namespace MongoDB.EntityFrameworkCore.Metadata.Search.Builders;

/// <summary>
/// Model building fluent API that builds an "date" search index type. Called from
/// <see cref="SearchIndexBuilder"/> or <see cref="NestedSearchIndexBuilder"/>, or their generic
/// counterparts, <see cref="SearchIndexBuilder{TEntity}"/> or <see cref="NestedSearchIndexBuilder{TEntity}"/>
/// </summary>
/// <remarks>
/// There are currently no configuration options for this index type. This API is a compatability placeholder for
/// any future options.
/// For more information about the date index type, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/date-type/"/>
/// </remarks>
/// <param name="definition">The <see cref="SearchIndexDateDefinition"/> defining the index type being built.</param>
#pragma warning disable CS9113 // Parameter is unread.
public class DateSearchIndexBuilder(SearchIndexDateDefinition definition)
#pragma warning restore CS9113 // Parameter is unread.
{
    // There are currently no configuration options for this index type. This API is a compatability placeholder for
    // any future options.
}
