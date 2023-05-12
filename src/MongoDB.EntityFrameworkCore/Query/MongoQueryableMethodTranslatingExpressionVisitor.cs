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
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// MongoDB-specific expression visitor for translating queryable methods.
/// </summary>
internal class MongoQueryableMethodTranslatingExpressionVisitor : QueryableMethodTranslatingExpressionVisitor
{
    private readonly MongoExpressionTranslatingExpressionVisitor _expressionTranslator;

    /// <summary>
    /// Creates a <see cref="MongoQueryableMethodTranslatingExpressionVisitor"/> with the given
    /// dependencies and query compilation context.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryableMethodTranslatingExpressionVisitorDependencies"/> this visitor depends upon.</param>
    /// <param name="queryCompilationContext">The <see cref="QueryCompilationContext"/> this visitor should use to correctly translate the expressions.</param>
    public MongoQueryableMethodTranslatingExpressionVisitor(
        QueryableMethodTranslatingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext, subquery: false)
    {
        _expressionTranslator = new MongoExpressionTranslatingExpressionVisitor(queryCompilationContext, this);
    }

    /// <inheritdoc />
    protected override QueryableMethodTranslatingExpressionVisitor CreateSubqueryVisitor() =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType)
        => CreateShapedQueryExpression(entityType, new MongoQueryExpression(entityType));

    private static ShapedQueryExpression CreateShapedQueryExpression(IEntityType entityType, Expression queryExpression) =>
        new ShapedQueryExpression(
            queryExpression,
            new EntityShaperExpression(
                entityType,
                new ProjectionBindingExpression(queryExpression, new ProjectionMember(), typeof(ValueBuffer)),
                false));

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateAll(ShapedQueryExpression source, LambdaExpression predicate) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateAny(ShapedQueryExpression source, LambdaExpression? predicate) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateAverage(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateCast(ShapedQueryExpression source, Type castType) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateConcat(ShapedQueryExpression source1,
        ShapedQueryExpression source2) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateContains(ShapedQueryExpression source, Expression item) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression?
        TranslateCount(ShapedQueryExpression source, LambdaExpression? predicate) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateDefaultIfEmpty(ShapedQueryExpression source,
        Expression? defaultValue) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateDistinct(ShapedQueryExpression source) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateElementAtOrDefault(ShapedQueryExpression source,
        Expression index, bool returnDefault) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateExcept(ShapedQueryExpression source1,
        ShapedQueryExpression source2) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateFirstOrDefault(ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType, bool returnDefault)
    {
        // TODO: Handle predicate
        // TODO: Apply limit 1

        return source.ShaperExpression.Type != returnType
            ? source.UpdateShaperExpression(Expression.Convert(source.ShaperExpression, returnType))
            : source;
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateGroupBy(ShapedQueryExpression source,
        LambdaExpression keySelector,
        LambdaExpression? elementSelector, LambdaExpression? resultSelector) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateGroupJoin(ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateIntersect(ShapedQueryExpression source1,
        ShapedQueryExpression source2) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateJoin(ShapedQueryExpression outer, ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateLeftJoin(ShapedQueryExpression outer,
        ShapedQueryExpression inner,
        LambdaExpression outerKeySelector, LambdaExpression innerKeySelector, LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateLastOrDefault(ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType, bool returnDefault) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateLongCount(ShapedQueryExpression source,
        LambdaExpression? predicate) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateMax(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateMin(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateOfType(ShapedQueryExpression source, Type resultType) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateOrderBy(ShapedQueryExpression source,
        LambdaExpression keySelector, bool ascending) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateReverse(ShapedQueryExpression source) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression TranslateSelect(ShapedQueryExpression source, LambdaExpression selector) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateSelectMany(ShapedQueryExpression source,
        LambdaExpression collectionSelector,
        LambdaExpression resultSelector) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateSelectMany(ShapedQueryExpression source,
        LambdaExpression selector) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateSingleOrDefault(ShapedQueryExpression source,
        LambdaExpression? predicate,
        Type returnType, bool returnDefault) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateSkip(ShapedQueryExpression source, Expression count) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateSkipWhile(ShapedQueryExpression source,
        LambdaExpression predicate) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateSum(ShapedQueryExpression source, LambdaExpression? selector,
        Type resultType) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateTake(ShapedQueryExpression source, Expression count)
    {
        var queryExpression = (MongoQueryExpression)source.QueryExpression;
        var newCount = TranslateExpression(count);
        if (newCount == null)
        {
            return null;
        }

        queryExpression.ApplyLimit(newCount);
        return source;
    }

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateTakeWhile(ShapedQueryExpression source,
        LambdaExpression predicate) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateThenBy(ShapedQueryExpression source,
        LambdaExpression keySelector, bool ascending) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateUnion(ShapedQueryExpression source1,
        ShapedQueryExpression source2) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override ShapedQueryExpression? TranslateWhere(ShapedQueryExpression source, LambdaExpression predicate)
    {
        ((MongoQueryExpression)source.QueryExpression).ApplyPredicate(predicate);
        return source;
    }

    private Expression? TranslateExpression(Expression expression)
    {
        return _expressionTranslator.Translate(expression);
    }
}
