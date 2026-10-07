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
/// The operand of a constructed-tuple comparison (<c>new Tuple&lt;string&gt;(c.City) ==
/// new Tuple&lt;string&gt;("London")</c>), rendered as a literal MQL array so <c>$eq</c>/<c>$ne</c> compares
/// element-by-element.
/// </summary>
/// <remarks>
/// Not a reuse of <see cref="MongoValueListExpression"/>: that type is only renderable as an <c>$in</c> haystack
/// via <c>RenderInValues</c>, whereas this is only a top-level <c>$eq</c>/<c>$ne</c> operand and its elements
/// may be field references.
/// </remarks>
internal sealed class MongoTupleExpression : MongoExpression
{
    public MongoTupleExpression(IReadOnlyList<MongoExpression> elements)
    {
        Elements = elements;
    }

    /// <summary>The per-member value nodes, in constructor-argument order.</summary>
    public IReadOnlyList<MongoExpression> Elements { get; }

    /// <inheritdoc />
    public override Type Type => typeof(object);
}
