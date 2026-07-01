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
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits an expression tree translating various types of binding expressions.
/// </summary>
internal sealed partial class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
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
            case StructuralTypeShaperExpression:
            case MaterializeCollectionNavigationExpression:
                return base.Visit(expression);

#if EF8 || EF9
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
#else
            case QueryParameterExpression queryParameter:
                return Expression.Call(
                    GetParameterValueMethodInfo.MakeGenericMethod(queryParameter.Type),
                    QueryCompilationContext.QueryContextParameter,
                    Expression.Constant(queryParameter.Name));

            case ParameterExpression parameterExpression:
                return _collectionShaperMapping.ContainsKey(parameterExpression)
                    ? parameterExpression
                    : throw new InvalidOperationException(CoreStrings.TranslationFailed(parameterExpression.Print()));
#endif

            case ConstantExpression:
                return expression;

            // g.Key over a grouping: bind against the (already root-rebound) key selector so a scalar key
            // folds to a root member and a composite/anonymous key is walked by VisitNew.
            case MemberExpression { Expression: GroupByShaperExpression groupByShaper, Member.Name: "Key" }:
                return Visit(groupByShaper.KeySelector);

            case MemberExpression memberExpression:
                var currentProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[currentProjectionMember] = memberExpression;

                return new ProjectionBindingExpression(_queryExpression, currentProjectionMember, expression.Type);

            case MethodCallExpression methodCallExpression
                when IsScalarMethodPropertyAccess(methodCallExpression):
                var projMember = GetCurrentProjectionMember();
                _projectionMapping[projMember] = methodCallExpression;

                return new ProjectionBindingExpression(_queryExpression, projMember, expression.Type);

            // An aggregate over a grouping (g.Count(), g.Sum(o => o.X), g.Min/Max/Average/LongCount).
            // A selector-bearing aggregate is lowered to Sum(Select(grouping, sel)), so the grouping is
            // reached by unwrapping the Enumerable/Queryable source chain rather than being Arguments[0].
            // The driver renders the actual accumulator from the captured chain; we only need a scalar
            // projection binding so the shaper reads the aggregated field from the result document.
            case MethodCallExpression aggregateCall when IsGroupByAggregate(aggregateCall):
                var aggMember = GetCurrentProjectionMember();
                _projectionMapping[aggMember] = aggregateCall;

                return new ProjectionBindingExpression(_queryExpression, aggMember, expression.Type);

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
                        (ProjectionBindingExpression)structuralTypeShaperExpression.ValueBufferExpression;

                    EntityProjectionExpression entityProjection;
                    if (projectionBindingExpression.Index is int existingIndex
                        && projectionBindingExpression.QueryExpression == _queryExpression)
                    {
                        // Already bound by index to our query expression (e.g., from join rebinding)
                        entityProjection = (EntityProjectionExpression)_queryExpression.Projection[existingIndex].Expression;
                    }
                    else
                    {
                        entityProjection = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                            projectionBindingExpression.ProjectionMember);
                    }

                    return structuralTypeShaperExpression.Update(
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
                    if (includeExpression.Navigation is not INavigation includableNavigation)
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{
                                nameof(includeExpression.Navigation)
                            }' is not supported.");
                    }

                    if (!includableNavigation.IsEmbedded() && includableNavigation.IsCollection)
                    {
                        var lookup = new LookupExpression(includableNavigation);

                        // For multi-level Include where the declaring entity is a cross-collection
                        // reference (handled by LeftJoin producing _outer/_inner), the $lookup
                        // localField must be prefixed to reference the inner sub-document.
                        // When a LeftJoin restructures the document (_outer/_inner),
                        // $lookup fields must be prefixed with the correct sub-document path.
                        if (_queryExpression.UsesDriverJoinFields)
                        {
                            var declaringType = includableNavigation.DeclaringEntityType;
                            var rootType = _queryExpression.CollectionExpression.EntityType;
                            if (declaringType == rootType || declaringType.IsOwned())
                            {
                                lookup.LocalField = $"_outer.{lookup.LocalField}";
                                lookup.As = $"_outer.{lookup.As}";
                            }
                            else
                            {
                                lookup.LocalField = $"_inner.{lookup.LocalField}";
                                lookup.As = $"_inner.{lookup.As}";
                            }
                        }
                        else
                        {
                            // Flat multi-lookup mode: when two or more cross-collection reference
                            // navigations were chained (e.g. OrderDetail.Order.Customer.Orders), the
                            // reference chain is emitted as a series of root-level $lookup+$unwind
                            // stages aliased "_lookup_<Nav>" rather than the driver's _outer/_inner
                            // shape. A trailing collection Include whose declaring entity is one of
                            // those unwound intermediates must match against that intermediate's
                            // sub-document, so its $lookup localField needs the "_lookup_<Nav>." prefix.
                            // The output "as" is nested under the same intermediate sub-document because
                            // the shaper reads the collection array relative to the intermediate's
                            // ParentAccessExpression (i.e. "_lookup_<Nav>._lookup_<Collection>").
                            var declaringType = includableNavigation.DeclaringEntityType;
                            var intermediateMatches = _queryExpression.GetPendingLookups().Where(
                                l => l.IsReference
                                     && l.ForceUnwind
                                     && l.Navigation.TargetEntityType == declaringType).ToList();

                            // The intermediate is matched by its target entity type, not by its alias. When
                            // more than one reference lookup targets the same entity type — e.g. two reference
                            // navigations to the same type, or a self-referential chain — the match is
                            // ambiguous: there is no basis here to tell which intermediate sub-document this
                            // collection Include is nested under, and choosing arbitrarily would prefix the
                            // $lookup with the wrong "_lookup_<Nav>." path and silently return wrong results.
                            // Fail translation cleanly instead.
                            if (intermediateMatches.Count > 1)
                            {
                                throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                            }

                            var intermediateLookup = intermediateMatches.Count == 1 ? intermediateMatches[0] : null;
                            if (intermediateLookup != null)
                            {
                                lookup.LocalField = $"{intermediateLookup.As}.{lookup.LocalField}";
                                lookup.As = $"{intermediateLookup.As}.{lookup.As}";
                            }
                        }

                        // Extract filtered Include pipeline stages (OrderBy, Skip, Take)
                        // and nested ThenInclude $lookups from the NavigationExpression.
                        ExtractNestedIncludePipeline(includeExpression.NavigationExpression, lookup, includableNavigation.TargetEntityType);
                        _queryExpression.AddLookup(lookup);
                        return RewriteCollectionIncludeForLookup(includeExpression, includableNavigation);
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
        // A projected cross-collection collection navigation (e.g. select new { ..., Orders = c.Orders.ToList() }).
        // EF Core lowers this to Enumerable.ToList(Queryable.Select(Queryable.Where(DbSet<Target>(), joinPred), selector)).
        // There is no enclosing IncludeExpression to set up the $lookup, so bind it here to a CollectionShaperExpression
        // that reads from a dedicated "_lookup_<Nav>" array, mirroring the cross-collection Include path.
        if (TryBindProjectedCollectionNavigation(methodCallExpression, out var boundCollection))
        {
            return boundCollection;
        }

        // A projected cross-collection collection-navigation Count (e.g. select new { ..., c.Orders.Count }).
        // EF Core lowers this to Queryable.Count(Queryable.Where(DbSet<Target>(), joinPred)) with no enclosing
        // IncludeExpression. Register a "_lookup_<Nav>" $lookup (injected right after the root source) and bind
        // the count as a scalar projection; the EF-to-driver translator rewrites the subtree into a server-side
        // { $size: "$_lookup_<Nav>" }.
        if (TryBindProjectedCollectionNavigationCount(methodCallExpression, out var boundCount))
        {
            return boundCount;
        }

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
                navigationProjection = innerEntityProjection.BindMember(memberName, visitedSource.Type, out var propertyBase);
                if (propertyBase is not INavigation projectedNavigation
                    || (!projectedNavigation.IsEmbedded() && !_includedNavigations.Contains(projectedNavigation)))
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
        var newArguments = new Expression[methodCallExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
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
        var hasMembers = newExpression.Members != null;

        var newArguments = new Expression[newExpression.Arguments.Count];
        for (var i = 0; i < newArguments.Length; i++)
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
        var innerExpression = Visit(memberExpression.Expression);

        StructuralTypeShaperExpression shaperExpression;
        switch (innerExpression)
        {
            case StructuralTypeShaperExpression shaper:
                shaperExpression = shaper;
                break;

            case UnaryExpression unaryExpression:
                shaperExpression = unaryExpression.Operand as StructuralTypeShaperExpression;
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

    /// <summary>
    /// Checks whether a method call expression represents a scalar property access that should
    /// be stored in the projection mapping (like <see cref="MemberExpression"/>), rather than
    /// being fully visited. This covers <c>EF.Property</c> (for non-navigation properties) and
    /// <c>Mql.Field</c> calls.
    /// </summary>
    /// <summary>
    /// Whether a method call is a scalar aggregate terminal (<c>Count</c>/<c>LongCount</c>/<c>Sum</c>/
    /// <c>Min</c>/<c>Max</c>/<c>Average</c>) whose source chain roots at a grouping. Selector-bearing
    /// aggregates are lowered to <c>Sum(Select(grouping, sel))</c>, so the grouping is found by unwrapping
    /// the <see cref="Enumerable"/>/<see cref="Queryable"/> source chain.
    /// </summary>
    private static bool IsGroupByAggregate(MethodCallExpression call)
    {
        if (call.Arguments.Count == 0)
        {
            return false;
        }

        switch (call.Method.Name)
        {
            case nameof(Enumerable.Count):
            case nameof(Enumerable.LongCount):
            case nameof(Enumerable.Sum):
            case nameof(Enumerable.Min):
            case nameof(Enumerable.Max):
            case nameof(Enumerable.Average):
                break;
            default:
                return false;
        }

        var source = call.Arguments[0];
        while (true)
        {
            switch (source)
            {
                case GroupByShaperExpression:
                    return true;
                case MethodCallExpression { Arguments.Count: > 0 } inner
                    when inner.Method.DeclaringType == typeof(Enumerable)
                         || inner.Method.DeclaringType == typeof(Queryable):
                    source = inner.Arguments[0];
                    continue;
                default:
                    return false;
            }
        }
    }

    private static bool IsScalarMethodPropertyAccess(MethodCallExpression methodCallExpression)
    {
        if (methodCallExpression.TryGetEFPropertyArguments(out var source, out var memberName))
        {
            if (source is StructuralTypeShaperExpression { StructuralType: IEntityType entityType })
            {
                var navigation = entityType.FindNavigation(memberName);
                // Embedded navigations should be handled by VisitMethodCall
                return navigation == null || !navigation.IsEmbedded();
            }

            return false;
        }

        // Mql.Field<TDoc, TField>() is always a scalar field extraction
        if (methodCallExpression.Method is { Name: "Field", DeclaringType.FullName: "MongoDB.Driver.Mql" })
        {
            return true;
        }

        return false;
    }

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

#if EF8 || EF9
    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.ParameterValues[parameterName];
#else
    private static T GetParameterValue<T>(
        QueryContext queryContext,
        string parameterName)
        => (T)queryContext.Parameters[parameterName];
#endif
}
