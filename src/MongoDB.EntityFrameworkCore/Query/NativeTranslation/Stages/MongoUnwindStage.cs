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

using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation.Stages;

/// <summary>An <c>$unwind</c> aggregation stage over a lookup's array field.</summary>
internal sealed class MongoUnwindStage : MongoPipelineStage
{
    public MongoUnwindStage(LookupExpression lookup, bool preserveNullAndEmptyArrays = true, bool atMostOneMatch = false)
    {
        Lookup = lookup;
        PreserveNullAndEmptyArrays = preserveNullAndEmptyArrays;
        AtMostOneMatch = atMostOneMatch;
    }

    public LookupExpression Lookup { get; }

    /// <summary>
    /// Whether a document with no match is kept (left-join) or dropped (inner-join, e.g. a reference SelectMany).
    /// Reference-Include passes <see cref="LookupExpression.PreserveNullAndEmptyArrays"/> (the navigation's requiredness).
    /// </summary>
    public bool PreserveNullAndEmptyArrays { get; }

    /// <summary>
    /// Whether the lookup yields at most one document per outer row (see <see cref="LookupExpression.MatchesAtMostOneDocument"/>), so
    /// unwinding it is the same as taking its first element. The factory then renders the cheaper <c>$match</c> + <c>$set</c>
    /// form: a <c>$lookup</c> followed by <c>$unwind</c> is absorbed by the server into a form that cannot use its slot-based
    /// <c>$lookup</c>, which is 2-6x slower on 8.x for the same documents.
    /// </summary>
    public bool AtMostOneMatch { get; }

    /// <summary>
    /// Whether the cursor's reader drops the rows with no match and reads the lookup's array as it is, so the stage renders nothing: no
    /// <c>$match</c> on the array and no <c>$set</c> to its first element. A <c>$match</c> that reads a <c>$lookup</c>'s output costs the
    /// server about as much as the lookup itself. Only for <see cref="AtMostOneMatch"/> on an inner lookup, and cleared by
    /// <see cref="MongoSelectLowerer"/> when a stage after it depends on the rows that would have been dropped.
    /// </summary>
    public bool ClientFiltered { get; set; }
}
