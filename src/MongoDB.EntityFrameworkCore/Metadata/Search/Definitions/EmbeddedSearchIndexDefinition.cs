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
/// Defines a MongoDB search index part that contains field mappings for an embedded (nested) document.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class EmbeddedSearchIndexDefinition : EmbeddedSearchIndexDefinitionBase
{
    /// <summary>
    /// Returns "document" for a single embedded (nested) document.
    /// </summary>
    protected override string EmbeddingType
        => "document";

    /// <summary>
    /// Returns <see langword="null"/>, because embedded document stored source is defined at the top level.
    /// </summary>
    /// <returns><see langword="null"/> indicating stored-source processing should stop.</returns>
    protected override BsonValue? ToStoredSource()
        => null;
}
