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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Compiles the shaper expression for a given shaped query expression.
/// </summary>
internal class MongoShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    private readonly Type _contextType;
    private readonly bool _threadSafetyChecksEnabled;

    static readonly MethodInfo __translateAndExecuteQuery = typeof(MongoShapedQueryCompilingExpressionVisitor)
        .GetTypeInfo()
        .DeclaredMethods
        .Single(m => m.Name == "TranslateAndExecuteQuery");

    /// <summary>
    /// Creates a <see cref="MongoShapedQueryCompilingExpressionVisitor"/> with the required dependencies and compilation context.
    /// </summary>
    /// <param name="dependencies">The <see cref="ShapedQueryCompilingExpressionVisitorDependencies"/> used by this compiler.</param>
    /// <param name="queryCompilationContext">The <see cref="MongoQueryCompilationContext"/> gor this specific query.</param>
    public MongoShapedQueryCompilingExpressionVisitor(
        ShapedQueryCompilingExpressionVisitorDependencies dependencies,
        MongoQueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
    {
        _contextType = queryCompilationContext.ContextType;
        _threadSafetyChecksEnabled = dependencies.CoreSingletonOptions.AreThreadSafetyChecksEnabled;
    }

    /// <inheritdoc/>
    protected override Expression VisitShapedQuery(ShapedQueryExpression shapedQueryExpression)
    {
        var shaperBody = shapedQueryExpression.ShaperExpression;
        var shaperParameter = Expression.Parameter(shaperBody.Type, "e");
        var shaperLambda = Expression.Lambda(shaperBody, QueryCompilationContext.QueryContextParameter, shaperParameter);

        var queryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;
        var resultType = DetermineResultType(queryExpression.ShuntedExpression) ?? shaperBody.Type;

        return Expression.Call(null,
            __translateAndExecuteQuery.MakeGenericMethod(shaperBody.Type, resultType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(queryExpression.Collection),
            Expression.Constant(queryExpression),
            Expression.Constant(_contextType),
            Expression.Constant(QueryCompilationContext.QueryTrackingBehavior ==
                                QueryTrackingBehavior.NoTrackingWithIdentityResolution),
            Expression.Constant(_threadSafetyChecksEnabled),
            Expression.Constant(shapedQueryExpression.ResultCardinality));
    }

    private static Type? DetermineResultType(Expression? expression)
    {
        return expression == null
            ? null
            : expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == typeof(IQueryable<>)
                ? expression.Type.GetGenericArguments()[0]
                : expression.Type;
    }

    private static IEnumerable<TResult> TranslateAndExecuteQuery<TDocument, TResult>(
        QueryContext queryContext,
        string collectionName,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        // Console.WriteLine(
        //    $"ExecuteSequence 0x{queryExpression.GetHashCode():x8} 0x{queryContext.GetHashCode():x8}");

        var mongoQueryContext = (MongoQueryContext)queryContext;
        var source = mongoQueryContext.MongoClient.Database.GetCollection<TDocument>(collectionName).AsQueryable();
        var finalExpression = DetermineFinalExpression(queryContext, queryExpression, source);

        // EF wants single items returned in an enumerable but LINQ providers do it differently
        IEnumerable<TResult> serverEnumerable = resultCardinality == ResultCardinality.Enumerable
            ? source.Provider.CreateQuery<TResult>(finalExpression)
            : new[] {source.Provider.Execute<TResult>(finalExpression)};

        return new QueryingEnumerable<TResult>(
            mongoQueryContext,
            serverEnumerable,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled);
    }

    private static Expression DetermineFinalExpression(
        QueryContext queryContext,
        MongoQueryExpression queryExpression,
        IMongoQueryable source)
    {
        if (queryExpression.ShuntedExpression == null) // No LINQ methods, e.g. direct ToList() against DbSet
        {
            return source.Expression;
        }

        var query =
            (MethodCallExpression)new MongoToV3TranslatingEvaluatorExpressionVisitor(queryContext, source.Expression).Visit(
                queryExpression
                    .ShuntedExpression)!;

        return Expression.Call(null, query.Method, query.Arguments);
    }
}
