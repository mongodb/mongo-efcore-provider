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
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// A <see cref="DiagnosticSource" /> event payload class for MongoDB transaction events.
/// </summary>
/// <remarks>
/// See <see href="https://aka.ms/efcore-docs-diagnostics">Logging, events, and diagnostics</see> for more information and examples.
/// </remarks>
public class MongoTransactionStartingEventData : DbContextEventData
{
    /// <summary>
    /// Constructs the event payload.
    /// </summary>
    /// <param name="eventDefinition">The event definition.</param>
    /// <param name="messageGenerator">A delegate that generates a log message for this event.</param>
    /// <param name="context">The <see cref="DbContext" /> currently in use, or <see langword="null" /> if not known.</param>
    /// <param name="session">The <see cref="IClientSession"/> being used for this transaction.</param>
    /// <param name="transactionOptions">The transaction options.</param>
    /// <param name="transactionId">A correlation ID that identifies the Entity Framework transaction being used.</param>
    /// <param name="async">Indicates whether or not the transaction is being used asynchronously.</param>
    /// <param name="startTime">The start time of this event.</param>
    public MongoTransactionStartingEventData(
        EventDefinitionBase eventDefinition,
        Func<EventDefinitionBase, EventData, string> messageGenerator,
        DbContext? context,
        IClientSession session,
        TransactionOptions transactionOptions,
        Guid transactionId,
        bool async,
        DateTimeOffset startTime)
        : base(eventDefinition, messageGenerator, context)
    {
        Session = session;
        TransactionOptions = transactionOptions;
        TransactionId = transactionId;
        IsAsync = async;
        StartTime = startTime;
    }

    /// <summary>
    /// The <see cref="IClientSession"/> being used for this transaction.
    /// </summary>
    public IClientSession Session { get; }

    /// <summary>
    /// The <see cref="TransactionOptions"/> used in this transaction.
    /// </summary>
    public TransactionOptions TransactionOptions { get; }

    /// <summary>
    /// A correlation ID that identifies the Entity Framework transaction being used.
    /// </summary>
    public Guid TransactionId { get; }

    /// <summary>
    /// Indicates whether or not the transaction is being used asynchronously.
    /// </summary>
    public bool IsAsync { get; }

    /// <summary>
    /// The start time of this event.
    /// </summary>
    public DateTimeOffset StartTime { get; }
}
