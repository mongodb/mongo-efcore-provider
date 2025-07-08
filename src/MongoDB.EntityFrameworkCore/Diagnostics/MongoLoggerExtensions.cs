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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

using MqlQueryEventDefinition = EventDefinition<string, CollectionNamespace, string>;

/// <summary>
/// MongoDB-specific logging extensions.
/// </summary>
internal static class MongoLoggerExtensions
{
    internal static void ExecutedMqlQuery(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Command> diagnostics,
        MongoExecutableQuery mongoExecutableQuery)
        => ExecutedMqlQuery(diagnostics, mongoExecutableQuery.CollectionNamespace, mongoExecutableQuery.Provider.LoggedStages);

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
    {
        var definition = ((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery;
        if (definition == null)
        {
            definition = NonCapturingLazyInitializer.EnsureInitialized(
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
                        LogExecutedMqlQueryString)));
        }

        return (MqlQueryEventDefinition)definition;
    }

    private const string LogExecutedMqlQueryString = "Executed MQL query{newLine}{collectionNamespace}.aggregate([{queryMql}])";


    public static void RecommendedMinMaxRangeMissing(
        this IDiagnosticsLogger<DbLoggerCategory.Model.Validation> diagnostics,
        IProperty property)
    {
        var definition = LogRecommendedMinMaxRangeMissing(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, property.DeclaringType.DisplayName(), property.Name,
                BsonTypeHelper.GetBsonType(property).ToString());
            ;
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new PropertyEventData(
                definition,
                (d, p) => ((EventDefinition<string, string, string>)d).GenerateMessage(
                    ((PropertyEventData)p).Property.DeclaringType.DisplayName(),
                    ((PropertyEventData)p).Property.Name,
                    BsonTypeHelper.GetBsonType(((PropertyEventData)p).Property).ToString()),
                property);
            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition<string, string, string> LogRecommendedMinMaxRangeMissing(IDiagnosticsLogger logger)
    {
        var definition = ((MongoLoggingDefinitions)logger.Definitions).LogRecommendedMinMaxRangeMissing;
        if (definition == null)
        {
            definition = NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogRecommendedMinMaxRangeMissing,
                logger,
                static logger => new EventDefinition<string, string, string>(
                    logger.Options,
                    MongoEventId.RecommendedMinMaxRangeMissing,
                    LogLevel.Warning,
                    "MongoEventId.RecommendedMinMaxRangeMissing",
                    level => LoggerMessage.Define<string, string, string>(
                        level,
                        MongoEventId.RecommendedMinMaxRangeMissing,
                        LogRecommendedMinMaxRangeMissingString)));
        }

        return (EventDefinition<string, string, string>)definition;
    }

    private const string LogRecommendedMinMaxRangeMissingString =
        "The property '{entityType}.{propertyType}' is configured for Queryable Encryption range queries but is missing the recommended min/max values required for {bsonType} storage.";


    public static void EncryptedNullablePropertyEncountered(
        this IDiagnosticsLogger<DbLoggerCategory.Model.Validation> diagnostics,
        IProperty property)
    {
        var definition = LogEncryptedNullablePropertyEncountered(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            definition.Log(diagnostics, property.DeclaringType.DisplayName(), property.Name);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new PropertyEventData(
                definition,
                (d, p) => ((EventDefinition<string, string>)d).GenerateMessage(
                    ((PropertyEventData)p).Property.DeclaringType.DisplayName(),
                    ((PropertyEventData)p).Property.Name),
                property);
            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }

    private static EventDefinition<string, string> LogEncryptedNullablePropertyEncountered(IDiagnosticsLogger logger)
    {
        var definition = ((MongoLoggingDefinitions)logger.Definitions).LogEncryptedNullablePropertyEncountered;
        if (definition == null)
        {
            definition = NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogEncryptedNullablePropertyEncountered,
                logger,
                static logger => new EventDefinition<string, string>(
                    logger.Options,
                    MongoEventId.EncryptedNullablePropertyEncountered,
                    LogLevel.Warning,
                    "MongoEventId.EncryptedNullablePropertyEncountered",
                    level => LoggerMessage.Define<string, string>(
                        level,
                        MongoEventId.EncryptedNullablePropertyEncountered,
                        EncryptedNullablePropertyEncounteredString)));
        }

        return (EventDefinition<string, string>)definition;
    }

    private const string EncryptedNullablePropertyEncounteredString =
        "The property '{entityType}.{property}' is configured for Queryable Encryption but is also nullable. Null is not supported by Queryable Encryption and will throw when attempting to save.";
}
