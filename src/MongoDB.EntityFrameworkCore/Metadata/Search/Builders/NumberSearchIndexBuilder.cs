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
/// Model building fluent API that builds an "number" search index type. Called from
/// <see cref="SearchIndexBuilder"/> or <see cref="SearchIndexBuilder{TEntity}"/>, or their generic
/// counterparts, <see cref="SearchIndexBuilder{TEntity}"/> or <see cref="NestedSearchIndexBuilder{TEntity}"/>
/// </summary>
/// <remarks>
/// For more information about the number index type, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/number-type/"/>
/// </remarks>
/// <param name="definition">The <see cref="NestedSearchIndexBuilder"/> defining the index type being built.</param>
public class NumberSearchIndexBuilder(SearchIndexNumberDefinition definition)
{
    /// <summary>
    /// Configures the data type of the field to index.
    /// </summary>
    /// <remarks>
    /// Use <see cref="SearchNumberRepresentation.Int64"/> for indexing large integers without loss of precision and
    /// for rounding double values to integers. You can't use this type to index large double values. Use
    /// <see cref="SearchNumberRepresentation.Double"/> for indexing large double values without rounding.
    /// </remarks>
    /// <param name="representation">The representation used, from <see cref="SearchNumberRepresentation"/>.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NumberSearchIndexBuilder WithRepresentation(SearchNumberRepresentation representation)
    {
        definition.Representation = representation;
        return this;
    }

    /// <summary>
    /// Configured a flag that indicates whether to index or omit indexing 32 and 64-bit integer values.
    /// Defaults to <see langword="true"/> if not set.
    /// </summary>
    /// <remarks>
    /// Only one of this or <see cref="IndexDoubles"/> can be set to <see langword="false"/>.
    /// </remarks>
    /// <param name="indexIntegers">If <see langword="false"/>, then integers are not indexed, otherwise they are.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NumberSearchIndexBuilder IndexIntegers(bool indexIntegers = true)
    {
        definition.IndexIntegers = indexIntegers;
        return this;
    }

    /// <summary>
    /// Configured a flag that indicates whether to index or omit indexing floating-point values.
    /// Defaults to <see langword="true"/> if not set.
    /// </summary>
    /// <remarks>
    /// Only one of this or <see cref="IndexIntegers"/> can be set to <see langword="false"/>.
    /// </remarks>
    /// <param name="indexDoubles">
    /// If <see langword="false"/>, then floating-point values are not indexed, otherwise they are.
    /// </param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public NumberSearchIndexBuilder IndexDoubles(bool indexDoubles = true)
    {
        definition.IndexDoubles = indexDoubles;
        return this;
    }
}
