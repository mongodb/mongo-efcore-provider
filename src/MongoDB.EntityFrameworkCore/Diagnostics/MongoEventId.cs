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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Event IDs for MongoDB events that correspond to messages logged to an <see cref="ILogger" />
/// and events sent to a <see cref="DiagnosticSource" />.
/// </summary>
/// <remarks>
/// These IDs are also used with <see cref="WarningsConfigurationBuilder" /> to configure the behavior of warnings.
/// </remarks>
public static class MongoEventId
{
    // Warning: These values must not change between releases.
    // Only add new values to the end of sections, never in the middle.
    private enum Id
    {
        ExecutedMqlQuery = CoreEventId.ProviderDesignBaseId,
        ExecutingBulkWrite,
        ExecutedBulkWrite,
        TransactionStarting,
        TransactionStarted,
        TransactionCommitting,
        TransactionCommitted,
        TransactionRollingBack,
        TransactionRolledBack,
        TransactionError,
        RecommendedMinMaxRangeMissing
    }

    private static EventId MakeDatabaseCommandId(Id id)
        => new((int)id, DbLoggerCategory.Database.Command.Name + "." + id);

    /// <summary>
    /// An MQL query has been executed.
    /// </summary>
    /// <remarks>
    /// <para>This event is in the <see cref="DbLoggerCategory.Database.Command" /> category.</para>
    /// <para>
    /// This event uses the <see cref="MongoQueryEventData" /> payload when used with a <see cref="DiagnosticSource" />.
    /// </para>
    /// </remarks>
    public static readonly EventId ExecutedMqlQuery = MakeDatabaseCommandId(Id.ExecutedMqlQuery);

    private static EventId MakeUpdateId(Id id)
        => new((int)id, DbLoggerCategory.Update.Name + "." + id);

    /// <summary>
    /// A bulk write is being executed.
    /// </summary>
    /// <remarks>
    /// <para>This event is in the <see cref="DbLoggerCategory.Update" /> category.</para>
    /// <para>
    /// This event uses the <see cref="MongoBulkWriteEventData" /> payload when used with a <see cref="DiagnosticSource" />.
    /// </para>
    /// </remarks>
    public static readonly EventId ExecutingBulkWrite = MakeUpdateId(Id.ExecutingBulkWrite);

    /// <summary>
    /// A bulk write has been executed.
    /// </summary>
    /// <remarks>
    /// <para>This event is in the <see cref="DbLoggerCategory.Update" /> category.</para>
    /// <para>
    /// This event uses the <see cref="MongoBulkWriteEventData" /> payload when used with a <see cref="DiagnosticSource" />.
    /// </para>
    /// </remarks>
    public static readonly EventId ExecutedBulkWrite = MakeUpdateId(Id.ExecutedBulkWrite);

    private static EventId MakeTransactionId(Id id)
        => new((int)id, DbLoggerCategory.Database.Transaction.Name + "." + id);

    /// <summary>
    /// A MongoDB transaction is starting.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionStartingEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionStarting = MakeTransactionId(Id.TransactionStarting);

    /// <summary>
    /// A MongoDB transaction has been started.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionEndEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionStarted = MakeTransactionId(Id.TransactionStarted);

    /// <summary>
    /// A MongoDB transaction is committing.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionCommitting = MakeTransactionId(Id.TransactionCommitting);

    /// <summary>
    /// A MongoDB transaction has been committed.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionEndEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionCommitted = MakeTransactionId(Id.TransactionCommitted);

    /// <summary>
    /// A MongoDB transaction is being rolled back.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionRollingBack = MakeTransactionId(Id.TransactionRollingBack);

    /// <summary>
    /// A MongoDB transaction has been rolled back.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionEndEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionRolledBack = MakeTransactionId(Id.TransactionRolledBack);

    /// <summary>
    /// An error has occurred while using. committing, or rolling back a MongoDB transaction.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database.Transaction" /> category.</para>
    ///     <para>This event uses the <see cref="MongoTransactionErrorEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId TransactionError = MakeTransactionId(Id.TransactionError);

    private static EventId MakeValidationId(Id id)
        => new((int)id, DbLoggerCategory.Model.Validation.Name + "." + id);

    /// <summary>
    /// A min or max value for a Queryable Encryption range-query property has not been defined.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Model.Validation" /> category.</para>
    ///     <para>This event uses the <see cref="PropertyEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId RecommendedMinMaxRangeMissing = MakeValidationId(Id.RecommendedMinMaxRangeMissing);
}
