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
using MongoDB.EntityFrameworkCore.Metadata;
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
    /// Get an <see cref="IMongoCollection{T}"/> for the given <paramref name="collectionName"/>.
    /// </summary>
    /// <param name="collectionName">The name of the collection to get a collection for.</param>
    /// <typeparam name="T">The type of data that will be returned by the collection.</typeparam>
    /// <returns>The <see cref="IMongoCollection{T}"/>.</returns>
    IMongoCollection<T> GetCollection<T>(string collectionName);

    /// <summary>
    /// Execute a <see cref="MongoExecutableQuery"/> and return  a <see cref="Action"/>
    /// that should be executed once the first item has been enumerated.
    /// </summary>
    /// <param name="executableQuery">The <see cref="MongoExecutableQuery"/> containing everything needed to run the query.</param>
    /// <param name="log">The <see cref="Action"/> returned that will perform the MQL log once evaluation has happened.</param>
    /// <typeparam name="T">The type of items being returned by the query.</typeparam>
    /// <returns>An <see cref="IEnumerable{T}"/> containing the items returned by the query.</returns>
    IEnumerable<T> Execute<T>(MongoExecutableQuery executableQuery, out Action log);

    /// <summary>
    /// Create a new database with the name specified in the connection options.
    /// </summary>
    /// <remarks>If the database already exists only new collections will be created.</remarks>
    /// <param name="model">The <see cref="IDesignTimeModel"/> that informs how the database should be created.</param>
    /// <returns><see langword="true" /> if the database was created from scratch, <see langword="false" /> if it already existed.</returns>
    bool CreateDatabase(IDesignTimeModel model);

    /// <summary>
    /// Create a new database with the name specified in the connection options.
    /// </summary>
    /// <remarks>If the database already exists only new collections will be created.</remarks>
    /// <param name="model">The <see cref="IDesignTimeModel"/> that informs how the database should be created.</param>
    /// <param name="options">An <see cref="MongoDatabaseCreationOptions"/> object specifying additional actions to be taken.</param>
    /// <param name="seed">A delegate called to seed the database before any Atlas indexes are created.</param>
    /// <returns><see langword="true" /> if the database was created from scratch, <see langword="false" /> if it already existed.</returns>
    bool CreateDatabase(IDesignTimeModel model, MongoDatabaseCreationOptions options, Action? seed);

    /// <summary>
    /// Create a new database with the name specified in the connection options asynchronously.
    /// </summary>
    /// <remarks>If the database already exists only new collections will be created.</remarks>
    /// <param name="model">The <see cref="IDesignTimeModel"/> that informs how the database should be created.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database was created from scratch, <see langword="false" /> if it already existed.
    /// </returns>
    Task<bool> CreateDatabaseAsync(IDesignTimeModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Create a new database with the name specified in the connection options asynchronously.
    /// </summary>
    /// <remarks>If the database already exists only new collections will be created.</remarks>
    /// <param name="model">The <see cref="IDesignTimeModel"/> that informs how the database should be created.</param>
    /// <param name="options">An <see cref="MongoDatabaseCreationOptions"/> object specifying additional actions to be taken.</param>
    /// <param name="seedAsync">A delegate called to seed the database before any Atlas indexes are created.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database was created from scratch, <see langword="false" /> if it already existed.
    /// </returns>
    Task<bool> CreateDatabaseAsync(IDesignTimeModel model, MongoDatabaseCreationOptions options, Func<CancellationToken, Task>? seedAsync, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the database specified in the connection options.
    /// </summary>
    /// <returns><see langword="true" /> if the database was deleted, <see langword="false" /> if it did not exist.</returns>
    bool DeleteDatabase();

    /// <summary>
    /// Delete the database specified in the connection options asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database was deleted, <see langword="false" /> if it already existed.
    /// </returns>
    Task<bool> DeleteDatabaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Determine if the database already exists or not.
    /// </summary>
    /// <returns><see langword="true" /> if the database exists, <see langword="false" /> if it does not.</returns>
    bool DatabaseExists();

    /// <summary>
    /// Determine if the database already exists or not asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will be
    /// <see langword="true" /> if the database exists, <see langword="false" /> if it does not.
    /// </returns>
    Task<bool> DatabaseExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an index in MongoDB based on the EF <see cref="IIndex"/> definition. No attempt is made to check that the index
    /// does not already exist and can therefore be created. The index may be an Atlas index or a normal MongoDB index.
    /// </summary>
    /// <param name="index">The <see cref="IIndex"/> definition.</param>
    void CreateIndex(IIndex index);

    /// <summary>
    /// Creates an index in MongoDB based on the EF <see cref="IIndex"/> definition. No attempt is made to check that the index
    /// does not already exist and can therefore be created. The index may be an Atlas index or a normal MongoDB index.
    /// </summary>
    /// <param name="index">The <see cref="IIndex"/> definition.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    Task CreateIndexAsync(IIndex index, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates any non-Atlas MongoDB indexes defined in the EF model that do not already exist.
    /// </summary>
    /// <param name="model">The EF <see cref="IModel"/>.</param>
    void CreateMissingIndexes(IModel model);

    /// <summary>
    /// Creates any non-Atlas MongoDB indexes defined in the EF model that do not already exist.
    /// </summary>
    /// <param name="model">The EF <see cref="IModel"/>.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    Task CreateMissingIndexesAsync(IModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates any MongoDB Atlas vector indexes defined in the EF model that do not already exist.
    /// </summary>
    /// <param name="model">The EF <see cref="IModel"/>.</param>
    void CreateMissingVectorIndexes(IModel model);

    /// <summary>
    /// Creates any MongoDB Atlas vector indexes defined in the EF model that do not already exist.
    /// </summary>
    /// <param name="model">The EF <see cref="IModel"/>.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    Task CreateMissingVectorIndexesAsync(IModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks until all vector indexes in the mapped collections are reporting the 'READY' state.
    /// </summary>
    /// <param name="model">The EF <see cref="IModel"/></param>
    /// <param name="timeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
    /// The default is 15 seconds. Zero seconds means no timeout.</param>
    /// <exception cref="InvalidOperationException">if the timeout expires before all indexes are 'READY'.</exception>
    void WaitForVectorIndexes(IModel model, TimeSpan? timeout = null);

    /// <summary>
    /// Blocks until all vector indexes in the mapped collections are reporting the 'READY' state.
    /// </summary>
    /// <param name="model">The EF <see cref="IModel"/></param>
    /// <param name="timeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
    /// The default is 15 seconds. Zero seconds means no timeout.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    /// <exception cref="InvalidOperationException">if the timeout expires before all indexes are 'READY'.</exception>
    Task WaitForVectorIndexesAsync(IModel model, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Start a new client session.
    /// </summary>
    /// <returns>The new <see cref="IClientSessionHandle"/>.</returns>
    IClientSessionHandle StartSession();

    /// <summary>
    /// Start a new client session asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>
    /// A <see cref="Task"/> that, when resolved, will contain the new <see cref="IClientSessionHandle"/>.
    /// </returns>
    Task<IClientSessionHandle> StartSessionAsync(CancellationToken cancellationToken = default);
}
