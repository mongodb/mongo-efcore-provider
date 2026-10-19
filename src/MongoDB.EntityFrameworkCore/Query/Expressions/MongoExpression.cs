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
using System.Linq.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Abstract base class for all dialect-agnostic MongoDB query expression nodes, used by the native translator,
/// lowerer, and renderers.
/// </summary>
/// <remarks>
/// <see cref="VisitChildren"/> is a no-op identity; consumers walk the tree with hand-written <c>switch</c>es rather
/// than visitors.
/// </remarks>
internal abstract class MongoExpression : Expression
{
    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
        => this;
}

/// <summary>
/// Binary comparison and logical operators for <see cref="MongoBinaryExpression"/>.
/// </summary>
internal enum MongoBinaryOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    AndAlso,
    OrElse,
    Add,
    Subtract,
    Multiply,
    Divide,

    /// <summary>
    /// C# integer division: <c>$divide</c> wrapped in <c>$trunc</c>. MongoDB's <c>$divide</c> always yields a
    /// <c>double</c>, which would compare wrongly and fail to deserialize into an integral member.
    /// </summary>
    /// <remarks>
    /// Chosen at translate time from the division node's CLR type, not at render time from the operands: widening
    /// <c>Convert</c>s (<c>(double)a / b</c>) are unwrapped by then, so the operands would look integral.
    /// </remarks>
    IntegerDivide,

    Modulo
}

/// <summary>
/// Unary operators for <see cref="MongoUnaryExpression"/>.
/// </summary>
internal enum MongoUnaryOperator
{
    Not
}
