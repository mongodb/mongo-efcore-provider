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
/// Represents a binary comparison or logical operation in a MongoDB query expression tree.
/// Logical operators (<see cref="MongoBinaryOperator.AndAlso"/>, <see cref="MongoBinaryOperator.OrElse"/>)
/// always produce <see cref="bool"/>; comparison operators take their type from the left operand.
/// </summary>
internal sealed class MongoBinaryExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoBinaryExpression"/>.
    /// </summary>
    /// <param name="op">The binary operator.</param>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public MongoBinaryExpression(MongoBinaryOperator op, MongoExpression left, MongoExpression right)
    {
        Operator = op;
        Left = left;
        Right = right;
    }

    /// <summary>The binary operator.</summary>
    public MongoBinaryOperator Operator { get; }

    /// <summary>The left operand.</summary>
    public MongoExpression Left { get; }

    /// <summary>The right operand.</summary>
    public MongoExpression Right { get; }

    /// <inheritdoc />
    public override Type Type
        => Operator is MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse
            ? typeof(bool)
            : Left.Type;

    /// <summary>
    /// Returns a new <see cref="MongoBinaryExpression"/> if either operand changed;
    /// otherwise returns <see langword="this"/>.
    /// </summary>
    public MongoBinaryExpression Update(MongoExpression left, MongoExpression right)
        => ReferenceEquals(left, Left) && ReferenceEquals(right, Right)
            ? this
            : new MongoBinaryExpression(Operator, left, right);

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var newLeft = (MongoExpression)visitor.Visit(Left);
        var newRight = (MongoExpression)visitor.Visit(Right);
        return Update(newLeft, newRight);
    }
}
