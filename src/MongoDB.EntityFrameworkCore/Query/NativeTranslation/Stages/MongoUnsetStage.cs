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
/// EF-401 (stream 1, slice B). Removes the synthetic sort fields <see cref="MongoAddFieldsStage"/> added.
/// <para>
/// <b>Neither shaper needs this stage — it ships for SET-OP HYGIENE, and the comment is here so the next
/// reader does not measure that and delete it.</b> MEASURED (spike §3.2): with the <c>$unset</c> suppressed,
/// the streaming materializer, the DOM shaper, a trailing projection and a tracking round-trip all behave
/// identically, and no synthetic element is written back on <c>SaveChanges</c>. But the synthetic value
/// survives into the document STREAM, and two native operations downstream compare WHOLE documents —
/// <c>Union</c>'s dedup (<c>$group {_id: "$$ROOT"}</c>) and <c>Intersect</c>/<c>Except</c>'s source tagging
/// (<c>$group {_id: "$_doc"}</c>) — while a set-op operand is explicitly allowed to carry a sort
/// (<c>IsPlainWholeEntitySelect</c>). Without the <c>$unset</c> the synthetic value would fold into the
/// comparison key and change set semantics. One stage per query makes that structurally impossible.
/// </para>
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
