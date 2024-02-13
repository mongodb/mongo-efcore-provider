﻿/* Copyright 2023-present MongoDB Inc.
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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Captures the final query expression in the chain so it can be run against the MongoDB LINQ v3 provider while also
/// following the shape of the transformation so that the shaper may be correctly adjusted.
/// </summary>
internal sealed class MongoQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
{
    private readonly MongoProjectionBindingExpressionVisitor _projectionBindingExpressionVisitor = new();

    private Expression? _finalExpression;

    /// <summary>
    /// Create a <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryableMethodTranslatingExpressionVisitorDependencies"/> this visitor depends upon.</param>
    /// <param name="queryCompilationContext">The <see cref="MongoQueryCompilationContext"/> this visitor should use to correctly translate the expressions.</param>
    public MongoQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        MongoQueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext, subquery: false)
    {
    }

    /// <inheritdoc />
    public override Expression? Visit(Expression? expression)
    {
        _finalExpression ??= expression;
        return base.Visit(expression);
    }

    /// <summary>
    /// Visit the <see cref="MethodCallExpression"/> to capture the cardinality and final expression
    /// when found on a <see cref="Queryable"/> method.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="ShapedQueryExpression"/> if this method was on a <see cref="Queryable"/>,
    /// otherwise <see cref="QueryCompilationContext.NotTranslatedExpression"/>.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(Queryable))
        {
            var source = Visit(methodCallExpression.Arguments[0]);
            if (source is ShapedQueryExpression shapedQueryExpression)
            {
                var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;

                var visitedShapedQuery = shapedQueryExpression;
                switch (method.Name)
                {
                    case nameof(Queryable.Select) when genericMethod == QueryableMethods.Select:
                        visitedShapedQuery = (ShapedQueryExpression)base.VisitMethodCall(methodCallExpression);
                        break;
                    case nameof(Queryable.Count) when genericMethod == QueryableMethods.CountWithoutPredicate:
                        visitedShapedQuery = (ShapedQueryExpression)base.VisitMethodCall(methodCallExpression);
                        break;
                    case nameof(Queryable.LongCount) when genericMethod == QueryableMethods.LongCountWithoutPredicate:
                        visitedShapedQuery = (ShapedQueryExpression)base.VisitMethodCall(methodCallExpression);
                        break;
                    case nameof(Queryable.Any) when genericMethod == QueryableMethods.AnyWithoutPredicate:
                        visitedShapedQuery = (ShapedQueryExpression)base.VisitMethodCall(methodCallExpression);
                        break;
                }

                var newCardinality = GetResultCardinality(method);
                if (newCardinality != visitedShapedQuery.ResultCardinality)
                    visitedShapedQuery = visitedShapedQuery.UpdateResultCardinality(newCardinality);

                ((MongoQueryExpression)visitedShapedQuery.QueryExpression).CapturedExpression = _finalExpression;
                return visitedShapedQuery;
            }
        }

        return QueryCompilationContext.NotTranslatedExpression;
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression TranslateSelect(
        ShapedQueryExpression source,
        LambdaExpression selector)
    {
        // Handle .Select(p => p) no-op/pass-thru
        if (selector.Body == selector.Parameters[0])
        {
            return source;
        }

        var mongoQueryExpression = (MongoQueryExpression)source.QueryExpression;
        var newSelectorBody =
            ReplacingExpressionVisitor.Replace(selector.Parameters.Single(), source.ShaperExpression, selector.Body);
        var newShaper = _projectionBindingExpressionVisitor.Translate(mongoQueryExpression, newSelectorBody);

        return source.UpdateShaperExpression(newShaper);
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression TranslateCount(
        ShapedQueryExpression source,
        LambdaExpression? predicate)
    {
        return source.UpdateShaperExpression(
            Expression.Convert(
                new ProjectionBindingExpression(source.QueryExpression, new ProjectionMember(), typeof(int?)),
                typeof(int)));
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression TranslateLongCount(
        ShapedQueryExpression source,
        LambdaExpression? predicate)
    {
        return source.UpdateShaperExpression(
            Expression.Convert(
                new ProjectionBindingExpression(source.QueryExpression, new ProjectionMember(), typeof(long?)),
                typeof(long)));
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression TranslateAny(
        ShapedQueryExpression source,
        LambdaExpression? predicate)
    {
        return source.UpdateShaperExpression(
            Expression.Convert(
                new ProjectionBindingExpression(source.QueryExpression, new ProjectionMember(), typeof(bool?)),
                typeof(bool)));
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression TranslateAll(ShapedQueryExpression source, LambdaExpression predicate) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
    {
        var queryExpression = new MongoQueryExpression(entityType);
        return new ShapedQueryExpression(
            queryExpression,
            shaperExpression: new StructuralTypeShaperExpression(
                entityType,
                new ProjectionBindingExpression(queryExpression, new ProjectionMember(), typeof(ValueBuffer)),
                false));
    }

    private static ResultCardinality GetResultCardinality(MethodInfo method)
    {
        var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;
        switch (method.Name)
        {
            // Singles
            case nameof(Queryable.All)
                when genericMethod == QueryableMethods.All:
            case nameof(Queryable.Any)
                when genericMethod == QueryableMethods.AnyWithoutPredicate:
            case nameof(Queryable.Any)
                when genericMethod == QueryableMethods.AnyWithPredicate:
            case nameof(Queryable.Average)
                when QueryableMethods.IsAverageWithoutSelector(method) || QueryableMethods.IsAverageWithSelector(method):
            case nameof(Queryable.Contains)
                when genericMethod == QueryableMethods.Contains:
            case nameof(Queryable.Count)
                when genericMethod == QueryableMethods.CountWithoutPredicate:
            case nameof(Queryable.Count)
                when genericMethod == QueryableMethods.CountWithPredicate:
            case nameof(Queryable.ElementAt)
                when genericMethod == QueryableMethods.ElementAt:
            case nameof(Queryable.First)
                when genericMethod == QueryableMethods.FirstWithoutPredicate ||
                     genericMethod == QueryableMethods.FirstWithPredicate:
            case nameof(Queryable.Last)
                when genericMethod == QueryableMethods.LastWithoutPredicate ||
                     genericMethod == QueryableMethods.LastWithPredicate:
            case nameof(Queryable.LongCount)
                when genericMethod == QueryableMethods.LongCountWithoutPredicate ||
                     genericMethod == QueryableMethods.LongCountWithPredicate:
            case nameof(Queryable.Max)
                when genericMethod == QueryableMethods.MaxWithoutSelector || genericMethod == QueryableMethods.MaxWithSelector:
            case nameof(Queryable.Min)
                when genericMethod == QueryableMethods.MinWithoutSelector || genericMethod == QueryableMethods.MinWithSelector:
            case nameof(Queryable.Single)
                when genericMethod == QueryableMethods.SingleWithoutPredicate ||
                     genericMethod == QueryableMethods.SingleWithPredicate:
            case nameof(Queryable.Sum)
                when QueryableMethods.IsSumWithoutSelector(method) || QueryableMethods.IsSumWithSelector(method):

                return ResultCardinality.Single;

            // Single or defaults
            case nameof(Queryable.ElementAtOrDefault)
                when genericMethod == QueryableMethods.ElementAtOrDefault:
            case nameof(Queryable.FirstOrDefault)
                when genericMethod == QueryableMethods.FirstOrDefaultWithoutPredicate ||
                     genericMethod == QueryableMethods.FirstOrDefaultWithPredicate:
            case nameof(Queryable.LastOrDefault)
                when genericMethod == QueryableMethods.LastOrDefaultWithoutPredicate ||
                     genericMethod == QueryableMethods.LastOrDefaultWithPredicate:
            case nameof(Queryable.SingleOrDefault)
                when genericMethod == QueryableMethods.SingleOrDefaultWithoutPredicate ||
                     genericMethod == QueryableMethods.SingleWithoutPredicate:

                return ResultCardinality.SingleOrDefault;
        }

        return ResultCardinality.Enumerable;
    }

    #region Not implemented as we're capturing the query rather than translating here

    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor() =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateAverage(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateCast(ShapedQueryExpression source, Type castType) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateConcat(ShapedQueryExpression source1, ShapedQueryExpression source2) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateContains(ShapedQueryExpression source, Expression item) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateDefaultIfEmpty(ShapedQueryExpression source, Expression? defaultValue) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateDistinct(ShapedQueryExpression source) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateElementAtOrDefault(ShapedQueryExpression source, Expression index,
        bool returnDefault) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateExcept(ShapedQueryExpression source1, ShapedQueryExpression source2) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateFirstOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType,
        bool returnDefault) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateGroupBy(ShapedQueryExpression source, LambdaExpression keySelector,
        LambdaExpression? elementSelector, LambdaExpression? resultSelector) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateGroupJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateIntersect(ShapedQueryExpression source1, ShapedQueryExpression source2) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector,
        LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateLeftJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateLastOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType,
        bool returnDefault) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateMax(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateMin(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateOfType(ShapedQueryExpression source, Type resultType) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateOrderBy(ShapedQueryExpression source, LambdaExpression keySelector,
        bool ascending) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateReverse(ShapedQueryExpression source) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateSelectMany(ShapedQueryExpression source, LambdaExpression collectionSelector,
        LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateSelectMany(ShapedQueryExpression source, LambdaExpression selector) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateSingleOrDefault(ShapedQueryExpression source, LambdaExpression? predicate,
        Type returnType,
        bool returnDefault) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateSkip(ShapedQueryExpression source, Expression count) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateSkipWhile(ShapedQueryExpression source, LambdaExpression predicate) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateSum(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    protected override ShapedQueryExpression? TranslateTake(ShapedQueryExpression source, Expression count) => null;

    protected override ShapedQueryExpression TranslateTakeWhile(ShapedQueryExpression source, LambdaExpression predicate) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateThenBy(ShapedQueryExpression source, LambdaExpression keySelector,
        bool ascending) => throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateUnion(ShapedQueryExpression source1, ShapedQueryExpression source2) =>
        throw new NotImplementedException();

    protected override ShapedQueryExpression TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate) =>
        throw new NotSupportedException();

    #endregion
}
