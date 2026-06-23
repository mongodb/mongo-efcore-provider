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

namespace MongoDB.EntityFrameworkCore.Diagnostics;

using BulkWriteEventDefinition = EventDefinition<string, CollectionNamespace, long, long, long>;
#if !EF8
using BulkExecutingEventDefinition = EventDefinition<string, CollectionNamespace>;
using BulkExecutingTwoPhaseEventDefinition = EventDefinition<string, CollectionNamespace, long>;
using BulkExecutedEventDefinition = EventDefinition<string, CollectionNamespace, long>;
#endif

/// <summary>
/// MongoDB-specific logging extensions for operations in the Update category.
/// </summary>
internal static class MongoLoggerUpdateExtensions
{
    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutingBulkWrite" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this query is using.</param>
    /// <param name="insertCount">The number of documents to insert.</param>
    /// <param name="deleteCount">The number of documents to delete.</param>
    /// <param name="modifyCount">The number of documents to modify.</param>
    public static void ExecutingBulkWrite(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long insertCount,
        long deleteCount,
        long modifyCount)
    {
        var definition = LogExecutingBulkWrite(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(
                diagnostics,
                duration.TotalMilliseconds.ToString(),
                collectionNamespace,
                insertCount,
                deleteCount,
                modifyCount);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkWriteEventData(
                definition,
                ExecutingBulkWriteMessage,
                duration,
                collectionNamespace,
                insertCount,
                deleteCount,
                modifyCount);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string ExecutingBulkWriteMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkWriteEventDefinition)definition;
        var p = (MongoBulkWriteEventData)payload;
        return d.GenerateMessage(
            p.Elapsed.TotalMilliseconds.ToString(),
            p.CollectionNamespace,
            p.InsertCount,
            p.DeleteCount,
            p.ModifyCount);
    }

    private static BulkWriteEventDefinition LogExecutingBulkWrite(IDiagnosticsLogger logger)
        => (BulkWriteEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkWrite ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkWrite,
                logger,
                static logger => new BulkWriteEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutingBulkWrite,
                    LogLevel.Information,
                    "MongoEventId.ExecutingBulkWrite",
                    level => LoggerMessage.Define<string, CollectionNamespace, long, long, long>(
                        level,
                        MongoEventId.ExecutingBulkWrite,
                        "Executing Bulk Write ({elapsed} ms) Collection='{collectionNamespace}', Insertions={inserted}, Deletions={deleted}, Modifications={modified}"))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutedBulkWrite" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this query is using.</param>
    /// <param name="insertCount">The number of documents inserted.</param>
    /// <param name="deleteCount">The number of documents deleted.</param>
    /// <param name="modifyCount">The number of documents modified.</param>
    public static void ExecutedBulkWrite(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long insertCount,
        long deleteCount,
        long modifyCount)
    {
        var definition = LogExecutedBulkWrite(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(
                diagnostics,
                duration.TotalMilliseconds.ToString(),
                collectionNamespace,
                insertCount,
                deleteCount,
                modifyCount);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkWriteEventData(
                definition,
                ExecutedBulkWrite,
                duration,
                collectionNamespace,
                insertCount,
                deleteCount,
                modifyCount);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string ExecutedBulkWrite(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkWriteEventDefinition)definition;
        var p = (MongoBulkWriteEventData)payload;
        return d.GenerateMessage(
            p.Elapsed.TotalMilliseconds.ToString(),
            p.CollectionNamespace,
            p.InsertCount,
            p.DeleteCount,
            p.ModifyCount);
    }

    private static BulkWriteEventDefinition LogExecutedBulkWrite(IDiagnosticsLogger logger)
        => (BulkWriteEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutedBulkWrite ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutedBulkWrite,
                logger,
                static logger => new BulkWriteEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutedBulkWrite,
                    LogLevel.Information,
                    "MongoEventId.ExecutedBulkWrite",
                    level => LoggerMessage.Define<string, CollectionNamespace, long, long, long>(
                        level,
                        MongoEventId.ExecutedBulkWrite,
                        "Executed Bulk Write ({elapsed} ms) Collection='{collectionNamespace}', Inserted={inserted}, Deleted={deleted}, Modified={modified}"))));

