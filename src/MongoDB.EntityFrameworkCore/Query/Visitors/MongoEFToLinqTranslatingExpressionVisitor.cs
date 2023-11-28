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
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits the tree resolving any query context parameter bindings and EF references so the query can be used with the MongoDB V3 LINQ provider.
/// </summary>
internal sealed class MongoEFToLinqTranslatingExpressionVisitor : ExpressionVisitor
{
    private readonly QueryContext _queryContext;
    private readonly Expression _source;

    internal MongoEFToLinqTranslatingExpressionVisitor(
        QueryContext queryContext,
        Expression source)
    {
        _queryContext = queryContext;
        _source = source;
    }

    public MethodCallExpression Translate(
        Expression? efQueryExpression,
        ResultCardinality resultCardinality)
    {
        if (efQueryExpression == null) // No LINQ methods, e.g. Direct ToList() against DbSet
        {
            return InjectAsBsonDocumentMethod(_source, BsonDocumentSerializer.Instance);
        }

        var query = (MethodCallExpression)Visit(efQueryExpression)!;

        if (resultCardinality == ResultCardinality.Enumerable)
        {
            return InjectAsBsonDocumentMethod(query, BsonDocumentSerializer.Instance);
        }

        var documentQueryableSource = InjectAsBsonDocumentMethod(query.Arguments[0], BsonDocumentSerializer.Instance);

        return Expression.Call(
            null,
            query.Method.GetGenericMethodDefinition().MakeGenericMethod(typeof(BsonDocument)),
            documentQueryableSource);
    }

    private static MethodCallExpression InjectAsBsonDocumentMethod(
        Expression query,
        BsonDocumentSerializer resultSerializer)
    {
        var asMethodInfo = __asMethodInfo.MakeGenericMethod(query.Type.GenericTypeArguments[0], typeof(BsonDocument));
        var cast = Expression.Convert(query, typeof(IMongoQueryable<>).MakeGenericType(query.Type.GenericTypeArguments[0]));
        var serializerExpression = Expression.Constant(resultSerializer, resultSerializer.GetType());

        return Expression.Call(
            null,
            asMethodInfo,
            cast,
            serializerExpression
        );
    }

    public override Expression? Visit(Expression? expression)
    {
        switch (expression)
        {
            // Replace the QueryContext parameter values with constant values for this execution.
            case ParameterExpression parameterExpression:
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal) == true)
                {
                    return ConvertIfRequired(Expression.Constant(_queryContext.ParameterValues[parameterExpression.Name]), expression.Type);
                }

                break;

            // Unwrap include expressions.
            case IncludeExpression includeExpression:
                return Visit(includeExpression.EntityExpression);

            // Replace the root with the MongoDB LINQ V3 provider source.
            case EntityQueryRootExpression:
                return _source;
        }

        return base.Visit(expression);
    }

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        switch (methodCallExpression)
        {
            // Replace EF-generated Property(p, "propName") with p.propName.
            case not null when methodCallExpression.Method.IsEFPropertyMethod()
                               && methodCallExpression.Arguments[1] is ConstantExpression propertyNameExpression:
                var source = methodCallExpression.Arguments[0];
                string propertyName = propertyNameExpression.GetConstantValue<string>();
                var property = source.Type.GetProperties().First(prop => prop.Name == propertyName);
                return ConvertIfRequired(Expression.Property(source, property), methodCallExpression.Method.ReturnType);
        }

        return base.VisitMethodCall(methodCallExpression!);
    }

    private static Expression ConvertIfRequired(Expression expression, Type targetType) =>
        expression.Type == targetType ? expression : Expression.Convert(expression, targetType);

    private static readonly MethodInfo __asMethodInfo = typeof(MongoQueryable)
        .GetMethods()
        .First(mi => mi is { Name: nameof(MongoQueryable.As), IsPublic: true, IsStatic: true } && mi.GetParameters().Length == 2);
}
