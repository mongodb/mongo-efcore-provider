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
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Query.Expressions;

#nullable disable

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits an expression tree translating various types of binding expressions.
/// </summary>
internal sealed class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    private readonly Stack<ProjectionMember> _projectionMembers = new();

    private MongoQueryExpression _queryExpression;
    private int _bindingIndex;

    /// <summary>
    /// Perform translation of the <paramref name="expression" /> that belongs to the
    /// supplied <paramref name="queryExpression"/>.
    /// </summary>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> the expression being translated belongs to.</param>
    /// <param name="expression">The <see cref="Expression"/> being translated.</param>
    /// <returns>The translated expression tree.</returns>
    public Expression Translate(MongoQueryExpression queryExpression, Expression expression)
    {
        _bindingIndex = 0;
        _queryExpression = queryExpression;
        _projectionMembers.Push(new ProjectionMember());

        var result = Visit(expression);

        _projectionMembers.Clear();
        _queryExpression = null;
        _bindingIndex = 0;

        return MatchTypes(result, expression.Type);
    }

    /// <inheritdoc />
    public override Expression Visit(Expression expression)
    {
        switch (expression)
        {
            case null:
                return null;
            case MemberExpression {Expression: EntityShaperExpression}:
                {
                    var projectionMember = _projectionMembers.Peek();
                    return projectionMember.Last != null ?
                        // Name based projection mapping, follow projected name
                        new ProjectionBindingExpression(_queryExpression, projectionMember, expression.Type) :
                        // Reference to field - access via index and _v container
                        new ProjectionBindingExpression(_queryExpression, _bindingIndex++, expression.Type);
                }
            default:
                return base.Visit(expression);
        }
    }

    /// <inheritdoc />
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case EntityShaperExpression entityShaperExpression:
                {
                    var projectionMember = _projectionMembers.Peek();
                    var projectionBindingExpression = projectionMember.Last != null ?
                        // Name based projection mapping, follow projected name
                        new ProjectionBindingExpression(_queryExpression, projectionMember, typeof(ValueBuffer)) :
                        // Reference to field - access via index and _v container
                        new ProjectionBindingExpression(_queryExpression, _bindingIndex++, typeof(ValueBuffer));
                    return entityShaperExpression.Update(projectionBindingExpression);
                }

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
        }
    }

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var newObject = Visit(methodCallExpression.Object);
        var newArguments = new Expression[methodCallExpression.Arguments.Count];
        for (int i = 0; i < newArguments.Length; i++)
        {
            var argument = methodCallExpression.Arguments[i];
            var newArgument = Visit(argument);
            newArguments[i] = MatchTypes(newArgument, argument.Type);
        }

        Expression updatedMethodCallExpression = methodCallExpression.Update(
            newObject != null ? MatchTypes(newObject, methodCallExpression.Object?.Type) : newObject,
            newArguments);

        if (newObject?.Type.IsNullableType() == true
            && !methodCallExpression.Object.Type.IsNullableType())
        {
            var nullableReturnType = methodCallExpression.Type.MakeNullable();
            if (!methodCallExpression.Type.IsNullableType())
            {
                updatedMethodCallExpression = Expression.Convert(updatedMethodCallExpression, nullableReturnType);
            }

            return Expression.Condition(
                Expression.Equal(newObject, Expression.Default(newObject.Type)),
                Expression.Constant(null, nullableReturnType),
                updatedMethodCallExpression);
        }

        return updatedMethodCallExpression;
    }

    /// <inheritdoc />
    protected override Expression VisitNew(NewExpression newExpression)
    {
        if (newExpression.Arguments.Count == 0) return newExpression;
        bool hasMembers = newExpression.Members != null;

        var newArguments = new Expression[newExpression.Arguments.Count];
        for (int i = 0; i < newArguments.Length; i++)
        {
            var argument = newExpression.Arguments[i];

            if (hasMembers)
            {
                var projectionMember = _projectionMembers.Peek().Append(newExpression.Members[i]);
                _projectionMembers.Push(projectionMember);
            }

            var visitedArgument = Visit(argument);
            if (visitedArgument == null)
            {
                return null!;
            }

            if (hasMembers)
            {
                _projectionMembers.Pop();
            }

            newArguments[i] = MatchTypes(visitedArgument, argument.Type);
        }

        return newExpression.Update(newArguments);
    }

    /// <inheritdoc />
    protected override ElementInit VisitElementInit(ElementInit elementInit)
        => elementInit.Update(elementInit.Arguments.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <inheritdoc />
    protected override Expression VisitNewArray(NewArrayExpression newArrayExpression)
        => newArrayExpression.Update(newArrayExpression.Expressions.Select(e => MatchTypes(Visit(e), e.Type)));

    protected override Expression VisitMemberInit(MemberInitExpression memberInitExpression)
        => GetConstantOrNull(memberInitExpression);

    private static Expression MatchTypes(Expression expression, Type targetType) =>
        targetType != expression.Type && targetType.TryGetItemType() == null
            ? Expression.Convert(expression, targetType)
            : expression;

    private static ConstantExpression GetConstantOrNull(Expression expression)
        => CanEvaluate(expression)
            ? Expression.Constant(
                Expression.Lambda<Func<object>>(Expression.Convert(expression, typeof(object)))
                    .Compile(preferInterpretation: true)
                    .Invoke(),
                expression.Type)
            : null;

    private static bool CanEvaluate(Expression expression) =>
        expression switch
        {
            ConstantExpression => true,
            NewExpression newExpression => newExpression.Arguments.All(CanEvaluate),
            MemberInitExpression memberInitExpression => CanEvaluate(memberInitExpression.NewExpression) &&
                                                         memberInitExpression.Bindings.All(mb =>
                                                             mb is MemberAssignment memberAssignment &&
                                                             CanEvaluate(memberAssignment.Expression)),
            _ => false
        };
}
