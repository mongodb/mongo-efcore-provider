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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Compiles the shaper expression for a given shaped query expression.
/// </summary>
public class MongoShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    static readonly MethodInfo __executeSequence = typeof(MongoShapedQueryCompilingExpressionVisitor).GetTypeInfo().DeclaredMethods
        .Single(m => m.Name == "ExecuteSequence");

    static readonly MethodInfo __executeSingle = typeof(MongoShapedQueryCompilingExpressionVisitor).GetTypeInfo().DeclaredMethods
        .Single(m => m.Name == "ExecuteSingle");

    /// <summary>
    /// Creates a <see cref="MongoShapedQueryCompilingExpressionVisitor"/> with the required dependencies and compilation context.
    /// </summary>
    /// <param name="dependencies">The <see cref="ShapedQueryCompilingExpressionVisitorDependencies"/> used by this compiler.</param>
    /// <param name="queryCompilationContext">The <see cref="QueryCompilationContext"/> gor this specific query.</param>
    public MongoShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
    {
    }

    /// <inheritdoc/>
    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        // The ShapedQueryExpression supplied gives us three important things
        // 1. The actual query that needs to be translated in QueryExpression
        // 2. The "shaper" that turns query results back into CLR types in ShaperExpression
        // 3. An indication as to whether this is a sequence, a single item or a single/default item in ResultCardinality

        var shaperParameter = Expression.Parameter(typeof(string), "s");
        var shaperBody = (EntityShaperExpression)shapedQueryExpression.ShaperExpression;
        var shaperLambda = Expression.Lambda(shaperBody, QueryCompilationContext.QueryContextParameter, shaperParameter);

        var queryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;

        string collectionName = queryExpression.Collection;

        switch (shapedQueryExpression.ResultCardinality)
        {
            case ResultCardinality.Enumerable:
                {
                    return Expression.Call(null, __executeSequence.MakeGenericMethod(shaperBody.Type),
                        QueryCompilationContext.QueryContextParameter,
                        Expression.Constant(collectionName),
                        Expression.Constant(queryExpression));
                }
            case ResultCardinality.Single:
            case ResultCardinality.SingleOrDefault:
                {
                    return Expression.Call(null, __executeSingle.MakeGenericMethod(shaperBody.Type),
                        QueryCompilationContext.QueryContextParameter,
                        Expression.Constant(collectionName),
                        Expression.Constant(queryExpression));
                }
            default:
                throw new NotSupportedException($"Unknown Shaper ResultCardinality of {shapedQueryExpression.ResultCardinality}");
        }
    }

    private static IEnumerable<T> ExecuteSequence<T>(
        QueryContext queryContext,
        string collectionName,
        MongoQueryExpression queryExpression)
    {
        var client = ((MongoQueryContext)queryContext).MongoClient;
        IQueryable<T> queryable = client.Database.GetCollection<T>(collectionName).AsQueryable();

        if (queryExpression.Limit is ParameterExpression takeParameter)
        {
            var takeMethod = QueryableMethods.Take.MakeGenericMethod(typeof(T));
            var limit = Expression.Constant(
                Expression.Lambda(queryExpression.Limit).Compile().DynamicInvoke(queryContext, takeParameter.Name),
                queryExpression.Limit.Type);
            queryable = queryable.Provider.CreateQuery<T>(
                Expression.Call(
                    null,
                    takeMethod,
                    queryable.Expression, limit
                ));
        }

        return queryable;
    }

    private static T? ExecuteSingle<T>(
        QueryContext queryContext,
        string collectionName,
        MongoQueryExpression queryExpression)
    {
        var client = ((MongoQueryContext)queryContext).MongoClient;
        return client.Database.GetCollection<T>(collectionName).AsQueryable().FirstOrDefault();
    }
}
