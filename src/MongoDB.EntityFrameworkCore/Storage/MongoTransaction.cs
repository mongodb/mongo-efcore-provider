﻿/* Copyright 2023-present MongoDB Inc.
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
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Diagnostics;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Provides an EF-friendly wrapper around a MongoDB transaction.
/// </summary>
/// <param name="transactionLogger">A <see cref="IDiagnosticsLogger"/> for logging the <see cref="DbLoggerCategory.Database.Transaction"/> messages.</param>
/// <param name="session">A <see cref="IClientSession"/> for handling the transaction in MongoDB.</param>
public sealed class MongoTransaction(
    IClientSession session,
    DbContext context,
    Guid transactionId,
    IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> transactionLogger)
    : IDbContextTransaction
{
    /// <inheritdoc />
    public Guid TransactionId { get; } = transactionId;

    /// <summary>
    /// The <see cref="DbContext"/> this transaction is being used with.
    /// </summary>
    public DbContext Context { get; } = context;

    /// <inheritdoc />
    public void Commit()
    {
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();

        transactionLogger.TransactionCommitting(this, false, startTime);
        try
        {
            session.CommitTransaction();
        }
        catch (Exception ex)
        {
            transactionLogger.TransactionError(this, "Commit", ex, false, startTime, stopwatch.Elapsed);
            throw;
        }

        transactionLogger.TransactionCommitted(this, false, startTime, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = new())
    {
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();

        transactionLogger.TransactionCommitting(this, true, startTime);
        try
        {
            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            transactionLogger.TransactionError(this, "Commit", ex, true, startTime, stopwatch.Elapsed);
            throw;
        }

        transactionLogger.TransactionCommitted(this, true, startTime, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public void Rollback()
    {
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();

        transactionLogger.TransactionRollingBack(this, false, startTime);
        try
        {
            session.AbortTransaction();
        }
        catch (Exception ex)
        {
            transactionLogger.TransactionError(this, "Rollback", ex, false, startTime, stopwatch.Elapsed);
            throw;
        }

        transactionLogger.TransactionRolledBack(this, false, startTime, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = new())
    {
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();

        transactionLogger.TransactionRollingBack(this, true, startTime);
        try
        {
            await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            transactionLogger.TransactionError(this, "Rollback", ex, true, startTime, stopwatch.Elapsed);
            throw;
        }

        transactionLogger.TransactionRolledBack(this, true, startTime, stopwatch.Elapsed);
    }

    /// <summary>
    /// Obtain the <see cref="TransactionOptions"/> for this transaction.
    /// </summary>
    public TransactionOptions TransactionOptions
        => session.WrappedCoreSession.CurrentTransaction.TransactionOptions;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}