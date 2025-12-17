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
/// A <see cref="DiagnosticSource" /> event payload class for MongoDB bulk write events.
/// </summary>
/// <remarks>
/// See <see href="https://aka.ms/efcore-docs-diagnostics">Logging, events, and diagnostics</see> for more information and examples.
/// </remarks>
public class MongoBulkWriteEventData : EventData
{
    /// <summary>
    /// Constructs the event payload.
    /// </summary>
    /// <param name="eventDefinition">The event definition.</param>
    /// <param name="messageGenerator">A delegate that generates a log message for this event.</param>
    /// <param name="elapsed">The time elapsed since the command was sent to the database.</param>
    /// <param name="collectionNamespace">The <see cref="CollectionNamespace"/> being operated upon.</param>
    /// <param name="insertCount">The number of documents to insert in this bulk write operation.</param>
    /// <param name="deleteCount">The number of documents to delete by this bulk write operation.</param>
    /// <param name="modifyCount">The number of documents to modify by this bulk write operation.</param>
    public MongoBulkWriteEventData(
        EventDefinitionBase eventDefinition,
        Func<EventDefinitionBase, EventData, string> messageGenerator,
        TimeSpan elapsed,
        CollectionNamespace collectionNamespace,
        long insertCount,
        long deleteCount,
        long modifyCount)
        : base(eventDefinition, messageGenerator)
    {
        Elapsed = elapsed;
        CollectionNamespace = collectionNamespace;
        InsertCount = insertCount;
        DeleteCount = deleteCount;
        ModifyCount = modifyCount;
    }

    /// <summary>
    /// The time elapsed since the command was sent to the database.
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// The <see cref="CollectionNamespace"/> being queried.
    /// </summary>
    public CollectionNamespace CollectionNamespace { get; }

    /// <summary>
    /// The number of documents to insert in this bulk write operation.
    /// </summary>
    public long InsertCount { get; }

    /// <summary>
    /// The number of documents to delete by this bulk write operation.
    /// </summary>
    public long DeleteCount { get; }

    /// <summary>
    /// The number of documents to modify by this bulk write operation.
    /// </summary>
    public long ModifyCount { get; }
}
