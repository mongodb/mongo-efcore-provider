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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Placeholder for a MongoDB <see cref="IDatabaseCreator"/> that is a required
/// dependency of the <see cref="MongoTransactionManager"/> placeholder.
/// </summary>
public class MongoDatabaseCreator : IDatabaseCreator
{
    /// <inheritdoc/>
    public bool EnsureDeleted()
        => throw CreateNotSupportedException();

    /// <inheritdoc/>
    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = new())
        => throw CreateNotSupportedException();

    /// <inheritdoc/>
    public bool EnsureCreated()
        => throw CreateNotSupportedException();

    /// <inheritdoc/>
    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = new())
        => throw CreateNotSupportedException();

    /// <inheritdoc/>
    public bool CanConnect()
        => throw CreateNotSupportedException();

    /// <inheritdoc/>
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = new())
        => throw CreateNotSupportedException();

    private static NotSupportedException CreateNotSupportedException([CallerMemberName] string? method = null)
        => new($"The MongoDB EF Core Provider does not support '{method}' of '{nameof(MongoDatabaseCreator)}'.");
}
