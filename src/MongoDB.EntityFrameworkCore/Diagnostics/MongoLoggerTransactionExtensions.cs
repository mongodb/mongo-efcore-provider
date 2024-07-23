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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

using TransactionStartingEventDefinition = EventDefinition<string>;
using TransactionEndEventDefinition = EventDefinition<string>;

/// <summary>
/// MongoDB-specific logging extensions for operations in the Transaction category.
/// </summary>
internal static class MongoLoggerTransactionExtensions
{
    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionStarting" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="session">The <see cref="IClientSession"/> this transaction is using.</param>
    /// <param name="context">The <see cref="DbContext"/> this transaction is for.</param>
    /// <param name="transactionOptions">The <see cref="TransactionOptions"/> this transaction is using.</param>
    /// <param name="transactionId">A correlation ID that identifies the Entity Framework transaction being used.</param>
    /// <param name="async">Indicates whether or not the transaction is being used asynchronously.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    public static void TransactionStarting(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        IClientSession session,
        DbContext context,
        TransactionOptions transactionOptions,
        Guid transactionId,
        bool async,
        DateTimeOffset startTime)
    {
        var definition = LogBeginningTransaction(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, FormatTransactionOptions(transactionOptions));
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionStartingEventData(
                definition,
                TransactionStarting,
                context,
                session,
                transactionOptions,
                transactionId,
                async,
                startTime);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string FormatTransactionOptions(TransactionOptions transactionOptions)
        => $"MaxCommitTime '{transactionOptions.MaxCommitTime}', " +
           $"ReadConcern '{transactionOptions.ReadConcern}', " +
           $"ReadPreference '{transactionOptions.ReadPreference}', " +
           $"WriteConcern '{transactionOptions.WriteConcern}'";

    private static string TransactionStarting(EventDefinitionBase definition, EventData payload)
    {
        var d = (TransactionStartingEventDefinition)definition;
        var p = (MongoTransactionStartingEventData)payload;
        return d.GenerateMessage(FormatTransactionOptions(p.TransactionOptions));
    }

    private static TransactionStartingEventDefinition LogBeginningTransaction(IDiagnosticsLogger logger)
        => (TransactionStartingEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogBeginningTransaction
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogBeginningTransaction,
                 logger,
                 static logger => new TransactionStartingEventDefinition(
                     logger.Options,
                     MongoEventId.TransactionStarting,
                     LogLevel.Information,
                     "MongoEventId.TransactionStarting",
                     level => LoggerMessage.Define<string>(
                         level,
                         MongoEventId.TransactionStarting,
                         "Beginning transaction with options {transactionOptions}"))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionStarted" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="transaction">The <see cref="MongoTransaction"/> that was started.</param>
    /// <param name="async">True if this operation is asynchronous, false if it is synchronous.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    /// <param name="duration">The elapsed time from when the operation was started.</param>
    public static void TransactionStarted(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        MongoTransaction transaction,
        bool async,
        DateTimeOffset startTime,
        TimeSpan duration)
    {
        var definition = LogBeganTransaction(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, FormatTransactionOptions(transaction.TransactionOptions));
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionEndEventData(
                definition,
                TransactionStarted,
                transaction,
                async,
                startTime,
                duration);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string TransactionStarted(EventDefinitionBase definition, EventData payload)
    {
        var d = (TransactionEndEventDefinition)definition;
        var p = (MongoTransactionEndEventData)payload;
        return d.GenerateMessage(FormatTransactionOptions(p.Transaction.TransactionOptions));
    }

    private static TransactionEndEventDefinition LogBeganTransaction(IDiagnosticsLogger logger)
        => (TransactionEndEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogBeganTransaction
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogBeganTransaction,
                 logger,
                 static logger => new TransactionEndEventDefinition(
                     logger.Options,
                     MongoEventId.TransactionStarted,
                     LogLevel.Debug,
                     "MongoEventId.TransactionStarted",
                     level => LoggerMessage.Define<string>(
                         level,
                         MongoEventId.TransactionStarted,
                         "Began transaction with options {transactionOptions}."))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionCommitting" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="transaction">The <see cref="MongoTransaction"/> that is being committed.</param>
    /// <param name="async">True if this operation is asynchronous, false if it is synchronous.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    public static void TransactionCommitting(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        MongoTransaction transaction,
        bool async,
        DateTimeOffset startTime)
    {
        var definition = LogCommittingTransaction(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionEventData(
                definition,
                (d, _) => ((EventDefinition)d).GenerateMessage(),
                transaction,
                async,
                startTime);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition LogCommittingTransaction(IDiagnosticsLogger logger)
        => (EventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogCommittingTransaction
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogCommittingTransaction,
                 logger,
                 static logger => new EventDefinition(
                     logger.Options,
                     MongoEventId.TransactionCommitting,
                     LogLevel.Debug,
                     "MongoEventId.TransactionCommitting",
                     level => LoggerMessage.Define(
                         level,
                         MongoEventId.TransactionCommitting,
                         "Committing transaction."))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionCommitted" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="transaction">The <see cref="MongoTransaction"/> that was committed.</param>
    /// <param name="async">True if this operation is asynchronous, false if it is synchronous.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    /// <param name="duration">The elapsed time from when the operation was started.</param>
    public static void TransactionCommitted(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        MongoTransaction transaction,
        bool async,
        DateTimeOffset startTime,
        TimeSpan duration)
    {
        var definition = LogCommittedTransaction(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionEndEventData(
                definition,
                (d, _) => ((EventDefinition)d).GenerateMessage(),
                transaction,
                async,
                startTime,
                duration);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition LogCommittedTransaction(IDiagnosticsLogger logger)
        => (EventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogCommittedTransaction
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogCommittedTransaction,
                 logger,
                 static logger => new EventDefinition(
                     logger.Options,
                     MongoEventId.TransactionCommitted,
                     LogLevel.Debug,
                     "MongoEventId.TransactionCommitted",
                     level => LoggerMessage.Define(
                         level,
                         MongoEventId.TransactionCommitted,
                         "Committed transaction."))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionRollingBack" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="transaction">The <see cref="MongoTransaction"/> that is being rolled back.</param>
    /// <param name="async">True if this operation is asynchronous, false if it is synchronous.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    public static void TransactionRollingBack(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        MongoTransaction transaction,
        bool async,
        DateTimeOffset startTime)
    {
        var definition = LogRollingBackTransaction(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionEventData(
                definition,
                (d, _) => ((EventDefinition)d).GenerateMessage(),
                transaction,
                async,
                startTime);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition LogRollingBackTransaction(IDiagnosticsLogger logger)
        => (EventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogRollingBackTransaction
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogRollingBackTransaction,
                 logger,
                 static logger => new EventDefinition(
                     logger.Options,
                     MongoEventId.TransactionRollingBack,
                     LogLevel.Debug,
                     "MongoEventId.TransactionRollingBack",
                     level => LoggerMessage.Define(
                         level,
                         MongoEventId.TransactionRollingBack,
                         "Rolling back transaction."))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionRolledBack" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="transaction">The <see cref="MongoTransaction"/> that was rolled back.</param>
    /// <param name="async">True if this operation is asynchronous, false if it is synchronous.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    /// <param name="duration">The elapsed time from when the operation was started.</param>
    public static void TransactionRolledBack(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        MongoTransaction transaction,
        bool async,
        DateTimeOffset startTime,
        TimeSpan duration)
    {
        var definition = LogRolledBackTransaction(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionEndEventData(
                definition,
                (d, _) => ((EventDefinition)d).GenerateMessage(),
                transaction,
                async,
                startTime,
                duration);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition LogRolledBackTransaction(IDiagnosticsLogger logger)
        => (EventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogRolledBackTransaction
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogRolledBackTransaction,
                 logger,
                 static logger => new EventDefinition(
                     logger.Options,
                     MongoEventId.TransactionRolledBack,
                     LogLevel.Debug,
                     "MongoEventId.TransactionRolledBack",
                     level => LoggerMessage.Define(
                         level,
                         MongoEventId.TransactionRolledBack,
                         "Rolled back transaction."))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.TransactionError" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="transaction">The <see cref="MongoTransaction"/> that the error occured in.</param>
    /// <param name="action">The action being taken.</param>
    /// <param name="exception">The exception that represents the error.</param>
    /// <param name="async">True if this operation is asynchronous, false if it is synchronous.</param>
    /// <param name="startTime">The time that the operation was started.</param>
    /// <param name="duration">The elapsed time from when the operation was started.</param>
    public static void TransactionError(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Transaction> diagnostics,
        MongoTransaction transaction,
        string action,
        Exception exception,
        bool async,
        DateTimeOffset startTime,
        TimeSpan duration)
    {
        var definition = LogTransactionError(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoTransactionErrorEventData(
                definition,
                (d, _) => ((EventDefinition)d).GenerateMessage(),
                transaction,
                async,
                action,
                exception,
                startTime,
                duration);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition LogTransactionError(IDiagnosticsLogger logger)
        => (EventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogTransactionError
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogTransactionError,
                 logger,
                 static logger => new EventDefinition(
                     logger.Options,
                     MongoEventId.TransactionError,
                     LogLevel.Debug,
                     "MongoEventId.TransactionError",
                     level => LoggerMessage.Define(
                         level,
                         MongoEventId.TransactionError,
                         "An error occurred using a transaction."))));
}
