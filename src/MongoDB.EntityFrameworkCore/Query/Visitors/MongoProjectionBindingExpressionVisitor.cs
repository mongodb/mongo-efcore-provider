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

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits an expression tree translating various types of binding expressions.
/// </summary>
internal sealed class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    private readonly Dictionary<MongoProjectionMember, Expression> _projectionMapping = new();
    private readonly Stack<MongoProjectionMember> _projectionMembers = new();
    private readonly Dictionary<ParameterExpression, CollectionShaperExpression> _collectionShaperMapping = new();
    private readonly Stack<INavigation> _includedNavigations = new();

    private MongoQueryExpression _queryExpression;
    private int _currentOrdinal = -1;
    private Type _currentType;

    /// <summary>
    /// Perform translation of the <paramref name="expression" /> that belongs to the
    /// supplied <paramref name="queryExpression"/>.
    /// </summary>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> the expression being translated belongs to.</param>
    /// <param name="expression">The <see cref="Expression"/> being translated.</param>
    /// <returns>The translated expression tree.</returns>
    public Expression Translate(
        MongoQueryExpression queryExpression,
        Expression expression)
    {
        _queryExpression = queryExpression;
        _projectionMembers.Push(new MongoProjectionMember());

        var result = Visit(expression);

        _queryExpression.ReplaceProjectionMapping(_projectionMapping);
        _projectionMapping.Clear();
        _queryExpression = null;

        _projectionMembers.Clear();

        return MatchTypes(result, expression.Type);
    }

    /// <inheritdoc />
    public override Expression Visit(Expression expression)
    {
        switch (expression)
        {
            case null:
                return null;

            case ParameterExpression parameterExpression:
                if (_collectionShaperMapping.ContainsKey(parameterExpression))
                {
                    return parameterExpression;
                }

                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    return Expression.Call(
                        GetParameterValueMethodInfo.MakeGenericMethod(parameterExpression.Type),
                        QueryCompilationContext.QueryContextParameter,
                        Expression.Constant(parameterExpression.Name));
                }

                throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));

            case ConstantExpression:
                return expression;

            default:
                return base.Visit(expression);
        }
    }

    /// <inheritdoc />
    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case StructuralTypeShaperExpression structuralTypeShaperExpression:
                {
                    var projectionBindingExpression =
                        (MongoProjectionBindingExpression)structuralTypeShaperExpression.ValueBufferExpression;

                    var entityProjection = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                        projectionBindingExpression.ProjectionMember);

                    return structuralTypeShaperExpression.Update(
                        new MongoProjectionBindingExpression(
                            _queryExpression, _queryExpression.AddToProjection(entityProjection), typeof(ValueBuffer), typeof(ValueBuffer)));
                }

            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                return materializeCollectionNavigationExpression.Navigation is INavigation embeddableNavigation
                       && embeddableNavigation.IsEmbedded()
                    ? base.Visit(materializeCollectionNavigationExpression.Subquery)
                    : base.VisitExtension(materializeCollectionNavigationExpression);

            case IncludeExpression includeExpression:
                {
                    if (!(includeExpression.Navigation is INavigation includableNavigation && includableNavigation.IsEmbedded()))
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{
                                nameof(includeExpression.Navigation)
                            }' is not supported as the navigation is not embedded in same resource.");
                    }

                    _includedNavigations.Push(includableNavigation);
                    var newIncludeExpression = base.VisitExtension(includeExpression);
                    _includedNavigations.Pop();
                    return newIncludeExpression;
                }
            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
        }
    }

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            var visitedSource = Visit(source);

            StructuralTypeShaperExpression shaperExpression;
            switch (visitedSource)
            {
                case StructuralTypeShaperExpression shaper:
                    shaperExpression = shaper;
                    break;

                case UnaryExpression unaryExpression:
                    shaperExpression = unaryExpression.Operand as StructuralTypeShaperExpression;
                    if (shaperExpression == null || unaryExpression.NodeType != ExpressionType.Convert)
                    {
                        return null;
                    }

                    break;

                case ParameterExpression parameterExpression:
                    if (!_collectionShaperMapping.TryGetValue(parameterExpression, out var collectionShaper))
                    {
                        return null;
                    }

                    shaperExpression = (StructuralTypeShaperExpression)collectionShaper.InnerShaper;
                    break;

                default:
                    return null;
            }

            EntityProjectionExpression innerEntityProjection;
            switch (shaperExpression.ValueBufferExpression)
            {
                case MongoProjectionBindingExpression innerProjectionBindingExpression:
                    innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                        innerProjectionBindingExpression.Index.Value].Expression;
                    break;

                case UnaryExpression unaryExpression:
                    innerEntityProjection = (EntityProjectionExpression)((UnaryExpression)unaryExpression.Operand).Operand;
                    break;

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }

            Expression navigationProjection;
            var navigation = _includedNavigations.FirstOrDefault(n => n.Name == memberName);
            if (navigation == null)
            {
                navigationProjection = innerEntityProjection.BindMember(memberName, visitedSource.Type, out var propertyBase);
                if (propertyBase is not INavigation projectedNavigation || !projectedNavigation.IsEmbedded())
                {
                    return null;
                }

                navigation = projectedNavigation;
            }
            else
            {
                navigationProjection = innerEntityProjection.BindNavigation(navigation);
            }

            switch (navigationProjection)
            {
                case EntityProjectionExpression entityProjection:
                    return new StructuralTypeShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                        nullable: true);

                case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                    {
                        var innerShaperExpression = new StructuralTypeShaperExpression(
                            navigation.TargetEntityType,
                            Expression.Convert(
                                Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                                typeof(ValueBuffer)),
                            nullable: true);

                        return new CollectionShaperExpression(
                            objectArrayProjectionExpression,
                            innerShaperExpression,
                            navigation,
                            innerShaperExpression.StructuralType.ClrType);
                    }

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }
        }

        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(Queryable))
        {
            var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
            var visitedSource = Visit(methodCallExpression.Arguments[0]);

            switch (method.Name)
            {
                case nameof(Queryable.AsQueryable)
                    when genericMethod == QueryableMethods.AsQueryable:
                    // Unwrap AsQueryable
                    return visitedSource;

                case nameof(Queryable.Select)
                    when genericMethod == QueryableMethods.Select:
                    if (visitedSource is not CollectionShaperExpression shaper)
                    {
                        return null;
                    }

                    var lambda = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();

                    _collectionShaperMapping.Add(lambda.Parameters.Single(), shaper);

                    lambda = Expression.Lambda(Visit(lambda.Body), lambda.Parameters);
                    return Expression.Call(
                        EnumerableMethods.Select.MakeGenericMethod(method.GetGenericArguments()),
                        shaper,
                        lambda);
            }
        }

        var newObject = Visit(methodCallExpression.Object);

        var argumentsCount = methodCallExpression.Arguments.Count;
        var methodParameters = methodCallExpression.Method.GetParameters();
        var parameterizeArguments = methodCallExpression.Method.IsGenericMethod
                                    && methodCallExpression.Method.GetGenericMethodDefinition() ==
                                    typeof(Tuple).GetMethods().Single(e => e.Name == nameof(Tuple.Create)
                                                                           && e.GetParameters().Length == argumentsCount);

        var oldOrdinal = _currentOrdinal;
        _currentOrdinal = -1;

        try
        {
            var newArguments = new Expression[argumentsCount];
            for (var i = 0; i < newArguments.Length; i++)
            {
                var argument = methodCallExpression.Arguments[i];

                if (parameterizeArguments)
                {
                    _currentOrdinal++;
                    _currentType = argument.Type;

                    EnterProjectionMember(methodParameters[i].Name);
                    try
                    {
                        newArguments[i] = MatchTypes(Visit(argument), argument.Type);
                    }
                    finally
                    {
                        ExitProjectionMember();
                    }
                }
                else
                {
                    var newArgument = Visit(argument);
                    newArguments[i] = MatchTypes(newArgument, argument.Type);
                }
            }

            Expression updatedMethodCallExpression = methodCallExpression.Update(
                newObject != null ? MatchTypes(newObject, methodCallExpression.Object?.Type) : null,
                newArguments);

            if (newObject?.Type.IsNullableType() == true && !methodCallExpression.Object.Type.IsNullableType())
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
        finally
        {
            _currentOrdinal = oldOrdinal;
        }
    }

    /// <inheritdoc />
    protected override Expression VisitNew(NewExpression newExpression)
    {
        if (newExpression.Arguments.Count == 0) return newExpression;
        var hasMembers = newExpression.Members != null;

        var newArguments = new Expression[newExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
        {
            var argument = newExpression.Arguments[i];

            if (hasMembers)
            {
                EnterProjectionMember(newExpression.Members[i].Name);
            }

            var visitedArgument = Visit(argument);

            if (hasMembers)
            {
                ExitProjectionMember();
            }

            if (visitedArgument == null)
            {
                return null!;
            }

            newArguments[i] = MatchTypes(visitedArgument, argument.Type);
        }

        return newExpression.Update(newArguments);
    }

    protected override MemberAssignment VisitMemberAssignment(MemberAssignment memberAssignment)
    {
        EnterProjectionMember(memberAssignment.Member.Name);
        var visitedExpression = Visit(memberAssignment.Expression);
        ExitProjectionMember();

        if (visitedExpression == null)
        {
            return null!;
        }

        return memberAssignment.Update(MatchTypes(visitedExpression, memberAssignment.Expression.Type));
    }

    /// <inheritdoc />
    protected override Expression VisitMemberInit(MemberInitExpression memberInitExpression)
    {
        var newExpression = Visit(memberInitExpression.NewExpression);
        if (newExpression == null)
        {
            return null!;
        }

        var newBindings = new MemberBinding[memberInitExpression.Bindings.Count];
        for (var i = 0; i < newBindings.Length; i++)
        {
            if (memberInitExpression.Bindings[i].BindingType != MemberBindingType.Assignment)
            {
                return null!;
            }

            newBindings[i] = VisitMemberBinding(memberInitExpression.Bindings[i]);

            if (newBindings[i] == null)
            {
                return null!;
            }
        }

        return memberInitExpression.Update((NewExpression)newExpression, newBindings);
    }

    protected override Expression VisitMember(MemberExpression memberExpression)
    {
        var currentProjectionMember = GetCurrentProjectionMember();
        _projectionMapping[currentProjectionMember] = memberExpression;

        return _currentOrdinal >= 0
            ? new MongoProjectionBindingExpression(_queryExpression, _currentOrdinal, memberExpression.Type, _currentType!)
            : new MongoProjectionBindingExpression(_queryExpression, currentProjectionMember, memberExpression.Type);
    }

    /// <inheritdoc />
    protected override ElementInit VisitElementInit(ElementInit elementInit)
        => elementInit.Update(elementInit.Arguments.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <inheritdoc />
    protected override Expression VisitNewArray(NewArrayExpression newArrayExpression)
        => newArrayExpression.Update(newArrayExpression.Expressions.Select(e => MatchTypes(Visit(e), e.Type)));

    private MongoProjectionMember GetCurrentProjectionMember()
        => _projectionMembers.Peek();

    private void EnterProjectionMember(string projectionName)
        => _projectionMembers.Push(_projectionMembers.Peek().Append(projectionName));

    private void ExitProjectionMember()
        => _projectionMembers.Pop();

    private static Expression MatchTypes(
        Expression expression,
        Type targetType)
        => expression == null
            ? Expression.Default(targetType)
            : targetType != expression.Type && targetType.TryGetItemType() == null
                ? Expression.Convert(expression, targetType)
                : expression;

    private static readonly MethodInfo GetParameterValueMethodInfo
        = typeof(MongoProjectionBindingExpressionVisitor)
            .GetTypeInfo().GetDeclaredMethod(nameof(GetParameterValue));

    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.ParameterValues[parameterName];
}
