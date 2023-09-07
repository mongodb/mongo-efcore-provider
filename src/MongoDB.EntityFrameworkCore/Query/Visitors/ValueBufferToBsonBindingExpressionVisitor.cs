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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

#nullable disable

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and the right
/// methods to obtain data instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
internal class ValueBufferToBsonBindingExpressionVisitor : ExpressionVisitor
{
    private readonly Stack<ParameterExpression> _currentParameters = new();

    /// <summary>
    /// Create a <see cref="ValueBufferToBsonBindingExpressionVisitor"/>.
    /// </summary>
    /// <param name="bsonDocParameter">
    /// The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.
    /// </param>
    public ValueBufferToBsonBindingExpressionVisitor(ParameterExpression bsonDocParameter)
    {
        _currentParameters.Push(bsonDocParameter);
    }

    public override Expression Visit(Expression node)
    {
        // We create an intermediary variable between the block and the projection bindings to ensure we can
        // shift them down into the sub-element or array. This code makes sure we get rid of that once the block
        // goes out of scope so the next projection can start again at the parent container.
        if (node is BlockExpression {Expressions: [BinaryExpression {Left: ParameterExpression parameterExpression}, _]})
        {
            var currentParameter = _currentParameters.Peek();
            var visited = base.Visit(node);
            if (currentParameter != parameterExpression && parameterExpression == _currentParameters.Peek())
                _currentParameters.Pop();
            return visited;
        }

        return base.Visit(node);
    }

    /// <summary>
    /// Visits an extension expression to ensure that any <see cref="ProjectionBindingExpression"/> are
    /// correctly bound to the expected result in the <see cref="BsonDocument"/> returned from MongoDB.
    /// </summary>
    /// <param name="extensionExpression">The <see cref="Expression"/> to visit.</param>
    /// <returns>A translated <see cref="Expression"/>.</returns>
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        return extensionExpression switch
        {
            ProjectionBindingExpression projectionBindingExpression
                => ResolveProjectionBindingExpression(projectionBindingExpression, projectionBindingExpression.Type),
            _ => base.VisitExtension(extensionExpression)
        };
    }

    /// <summary>
    /// Visits a <see cref="BinaryExpression"/> replacing empty ProjectionBindingExpressions
    /// while passing through visitation of all others.
    /// </summary>
    /// <param name="binaryExpression">The <see cref="BinaryExpression"/> to visit.</param>
    /// <returns>A <see cref="BinaryExpression"/> with any necessary adjustments.</returns>
    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        if (binaryExpression.NodeType == ExpressionType.Assign && binaryExpression.Left is ParameterExpression parameterExpression)
        {
            if (parameterExpression.Type == typeof(BsonDocument))
            {
                var projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
                if (projectionExpression is ProjectionBindingExpression projectionBindingExpression)
                {
                    var valueExpression = ResolveProjectionBindingExpression(projectionBindingExpression, parameterExpression.Type);
                    _currentParameters.Push(parameterExpression);
                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, valueExpression);
                }
            }

            // Replace empty ProjectionBindingExpression with Empty ValueBuffer.
            if (parameterExpression.Type == typeof(MaterializationContext) &&
                binaryExpression.Right is NewExpression newExpression &&
                newExpression.Arguments[0] is ProjectionBindingExpression)
            {
                var updatedExpression = Expression.New(
                    newExpression.Constructor!,
                    Expression.Constant(ValueBuffer.Empty),
                    newExpression.Arguments[1]);

                return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
            }
        }

        return base.VisitBinary(binaryExpression);
    }

    /// <summary>
    /// Visits a <see cref="MethodCallExpression"/> replacing calls to <see cref="ValueBuffer"/>
    /// with replacement alternatives from <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="Expression"/> to replace the original method call with.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
        if (genericMethod != ExpressionExtensions.ValueBufferTryReadValueMethod)
        {
            return base.VisitMethodCall(methodCallExpression);
        }

        var property = methodCallExpression.Arguments[2].GetConstantValue<IProperty>();
        var resultValue = BsonBinding.CreateGetValueExpression(_currentParameters.Peek(), property);
        return BsonBinding.ConvertTypeIfRequired(resultValue, methodCallExpression.Type);
    }

    private Expression ResolveProjectionBindingExpression(ProjectionBindingExpression projectionBindingExpression, Type type)
    {
        if (projectionBindingExpression.ProjectionMember != null)
        {
            if (projectionBindingExpression.ProjectionMember.Last != null)
            {
                return BsonBinding.CreateGetValueExpression(_currentParameters.Peek(),
                    projectionBindingExpression.ProjectionMember.Last?.Name!,
                    type);
            }

            return _currentParameters.Peek();
        }

        throw new NotSupportedException("Unknown ProjectionBindingExpression only ProjectionMember supported.");
    }
}
