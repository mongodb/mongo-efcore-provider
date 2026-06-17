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

#if !EF8

using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Whether a bulk operation deletes or updates documents.
/// </summary>
internal enum MongoBulkOperationKind
{
    Delete,
    Update
}

/// <summary>
/// How a bulk operation is executed: a single atomic command, or the transactional two-phase
/// (collect target <c>_id</c>s, then act by <c>$in</c>) strategy.
/// </summary>
internal enum MongoBulkStrategy
{
    SingleCommand,
    TwoPhase
}

/// <summary>
/// Compile-time plan describing a bulk <c>ExecuteDelete</c>/<c>ExecuteUpdate</c> operation, produced by the query
/// pipeline and consumed by <see cref="MongoBulkOperationExecutor"/> at execution time. The delegates defer the
/// (runtime, parameter-dependent) translation of the filter / update / target-id query to the Query area while the
/// executor owns the driver writes, transaction orchestration, and diagnostics.
/// <para>
/// Exactly one delegate set is populated per (<see cref="Strategy"/>, <see cref="Kind"/>) combination:
/// <list type="bullet">
/// <item>(<see cref="MongoBulkStrategy.SingleCommand"/>, <see cref="MongoBulkOperationKind.Delete"/>) → <see cref="BuildFilter"/></item>
/// <item>(<see cref="MongoBulkStrategy.SingleCommand"/>, <see cref="MongoBulkOperationKind.Update"/>) → <see cref="BuildFilter"/> + <see cref="BuildUpdate"/></item>
/// <item>(<see cref="MongoBulkStrategy.TwoPhase"/>, <see cref="MongoBulkOperationKind.Delete"/>) → <see cref="BuildTargetIdQuery"/></item>
/// <item>(<see cref="MongoBulkStrategy.TwoPhase"/>, <see cref="MongoBulkOperationKind.Update"/>) → <see cref="BuildTargetIdQuery"/> + <see cref="BuildUpdate"/></item>
/// </list>
/// </para>
/// </summary>
internal sealed class MongoBulkPlan
{
    /// <summary>Whether this operation deletes or updates documents.</summary>
    public required MongoBulkOperationKind Kind { get; init; }

    /// <summary>The execution strategy: a single atomic command, or the transactional two-phase strategy.</summary>
    public required MongoBulkStrategy Strategy { get; init; }

    /// <summary>The MongoDB collection the operation targets.</summary>
    public required string CollectionName { get; init; }

    /// <summary>Builds the server-side filter for a single-command operation. <see langword="null"/> for two-phase.</summary>
    public Func<QueryContext, FilterDefinition<BsonDocument>>? BuildFilter { get; init; }

    /// <summary>Builds the server-side update. <see langword="null"/> for deletes.</summary>
    public Func<QueryContext, UpdateDefinition<BsonDocument>>? BuildUpdate { get; init; }

    /// <summary>Builds the phase-1 read query yielding the target documents. <see langword="null"/> for single-command.</summary>
    public Func<QueryContext, IQueryable<BsonDocument>>? BuildTargetIdQuery { get; init; }
}

#endif