#if !EF8
    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutingBulkDelete" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this operation is using.</param>
    /// <param name="targetCount">
    /// When non-null, indicates that this is a two-phase bulk delete and specifies the number of
    /// target document ids collected in phase 1.
    /// </param>
    public static void ExecutingBulkDelete(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long? targetCount = null)
    {
        if (targetCount.HasValue)
        {
            var twoPhaseDefinition = LogExecutingBulkDeleteTwoPhase(diagnostics);

            if (diagnostics.ShouldLog(twoPhaseDefinition))
            {
                twoPhaseDefinition.Log(diagnostics, duration.TotalMilliseconds.ToString(), collectionNamespace, targetCount.Value);
            }

            if (diagnostics.NeedsEventData(twoPhaseDefinition, out var diagnosticSourceEnabled2, out var simpleLogEnabled2))
            {
                var twoPhaseEventData = new MongoBulkDeleteEventData(
                    twoPhaseDefinition,
                    ExecutingBulkDeleteTwoPhaseMessage,
                    duration,
                    collectionNamespace,
                    deleteCount: 0,
                    targetCount: targetCount.Value);

                diagnostics.DispatchEventData(twoPhaseDefinition, twoPhaseEventData, diagnosticSourceEnabled2, simpleLogEnabled2);
            }

            return;
        }

        var definition = LogExecutingBulkDelete(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, duration.TotalMilliseconds.ToString(), collectionNamespace);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkDeleteEventData(
                definition,
                ExecutingBulkDeleteMessage,
                duration,
                collectionNamespace,
                deleteCount: 0,
                targetCount: null);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string ExecutingBulkDeleteMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkExecutingEventDefinition)definition;
        var p = (MongoBulkDeleteEventData)payload;
        return d.GenerateMessage(p.Elapsed.TotalMilliseconds.ToString(), p.CollectionNamespace);
    }

    private static string ExecutingBulkDeleteTwoPhaseMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkExecutingTwoPhaseEventDefinition)definition;
        var p = (MongoBulkDeleteEventData)payload;
        return d.GenerateMessage(p.Elapsed.TotalMilliseconds.ToString(), p.CollectionNamespace, p.TargetCount!.Value);
    }

    private static BulkExecutingEventDefinition LogExecutingBulkDelete(IDiagnosticsLogger logger)
        => (BulkExecutingEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkDelete ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkDelete,
                logger,
                static logger => new BulkExecutingEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutingBulkDelete,
                    LogLevel.Information,
                    "MongoEventId.ExecutingBulkDelete",
                    level => LoggerMessage.Define<string, CollectionNamespace>(
                        level,
                        MongoEventId.ExecutingBulkDelete,
                        "Executing Bulk Delete ({elapsed} ms) Collection='{collectionNamespace}'"))));

    private static BulkExecutingTwoPhaseEventDefinition LogExecutingBulkDeleteTwoPhase(IDiagnosticsLogger logger)
        => (BulkExecutingTwoPhaseEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkDeleteTwoPhase ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkDeleteTwoPhase,
                logger,
                static logger => new BulkExecutingTwoPhaseEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutingBulkDelete,
                    LogLevel.Information,
                    "MongoEventId.ExecutingBulkDelete",
                    level => LoggerMessage.Define<string, CollectionNamespace, long>(
                        level,
                        MongoEventId.ExecutingBulkDelete,
                        "Executing Bulk Delete ({elapsed} ms) Collection='{collectionNamespace}' (two-phase, {targetCount} target(s))"))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutedBulkDelete" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this operation is using.</param>
    /// <param name="deleteCount">The number of documents deleted.</param>
    public static void ExecutedBulkDelete(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long deleteCount)
    {
        var definition = LogExecutedBulkDelete(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, duration.TotalMilliseconds.ToString(), collectionNamespace, deleteCount);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkDeleteEventData(
                definition,
                ExecutedBulkDeleteMessage,
                duration,
                collectionNamespace,
                deleteCount: deleteCount,
                targetCount: null);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string ExecutedBulkDeleteMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkExecutedEventDefinition)definition;
        var p = (MongoBulkDeleteEventData)payload;
        return d.GenerateMessage(p.Elapsed.TotalMilliseconds.ToString(), p.CollectionNamespace, p.DeleteCount);
    }

    private static BulkExecutedEventDefinition LogExecutedBulkDelete(IDiagnosticsLogger logger)
        => (BulkExecutedEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutedBulkDelete ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutedBulkDelete,
                logger,
                static logger => new BulkExecutedEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutedBulkDelete,
                    LogLevel.Information,
                    "MongoEventId.ExecutedBulkDelete",
                    level => LoggerMessage.Define<string, CollectionNamespace, long>(
                        level,
                        MongoEventId.ExecutedBulkDelete,
                        "Executed Bulk Delete ({elapsed} ms) Collection='{collectionNamespace}', Deleted={deleted}"))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutingBulkUpdate" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this operation is using.</param>
    /// <param name="targetCount">
    /// When non-null, indicates that this is a two-phase bulk update and specifies the number of
    /// target document ids collected in phase 1.
    /// </param>
    public static void ExecutingBulkUpdate(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long? targetCount = null)
    {
        if (targetCount.HasValue)
        {
            var twoPhaseDefinition = LogExecutingBulkUpdateTwoPhase(diagnostics);

            if (diagnostics.ShouldLog(twoPhaseDefinition))
            {
                twoPhaseDefinition.Log(diagnostics, duration.TotalMilliseconds.ToString(), collectionNamespace, targetCount.Value);
            }

            if (diagnostics.NeedsEventData(twoPhaseDefinition, out var diagnosticSourceEnabled2, out var simpleLogEnabled2))
            {
                var twoPhaseEventData = new MongoBulkUpdateEventData(
                    twoPhaseDefinition,
                    ExecutingBulkUpdateTwoPhaseMessage,
                    duration,
                    collectionNamespace,
                    modifyCount: 0,
                    targetCount: targetCount.Value);

                diagnostics.DispatchEventData(twoPhaseDefinition, twoPhaseEventData, diagnosticSourceEnabled2, simpleLogEnabled2);
            }

            return;
        }

        var definition = LogExecutingBulkUpdate(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, duration.TotalMilliseconds.ToString(), collectionNamespace);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkUpdateEventData(
                definition,
                ExecutingBulkUpdateMessage,
                duration,
                collectionNamespace,
                modifyCount: 0,
                targetCount: null);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string ExecutingBulkUpdateMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkExecutingEventDefinition)definition;
        var p = (MongoBulkUpdateEventData)payload;
        return d.GenerateMessage(p.Elapsed.TotalMilliseconds.ToString(), p.CollectionNamespace);
    }

    private static string ExecutingBulkUpdateTwoPhaseMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkExecutingTwoPhaseEventDefinition)definition;
        var p = (MongoBulkUpdateEventData)payload;
        return d.GenerateMessage(p.Elapsed.TotalMilliseconds.ToString(), p.CollectionNamespace, p.TargetCount!.Value);
    }

    private static BulkExecutingEventDefinition LogExecutingBulkUpdate(IDiagnosticsLogger logger)
        => (BulkExecutingEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkUpdate ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkUpdate,
                logger,
                static logger => new BulkExecutingEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutingBulkUpdate,
                    LogLevel.Information,
                    "MongoEventId.ExecutingBulkUpdate",
                    level => LoggerMessage.Define<string, CollectionNamespace>(
                        level,
                        MongoEventId.ExecutingBulkUpdate,
                        "Executing Bulk Update ({elapsed} ms) Collection='{collectionNamespace}'"))));

    private static BulkExecutingTwoPhaseEventDefinition LogExecutingBulkUpdateTwoPhase(IDiagnosticsLogger logger)
        => (BulkExecutingTwoPhaseEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkUpdateTwoPhase ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutingBulkUpdateTwoPhase,
                logger,
                static logger => new BulkExecutingTwoPhaseEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutingBulkUpdate,
                    LogLevel.Information,
                    "MongoEventId.ExecutingBulkUpdate",
                    level => LoggerMessage.Define<string, CollectionNamespace, long>(
                        level,
                        MongoEventId.ExecutingBulkUpdate,
                        "Executing Bulk Update ({elapsed} ms) Collection='{collectionNamespace}' (two-phase, {targetCount} target(s))"))));

    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutedBulkUpdate" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this operation is using.</param>
    /// <param name="modifyCount">The number of documents modified.</param>
    public static void ExecutedBulkUpdate(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long modifyCount)
    {
        var definition = LogExecutedBulkUpdate(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, duration.TotalMilliseconds.ToString(), collectionNamespace, modifyCount);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkUpdateEventData(
                definition,
                ExecutedBulkUpdateMessage,
                duration,
                collectionNamespace,
                modifyCount: modifyCount,
                targetCount: null);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string ExecutedBulkUpdateMessage(EventDefinitionBase definition, EventData payload)
    {
        var d = (BulkExecutedEventDefinition)definition;
        var p = (MongoBulkUpdateEventData)payload;
        return d.GenerateMessage(p.Elapsed.TotalMilliseconds.ToString(), p.CollectionNamespace, p.ModifyCount);
    }

    private static BulkExecutedEventDefinition LogExecutedBulkUpdate(IDiagnosticsLogger logger)
        => (BulkExecutedEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutedBulkUpdate ?? NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutedBulkUpdate,
                logger,
                static logger => new BulkExecutedEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutedBulkUpdate,
                    LogLevel.Information,
                    "MongoEventId.ExecutedBulkUpdate",
                    level => LoggerMessage.Define<string, CollectionNamespace, long>(
                        level,
                        MongoEventId.ExecutedBulkUpdate,
                        "Executed Bulk Update ({elapsed} ms) Collection='{collectionNamespace}', Modified={modified}"))));
#endif
}
