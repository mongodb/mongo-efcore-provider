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
/// Provides an EF Core-friendly wrapper around a MongoDB transaction.
/// </summary>
/// <param name="session">A <see cref="IClientSession"/> for handling the transaction in MongoDB.</param>
/// <param name="context">The <see cref="DbContext"/> this transaction is being used with.</param>
/// <param name="transactionId">The unique identifier from EF Core for this transaction.</param>
/// <param name="transactionManager">The <see cref="IMongoTransactionManager"/> this transaction is started from or <see langword="null"/> if it is implicit.</param>
/// <param name="transactionLogger">A <see cref="IDiagnosticsLogger"/> for logging the <see cref="DbLoggerCategory.Database.Transaction"/> messages.</param>
public sealed class MongoTransaction(
    IClientSessionHandle session,
    DbContext context,
    Guid transactionId,
    IMongoTransactionManager? transactionManager,
    IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> transactionLogger)
    : IDbContextTransaction
{
    private enum TransactionState
    {
        Active,
        Committed,
        RolledBack,
        Failed,
        Disposed
    }

    private TransactionState _transactionState = TransactionState.Active;

    internal static MongoTransaction Start(
        IClientSessionHandle session,
        DbContext context,
        bool async,
        TransactionOptions transactionOptions,
        IMongoTransactionManager? transactionManager,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> transactionLogger)
    {
        var startTime = DateTimeOffset.UtcNow;
        var transactionId = Guid.NewGuid();
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        transactionLogger.TransactionStarting(session, context, transactionOptions, transactionId, true, startTime);
        try
        {
            session.StartTransaction(transactionOptions);
        }
        catch (NotSupportedException ex)
        {
            if (ex.Message == "Standalone servers do not support transactions.")
            {
                throw new NotSupportedException(string.Join(" ", TransactionByDefault,
                        "Your current MongoDB server configuration does not support transactions and you should consider switching to a replica set or load balanced configuration.",
                        DisableTransactions),
                    ex);
            }

            throw new NotSupportedException(string.Join(" ", TransactionByDefault,
                    "Your current MongoDB server version does not support transactions and you should consider upgrading to a newer version.",
                    DisableTransactions),
                ex);
        }

        var transaction = new MongoTransaction(session, context, transactionId, transactionManager, transactionLogger);
        transactionLogger.TransactionStarted(transaction, async, startTime, stopwatch.Elapsed);
        return transaction;
    }

    private const string TransactionByDefault =
        "The MongoDB EF Core Provider now uses transactions to ensure all updates in a SaveChanges operation are applied together or not at all.";

    private const string DisableTransactions =
        "If you are sure you do not need save consistency or optimistic concurrency you can disable transactions by setting 'Database.AutoTransactionBehavior = AutoTransactionBehavior.Never' on your DbContext.";

    /// <summary>
    /// The underlying <see cref="IClientSession"/> this transaction is using which is required
    /// to issue commands against.
    /// </summary>
    internal IClientSessionHandle Session => session;

    /// <inheritdoc />
    public Guid TransactionId { get; } = transactionId;

    /// <summary>
    /// The <see cref="DbContext"/> this transaction is being used with.
    /// </summary>
    public DbContext Context { get; } = context;

    /// <inheritdoc />
    public void Commit()
    {
        AssertCorrectState("Commit", TransactionState.Active);
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        transactionLogger.TransactionCommitting(this, false, startTime);
        try
        {
            session.CommitTransaction();
        }
        catch (Exception ex)
        {
            _transactionState = TransactionState.Failed;
            transactionLogger.TransactionError(this, "Commit", ex, false, startTime, stopwatch.Elapsed);
            throw;
        }

        _transactionState = TransactionState.Committed;
        transactionLogger.TransactionCommitted(this, false, startTime, stopwatch.Elapsed);
        transactionManager?.ResetState();
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = new())
    {
        AssertCorrectState("Commit", TransactionState.Active);
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        transactionLogger.TransactionCommitting(this, true, startTime);
        try
        {
            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _transactionState = TransactionState.Failed;
            transactionLogger.TransactionError(this, "Commit", ex, true, startTime, stopwatch.Elapsed);
            throw;
        }

        _transactionState = TransactionState.Committed;
        transactionLogger.TransactionCommitted(this, true, startTime, stopwatch.Elapsed);
        if (transactionManager is not null)
        {
            await transactionManager.ResetStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Rollback()
    {
        AssertCorrectState("Rollback", TransactionState.Active);
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        transactionLogger.TransactionRollingBack(this, false, startTime);
        try
        {
            session.AbortTransaction();
        }
        catch (Exception ex)
        {
            _transactionState = TransactionState.Failed;
            transactionLogger.TransactionError(this, "Rollback", ex, false, startTime, stopwatch.Elapsed);
            throw;
        }

        _transactionState = TransactionState.RolledBack;
        transactionLogger.TransactionRolledBack(this, false, startTime, stopwatch.Elapsed);
        transactionManager?.ResetState();
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = new())
    {
        AssertCorrectState("Rollback", TransactionState.Active);
        var startTime = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        transactionLogger.TransactionRollingBack(this, true, startTime);
        try
        {
            await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _transactionState = TransactionState.Failed;
            transactionLogger.TransactionError(this, "Rollback", ex, true, startTime, stopwatch.Elapsed);
            throw;
        }

        _transactionState = TransactionState.RolledBack;
        transactionLogger.TransactionRolledBack(this, true, startTime, stopwatch.Elapsed);
        if (transactionManager is not null)
        {
            await transactionManager.ResetStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Obtain the <see cref="TransactionOptions"/> for this transaction.
    /// </summary>
    public TransactionOptions TransactionOptions
        => session.WrappedCoreSession.CurrentTransaction.TransactionOptions;

    /// <inheritdoc />
    public void Dispose()
    {
        switch (_transactionState)
        {
            case TransactionState.Disposed:
                return;

            case TransactionState.Active:
                Rollback();
                break;

            case TransactionState.Committed:
            case TransactionState.RolledBack:
            case TransactionState.Failed:
                break;
        }

        _transactionState = TransactionState.Disposed;
        session.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        switch (_transactionState)
        {
            case TransactionState.Disposed:
                return;

            case TransactionState.Active:
                await RollbackAsync();
                break;

            case TransactionState.Committed:
            case TransactionState.RolledBack:
            case TransactionState.Failed:
                break;
        }

        _transactionState = TransactionState.Disposed;
        session.Dispose();
    }

    private void AssertCorrectState(string action, TransactionState validState)
    {
        if (_transactionState != validState)
            throw new
                InvalidOperationException($"Can not {action} MongoTransaction {TransactionId} because it is {_transactionState}.");
    }
}
