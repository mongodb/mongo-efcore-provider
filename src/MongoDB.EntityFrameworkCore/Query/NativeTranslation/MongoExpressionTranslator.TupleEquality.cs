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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — constructed-tuple equality/inequality
/// (<c>new Tuple&lt;string&gt;(c.City) == new Tuple&lt;string&gt;("London")</c>, plus <c>ValueTuple</c>,
/// multi-member, <c>!=</c> and <c>Tuple.Create(...)</c> spellings).
/// </summary>
/// <remarks>
/// Like the driver, each tuple becomes an MQL array of its elements compared with <c>$eq</c>/<c>$ne</c>. A
/// <c>Tuple.Create(a, b)</c> with no free reference to the query source is funcletized by EF into a
/// <see cref="ConstantExpression"/> holding the whole <see cref="ITuple"/>; a whole-tuple query parameter is handled
/// defensively by per-element <see cref="MongoParameterExpression"/>s that <see cref="MongoPipelineFactory"/> indexes
/// positionally at execution.
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

        // A constant/parameter element whose CLR type BsonValue.Create rejects (Guid, ...) must serialize through
        // the property it is compared with; with no such counterpart the whole comparison declines.
        if (!TryRebindUnserializableElements(leftElements, rightElements)
            || !TryRebindUnserializableElements(rightElements, leftElements))
        {
            return false;
        }

        var leftTuple = new MongoTupleExpression(leftElements);
        var rightTuple = new MongoTupleExpression(rightElements);

        // A tuple element renders through the raw $expr field path with no serializer, so a converted member
        // would compare in stored form rather than model form.
        if (!AllFieldsDefaultSerialized(leftTuple) || !AllFieldsDefaultSerialized(rightTuple))
            return false;

        result = new MongoBinaryExpression(
            be.NodeType == ExpressionType.NotEqual ? MongoBinaryOperator.NotEqual : MongoBinaryOperator.Equal,
            leftTuple,
            rightTuple);
        return true;
    }

    /// <summary>
    /// Rebinds, in place, each constant/parameter element that <c>BsonValue.Create</c> cannot serialize
    /// (<see cref="NativeSlotPopulator.TryProbeBareValueRenders"/>; <see cref="Guid"/>, ...) to the <see cref="IProperty"/>
    /// of the field at the same position in <paramref name="others"/>. Returns <see langword="false"/> when such an
    /// element has no field counterpart. Elements <c>BsonValue.Create</c> handles are left untouched.
    /// </summary>
    private static bool TryRebindUnserializableElements(MongoExpression[] elements, MongoExpression[] others)
    {
        for (var i = 0; i < elements.Length; i++)
        {
            var declaredType = elements[i] switch
            {
                MongoConstantExpression { ForSerialization: null } constant => constant.Type,
                MongoParameterExpression { ForSerialization: null, ValueType: { } valueType } => valueType,
                _ => null
            };

            if (declaredType is null || NativeSlotPopulator.TryProbeBareValueRenders(elements[i], declaredType))
                continue;

            if (others[i] is not MongoFieldExpression field)
                return false;

            elements[i] = elements[i] switch
            {
                MongoConstantExpression constant => new MongoConstantExpression(constant.Value, field.Property),
                MongoParameterExpression parameter => new MongoParameterExpression(
                    parameter.Name, field.Property, arrayElementIndex: parameter.ArrayElementIndex,
                    valueType: parameter.ValueType, runtimeEvaluator: parameter.RuntimeEvaluator),
                _ => elements[i]
            };
        }

        return true;
    }

    private static bool IsTupleType(Type type)
        => typeof(ITuple).IsAssignableFrom(type);

    private bool TryDecomposeTupleOperand(Expression operand, out MongoExpression[] elements)
    {
        switch (operand)
        {
            case NewExpression newTuple:
                return TryTranslateTupleArguments(newTuple.Arguments, out elements);

            case MethodCallExpression call when IsTupleCreateCall(call):
                return TryTranslateTupleArguments(call.Arguments, out elements);

            // A funcletized Tuple.Create(...) (the common shape).
            case ConstantExpression { Value: ITuple tupleValue }:
            {
                var constantElements = new MongoExpression[tupleValue.Length];
                for (var i = 0; i < tupleValue.Length; i++)
                    constantElements[i] = new MongoConstantExpression(tupleValue[i], forSerialization: null);

                elements = constantElements;
                return true;
            }

            default:
                // Defensive: a whole-tuple query parameter; MongoPipelineFactory extracts each ArrayElementIndex from the ITuple.
                if (NativeQueryParameter.TryGetQueryParameterName(operand, out var parameterName))
                {
                    var elementTypes = operand.Type.GetGenericArguments();
                    var parameterElements = new MongoExpression[elementTypes.Length];
                    for (var i = 0; i < elementTypes.Length; i++)
                    {
                        parameterElements[i] = new MongoParameterExpression(
                            parameterName, forSerialization: null, arrayElementIndex: i, valueType: elementTypes[i]);
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

    private static bool IsTupleCreateCall(MethodCallExpression call)
        => call.Method is { IsStatic: true, IsGenericMethod: true, Name: nameof(Tuple.Create) }
            && call.Method.DeclaringType == typeof(Tuple);
}
