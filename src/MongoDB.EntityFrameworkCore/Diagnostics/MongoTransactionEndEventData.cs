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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Driver;
using MongoDB.Driver.Core.Bindings;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// A <see cref="DiagnosticSource" /> event payload class for MongoDB transaction events.
/// </summary>
/// <remarks>
/// See <see href="https://aka.ms/efcore-docs-diagnostics">Logging, events, and diagnostics</see> for more information and examples.
/// </remarks>
public class MongoTransactionEndEventData : MongoTransactionEventData
{
    /// <summary>
    /// Constructs the event payload.
    /// </summary>
    /// <param name="eventDefinition">The event definition.</param>
    /// <param name="messageGenerator">A delegate that generates a log message for this event.</param>
    /// <param name="transaction">The <see cref="CoreTransaction" />.</param>
    /// <param name="context">The <see cref="DbContext" /> currently in use, or <see langword="null" /> if not known.</param>
    /// <param name="transactionNumber">A transaction number issued by the MongoDB client.</param>
    /// <param name="clientSession">The <see cref="IClientSession"/> being used to connect to MongoDB.</param>
    /// <param name="async">Indicates whether or not the transaction is being used asynchronously.</param>
    /// <param name="startTime">The start time of this event.</param>
    /// <param name="duration">The duration this event.</param>
    public MongoTransactionEndEventData(
        EventDefinitionBase eventDefinition,
        Func<EventDefinitionBase, EventData, string> messageGenerator,
        CoreTransaction transaction,
        DbContext? context,
        long transactionNumber,
        IClientSession clientSession,
        bool async,
        DateTimeOffset startTime,
        TimeSpan duration)
        : base(eventDefinition, messageGenerator, transaction, context, transactionNumber, clientSession, async, startTime)
    {
        Duration = duration;
    }

    /// <summary>
    /// The duration of this event.
    /// </summary>
    public TimeSpan Duration { get; set; }
}
