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

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>
/// Represents an <c>$unset</c> aggregation stage that removes one or more top-level fields from each document.
/// </summary>
/// <remarks>
/// Removes the synthetic sort fields <see cref="MongoAddFieldsStage"/> added.
/// <b>Required for set-op correctness, not just shaper hygiene:</b> a set-op operand is allowed to carry a
/// sort, but <c>Union</c>'s dedup (<c>$group {_id: "$$ROOT"}</c>) and <c>Intersect</c>/<c>Except</c>'s source
/// tagging (<c>$group {_id: "$_doc"}</c>) compare WHOLE documents downstream. Without this <c>$unset</c> the
/// synthetic sort field would fold into that comparison key and silently change set semantics.
/// </remarks>
internal sealed class MongoUnsetStage : MongoPipelineStage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MongoUnsetStage"/> class.
    /// </summary>
    /// <param name="fieldNames">The top-level field names to remove.</param>
    public MongoUnsetStage(IReadOnlyList<string> fieldNames)
    {
        FieldNames = fieldNames;
    }

    /// <summary>
    /// Gets the top-level field names to remove.
    /// </summary>
    public IReadOnlyList<string> FieldNames { get; }
}
