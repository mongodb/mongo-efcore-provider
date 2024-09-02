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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// For internal use only. Interface may change between minor versions.
/// Provides the interface between the MongoDB Entity Framework provider
/// and the underlying <see cref="IMongoClient"/> for a given database.
/// </summary>
public interface IMongoClientWrapper
{
    /// <summary>
    /// Get an <see cref="IMongoCollection{T}"/> for the given <paramref name="collectionName"/>;
    /// </summary>
    /// <param name="collectionName">The name of the collection to get a collection for.</param>
    /// <typeparam name="T">The type of data that will be returned by the collection.</typeparam>
    /// <returns>The <see cref="IMongoCollection{T}"/> </returns>
    public IMongoCollection<T> GetCollection<T>(string collectionName);

    /// <summary>
    /// Execute a <see cref="MongoExecutableQuery"/> and return  a <see cref="Action"/>
    /// that should be executed once the first item has been enumerated.
    /// </summary>
    /// <param name="executableQuery">The <see cref="MongoExecutableQuery"/> containing everything needed to run the query.</param>
    /// <param name="log">The <see cref="Action"/> returned that will perform the MQL log once evaluation has happened.</param>
    /// <typeparam name="T">The type of items being returned by the query.</typeparam>
    /// <returns>An <see cref="IEnumerable{T}"/> containing the items returned by the query.</returns>
    public IEnumerable<T> Execute<T>(MongoExecutableQuery executableQuery, out Action log);

    /// <summary>
    /// Create a new database with the name specified in the connection options.
    /// </summary>
    /// <remarks>If the database already exists only new collections will be created.</remarks>
    /// <param name="model">The <see cref="IModel"/> that informs how the database should be created.</param>
    /// <returns><see langword="true" /> if the database was created from scratch, <see langword="false" /> if it already existed.</returns>
    public bool CreateDatabase(IModel model);

    /// <summary>
    /// Create a new database with the name specified in the connection options asynchronously.
    /// </summary>
    /// <remarks>If the database already exists only new collections will be created.</remarks>
    /// <param name="model">The <see cref="IModel"/> that informs how the database should be created.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database was created from scratch, <see langword="false" /> if it already existed.
    /// </returns>
    public Task<bool> CreateDatabaseAsync(IModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the database specified in the connection options.
    /// </summary>
    /// <returns><see langword="true" /> if the database was deleted, <see langword="false" /> if it did not exist.</returns>
    public bool DeleteDatabase();

    /// <summary>
    /// Delete the database specified in the connection options asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database was deleted, <see langword="false" /> if it already existed.
    /// </returns>
    public Task<bool> DeleteDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determine if the database already exists or not.
    /// </summary>
    /// <returns><see langword="true" /> if the database exists, <see langword="false" /> if it does not.</returns>
    public bool DatabaseExists();

    /// <summary>
    /// Determine if the database already exists or not asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database exists, <see langword="false" /> if it does not.
    /// </returns>
    public Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Start a new client session.
    /// </summary>
    /// <returns>The new <see cref="IClientSessionHandle"/>.</returns>
    public IClientSessionHandle StartSession();

    /// <summary>
    /// Start a new client session asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will contain the new <see cref="IClientSessionHandle"/>.
    /// </returns>
    public Task<IClientSessionHandle> StartSessionAsync(CancellationToken cancellationToken = default);
}
