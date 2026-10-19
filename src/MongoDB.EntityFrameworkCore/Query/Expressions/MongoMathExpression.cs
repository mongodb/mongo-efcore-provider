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
/// this provider translates natively.
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
/// One node type discriminated by <see cref="Function"/>: every function has the same shape (1–2 operands, one
/// operator out), so per-function types would differ only in a rendered string. <see cref="Type"/> is passed in
/// (<c>call.Method.ReturnType</c>) because many functions change CLR type (e.g. <c>Math.Sign(double) : int</c>).
/// </remarks>
internal sealed class MongoMathExpression(
    MongoMathFunction function, IReadOnlyList<MongoExpression> operands, Type type)
    : MongoExpression
{
    public MongoMathFunction Function { get; } = function;

    /// <summary>
    /// The translated arguments: one for unary functions, two for <c>Pow</c>/<c>Atan2</c>/<c>Max</c>/<c>Min</c>,
    /// <c>RoundDigits</c> and <c>LogNewBase</c>.
    /// </summary>
    public IReadOnlyList<MongoExpression> Operands { get; } = operands;

    /// <inheritdoc />
    public override Type Type { get; } = type;
}
