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

/// <summary>
/// MongoDB-specific logging extensions for operations in the Update category.
/// </summary>
internal static class MongoLoggerUpdateExtensions
{
    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutedBulkWrite" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="duration">The amount of time the operation took.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this query is using.</param>
    /// <param name="documentsInserted">The number of documents inserted.</param>
    /// <param name="documentedDeleted">The number of documents deleted.</param>
    /// <param name="documentsModified">The number of documents modified.</param>
    public static void ExecutedBulkWrite(
        this IDiagnosticsLogger<DbLoggerCategory.Update> diagnostics,
        TimeSpan duration,
        CollectionNamespace collectionNamespace,
        long documentsInserted,
        long documentedDeleted,
        long documentsModified)
    {
        var definition = LogExecutedBulkWrite(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(
                diagnostics,
                duration.TotalMilliseconds.ToString(),
                collectionNamespace,
                documentsInserted,
                documentedDeleted,
                documentsModified);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoBulkWriteEventData(
                definition,
                ExecutedBulkWrite,
                duration,
                collectionNamespace,
                documentsInserted,
                documentedDeleted,
                documentsModified,
                diagnostics.ShouldLogSensitiveData());

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
            p.DocumentsInserted,
            p.DocumentsDeleted,
            p.DocumentsModified);
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
