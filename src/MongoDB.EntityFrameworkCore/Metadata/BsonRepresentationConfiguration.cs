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

using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// A BSON representation configuration.
/// </summary>
public record BsonRepresentationConfiguration
{
    /// <summary>
    /// Create a new instance of <see cref="BsonRepresentationConfiguration"/>.
    /// </summary>
    /// <param name="bsonType">The <see cref="BsonType"/> for this configuration.</param>
    /// <param name="allowOverflow">WWhether to allow overflow or not.</param>
    /// <param name="allowTruncation">Whether to allow truncation or not.</param>
    public BsonRepresentationConfiguration(BsonType bsonType, bool? allowOverflow = null, bool? allowTruncation = null)
    {
        BsonType = bsonType;
        AllowOverflow = allowOverflow ?? false;
        AllowTruncation = allowTruncation ?? bsonType == BsonType.Decimal128;
    }

    /// <summary>The <see cref="BsonType"/> the property will be stored as.</summary>
    public BsonType BsonType { get; }

    /// <summary>Whether to allow overflow or not.</summary>
    public bool AllowOverflow { get; }

    /// <summary>Whether to allow truncation or not.</summary>
    public bool AllowTruncation { get; }
}
