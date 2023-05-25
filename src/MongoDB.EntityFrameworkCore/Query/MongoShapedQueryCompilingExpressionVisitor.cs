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
        .Single(m => m.Name == "ExecuteQuery");

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
        // TODO: Support shapers once we can deal with BSON directly
        var shaperBody = shapedQueryExpression.ShaperExpression;

        // TODO: Translate, compile and pass shaperLambda
        var queryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;
        var resultType = DetermineResultType(queryExpression.ShuntedExpression) ?? shaperBody.Type;
        var standAloneStateManager = QueryCompilationContext.QueryTrackingBehavior ==
                                     QueryTrackingBehavior.NoTrackingWithIdentityResolution;

        return Expression.Call(null,
            __translateAndExecuteQuery.MakeGenericMethod(shaperBody.Type, resultType),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(queryExpression),
            Expression.Constant(_contextType),
            Expression.Constant(standAloneStateManager),
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

    private static IEnumerable<TResult> ExecuteQuery<TDocument, TResult>(
        QueryContext queryContext,
        MongoQueryExpression queryExpression,
        Type contextType,
        bool standAloneStateManager,
        bool threadSafetyChecksEnabled,
        ResultCardinality resultCardinality)
    {
        // TODO: Make this method non-generic by getting the LINQ provider in a non-generic way
        var mongoQueryContext = (MongoQueryContext)queryContext;
        var source = mongoQueryContext.MongoClient.Database.GetCollection<TDocument>(queryExpression.Collection).AsQueryable();
        var retargetedExpression = RetargetQueryableExpression(queryContext, queryExpression, source);

        // TODO: Figure out how to do a real shaper here or bypass it entirely and use QueryContext.StartTracking
        TResult FakeShaper(QueryContext qc, TResult t) => t;

        if (resultCardinality != ResultCardinality.Enumerable)
            return new[] {FakeShaper(queryContext, source.Provider.Execute<TResult>(retargetedExpression))};

        return new QueryingEnumerable<TResult>(
            mongoQueryContext,
            source.Provider.CreateQuery<TResult>(retargetedExpression),
            FakeShaper,
            contextType,
            standAloneStateManager,
            threadSafetyChecksEnabled);
    }

    private static Expression RetargetQueryableExpression(
        QueryContext queryContext,
        MongoQueryExpression queryExpression,
        IMongoQueryable source)
    {
        if (queryExpression.ShuntedExpression == null) // No LINQ methods, e.g. Direct ToList() against DbSet
        {
            return source.Expression;
        }

        var query =
            (MethodCallExpression)new MongoToLinqTranslatingExpressionVisitor(queryContext, source.Expression).Visit(
                queryExpression
                    .ShuntedExpression)!;

        return Expression.Call(null, query.Method, query.Arguments);
    }
}
