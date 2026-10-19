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

using System.Collections.Generic;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents a <c>$set</c> (<c>$addFields</c>) aggregation stage that adds one or more computed fields to
/// each document, leaving every existing field in place.
/// </summary>
/// <remarks>
/// Emitted only as part of the <c>$set</c> → <c>$sort</c> → <c>$unset</c> triple the lowerer produces for a
/// COMPUTED sort key: MQL <c>$sort</c> accepts field paths only, so a non-field key must be materialized
/// into a synthetic field first. The payload mirrors <see cref="MongoProjectStage"/>'s — alias/expression
/// pairs rendered through <c>MongoAggregationExpressionRenderer</c> — keeping the lowerer BSON-free.
/// </remarks>
internal sealed class MongoAddFieldsStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoAddFieldsStage"/> class.
    /// </summary>
    /// <param name="fields">The ordered fields to add, as alias/expression pairs.</param>
    public MongoAddFieldsStage(IReadOnlyList<MongoProjection> fields)
    {
        Fields = fields;
    }

    /// <summary>
    /// Gets the ordered fields to add, as alias/expression pairs.
    /// </summary>
    public IReadOnlyList<MongoProjection> Fields { get; }
}
