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
/// A binary comparison, logical or arithmetic operation. <see cref="MongoBinaryOperator.AndAlso"/> and
/// <see cref="MongoBinaryOperator.OrElse"/> are typed <see cref="bool"/>; every other operator takes the left
/// operand's type.
/// </summary>
internal sealed class MongoBinaryExpression : MongoExpression
{
    public MongoBinaryExpression(MongoBinaryOperator op, MongoExpression left, MongoExpression right)
    {
        Operator = op;
        Left = left;
        Right = right;
    }

    public MongoBinaryOperator Operator { get; }

    public MongoExpression Left { get; }

    public MongoExpression Right { get; }

    /// <inheritdoc />
    public override Type Type
        => Operator is MongoBinaryOperator.AndAlso or MongoBinaryOperator.OrElse
            or MongoBinaryOperator.Equal or MongoBinaryOperator.NotEqual
            or MongoBinaryOperator.LessThan or MongoBinaryOperator.LessThanOrEqual
            or MongoBinaryOperator.GreaterThan or MongoBinaryOperator.GreaterThanOrEqual
            ? typeof(bool)
            : Left.Type;

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
