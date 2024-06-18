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
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// Configuration of a BSON representation defining how the value from property
/// should be stored in the MongoDB database.
/// </summary>
public record BsonRepresentationConfiguration
{
    /// <summary>
    /// Create a new instance of <see cref="BsonRepresentationConfiguration"/>.
    /// </summary>
    /// <param name="bsonType">The <see cref="BsonType"/> for this configuration.</param>
    /// <param name="allowOverflow"><langref>true</langref> to allow overflow, <langref>false</langref> to throw when overflow would occur.</param>
    /// <param name="allowTruncation"><langref>true</langref> to allow truncation, <langref>false</langref> to throw when truncation would occur.</param>
    public BsonRepresentationConfiguration(BsonType bsonType, bool? allowOverflow = null, bool? allowTruncation = null)
    {
        BsonType = bsonType;
        AllowOverflow = allowOverflow;
        AllowTruncation = allowTruncation;
    }

    /// <summary>The <see cref="BsonType"/> the value from the property will be stored as.</summary>
    public BsonType BsonType { get; }

    /// <summary>
    /// <langref>true</langref> to allow overflow,
    /// <langref>false</langref> to throw when overflow would occur,
    /// <langref>null</langref> to default to provider semantics.
    /// </summary>
    public bool? AllowOverflow { get; }

    /// <summary>
    /// <langref>true</langref> to allow truncation,
    /// <langref>false</langref> to throw when truncation would occur,
    /// <langref>null</langref> to default to provider semantics.
    /// </summary>
    public bool? AllowTruncation { get; }

    /// <summary>
    /// Convert this configuration to an IDictionary representation so it may be
    /// used within an EF Core static model.
    /// </summary>
    /// <returns>An IDictionary containing the named properties of this configuration.</returns>
    /// <remarks>Is recreated from this dictionary via <see cref="CreateFrom"/>.</remarks>
    public IDictionary<string, object> ToDictionary()
        => new Dictionary<string, object>
        {
            { "BsonType", BsonType },
            { "AllowOverflow", AllowOverflow },
            { "AllowTruncation", AllowTruncation }
        };

    /// <summary>
    /// Create a <see cref="BsonRepresentationConfiguration"/> from an IDictionary representation
    /// so it may be recreated from an EF Core static model.
    /// </summary>
    /// <returns>A <see cref="BsonRepresentationConfiguration"/> with the properties set from the dictionary.</returns>
    /// <remarks>The dictionary passes is typically created by <see cref="ToDictionary"/>.</remarks>
    public static BsonRepresentationConfiguration CreateFrom(IDictionary<string, object> values)
    {
        var bsonType = (BsonType?)values["BsonType"] ?? throw new InvalidOperationException("BsonType is required.");
        var allowOverflow = (bool?)values["AllowOverflow"];
        var allowTruncation = (bool?)values["AllowTruncation"];
        return new BsonRepresentationConfiguration(bsonType, allowOverflow, allowTruncation);
    }
}
