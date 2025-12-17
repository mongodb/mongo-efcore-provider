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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Explicit transaction manager for MongoDB EF Core provider.
/// </summary>
public class MongoTransactionManager : IMongoTransactionManager
{
    private readonly IMongoClientWrapper _client;
    private readonly DbContext _context;
    private readonly IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> _logger;
    private readonly TransactionOptions _defaultTransactionOptions = new();

    /// <summary>
    /// Create a <see cref="MongoTransactionManager"/>.
    /// </summary>
    public MongoTransactionManager(
        IMongoClientWrapper client,
        ICurrentDbContext currentContext,
        IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> logger)
    {
        _client = client;
        _context = currentContext.Context;
        _logger = logger;
    }

    /// <inheritdoc />
    public void ResetState()
    {
        CurrentTransaction?.Dispose();
        CurrentTransaction = null;
    }

    /// <inheritdoc />
    public async Task ResetStateAsync(CancellationToken cancellationToken = new())
    {
        if (CurrentTransaction != null)
        {
            await CurrentTransaction.DisposeAsync().ConfigureAwait(false);
        }

        CurrentTransaction = null;
    }

    /// <inheritdoc />
    public IDbContextTransaction BeginTransaction()
    {
        EnsureNoTransactions();
        var session = _client.StartSession();
        return CurrentTransaction = MongoTransaction.Start(session, _context, async: false, _defaultTransactionOptions, this, _logger);
    }

    /// <inheritdoc />
    public IDbContextTransaction BeginTransaction(TransactionOptions transactionOptions)
    {
        EnsureNoTransactions();
        var session = _client.StartSession();
        return CurrentTransaction = MongoTransaction.Start(session, _context, async: false, transactionOptions, this, _logger);
    }

    /// <inheritdoc />
    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = new())
    {
        EnsureNoTransactions();
        var session = await _client.StartSessionAsync(cancellationToken).ConfigureAwait(false);
        return CurrentTransaction = MongoTransaction.Start(session, _context, async: true, _defaultTransactionOptions, this, _logger);
    }

    /// <inheritdoc />
    public async Task<IDbContextTransaction> BeginTransactionAsync(TransactionOptions transactionOptions, CancellationToken cancellationToken = new())
    {
        EnsureNoTransactions();
        var session = await _client.StartSessionAsync(cancellationToken).ConfigureAwait(false);
        return CurrentTransaction = MongoTransaction.Start(session, _context, async: true, transactionOptions, this, _logger);
    }

    /// <inheritdoc />
    public void CommitTransaction()
    {
        if (GetRequiredCurrentTransaction() is { } acquiredTransaction)
        {
            acquiredTransaction.Commit();
            acquiredTransaction.Dispose();
            CurrentTransaction = null;
        }
    }

    /// <inheritdoc />
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = new())
    {
        if (GetRequiredCurrentTransaction() is { } acquiredTransaction)
        {
            await acquiredTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await acquiredTransaction.DisposeAsync().ConfigureAwait(false);
            CurrentTransaction = null;
        }
    }

    /// <inheritdoc />
    public void RollbackTransaction()
    {
        if (GetRequiredCurrentTransaction() is { } acquiredTransaction)
        {
            acquiredTransaction.Rollback();
            acquiredTransaction.Dispose();
            CurrentTransaction = null;
        }
    }

    /// <inheritdoc />
    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = new())
    {
        if (GetRequiredCurrentTransaction() is { } acquiredTransaction)
        {
            await acquiredTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await acquiredTransaction.DisposeAsync().ConfigureAwait(false);
            CurrentTransaction = null;
        }
    }

    private IDbContextTransaction GetRequiredCurrentTransaction()
        => CurrentTransaction
           ?? throw new InvalidOperationException("No transaction is in progress. Call BeginTransaction to start a transaction.");

    /// <inheritdoc />
    public IDbContextTransaction? CurrentTransaction { get; internal set; }

    private void EnsureNoTransactions()
    {
        if (CurrentTransaction != null)
        {
            throw new InvalidOperationException(
                "The connection is already in a transaction and cannot participate in another transaction.");
        }

        if (System.Transactions.Transaction.Current != null)
        {
            throw new InvalidOperationException(
                "An ambient transaction has been detected. The ambient transaction needs to be completed before starting a new transaction on this connection.");
        }
    }
}
