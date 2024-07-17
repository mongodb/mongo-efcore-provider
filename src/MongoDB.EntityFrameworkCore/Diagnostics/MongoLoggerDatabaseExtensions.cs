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
using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

using MqlQueryEventDefinition = EventDefinition<string, CollectionNamespace, string>;

/// <summary>
/// MongoDB-specific logging extensions for operations in the Database category.
/// </summary>
internal static class MongoLoggerDatabaseExtensions
{
    internal static void ExecutedMqlQuery(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Command> diagnostics,
        MongoExecutableQuery mongoExecutableQuery)
        => ExecutedMqlQuery(diagnostics, mongoExecutableQuery.CollectionNamespace, mongoExecutableQuery.Provider.LoggedStages);

    /// <summary>
    /// Logs for the <see cref="MongoEventId.ExecutedMqlQuery" /> event.
    /// </summary>
    /// <param name="diagnostics">The diagnostics logger to use.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> this query is using.</param>
    /// <param name="loggedStages">The <see cref="BsonDocument"/> array containing the query definition.</param>
    public static void ExecutedMqlQuery(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Command> diagnostics,
        CollectionNamespace collectionNamespace,
        BsonDocument[]? loggedStages)
    {
        var definition = LogExecutedMqlQuery(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            // Ideally we would always log the query and then log parameter data
            // when sensitive data is enabled. Unfortunately the LINQ provider
            // only gives us the query with full values in so we only log MQL
            // when sensitive logging is enabled.
            var mql = diagnostics.ShouldLogSensitiveData() ? LoggedStagesToMql(loggedStages) : "?";
            definition.Log(
                diagnostics,
                Environment.NewLine,
                collectionNamespace,
                mql);
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
        var d = (MqlQueryEventDefinition)definition;
        var p = (MongoQueryEventData)payload;
        return d.GenerateMessage(
            Environment.NewLine,
            p.CollectionNamespace,
            p.LogSensitiveData ? p.QueryMql : "?");
    }

    private static MqlQueryEventDefinition LogExecutedMqlQuery(IDiagnosticsLogger logger)
        => (MqlQueryEventDefinition)
            (((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery
             ?? NonCapturingLazyInitializer.EnsureInitialized(
                 ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery,
                 logger,
                 static logger => new MqlQueryEventDefinition(
                     logger.Options,
                     MongoEventId.ExecutedMqlQuery,
                     LogLevel.Information,
                     "MongoEventId.ExecutedMqlQuery",
                     level => LoggerMessage.Define<string, CollectionNamespace, string>(
                         level,
                         MongoEventId.ExecutedMqlQuery,
                         "Executed MQL query{newLine}{collectionNamespace}.aggregate([{queryMql}])"))));
}
