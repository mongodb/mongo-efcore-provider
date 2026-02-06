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
/// Definition for the "includeOriginalTokens" token filter.
/// </summary>
/// <remarks>
/// This type is typically used by the database provider. It is only needed by applications when reading metadata from EF Core.
/// Use <see cref="MongoEntityTypeBuilderExtensions.HasSearchIndex(EntityTypeBuilder,string?)"/> or one of its overloads to
/// configure a MongoDB search index.
/// </remarks>
public class IncludeOriginalTokensFilterDefinition : SearchIndexDefinitionBase
{
    /// <summary>
    /// If <see langword="false"/>, then original tokens are also included in the output, otherwise they are not.
    /// </summary>
    public bool? IncludeOriginalTokens { get; set; }

    /// <inheritdoc/>
    public override BsonValue ToBson()
    {
        var document = new BsonDocument { { "type", Name } };
        document.Add("originalTokens", IncludeOriginalTokens == true ? "include" : "omit", IncludeOriginalTokens != null);
        return document;
    }
}
