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
/// Model building fluent API that builds an "geo" search index type. Called from
/// <see cref="SearchIndexBuilder"/> or <see cref="NestedSearchIndexBuilder"/>, or their generic
/// counterparts, <see cref="SearchIndexBuilder{TEntity}"/> or <see cref="NestedSearchIndexBuilder{TEntity}"/>
/// </summary>
/// <remarks>
/// For more information about the geo index type, see
/// <see href="https://www.mongodb.com/docs/atlas/atlas-search/field-types/geo-type/"/>
/// </remarks>
/// <param name="definition">The <see cref="SearchIndexGeoDefinition"/> defining the index type being built.</param>
public class GeoSearchIndexBuilder(SearchIndexGeoDefinition definition)
{
    /// <summary>
    /// Configures a flag that indicates whether to index shapes.  By default, MongoDB indexes points, even when nested,
    /// but does not index shape geometries such as lines and polygons.
    /// </summary>
    /// <param name="indexShapes">If <see langword="true"/>, then shapes are indexed, otherwise they are not.</param>
    /// <returns>This builder, so that method calls can be chained.</returns>
    public GeoSearchIndexBuilder IndexShapes(bool indexShapes = true)
    {
        definition.IndexShapes = indexShapes;
        return this;
    }
}
