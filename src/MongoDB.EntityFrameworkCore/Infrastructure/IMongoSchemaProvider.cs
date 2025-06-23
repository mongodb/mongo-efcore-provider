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

using System.Collections.Generic;
using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Provides any necessary schemas for a MongoClient based on the current EF Core model.
/// </summary>
/// <remarks>
/// This interface is intended for internal use by the MongoDB EF Core Provider
/// and may change in any release without prior notice.
/// </remarks>
public interface IMongoSchemaProvider
{
    /// <summary>
    /// Creates a Queryable Encryption schema for the current model based on the
    /// configured EF Core annotations.
    /// </summary>
    /// <returns>A Dictionary of BsonDocuments keyed by collection name.</returns>
    /// <remarks>
    /// The collection names must be transformed to include the database name
    /// before being used with the MongoDB client.
    /// </remarks>
    Dictionary<string, BsonDocument> GetQueryableEncryptionSchema();
}
