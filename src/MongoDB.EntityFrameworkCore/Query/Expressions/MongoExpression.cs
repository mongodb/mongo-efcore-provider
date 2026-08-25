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
/// Abstract base class for all dialect-agnostic MongoDB query expression nodes.
/// Subclasses of this type plug into the EF Core <see cref="ExpressionVisitor"/> machinery
/// and are used by the native translator, lowerer, and renderer.
/// </summary>
/// <remarks>
/// Deriving from <see cref="System.Linq.Expressions.Expression"/> (and the <see cref="VisitChildren"/> /
/// <c>Update</c> plumbing) is forward-looking: it leaves room for visitor-driven transforms / pushdown over
/// these nodes. That machinery is <em>not yet exercised at parity</em> — the renderer walks the tree with a
/// hand-written <c>switch</c> rather than via a visitor, and <see cref="VisitChildren"/> is a no-op identity.
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
    /// C# integer division: <c>$divide</c> wrapped in <c>$trunc</c>. MongoDB has no integer-division operator —
    /// <c>$divide</c> over two integral operands yields a <c>double</c> — so a bare <c>Divide</c> would both
    /// answer the wrong value for a comparison and fail to deserialize into an integral projection member.
    /// </summary>
    /// <remarks>
    /// The choice between this and <see cref="Divide"/> is made at TRANSLATE time, from the CLR type of the division
    /// node — not at render time
    /// from the operands' types. That is deliberate: an operand's widening <c>Convert</c> (e.g.
    /// <c>(double)a / b</c>) is unwrapped by <c>TranslateOperand</c>, so by render time both operands look
    /// integral even though C# computed in <c>double</c>. Only the original node's type distinguishes them.
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
