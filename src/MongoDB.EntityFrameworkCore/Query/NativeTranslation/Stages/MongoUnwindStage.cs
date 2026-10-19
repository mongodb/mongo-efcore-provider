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
    public MongoUnwindStage(LookupExpression lookup, bool preserveNullAndEmptyArrays = true)
    {
        Lookup = lookup;
        PreserveNullAndEmptyArrays = preserveNullAndEmptyArrays;
    }

    public LookupExpression Lookup { get; }

    /// <summary>
    /// Whether a document with no match is kept (left-join) or dropped (inner-join, e.g. a reference SelectMany).
    /// Reference-Include passes <see cref="LookupExpression.PreserveNullAndEmptyArrays"/> (the navigation's requiredness).
    /// </summary>
    public bool PreserveNullAndEmptyArrays { get; }
}
