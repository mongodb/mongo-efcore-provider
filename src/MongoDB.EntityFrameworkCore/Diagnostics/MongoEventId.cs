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

using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;
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
        RecommendedMinMaxRangeMissing,
        EncryptedNullablePropertyEncountered,
        ColumnAttributeWithTypeUsed,
        VectorSearchNeedsIndex,
        VectorSearchReturnedZeroResults,
        WaitingForVectorIndex,
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
    /// A min or max value for a MongoDB Queryable Encryption range-query property has not been declared but is recommended.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Model.Validation" /> category.</para>
    ///     <para>This event uses the <see cref="PropertyEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId RecommendedMinMaxRangeMissing = MakeValidationId(Id.RecommendedMinMaxRangeMissing);

    /// <summary>
    /// A property setup for MongoDB Queryable Encryption is nullable but null is not supported by queryable encryption.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Model.Validation" /> category.</para>
    ///     <para>This event uses the <see cref="PropertyEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId EncryptedNullablePropertyEncountered = MakeValidationId(Id.EncryptedNullablePropertyEncountered);

    private static EventId MakeModelId(Id id)
        => new((int)id, DbLoggerCategory.Model.Name + "." + id);

    /// <summary>
    /// A <see cref="ColumnAttribute"/> with a type name was found on the property of a type mapped to MongoDB.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Model" /> category.</para>
    ///     <para>This event uses the <see cref="PropertyEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId ColumnAttributeWithTypeUsed = MakeModelId(Id.ColumnAttributeWithTypeUsed);

    private static EventId MakeQueryId(Id id)
        => new((int)id, DbLoggerCategory.Query.Name + "." + id);

    /// <summary>
    /// A vector query could not be executed because the vector index for this query could not be found. Use 'HasIndex' on the
    /// EF model builder to specify the index, or disable this warning if you have created your MongoDB indexes outside of EF Core.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Query" /> category.</para>
    ///     <para>This event uses the <see cref="PropertyEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId VectorSearchNeedsIndex = MakeQueryId(Id.VectorSearchNeedsIndex);

    /// <summary>
    /// The vector query returned zero results. This could be because either there is no vector index defined in the database for
    /// the query property, or because vector data (embeddings) have recently been inserted and the index is still building.
    /// Consider disabling index creation in 'DbContext.Database.EnsureCreated' and performing initial ingestion of embeddings,
    /// before calling 'DbContext.Database.CreateMissingVectorIndexes' and 'DbContext.Database.WaitForVectorIndexes'.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Query" /> category.</para>
    ///     <para>This event uses the <see cref="PropertyAndIndexNameEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId VectorSearchReturnedZeroResults = MakeQueryId(Id.VectorSearchReturnedZeroResults);

    private static EventId MakeDatabaseId(Id id)
        => new((int)id, DbLoggerCategory.Database.Name + "." + id);

    /// <summary>
    /// EF Core is waiting for vector indexes to be ready.
    /// </summary>
    /// <remarks>
    ///     <para>This event is in the <see cref="DbLoggerCategory.Database" /> category.</para>
    ///     <para>This event uses the <see cref="TimeSpanEventData" /> payload when used with a <see cref="DiagnosticSource" />.</para>
    /// </remarks>
    public static readonly EventId WaitingForVectorIndex = MakeDatabaseId(Id.WaitingForVectorIndex);
}
