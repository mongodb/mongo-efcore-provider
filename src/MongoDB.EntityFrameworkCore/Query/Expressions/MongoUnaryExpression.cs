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
/// Represents a unary operation in a MongoDB query expression tree.
/// Currently only <see cref="MongoUnaryOperator.Not"/> is supported.
/// </summary>
internal sealed class MongoUnaryExpression : MongoExpression
{
    /// <summary>
    /// Creates a <see cref="MongoUnaryExpression"/>.
    /// </summary>
    /// <param name="op">The unary operator.</param>
    /// <param name="operand">The operand expression.</param>
    public MongoUnaryExpression(MongoUnaryOperator op, MongoExpression operand)
    {
        Operator = op;
        Operand = operand;
    }

    /// <summary>The unary operator.</summary>
    public MongoUnaryOperator Operator { get; }

    /// <summary>The operand expression.</summary>
    public MongoExpression Operand { get; }

    /// <inheritdoc />
    public override Type Type
        => typeof(bool);

    /// <summary>
    /// Returns a new <see cref="MongoUnaryExpression"/> if the operand changed;
    /// otherwise returns <see langword="this"/>.
    /// </summary>
    public MongoUnaryExpression Update(MongoExpression operand)
        => ReferenceEquals(operand, Operand)
            ? this
            : new MongoUnaryExpression(Operator, operand);

    /// <inheritdoc />
    protected override Expression VisitChildren(ExpressionVisitor visitor)
    {
        var newOperand = (MongoExpression)visitor.Visit(Operand);
        return Update(newOperand);
    }
}
