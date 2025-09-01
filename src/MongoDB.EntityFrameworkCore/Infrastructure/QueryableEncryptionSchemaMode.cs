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

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// How Queryable Encryption schemas are managed by the MongoDB EF Core Provider.
/// </summary>
public enum QueryableEncryptionSchemaMode
{
    /// <summary>
    /// The IsEncrypted field configuration is only applied to the client.
    /// </summary>
    /// <remarks>
    /// This is intended for local or pre-production environments where the schema may change during development.
    /// </remarks>
    ApplyToClient,

    /// <summary>
    /// The IsEncrypted field configuration is ignored so that server schema may be used exclusively.
    /// </summary>
    /// <remarks>
    /// This is intended for situations where the Queryable Encryption schema already exists on the server
    /// and any field-level configuration on the client should be ignored.
    /// </remarks>
    Ignore
}
