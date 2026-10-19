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
/// The computed-needle sibling of <see cref="MongoInExpression"/>: a collection-membership test whose
/// needle is a COMPUTED value (e.g. a string concatenation, <c>c.CustomerID + "SomeConstant"</c>) rather
/// than a bare field. Unlike <see cref="MongoInExpression"/>, this has no query-dialect form at all — a
/// computed needle can only be tested via the aggregation-expression array-form <c>{ $in: [needle, haystack] }</c>
/// inside <c>$expr</c>, never as <c>{ field: { $in: [...] } }</c> — so it is a distinct sealed type rather
/// than widening <see cref="MongoInExpression.Field"/>'s type, per the "sealed sibling type over a flag"
/// convention documented in Query/AGENTS.md.
/// </summary>
internal sealed class MongoComputedInExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoComputedInExpression"/>.
    /// </summary>
    /// <param name="needle">The computed value being tested for membership.</param>
    /// <param name="values">The candidate values to test against.</param>
    /// <param name="negated"><see langword="true"/> for negated membership (the value is not in the set).</param>
    public MongoComputedInExpression(MongoExpression needle, MongoExpression values, bool negated)
    {
        Needle = needle;
        Values = values;
        Negated = negated;
    }

    /// <summary>The computed value being tested for membership.</summary>
    public MongoExpression Needle { get; }

    /// <summary>The candidate values to test against.</summary>
    public MongoExpression Values { get; }

    /// <summary><see langword="true"/> for negated membership (the value is not in the set).</summary>
    public bool Negated { get; }

    /// <inheritdoc />
    public override Type Type => typeof(bool);
}
