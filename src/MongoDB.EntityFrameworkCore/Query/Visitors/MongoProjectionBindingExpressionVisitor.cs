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

            case MemberExpression memberExpression:
                var currentProjectionMember = GetCurrentProjectionMember();
                _projectionMapping[currentProjectionMember] = memberExpression;

                return new ProjectionBindingExpression(_queryExpression, currentProjectionMember, expression.Type);

            case MethodCallExpression methodCallExpression
                when IsScalarMethodPropertyAccess(methodCallExpression):
                var projMember = GetCurrentProjectionMember();
                _projectionMapping[projMember] = methodCallExpression;

                return new ProjectionBindingExpression(_queryExpression, projMember, expression.Type);

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

    /// <summary>
    /// For cross-collection collection Include (e.g., Customer.Include(c => c.Orders)),
    /// build an IncludeExpression with a CollectionShaperExpression that reads from the $lookup array.
    /// The $lookup stage is appended via AppendLookupStages in the LINQ translator.
    /// </summary>
    private Expression RewriteCollectionIncludeForLookup(
        IncludeExpression includeExpression,
        INavigation navigation)
    {
        _includedNavigations.Push(navigation);
        var visitedEntity = Visit(includeExpression.EntityExpression);

        EntityProjectionExpression outerEntityProjection;
        if (visitedEntity is StructuralTypeShaperExpression shaper
            && shaper.ValueBufferExpression is ProjectionBindingExpression binding
            && binding.Index is int idx)
        {
            outerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[idx].Expression;
        }
        else
        {
            _includedNavigations.Pop();
            return base.VisitExtension(includeExpression);
        }

        var lookupAlias = $"_lookup_{navigation.Name}";
        var objectArrayProjection = new ObjectArrayProjectionExpression(
            navigation, outerEntityProjection.ParentAccessExpression, lookupAlias);

        Expression innerShaperExpression = new StructuralTypeShaperExpression(
            navigation.TargetEntityType,
            Expression.Convert(
                Expression.Convert(objectArrayProjection.InnerProjection, typeof(object)),
                typeof(ValueBuffer)),
            nullable: true);

        // For ThenInclude on collection-then-collection paths, wrap the inner shaper
        // with IncludeExpressions for nested collections so the binding remover
        // processes them when iterating each parent document.
        innerShaperExpression = WrapWithNestedCollectionIncludes(
            includeExpression.NavigationExpression, innerShaperExpression, objectArrayProjection);

        var collectionShaper = new CollectionShaperExpression(
            objectArrayProjection,
            innerShaperExpression,
            navigation,
            navigation.TargetEntityType.ClrType);

        _queryExpression.AddToProjection(objectArrayProjection);
        _includedNavigations.Pop();

        return includeExpression.Update(visitedEntity, collectionShaper);
    }

    /// <summary>
    /// Process the NavigationExpression of an Include, extracting:
    /// - Nested ThenInclude $lookups (added as pipeline stages on the parent lookup)
    /// - Filtered Include operations (OrderBy, Skip, Take)
    /// </summary>
    private static void ExtractNestedIncludePipeline(
        Expression navigationExpression,
        LookupExpression parentLookup,
        IEntityType targetEntityType)
    {
        // Unwrap nested IncludeExpressions (ThenInclude on collections)
        while (navigationExpression is IncludeExpression nestedInclude
               && nestedInclude.Navigation is INavigation nestedNav
               && !nestedNav.IsEmbedded() && nestedNav.IsCollection)
        {
            var nestedLookup = new LookupExpression(nestedNav);
            // Recurse for deeper nesting
            ExtractNestedIncludePipeline(nestedInclude.NavigationExpression, nestedLookup, nestedNav.TargetEntityType);

            parentLookup.PipelineStages.Add(new BsonDocument("$lookup", new BsonDocument
            {
                { "from", nestedLookup.From },
                { "localField", nestedLookup.LocalField },
                { "foreignField", nestedLookup.ForeignField },
                { "as", nestedLookup.As }
            }));

            // Continue with the entity expression (which may have more wrapping)
            navigationExpression = nestedInclude.EntityExpression;
        }

        // Extract filtered Include operations and nested ThenIncludes from MaterializeCollectionNavigation subquery
        if (navigationExpression is MaterializeCollectionNavigationExpression materialize)
        {
            var subquery = materialize.Subquery;
            ExtractFilteredIncludePipeline(subquery, parentLookup, targetEntityType);

            // Check for ThenInclude inside Select(source, o => Include(o, nestedNav, ...))
            ExtractThenIncludesFromSubquery(subquery, parentLookup);
        }
    }

    /// <summary>
    /// Look for IncludeExpressions inside a Select lambda of a collection Include subquery.
    /// These represent ThenInclude on collection-then-collection paths.
    /// </summary>
    private static void ExtractThenIncludesFromSubquery(Expression subquery, LookupExpression parentLookup)
    {
        // The subquery is Select(Where(source, pred), o => Include(o, nav, navExpr))
        if (subquery is not MethodCallExpression { Method.Name: "Select" } selectCall)
            return;

        var selectorArg = selectCall.Arguments[1];
        while (selectorArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            selectorArg = quote.Operand;

        if (selectorArg is not LambdaExpression { Body: IncludeExpression includeExpr })
            return;

        // Walk nested IncludeExpressions
        var current = (Expression)includeExpr;
        while (current is IncludeExpression nested
               && nested.Navigation is INavigation nav
               && !nav.IsEmbedded() && nav.IsCollection)
        {
            var nestedLookup = new LookupExpression(nav);

            // Check for deeper nesting inside this ThenInclude's NavigationExpression
            if (nested.NavigationExpression is MaterializeCollectionNavigationExpression innerMaterialize)
            {
                ExtractThenIncludesFromSubquery(innerMaterialize.Subquery, nestedLookup);
                ExtractFilteredIncludePipeline(innerMaterialize.Subquery, nestedLookup, nav.TargetEntityType);
            }

            var nestedLookupDoc = new BsonDocument("$lookup", new BsonDocument
            {
                { "from", nestedLookup.From },
                { "localField", nestedLookup.LocalField },
                { "foreignField", nestedLookup.ForeignField },
                { "as", nestedLookup.As }
            });

            if (nestedLookup.HasPipeline)
            {
                // Use pipeline form for nested lookups with their own stages
                var pipeline = new BsonArray
                {
                    new BsonDocument("$match",
                        new BsonDocument("$expr",
                            new BsonDocument("$eq", new BsonArray { $"${nestedLookup.ForeignField}", "$$localField" })))
                };
                foreach (var stage in nestedLookup.PipelineStages)
                    pipeline.Add(stage);

                nestedLookupDoc = new BsonDocument("$lookup", new BsonDocument
                {
                    { "from", nestedLookup.From },
                    { "let", new BsonDocument("localField", $"${nestedLookup.LocalField}") },
                    { "pipeline", pipeline },
                    { "as", nestedLookup.As }
                });
            }

            parentLookup.PipelineStages.Add(nestedLookupDoc);
            current = nested.EntityExpression;
        }
    }

    /// <summary>
    /// Extract filtered Include operations (OrderBy, Skip, Take) from a subquery expression
    /// and add them as pipeline stages to the LookupExpression.
    /// </summary>
    private static void ExtractFilteredIncludePipeline(
        Expression navigationExpression,
        LookupExpression lookup,
        IEntityType targetEntityType)
    {
        // Walk from the outermost call inward, collecting stages in reverse order.
        var stages = new List<BsonDocument>();
        var current = navigationExpression;

        while (current is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;
            switch (methodName)
            {
                case "OrderBy" or "ThenBy":
                    stages.Add(new BsonDocument("$sort", new BsonDocument(GetSortField(methodCall, targetEntityType), 1)));
                    current = methodCall.Arguments[0];
                    break;

                case "OrderByDescending" or "ThenByDescending":
                    stages.Add(new BsonDocument("$sort", new BsonDocument(GetSortField(methodCall, targetEntityType), -1)));
                    current = methodCall.Arguments[0];
                    break;

                case "Skip":
                    if (methodCall.Arguments[1] is ConstantExpression skipConst)
                    {
                        stages.Add(new BsonDocument("$skip", (int)skipConst.Value!));
                    }
                    current = methodCall.Arguments[0];
                    break;

                case "Take":
                    if (methodCall.Arguments[1] is ConstantExpression takeConst)
                    {
                        stages.Add(new BsonDocument("$limit", (int)takeConst.Value!));
                    }
                    current = methodCall.Arguments[0];
                    break;

                case "Where":
                    // The Where clause is the join condition, already handled by the $lookup $match.
                    current = methodCall.Arguments[0];
                    break;

                default:
                    // Hit the base (EntityQueryRootExpression or similar) — stop.
                    current = null;
                    break;
            }
        }

        // Stages were collected outermost-first; reverse so they execute in the right order.
        stages.Reverse();
        lookup.PipelineStages.AddRange(stages);
    }

    /// <summary>
    /// Extract the sort field name from an OrderBy/OrderByDescending method call.
    /// </summary>
    private static string GetSortField(MethodCallExpression orderByCall, IEntityType entityType)
    {
        // The key selector is the second argument: e.g., o => o.OrderID
        var keySelector = orderByCall.Arguments[1];
        while (keySelector is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            keySelector = quote.Operand;
        }

        if (keySelector is LambdaExpression lambda)
        {
            var body = lambda.Body;
            while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
            {
                body = convert.Operand;
            }

            if (body is MemberExpression memberExpression)
            {
                var property = entityType.FindProperty(memberExpression.Member.Name);
                if (property != null)
                {
                    return Microsoft.EntityFrameworkCore.MongoPropertyExtensions.GetElementName(property);
                }

                return memberExpression.Member.Name;
            }
        }

        return "_id";
    }

    /// <summary>
    /// For ThenInclude on collection-then-collection paths, extract the nested IncludeExpressions
    /// from the subquery's Select lambda and wrap them around the inner shaper so the shaper
    /// reads the nested $lookup arrays from each parent document.
    /// </summary>
    private Expression WrapWithNestedCollectionIncludes(
        Expression navigationExpression,
        Expression innerShaper,
        ObjectArrayProjectionExpression parentArrayProjection)
    {
        if (navigationExpression is not MaterializeCollectionNavigationExpression materialize)
            return innerShaper;

        // Look for Select(source, o => Include(o, nav, navExpr))
        if (materialize.Subquery is not MethodCallExpression { Method.Name: "Select" } selectCall)
            return innerShaper;

        var selectorArg = selectCall.Arguments[1];
        while (selectorArg is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            selectorArg = quote.Operand;

        if (selectorArg is not LambdaExpression lambda)
            return innerShaper;

        // Collect nested IncludeExpressions from the lambda body
        var includes = new List<IncludeExpression>();
        var body = lambda.Body;
        while (body is IncludeExpression include)
        {
            includes.Add(include);
            body = include.EntityExpression;
        }

        if (includes.Count == 0)
            return innerShaper;

        // Wrap the inner shaper with Include expressions (innermost first)
        var result = innerShaper;
        for (var i = includes.Count - 1; i >= 0; i--)
        {
            var include = includes[i];
            if (include.Navigation is INavigation nav && !nav.IsEmbedded() && nav.IsCollection)
            {
                // Build a CollectionShaperExpression for this nested collection Include
                var nestedLookupAlias = $"_lookup_{nav.Name}";
                var nestedArrayProjection = new ObjectArrayProjectionExpression(
                    nav, parentArrayProjection.InnerProjection.ParentAccessExpression, nestedLookupAlias, null);

                var nestedInnerShaper = new StructuralTypeShaperExpression(
                    nav.TargetEntityType,
                    Expression.Convert(
                        Expression.Convert(nestedArrayProjection.InnerProjection, typeof(object)),
                        typeof(ValueBuffer)),
                    nullable: true);

                var nestedCollectionShaper = new CollectionShaperExpression(
                    nestedArrayProjection,
                    nestedInnerShaper,
                    nav,
                    nav.TargetEntityType.ClrType);

                result = new IncludeExpression(result, nestedCollectionShaper, nav, include.SetLoaded);
            }
            else if (include.Navigation is INavigation refNav && !refNav.IsEmbedded())
            {
                // Reference ThenInclude — wrap with IncludeExpression for the reference
                result = new IncludeExpression(result, Visit(include.NavigationExpression), refNav, include.SetLoaded);
            }
        }

        return result;
    }

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
