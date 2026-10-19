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
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// <see cref="MongoExpressionTranslator"/> — <c>Math</c>/<c>MathF</c>/<c>double.RadiansToDegrees</c>/
/// <c>float.DegreesToRadians</c> function calls.
/// </summary>
internal sealed partial class MongoExpressionTranslator
{
    /// <summary>
    /// <c>Math.X(double)</c>/<c>MathF.X(float)</c> names mapping 1:1 onto a unary <see cref="MongoMathFunction"/>.
    /// </summary>
    private static readonly Dictionary<string, MongoMathFunction> UnaryFunctionsByName = new()
    {
        [nameof(Math.Abs)] = MongoMathFunction.Abs,
        [nameof(Math.Ceiling)] = MongoMathFunction.Ceiling,
        [nameof(Math.Floor)] = MongoMathFunction.Floor,
        [nameof(Math.Exp)] = MongoMathFunction.Exp,
        [nameof(Math.Sqrt)] = MongoMathFunction.Sqrt,
        [nameof(Math.Truncate)] = MongoMathFunction.Truncate,
        [nameof(Math.Log10)] = MongoMathFunction.Log10,
        [nameof(Math.Log2)] = MongoMathFunction.Log2,
        [nameof(Math.Sign)] = MongoMathFunction.Sign,
        [nameof(Math.Acos)] = MongoMathFunction.Acos,
        [nameof(Math.Acosh)] = MongoMathFunction.Acosh,
        [nameof(Math.Asin)] = MongoMathFunction.Asin,
        [nameof(Math.Asinh)] = MongoMathFunction.Asinh,
        [nameof(Math.Atan)] = MongoMathFunction.Atan,
        [nameof(Math.Atanh)] = MongoMathFunction.Atanh,
        [nameof(Math.Cos)] = MongoMathFunction.Cos,
        [nameof(Math.Cosh)] = MongoMathFunction.Cosh,
        [nameof(Math.Sin)] = MongoMathFunction.Sin,
        [nameof(Math.Sinh)] = MongoMathFunction.Sinh,
        [nameof(Math.Tan)] = MongoMathFunction.Tan,
        [nameof(Math.Tanh)] = MongoMathFunction.Tanh
    };

    /// <summary>
    /// <c>RadiansToDegrees</c>/<c>DegreesToRadians</c> on <c>double</c>/<c>float</c> — .NET 8+ members declared on
    /// the numeric types, not on <c>Math</c>/<c>MathF</c>.
    /// </summary>
    private static readonly Dictionary<string, MongoMathFunction> AngleConversionsByName = new()
    {
        ["RadiansToDegrees"] = MongoMathFunction.RadiansToDegrees,
        ["DegreesToRadians"] = MongoMathFunction.DegreesToRadians
    };

    /// <summary>
    /// <c>Math.X(double, double)</c> names mapping 1:1 onto a binary <see cref="MongoMathFunction"/> (2-element array
    /// operand).
    /// </summary>
    private static readonly Dictionary<string, MongoMathFunction> BinaryFunctionsByName = new()
    {
        [nameof(Math.Pow)] = MongoMathFunction.Pow,
        [nameof(Math.Atan2)] = MongoMathFunction.Atan2,
        [nameof(Math.Max)] = MongoMathFunction.Max,
        [nameof(Math.Min)] = MongoMathFunction.Min
    };

    private bool TryTranslateMath(Expression node, bool allowNumericWidening, [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (node is MethodCallExpression angleCall
            && (angleCall.Method.DeclaringType == typeof(double) || angleCall.Method.DeclaringType == typeof(float))
            && angleCall.Arguments.Count == 1
            && AngleConversionsByName.TryGetValue(angleCall.Method.Name, out var angleFunction))
        {
            var angleOperand = TranslateOperand(angleCall.Arguments[0], allowNumericWidening: true);
            if (angleOperand is null)
                return false;

            result = new MongoMathExpression(angleFunction, [angleOperand], angleCall.Method.ReturnType);
            return true;
        }

        if (node is not MethodCallExpression call || call.Method.DeclaringType != typeof(Math) && call.Method.DeclaringType != typeof(MathF))
            return false;

        // Only Math.Round(x) and Math.Round(x, int). The MidpointRounding overloads share the name (and arg count), so
        // without the arg[1].Type == typeof(int) guard a MidpointRounding value would be misread as digits
        // ({"$round": [x, 1]}) instead of declining. Pinned by MongoExpressionTranslatorMathTests
        // .Math_Round_with_a_MidpointRounding_argument_declines_rather_than_misreading_it_as_digits.
        if (call.Method.Name == nameof(Math.Round)
            && (call.Arguments.Count == 1 || (call.Arguments.Count == 2 && call.Arguments[1].Type == typeof(int))))
        {
            return TryTranslateRoundOrLog(call, allowNumericWidening, call.Arguments.Count == 1 ? MongoMathFunction.Round : MongoMathFunction.RoundDigits, out result);
        }

        if (call.Method.Name == nameof(Math.Log) && call.Arguments.Count is 1 or 2)
            return TryTranslateRoundOrLog(call, allowNumericWidening, call.Arguments.Count == 1 ? MongoMathFunction.Ln : MongoMathFunction.LogNewBase, out result);

        if (call.Arguments.Count == 2 && BinaryFunctionsByName.TryGetValue(call.Method.Name, out var binaryFunction))
        {
            var left = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
            var right = left is null ? null : TranslateOperand(call.Arguments[1], allowNumericWidening: true);
            if (left is null || right is null)
                return false;

            result = new MongoMathExpression(binaryFunction, [left, right], call.Method.ReturnType);
            return true;
        }

        if (call.Arguments.Count != 1 || !UnaryFunctionsByName.TryGetValue(call.Method.Name, out var function))
            return false;

        var operand = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (operand is null)
            return false;

        result = new MongoMathExpression(function, [operand], call.Method.ReturnType);
        return true;
    }

    private bool TryTranslateRoundOrLog(
        MethodCallExpression call, bool allowNumericWidening, MongoMathFunction function,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        var first = TranslateOperand(call.Arguments[0], allowNumericWidening: true);
        if (first is null)
            return false;

        if (call.Arguments.Count == 1)
        {
            result = new MongoMathExpression(function, [first], call.Method.ReturnType);
            return true;
        }

        var second = TranslateOperand(call.Arguments[1], allowNumericWidening: true);
        if (second is null)
            return false;

        result = new MongoMathExpression(function, [first, second], call.Method.ReturnType);
        return true;
    }
}
