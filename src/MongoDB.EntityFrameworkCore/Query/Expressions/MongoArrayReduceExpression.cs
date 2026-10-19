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
/// Represents <c>{ &lt;Operator&gt;: "$path" }</c> — an aggregation-expression-dialect ARRAY-reducing operator
/// (<c>$avg</c>/<c>$max</c>/<c>$min</c>/<c>$sum</c>) applied to a named array field, as opposed to the SAME
/// operator names used as a <c>$group</c> accumulator (which reduce over the grouped documents themselves,
/// not a stored array).
/// </summary>
/// <remarks>
/// EF-322's sole use: the flattening <c>$project</c> that follows a <c>GroupBy(key).Select(aggregate)</c>'s
/// <c>$group</c>, for a member of the shape <c>g.Select(e =&gt; e.Field).Distinct().Average()</c> (and
/// <c>.Max()</c>/<c>.Min()</c>/<c>.Sum()</c>) — <c>NativeGroupByBinder.TryBindAccumulator</c> binds the
/// <c>$group</c> accumulator itself as <c>$addToSet</c> (collecting the group's DISTINCT projected values into
/// an array), and this node reduces that array back to the single scalar the aggregate asked for. Does not
/// wrap a <see cref="MongoFieldExpression"/> for the same reason <see cref="MongoSizeExpression"/> (its
/// <c>Count</c>/<c>LongCount</c> sibling — <c>$size</c> needs no array-reduce wrapper at all) does not: the
/// accumulator's own output field is a synthetic <c>$group</c>-stage alias, with no backing
/// <see cref="Microsoft.EntityFrameworkCore.Metadata.IProperty"/>. No null-safety wrapping is needed (contrast
/// <see cref="MongoSizeExpression.NullSafe"/> for an embedded array that may be missing): a <c>$group</c>
/// accumulator field is written for every group by construction, never missing.
/// </remarks>
internal sealed class MongoArrayReduceExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoArrayReduceExpression"/> over the named <c>$addToSet</c> accumulator field.
    /// </summary>
    /// <param name="operatorName">The MQL array-reduce operator: <c>"$avg"</c>, <c>"$max"</c>, <c>"$min"</c>, or <c>"$sum"</c>.</param>
    /// <param name="fieldName">The accumulator's own output field name (a top-level <c>$group</c>-stage alias).</param>
    /// <param name="type">The CLR type of the resulting reduced value.</param>
    public MongoArrayReduceExpression(string operatorName, string fieldName, Type type)
    {
        OperatorName = operatorName;
        FieldName = fieldName;
        Type = type;
    }

    /// <summary>The MQL array-reduce operator: <c>"$avg"</c>, <c>"$max"</c>, <c>"$min"</c>, or <c>"$sum"</c>.</summary>
    public string OperatorName { get; }

    /// <summary>The accumulator's own output field this reduces over.</summary>
    public string FieldName { get; }

    /// <inheritdoc />
    public override Type Type { get; }
}
