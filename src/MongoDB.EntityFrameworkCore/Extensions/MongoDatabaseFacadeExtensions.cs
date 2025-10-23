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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Storage;

// ReSharper disable once CheckNamespace
namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// MongoDB-specific extension methods for the <see cref="DatabaseFacade"/> obtained from the
/// EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>
/// </summary>
public static class MongoDatabaseFacadeExtensions
{
    /// <summary>
    /// Creates an index in MongoDB based on the EF Core <see cref="IIndex"/> definition. No attempt is made to check that the index
    /// does not already exist and can therefore be created. The index may be an Atlas index or a normal MongoDB index.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="index">The <see cref="IIndex"/> definition.</param>
    public static void CreateIndex(this DatabaseFacade databaseFacade, IIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context.GetService<IMongoClientWrapper>().CreateIndex(index);
    }

    /// <summary>
    /// Creates an index in MongoDB based on the EF Core <see cref="IIndex"/> definition. No attempt is made to check that the index
    /// does not already exist and can therefore be created. The index may be an Atlas index or a normal MongoDB index.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="index">The <see cref="IIndex"/> definition.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    public static Task CreateIndexAsync(this DatabaseFacade databaseFacade, IIndex index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);

        return ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context.GetService<IMongoClientWrapper>()
            .CreateIndexAsync(index, cancellationToken);
    }

    /// <summary>
    /// Creates indexes in the MongoDB database for all <see cref="IIndex"/> definitions in the EF Core model for which there
    /// is not already an index in the database. This method only creates regular, non-Atlas indexes.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    public static void CreateMissingIndexes(this DatabaseFacade databaseFacade)
    {
        var context = ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context;
        context.GetService<IMongoClientWrapper>().CreateMissingIndexes(context.GetService<IDesignTimeModel>().Model);
    }

    /// <summary>
    /// Creates missing Atlas vector indexes in the MongoDB database for all <see cref="IIndex"/> definitions in the EF Core model for
    /// which there is not already an index in the database.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    public static void CreateMissingVectorIndexes(this DatabaseFacade databaseFacade)
    {
        var context = ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context;
        context.GetService<IMongoClientWrapper>().CreateMissingVectorIndexes(context.GetService<IDesignTimeModel>().Model);
    }

    /// <summary>
    /// Creates indexes in the MongoDB database for all <see cref="IIndex"/> definitions in the EF Core model for which there
    /// is not already an index in the database. This method only creates regular, non-Atlas indexes.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    public static Task CreateMissingIndexesAsync(this DatabaseFacade databaseFacade, CancellationToken cancellationToken = default)
    {
        var context = ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context;
        return context.GetService<IMongoClientWrapper>().CreateMissingIndexesAsync(context.GetService<IDesignTimeModel>().Model, cancellationToken);
    }

    /// <summary>
    /// Creates missing Atlas vector indexes in the MongoDB database for all <see cref="IIndex"/> definitions in the EF Core model for
    /// which there is not already an index in the database.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    public static Task CreateMissingVectorIndexesAsync(this DatabaseFacade databaseFacade, CancellationToken cancellationToken = default)
    {
        var context = ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context;
        return context.GetService<IMongoClientWrapper>().CreateMissingVectorIndexesAsync(context.GetService<IDesignTimeModel>().Model, cancellationToken);
    }

    /// <summary>
    /// Blocks until all vector indexes in the mapped collections are reporting the 'READY' state.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="timeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
    /// The default is 15 seconds. Zero seconds means no timeout.</param>
    /// <exception cref="InvalidOperationException">if the timeout expires before all indexes are 'READY'.</exception>
    public static void WaitForVectorIndexes(this DatabaseFacade databaseFacade, TimeSpan? timeout = null)
    {
        var context = ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context;
        context.GetService<IMongoClientWrapper>().WaitForVectorIndexes(context.GetService<IDesignTimeModel>().Model, timeout);
    }

    /// <summary>
    /// Blocks until all vector indexes in the mapped collections are reporting the 'READY' state.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="timeout">The minimum amount of time to wait for all indexes to be 'READY' before aborting.
    /// The default is 15 seconds. Zero seconds means no timeout.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel this asynchronous request.</param>
    /// <returns>A <see cref="Task"/> to track this async operation.</returns>
    /// <exception cref="InvalidOperationException">if the timeout expires before all indexes are 'READY'.</exception>
    public static Task WaitForVectorIndexesAsync(this DatabaseFacade databaseFacade, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var context = ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context;
        return context.GetService<IMongoClientWrapper>().WaitForVectorIndexesAsync(context.GetService<IDesignTimeModel>().Model, timeout, cancellationToken);
    }

    /// <summary>
    /// Ensures that the database for the context exists. If it exists, no action is taken. If it does not
    /// exist then the MongoDB database is created using the <see cref="MongoDatabaseCreationOptions"/> to determine what
    /// additional actions to take.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="options">An <see cref="MongoDatabaseCreationOptions"/> object specifying additional actions to be taken.</param>
    /// <returns><see langword="true" /> if the database is created, <see langword="false" /> if it already existed.</returns>
    public static bool EnsureCreated(this DatabaseFacade databaseFacade, MongoDatabaseCreationOptions options)
        => ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context.GetService<IMongoDatabaseCreator>().EnsureCreated(options);

    /// <summary>
    /// Asynchronously ensures that the database for the context exists. If it exists, no action is taken. If it does not
    /// exist then the MongoDB database is created using the <see cref="MongoDatabaseCreationOptions"/> to determine what
    /// additional actions to take.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade"/> from the EF Core <see cref="Microsoft.EntityFrameworkCore.DbContext"/>.</param>
    /// <param name="options">An <see cref="MongoDatabaseCreationOptions"/> object specifying additional actions to be taken.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken" /> to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains <see langword="true" /> if the database is created, <see langword="false" /> if it already existed.
    /// </returns>
    public static Task<bool> EnsureCreatedAsync(this DatabaseFacade databaseFacade, MongoDatabaseCreationOptions options, CancellationToken cancellationToken = default)
        => ((IDatabaseFacadeDependenciesAccessor)databaseFacade).Context.GetService<IMongoDatabaseCreator>().EnsureCreatedAsync(options, cancellationToken);

    /// <summary>
    /// Begin a new transaction with the supplied <paramref name="transactionOptions"/>.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade" /> for the context.</param>
    /// <param name="transactionOptions">The <see cref="TransactionOptions"/> that control the behavior of this transaction.</param>
    /// <returns>The <see cref="IDbContextTransaction"/> that has begun.</returns>
    public static IDbContextTransaction BeginTransaction(
        this DatabaseFacade databaseFacade,
        TransactionOptions transactionOptions)
        => GetMongoTransactionManager(databaseFacade).BeginTransaction(transactionOptions);

    /// <summary>
    /// Begin a new async transaction with the supplied <paramref name="transactionOptions"/>.
    /// </summary>
    /// <param name="databaseFacade">The <see cref="DatabaseFacade" /> for the context.</param>
    /// <param name="transactionOptions">The <see cref="TransactionOptions"/> that control the behavior of this transaction.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken" /> to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the newly begun <see cref="IDbContextTransaction"/>.</returns>
    /// <exception cref="OperationCanceledException">If the <see cref="CancellationToken" /> is canceled.</exception>
    public static Task<IDbContextTransaction> BeginTransactionAsync(
        this DatabaseFacade databaseFacade,
        TransactionOptions transactionOptions,
        CancellationToken cancellationToken = default)
        => GetMongoTransactionManager(databaseFacade).BeginTransactionAsync(transactionOptions, cancellationToken);

    private static IMongoTransactionManager GetMongoTransactionManager(DatabaseFacade databaseFacade)
        => (IMongoTransactionManager)((IDatabaseFacadeDependenciesAccessor)databaseFacade).Dependencies.TransactionManager;
}
