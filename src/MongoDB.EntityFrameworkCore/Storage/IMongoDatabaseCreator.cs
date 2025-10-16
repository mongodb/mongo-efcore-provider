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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Creates and deletes MongoDB databases.
/// </summary>
public interface IMongoDatabaseCreator : IDatabaseCreator
{
    /// <summary>
    /// Ensures that the database for the context exists. If it exists, no action is taken. If it does not
    /// exist then the MongoDB database is created using the <see cref="MongoDatabaseCreationOptions"/> to determine what
    /// additional actions to take.
    /// </summary>
    /// <param name="options">An <see cref="MongoDatabaseCreationOptions"/> object specifying additional actions to be taken.</param>
    /// <returns><see langword="true" /> if the database is created, <see langword="false" /> if it already existed.</returns>
    bool EnsureCreated(MongoDatabaseCreationOptions options);

    /// <summary>
    /// Asynchronously ensures that the database for the context exists. If it exists, no action is taken. If it does not
    /// exist then the MongoDB database is created using the <see cref="MongoDatabaseCreationOptions"/> to determine what
    /// additional actions to take.
    /// </summary>
    /// <param name="options">An <see cref="MongoDatabaseCreationOptions"/> object specifying additional actions to be taken.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken" /> to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains <see langword="true" /> if the database is created, <see langword="false" /> if it already existed.
    /// </returns>
    Task<bool> EnsureCreatedAsync(MongoDatabaseCreationOptions options, CancellationToken cancellationToken = default);

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
    /// Creates an index in MongoDB based on the EF Core <see cref="IIndex"/> definition. No attempt is made to check that the index
    /// does not already exist and can therefore be created. The index may be an Atlas index or a normal MongoDB index.
    /// </summary>
    /// <param name="index">The <see cref="IIndex"/> definition.</param>
    void CreateIndex(IIndex index);

    /// <summary>
    /// Creates an index in MongoDB based on the EF Core <see cref="IIndex"/> definition. No attempt is made to check that the index
    /// does not already exist and can therefore be created. The index may be an Atlas index or a normal MongoDB index.
    /// </summary>
    /// <param name="index">The <see cref="IIndex"/> definition.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    Task CreateIndexAsync(IIndex index, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates any non-Atlas MongoDB indexes defined in the EF Core model that do not already exist.
    /// </summary>
    void CreateMissingIndexes();

    /// <summary>
    /// Creates any non-Atlas MongoDB indexes defined in the EF Core model that do not already exist.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    Task CreateMissingIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates any MongoDB Atlas vector indexes defined in the EF Core model that do not already exist.
    /// </summary>
    void CreateMissingVectorIndexes();

    /// <summary>
    /// Creates any MongoDB Atlas vector indexes defined in the EF Core model that do not already exist.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    Task CreateMissingVectorIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks until all vector indexes in the mapped collections are reporting the 'READY' state.
    /// </summary>
    /// <param name="timeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
    /// The default is 15 seconds. Zero seconds means no timeout.</param>
    /// <exception cref="InvalidOperationException">if the timeout expires before all indexes are 'READY'.</exception>
    void WaitForVectorIndexes(TimeSpan? timeout = null);

    /// <summary>
    /// Blocks until all vector indexes in the mapped collections are reporting the 'READY' state.
    /// </summary>
    /// <param name="timeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
    /// The default is 15 seconds. Zero seconds means no timeout.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    /// <exception cref="InvalidOperationException">if the timeout expires before all indexes are 'READY'.</exception>
    Task WaitForVectorIndexesAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
