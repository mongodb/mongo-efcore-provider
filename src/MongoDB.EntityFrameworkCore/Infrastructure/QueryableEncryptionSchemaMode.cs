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

using Microsoft.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace (extensions should be in the EF namespace for discovery)
namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// How Queryable Encryption schemas are managed by the MongoDB EF Core Provider.
/// </summary>
public enum QueryableEncryptionSchemaMode
{
    /// <summary>
    /// The schema only exists on the client. The server will not enforce any Queryable Encryption for other clients.
    /// </summary>
    /// <remarks>
    /// This is intended for local or pre-production environments where the schema might change before initial  production release.
    /// </remarks>
    ClientOnly,

    /// <summary>
    /// The schema from the client is also required server-side. Other clients must use a compatible Queryable Encryption schema.
    /// </summary>
    /// <remarks>
    /// This mode is intended for use in production environments and can not be automatically adjusted after initial creation.
    /// When <see cref="DatabaseFacade.EnsureCreated"/> or <see cref="DatabaseFacade.EnsureCreatedAsync"/>
    /// is called it will attempt to create the collection using the clients queryable encryption schema.
    /// If the collection already exists then the server will be checked to ensure the collection schema
    /// is compatible with the one in the client.
    /// </remarks>
    ClientAndServer,

    /// <summary>
    /// The schema from the client is ignored and only the server-side is respected.
    /// </summary>
    /// <remarks>
    /// This mode is intended for existing applications where the Queryable Encryption schema already exists on the server
    /// and any annotations in the client should be ignore.
    /// </remarks>
    ServerOnly
}
