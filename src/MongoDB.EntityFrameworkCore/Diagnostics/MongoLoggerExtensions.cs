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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// MongoDB-specific logging extensions.
/// </summary>
internal static class MongoLoggerExtensions
{
    public static void ExecutedMqlQuery(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Command> diagnostics,
        CollectionNamespace collectionNamespace,
        BsonDocument[]? loggedStages)
    {
        var definition = LogExecutedMqlQuery(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            var logSensitiveData = diagnostics.ShouldLogSensitiveData();

            // Ideally we would always log the query and only log the parameters
            // when sensitive data is enabled but unfortunately the LINQ provider
            // does not provide a layer for this.
            if (logSensitiveData)
            {
                definition.Log(
                    diagnostics,
                    Environment.NewLine,
                    collectionNamespace,
                    LoggedStagesToMql(loggedStages));
            }
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoQueryEventData(
                definition,
                ExecutedMqlQuery,
                collectionNamespace,
                LoggedStagesToMql(loggedStages),
                diagnostics.ShouldLogSensitiveData());

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static string LoggedStagesToMql(BsonDocument[]? documents)
        => documents == null
            ? ""
            : string.Join(", ", documents.Select(d => d.ToString()));

    private static string ExecutedMqlQuery(EventDefinitionBase definition, EventData payload)
    {
        var d = (EventDefinition<string, CollectionNamespace, string>)definition;
        var p = (MongoQueryEventData)payload;
        return d.GenerateMessage(
            Environment.NewLine,
            p.CollectionNamespace,
            p.QueryMql);
    }

    private static EventDefinition<string, CollectionNamespace, string> LogExecutedMqlQuery(IDiagnosticsLogger logger)
    {
        var definition = ((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery;
        if (definition == null)
        {
            definition = NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery,
                logger,
                static logger => new EventDefinition<string, CollectionNamespace, string>(
                    logger.Options,
                    MongoEventId.ExecutedMqlQuery,
                    LogLevel.Information,
                    "MongoEventId.ExecutedMqlQuery",
                    level => LoggerMessage.Define<string, CollectionNamespace, string>(
                        level,
                        MongoEventId.ExecutedMqlQuery,
                        LogExecutedMqlQueryString)));
        }

        return (EventDefinition<string, CollectionNamespace, string>)definition;
    }

    private const string LogExecutedMqlQueryString = "Executed MQL query{newLine}{collectionNamespace}.aggregate([{queryMql}])";
}
