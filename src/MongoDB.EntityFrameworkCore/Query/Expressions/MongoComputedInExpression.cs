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

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// The computed-needle sibling of <see cref="MongoInExpression"/> (e.g. <c>c.CustomerID + "X"</c> in a list). Has no
/// query-dialect form, only the aggregation <c>{ $in: [needle, haystack] }</c> inside <c>$expr</c>, so it is a
/// separate sealed type rather than a widened <see cref="MongoInExpression.Field"/>.
/// </summary>
internal sealed class MongoComputedInExpression : MongoExpression
{
    public MongoComputedInExpression(MongoExpression needle, MongoExpression values, bool negated)
    {
        Needle = needle;
        Values = values;
        Negated = negated;
    }

    public MongoExpression Needle { get; }

    public MongoExpression Values { get; }

    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
