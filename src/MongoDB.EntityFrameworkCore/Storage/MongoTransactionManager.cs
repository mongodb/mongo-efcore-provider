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
using System.Transactions;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Placeholder for a MongoDB transaction manager that for now just flags that transactions
/// are not supported in case somebody attempts to use them.
/// </summary>
public class MongoTransactionManager : IDbContextTransactionManager, ITransactionEnlistmentManager
{
    /// <inheritdoc />
    public void ResetState()
    {
    }

    /// <inheritdoc />
    public Task ResetStateAsync(CancellationToken cancellationToken = new())
        => Task.CompletedTask;

    /// <inheritdoc />
    public IDbContextTransaction BeginTransaction()
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = new())
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public void CommitTransaction()
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public Task CommitTransactionAsync(CancellationToken cancellationToken = new())
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public void RollbackTransaction()
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public Task RollbackTransactionAsync(CancellationToken cancellationToken = new())
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public void EnlistTransaction(Transaction? transaction)
        => throw CreateNotSupportedException();

    /// <inheritdoc />
    public Transaction? EnlistedTransaction
        => null;

    /// <inheritdoc />
    public IDbContextTransaction? CurrentTransaction
        => null;

    private static NotSupportedException CreateNotSupportedException()
        => new("The MongoDB EF Provider does not support transactions.");
}
