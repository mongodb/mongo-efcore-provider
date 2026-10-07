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
/// <c>{ &lt;Operator&gt;: "$path" }</c> — an array-reducing <c>$avg</c>/<c>$max</c>/<c>$min</c>/<c>$sum</c> over a
/// named array field (not the <c>$group</c> accumulator of the same name).
/// </summary>
/// <remarks>
/// Used in the flattening <c>$project</c> after <c>$group</c> for <c>g.Select(e =&gt; e.Field).Distinct().Average()</c>
/// and friends: <c>NativeGroupByBinder.TryBindAccumulator</c> collects distinct values with <c>$addToSet</c>, and
/// this reduces that array to the scalar. Holds a field name rather than a <see cref="MongoFieldExpression"/>
/// because the accumulator alias has no backing property. No null-safety wrap: a <c>$group</c> accumulator field is
/// always present.
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
