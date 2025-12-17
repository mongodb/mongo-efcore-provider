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

using Microsoft.Extensions.Logging;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

using BulkWriteEventDefinition = EventDefinition<string, CollectionNamespace, long, long, long>;

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
                ExecutedBulkWrite,
                duration,
                collectionNamespace,
                insertCount,
                deleteCount,
                modifyCount);

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
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
            p.Elapsed.Milliseconds.ToString(),
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
}
