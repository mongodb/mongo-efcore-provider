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
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// For internal use only. Interface may change between minor versions.
/// Provides the interface between the MongoDB Entity Framework provider
/// and the underlying <see cref="IMongoClient"/>.
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
    /// A query and the associated metadata and provider needed to execute that query.
    /// </summary>
    /// <param name="executableQuery">The <see cref="MongoExecutableQuery"/> that will be executed.</param>
    /// <param name="log">The <see cref="Action"/> that should be called upon evaluation of the query to log it.</param>
    /// <typeparam name="T">The type of results being returned.</typeparam>
    /// <returns>An <see cref="IEnumerable{T}"/> containing the results of the query.</returns>
    public IEnumerable<T> Execute<T>(MongoExecutableQuery executableQuery, out Action log);

    /// <summary>
    /// Save updates to a MongoDB database.
    /// </summary>
    /// <param name="updates">The updates to save to the database.</param>
    /// <returns>The number of affected documents.</returns>
    public long SaveUpdates(IEnumerable<MongoUpdate> updates);

    /// <summary>
    /// Save updates to a MongoDB database asynchronously.
    /// </summary>
    /// <param name="updates">The updates to save to the database.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>A task that when completed gives the number of affected documents.</returns>
    public Task<long> SaveUpdatesAsync(IEnumerable<MongoUpdate> updates, CancellationToken cancellationToken);
}
