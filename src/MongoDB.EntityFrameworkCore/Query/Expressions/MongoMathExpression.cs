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
/// Every <c>Math</c>/<c>MathF</c> function (plus <c>double.RadiansToDegrees</c>/<c>float.DegreesToRadians</c>)
/// this provider translates natively. One node type keyed by this enum, not one sealed type per function —
/// see <see cref="MongoMathExpression"/>'s own remarks.
/// </summary>
internal enum MongoMathFunction
{
    Abs, Ceiling, Floor, Exp, Sqrt, Truncate,
    Round, RoundDigits,
    Ln, Log10, Log2, LogNewBase,
    DegreesToRadians, RadiansToDegrees,
    Acos, Acosh, Asin, Asinh, Atan, Atanh, Cos, Cosh, Sin, Sinh, Tan, Tanh,
    Pow, Atan2, Max, Min,
    Sign
}

/// <summary>
/// A <c>Math</c>/<c>MathF</c> function call, rendered exclusively in the aggregation-expression dialect (no
/// query-dialect form — a numeric function over a field can never be a <c>$match</c> key-value shape).
/// </summary>
/// <remarks>
/// One node type for every function, discriminated by <see cref="Function"/> — mirrors
/// <c>MongoRegexExpression</c>'s <c>Kind</c> discriminator, not <c>MongoDateAddExpression</c>'s one-type-per-
/// concept split, because every function here shares the exact same shape (1 or 2 <see cref="MongoExpression"/>
/// operands in, one MQL operator or small fixed expression out) and a per-function type would be 30 sealed
/// classes differing only in a rendered string.
/// <para>
/// <see cref="Type"/> is passed in explicitly by the translator (<c>call.Method.ReturnType</c>), not inferred
/// from an operand — unlike <c>MongoDateAddExpression</c> where the operand's own type IS the result type,
/// most of these functions change CLR type from their operand (e.g. <c>Math.Sign(double) : int</c>).
/// </para>
/// </remarks>
internal sealed class MongoMathExpression(
    MongoMathFunction function, IReadOnlyList<MongoExpression> operands, Type type)
    : MongoExpression
{
    /// <summary>Which function this call represents.</summary>
    public MongoMathFunction Function { get; } = function;

    /// <summary>
    /// The function's arguments, already translated. 1 element for a unary function (<c>Abs</c>, trig, etc.),
    /// 2 for a binary one (<c>Pow</c>, <c>Atan2</c>, <c>Max</c>, <c>Min</c>), 1 or 2 for <c>Round</c>
    /// (<see cref="MongoMathFunction.Round"/> vs <see cref="MongoMathFunction.RoundDigits"/>) and <c>Log</c>
    /// (<see cref="MongoMathFunction.Ln"/> vs <see cref="MongoMathFunction.LogNewBase"/>).
    /// </summary>
    public IReadOnlyList<MongoExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override Type Type { get; } = type;
}
