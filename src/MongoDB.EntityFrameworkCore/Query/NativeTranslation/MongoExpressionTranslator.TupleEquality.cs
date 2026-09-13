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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — a constructed-tuple equality/inequality comparison
/// (<c>new Tuple&lt;string&gt;(c.City) == new Tuple&lt;string&gt;("London")</c>, and the <c>ValueTuple</c>/
/// multi-member/<c>!=</c> spellings of the same shape).
/// </summary>
/// <remarks>
/// Neither operand is a member access or a "simple value" (<see cref="IsSimpleValue"/> — see
/// <see cref="TranslateComparisonCore"/>), and neither is a whole root entity, so without this rewrite the
/// comparison falls all the way through to the general field-to-field/<c>$expr</c> path, where
/// <see cref="TranslateOperand"/> also has no <see cref="NewExpression"/> case — the whole comparison declines
/// and the query falls back to driver-LINQ. This mirrors what the driver's own LINQ provider already does for
/// this exact shape (an array-vs-array <c>$eq</c>/<c>$ne</c>): each tuple becomes a literal MQL array of its
/// constructor arguments, translated one-for-one via the same <see cref="TranslateOperand"/> used for any
/// other computed value, so a mix of field references, constants and parameters across tuple members works
/// exactly like it would in an ordinary comparison.
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateTupleEquality(BinaryExpression be, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (be.Left is not NewExpression left
            || be.Right is not NewExpression right
            || left.Type != right.Type
            || !IsTupleType(left.Type)
            || left.Arguments.Count != right.Arguments.Count)
        {
            return false;
        }

        var leftElements = new MongoExpression[left.Arguments.Count];
        var rightElements = new MongoExpression[right.Arguments.Count];
        for (var i = 0; i < left.Arguments.Count; i++)
        {
            var leftElement = TranslateOperand(left.Arguments[i]);
            if (leftElement is null)
                return false;

            var rightElement = TranslateOperand(right.Arguments[i]);
            if (rightElement is null)
                return false;

            leftElements[i] = leftElement;
            rightElements[i] = rightElement;
        }

        var leftTuple = new MongoTupleExpression(leftElements);
        var rightTuple = new MongoTupleExpression(rightElements);

        // Same "no value-converted/non-default-represented operand" discipline TranslateComparisonCore's own
        // general field-to-field/$expr path applies (see its remarks): a tuple element renders through the
        // raw $expr field path, with no serializer applied, so a converted/re-encoded member would compare in
        // stored form rather than model form.
        if (!AllFieldsDefaultSerialized(leftTuple) || !AllFieldsDefaultSerialized(rightTuple))
            return false;

        result = new MongoBinaryExpression(
            be.NodeType == ExpressionType.NotEqual ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal,
            leftTuple,
            rightTuple);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a <see cref="Tuple"/> or <see cref="ValueTuple"/> family type —
    /// the only structural-equality-by-position types the C# compiler still leaves as a plain (no
    /// <c>op_Equality</c>) <see cref="NewExpression"/> comparison. Both families implement
    /// <see cref="ITuple"/>, so checking that (rather than enumerating the eight generic arities by hand) is
    /// both simpler and future-proof.
    /// </summary>
    private static bool IsTupleType(Type type)
        => typeof(ITuple).IsAssignableFrom(type);
}
