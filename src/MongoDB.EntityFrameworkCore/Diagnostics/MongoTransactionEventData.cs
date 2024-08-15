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
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// The <see cref="DiagnosticSource" /> event payload base class for <see cref="MongoEventId" /> transaction events.
/// </summary>
/// <remarks>
/// See <see href="https://aka.ms/efcore-docs-diagnostics">Logging, events, and diagnostics</see> for more information and examples.
/// </remarks>
public class MongoTransactionEventData : DbContextEventData
{
    /// <summary>
    /// Constructs the event payload.
    /// </summary>
    /// <param name="eventDefinition">The event definition.</param>
    /// <param name="messageGenerator">A delegate that generates a log message for this event.</param>
    /// <param name="transaction">The <see cref="MongoTransaction" />.</param>
    /// <param name="async">Indicates whether or not the transaction is being used asynchronously.</param>
    /// <param name="startTime">The start time of this event.</param>
    public MongoTransactionEventData(
        EventDefinitionBase eventDefinition,
        Func<EventDefinitionBase, EventData, string> messageGenerator,
        MongoTransaction transaction,
        bool async,
        DateTimeOffset startTime)
        : base(eventDefinition, messageGenerator, transaction.Context)
    {
        Transaction = transaction;
        IsAsync = async;
        StartTime = startTime;
    }

    /// <summary>
    /// The <see cref="MongoTransaction" />.
    /// </summary>
    public MongoTransaction Transaction { get; }

    /// <summary>
    /// Indicates whether or not the transaction is being used asynchronously.
    /// </summary>
    public bool IsAsync { get; }

    /// <summary>
    /// The start time of this event.
    /// </summary>
    public DateTimeOffset StartTime { get; }
}
