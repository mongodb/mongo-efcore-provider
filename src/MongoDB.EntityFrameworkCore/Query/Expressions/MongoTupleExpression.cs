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
using System.Collections.Generic;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents the operand of a constructed-tuple comparison (<c>new Tuple&lt;string&gt;(c.City) ==
/// new Tuple&lt;string&gt;("London")</c>) — an ordered array of per-member value expressions, rendered as a
/// literal MQL array so the surrounding <c>$eq</c>/<c>$ne</c> compares the two tuples element-by-element.
/// </summary>
/// <remarks>
/// Deliberately a SIBLING of <see cref="MongoValueListExpression"/>, not a reuse of it: that type is pinned
/// (see its own tests) as NOT top-level-renderable — it exists only as <see cref="MongoInExpression.Values"/>,
/// rendered exclusively through <c>RenderInValues</c>. This node is the opposite: it is ONLY ever a top-level
/// <c>$eq</c>/<c>$ne</c> operand, never an <c>$in</c> haystack, and its elements may be field references, not
/// just constants/parameters.
/// </remarks>
internal sealed class MongoTupleExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoTupleExpression"/> wrapping <paramref name="elements"/>.
    /// </summary>
    /// <param name="elements">The per-member value nodes, in constructor-argument order.</param>
    public MongoTupleExpression(IReadOnlyList<MongoExpression> elements)
    {
        Elements = elements;
    }

    /// <summary>The per-member value nodes, in constructor-argument order.</summary>
    public IReadOnlyList<MongoExpression> Elements { get; }

    /// <inheritdoc />
    public override Type Type => typeof(object);
}
