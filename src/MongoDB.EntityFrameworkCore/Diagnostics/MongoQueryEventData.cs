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

using System.Diagnostics;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// A <see cref="DiagnosticSource" /> event payload class for MongoDB query events.
/// </summary>
/// <remarks>
/// See <see href="https://aka.ms/efcore-docs-diagnostics">Logging, events, and diagnostics</see> for more information and examples.
/// </remarks>
public class MongoQueryEventData : EventData
{
    /// <summary>
    /// Constructs the event payload.
    /// </summary>
    /// <param name="eventDefinition">The event definition.</param>
    /// <param name="messageGenerator">A delegate that generates a log message for this event.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> being queried.</param>
    /// <param name="queryMql">The MQL representing the query.</param>
    /// <param name="logSensitiveData">Indicates whether the application allows logging of sensitive data.</param>
    public MongoQueryEventData(
        EventDefinitionBase eventDefinition,
        Func<EventDefinitionBase, EventData, string> messageGenerator,
        CollectionNamespace collectionNamespace,
        string queryMql,
        bool logSensitiveData)
        : base(eventDefinition, messageGenerator)
    {
        CollectionNamespace = collectionNamespace;
        QueryMql = queryMql;
        LogSensitiveData = logSensitiveData;
    }

    /// <summary>
    /// The <see cref="CollectionNamespace"/> being queried.
    /// </summary>
    public virtual CollectionNamespace CollectionNamespace { get; }

    /// <summary>
    /// The MQL representing the query.
    /// </summary>
    public virtual string QueryMql { get; }

    /// <summary>
    /// Indicates whether the application allows logging of sensitive data.
    /// </summary>
    public virtual bool LogSensitiveData { get; }
}
