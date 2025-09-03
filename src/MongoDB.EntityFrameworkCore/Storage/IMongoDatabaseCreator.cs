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

using System.Threading;
using System.Threading.Tasks;
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
}
