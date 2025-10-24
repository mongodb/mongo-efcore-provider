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

namespace MongoDB.EntityFrameworkCore.Metadata;

/// <summary>
/// Creates a <see cref="MongoDatabaseCreationOptions"/> to determine which additional actions are taken when
/// <see cref="MongoDB.EntityFrameworkCore.MongoDatabaseFacadeExtensions.EnsureCreated"/> or
/// <see cref="MongoDB.EntityFrameworkCore.MongoDatabaseFacadeExtensions.EnsureCreatedAsync"/>
/// </summary>
/// <param name="CreateMissingCollections">Creates any MongoDB database collections that do not already exist. The default is true.</param>
/// <param name="CreateMissingIndexes">Creates any non-Atlas MongoDB indexes that do not already exist. The default is true.</param>
/// <param name="CreateMissingVectorIndexes">Creates any MongoDB Atlas vector indexes that do not already exist. The default is true.</param>
/// <param name="WaitForVectorIndexes">Waits all MongoDB Atlas vector indexes to be 'READY' before continuing. The default is true.</param>
/// <param name="IndexCreationTimeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
/// The default is 15 seconds. Zero seconds means no timeout.</param>
public readonly record struct MongoDatabaseCreationOptions(
    bool CreateMissingCollections = true,
    bool CreateMissingIndexes = true,
    bool CreateMissingVectorIndexes = true,
    bool WaitForVectorIndexes = true,
    TimeSpan? IndexCreationTimeout = null)
{
    /// <summary>
    /// Creates a <see cref="MongoDatabaseCreationOptions"/> with default values for all options.
    /// </summary>
    public MongoDatabaseCreationOptions()
        // This ensures that the default parameterless constructor for structs is not used, which would result in CLR defaults for all members.
        : this(CreateMissingCollections: true)
    {
    }
}
