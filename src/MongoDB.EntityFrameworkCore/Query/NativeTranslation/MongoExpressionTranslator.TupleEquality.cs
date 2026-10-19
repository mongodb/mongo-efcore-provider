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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — a constructed-tuple equality/inequality comparison
/// (<c>new Tuple&lt;string&gt;(c.City) == new Tuple&lt;string&gt;("London")</c>, and the <c>ValueTuple</c>/
/// multi-member/<c>!=</c>/<c>Tuple.Create(...)</c> spellings of the same shape).
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
/// <para>
/// <c>Tuple.Create(a, b)</c> (the static-factory spelling) is a <see cref="MethodCallExpression"/>, never a
/// <see cref="NewExpression"/> — the C# compiler does not inline factory-method bodies into expression trees —
/// so <see cref="TryDecomposeTupleOperand"/> also recognizes that call shape directly. Worse, when a
/// <c>Tuple.Create(...)</c> operand has no free reference to the query source (e.g. the RHS constant literal
/// in <c>Tuple.Create(c.City, c.Country) == Tuple.Create("London", "UK")</c>), EF Core's own parameter
/// extraction evaluates it eagerly into a single node carrying the whole materialized <see cref="Tuple"/>
/// instance — the per-argument tree structure is gone by the time this translator ever sees it.
/// <see cref="TryDecomposeTupleOperand"/> handles two shapes for that: MEASURED against the real EF Core
/// compiled-query pipeline, the funcletized node is a <see cref="ConstantExpression"/> whose boxed
/// <see cref="ITuple"/> value is decomposed right here, at translate time, into one
/// <see cref="MongoConstantExpression"/> per element. A query PARAMETER (rather than a baked-in constant)
/// carrying the same whole-tuple value is handled defensively too, by synthesizing one
/// <see cref="MongoParameterExpression"/> per tuple element (<c>ArrayElementIndex</c> 0..n-1, same parameter
/// <c>Name</c>) — <see cref="MongoPipelineFactory"/>'s per-execution substitution already knows how to index a
/// runtime <see cref="ITuple"/> value by position (it was originally written only for indexing a runtime
/// array/list, for the unrelated <c>args[i]</c>-over-a-query-parameter-array shape, but the extraction is a
/// plain positional index either way) — though no shape observed so far actually reaches the translator this
/// way for a tuple.
/// </para>
/// </remarks>
internal sealed partial class MongoExpressionTranslator
{
    private bool TryTranslateTupleEquality(BinaryExpression be, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (be.Left.Type != be.Right.Type || !IsTupleType(be.Left.Type))
            return false;

        if (!TryDecomposeTupleOperand(be.Left, out var leftElements)
            || !TryDecomposeTupleOperand(be.Right, out var rightElements)
            || leftElements.Length != rightElements.Length)
        {
            return false;
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

    /// <summary>
    /// Decomposes one side of a tuple comparison into its per-element <see cref="MongoExpression"/>s, across
    /// every spelling EF Core can hand the translator: a <see cref="NewExpression"/> (<c>new Tuple&lt;...&gt;
    /// (...)</c>), a <c>Tuple.Create(...)</c> <see cref="MethodCallExpression"/>, or — once EF's own parameter
    /// extraction has funcletized a <c>Tuple.Create(...)</c> operand with no free reference to the query
    /// source — a <see cref="ConstantExpression"/> (the shape MEASURED against the real pipeline) or a bare
    /// query parameter (handled defensively) whose runtime value is the whole materialized tuple. The first
    /// two shapes recurse each element through <see cref="TranslateOperand"/> exactly like
    /// <see cref="NewExpression.Arguments"/> always has, so a mix of field references, constants and
    /// parameters across tuple members still works.
    /// </summary>
    private bool TryDecomposeTupleOperand(Expression operand, out MongoExpression[] elements)
    {
        switch (operand)
        {
            case NewExpression newTuple:
                return TryTranslateTupleArguments(newTuple.Arguments, out elements);

            case MethodCallExpression call when IsTupleCreateCall(call):
                return TryTranslateTupleArguments(call.Arguments, out elements);

            // The funcletized shape MEASURED against the real EF Core pipeline: a `Tuple.Create(...)` operand
            // with no free reference to the query source is evaluated eagerly by EF's own parameter
            // extraction into a ConstantExpression carrying the whole materialized Tuple instance — decompose
            // it here, at translate time, via the same ITuple indexer IsTupleType uses to recognize the type.
            case ConstantExpression { Value: ITuple tupleValue }:
            {
                var constantElements = new MongoExpression[tupleValue.Length];
                for (var i = 0; i < tupleValue.Length; i++)
                    constantElements[i] = new MongoConstantExpression(tupleValue[i], forSerialization: null);

                elements = constantElements;
                return true;
            }

            default:
                // A funcletized Tuple.Create(...) with no free reference to the query source arrives as a
                // single opaque query parameter carrying the whole materialized Tuple instance — the
                // per-argument tree structure is gone. Synthesize one MongoParameterExpression per element
                // (ArrayElementIndex 0..n-1, same parameter Name); MongoPipelineFactory's per-execution
                // substitution extracts the matching position out of the runtime ITuple value.
                if (NativeQueryParameter.TryGetQueryParameterName(operand, out var parameterName))
                {
                    var arity = operand.Type.GetGenericArguments().Length;
                    var parameterElements = new MongoExpression[arity];
                    for (var i = 0; i < arity; i++)
                    {
                        parameterElements[i] =
                            new MongoParameterExpression(parameterName, forSerialization: null, arrayElementIndex: i);
                    }

                    elements = parameterElements;
                    return true;
                }

                elements = [];
                return false;
        }
    }

    private bool TryTranslateTupleArguments(
        IReadOnlyList<Expression> arguments, out MongoExpression[] elements)
    {
        var translated = new MongoExpression[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var element = TranslateOperand(arguments[i]);
            if (element is null)
            {
                elements = [];
                return false;
            }

            translated[i] = element;
        }

        elements = translated;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="call"/> is a call to one of the eight generic <see cref="Tuple"/>.Create
    /// overloads. The C# compiler never inlines a factory-method body into an expression tree, so
    /// <c>Tuple.Create(a, b)</c> reaches here as a <see cref="MethodCallExpression"/>, never as the
    /// <see cref="NewExpression"/> the sibling <c>new Tuple&lt;...&gt;(...)</c> spelling produces.
    /// </summary>
    private static bool IsTupleCreateCall(MethodCallExpression call)
        => call.Method is { IsStatic: true, IsGenericMethod: true, Name: nameof(Tuple.Create) }
            && call.Method.DeclaringType == typeof(Tuple);
}
