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
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

#nullable disable

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits an expression tree translating various types of binding expressions.
/// </summary>
internal sealed class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    private readonly Dictionary<ProjectionMember, Expression> _projectionMapping = new();
    private readonly Stack<ProjectionMember> _projectionMembers = new();
    private readonly Dictionary<ParameterExpression, CollectionShaperExpression> _collectionShaperMapping = new();
    private readonly Stack<INavigation> _includedNavigations = new();

    private MongoQueryExpression _queryExpression;

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
        _projectionMembers.Push(new ProjectionMember());

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

            case NewExpression:
            case MemberInitExpression:
            case EntityShaperExpression:
            case MaterializeCollectionNavigationExpression:
                return base.Visit(expression);

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

            case MemberExpression memberExpression:
                var currentProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[currentProjectionMember] = memberExpression;

                return new ProjectionBindingExpression(_queryExpression, currentProjectionMember, expression.Type.MakeNullable());

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
                    var projectionBindingExpression = (ProjectionBindingExpression)entityShaperExpression.ValueBufferExpression;

                    var entityProjection = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                        projectionBindingExpression.ProjectionMember);

                    return entityShaperExpression.Update(
                        new ProjectionBindingExpression(
                            _queryExpression, _queryExpression.AddToProjection(entityProjection), typeof(ValueBuffer)));
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
                            $"Including navigation '{nameof(includeExpression.Navigation)}' is not supported as the navigation is not embedded in same resource.");
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
        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out string memberName))
        {
            var visitedSource = Visit(source);

            EntityShaperExpression shaperExpression;
            switch (visitedSource)
            {
                case EntityShaperExpression shaper:
                    shaperExpression = shaper;
                    break;

                case UnaryExpression unaryExpression:
                    shaperExpression = unaryExpression.Operand as EntityShaperExpression;
                    if (shaperExpression == null
                        || unaryExpression.NodeType != ExpressionType.Convert)
                    {
                        return null;
                    }

                    break;

                case ParameterExpression parameterExpression:
                    if (!_collectionShaperMapping.TryGetValue(parameterExpression, out var collectionShaper))
                    {
                        return null;
                    }

                    shaperExpression = (EntityShaperExpression)collectionShaper.InnerShaper;
                    break;

                default:
                    return null;
            }

            EntityProjectionExpression innerEntityProjection;
            switch (shaperExpression.ValueBufferExpression)
            {
                case ProjectionBindingExpression innerProjectionBindingExpression:
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
                navigationProjection = innerEntityProjection.BindMember(
                    memberName, visitedSource.Type, out var propertyBase);

                if (!(propertyBase is INavigation projectedNavigation)
                    || !projectedNavigation.IsEmbedded())
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
                    return new EntityShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                        nullable: true);

                case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                    {
                        var innerShaperExpression = new EntityShaperExpression(
                            navigation.TargetEntityType,
                            Expression.Convert(
                                Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                                typeof(ValueBuffer)),
                            nullable: true);

                        return new CollectionShaperExpression(
                            objectArrayProjectionExpression,
                            innerShaperExpression,
                            navigation,
                            innerShaperExpression.EntityType.ClrType);
                    }

                default:
                    throw new InvalidOperationException(CoreStrings.TranslationFailed(methodCallExpression.Print()));
            }
        }

        var newObject = Visit(methodCallExpression.Object);
        var newArguments = new Expression[methodCallExpression.Arguments.Count];
        for (int i = 0; i < newArguments.Length; i++)
        {
            var argument = methodCallExpression.Arguments[i];
            var newArgument = Visit(argument);
            newArguments[i] = MatchTypes(newArgument, argument.Type);
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
                EnterProjectionMember(newExpression.Members[i]);
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
        EnterProjectionMember(memberAssignment.Member);
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
        for (int i = 0; i < newBindings.Length; i++)
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
        var innerExpression = Visit(memberExpression.Expression);

        EntityShaperExpression shaperExpression;
        switch (innerExpression)
        {
            case EntityShaperExpression shaper:
                shaperExpression = shaper;
                break;

            case UnaryExpression unaryExpression:
                shaperExpression = unaryExpression.Operand as EntityShaperExpression;
                if (shaperExpression == null
                    || unaryExpression.NodeType != ExpressionType.Convert)
                {
                    return NullSafeUpdate(innerExpression);
                }

                break;

            default:
                return NullSafeUpdate(innerExpression);
        }

        EntityProjectionExpression innerEntityProjection;
        switch (shaperExpression.ValueBufferExpression)
        {
            case ProjectionBindingExpression innerProjectionBindingExpression:
                innerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[
                    innerProjectionBindingExpression.Index.Value].Expression;
                break;

            case UnaryExpression unaryExpression:
                // Unwrap EntityProjectionExpression when the root entity is not projected
                innerEntityProjection = (EntityProjectionExpression)((UnaryExpression)unaryExpression.Operand).Operand;
                break;

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(memberExpression.Print()));
        }

        var navigationProjection = innerEntityProjection.BindMember(
            memberExpression.Member, innerExpression.Type, out var propertyBase);

        if (propertyBase is not INavigation navigation || !navigation.IsEmbedded())
        {
            return NullSafeUpdate(innerExpression);
        }

        switch (navigationProjection)
        {
            case EntityProjectionExpression entityProjection:
                return new EntityShaperExpression(
                    navigation.TargetEntityType,
                    Expression.Convert(Expression.Convert(entityProjection, typeof(object)), typeof(ValueBuffer)),
                    nullable: true);

            case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                {
                    var innerShaperExpression = new EntityShaperExpression(
                        navigation.TargetEntityType,
                        Expression.Convert(
                            Expression.Convert(objectArrayProjectionExpression.InnerProjection, typeof(object)),
                            typeof(ValueBuffer)),
                        nullable: true);

                    return new CollectionShaperExpression(
                        objectArrayProjectionExpression,
                        innerShaperExpression,
                        navigation,
                        innerShaperExpression.EntityType.ClrType);
                }

            default:
                throw new InvalidOperationException(CoreStrings.TranslationFailed(memberExpression.Print()));
        }

        Expression NullSafeUpdate(Expression expression)
        {
            Expression updatedMemberExpression = memberExpression.Update(
                expression != null ? MatchTypes(expression, memberExpression.Expression.Type) : expression);

            if (expression?.Type.IsNullableType() == true)
            {
                var nullableReturnType = memberExpression.Type.MakeNullable();
                if (!memberExpression.Type.IsNullableType())
                {
                    updatedMemberExpression = Expression.Convert(updatedMemberExpression, nullableReturnType);
                }

                updatedMemberExpression = Expression.Condition(
                    Expression.Equal(expression, Expression.Default(expression.Type)),
                    Expression.Constant(null, nullableReturnType),
                    updatedMemberExpression);
            }

            return updatedMemberExpression;
        }
    }


    /// <inheritdoc />
    protected override ElementInit VisitElementInit(ElementInit elementInit)
        => elementInit.Update(elementInit.Arguments.Select(e => MatchTypes(Visit(e), e.Type)));

    /// <inheritdoc />
    protected override Expression VisitNewArray(NewArrayExpression newArrayExpression)
        => newArrayExpression.Update(newArrayExpression.Expressions.Select(e => MatchTypes(Visit(e), e.Type)));

    private ProjectionMember GetCurrentProjectionMember()
        => _projectionMembers.Peek();

    private void EnterProjectionMember(MemberInfo memberInfo)
        => _projectionMembers.Push(_projectionMembers.Peek().Append(memberInfo));

    private void ExitProjectionMember()
        => _projectionMembers.Pop();

    private static Expression MatchTypes(
        Expression expression,
        Type targetType)
        => targetType != expression.Type && targetType.TryGetItemType() == null
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
