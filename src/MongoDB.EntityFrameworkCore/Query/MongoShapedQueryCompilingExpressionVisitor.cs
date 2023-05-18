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
internal class MongoShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
{
    static readonly MethodInfo __executeQuery = typeof(MongoShapedQueryCompilingExpressionVisitor).GetTypeInfo().DeclaredMethods
        .Single(m => m.Name == "ExecuteQuery");

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
        // 1. The "server" query that needs to be translated in QueryExpression
        // 2. The "shaper" that turns query results back into CLR types in ShaperExpression
        // 3. An indication as to whether this is a sequence, a single item or a single/default item in ResultCardinality
        // It wants back an expression it can execute multiple times giving a new QueryContext each time.
        // This expects a fully translated query back - we can't do that if we want
        // to push through the existing LINQ3 provider without major rework so instead we'll:
        // 1. Visit the expression to capture and map any necessary model information (TODO)
        // 2. Create a BSON class map with that information (TODO)
        // 3. Ensure our call that we return can evaluate the querycontext (done)
        // 4. Perform any additional pre and post processing to fit the LINQ3 provider in
        // 5. Execute the shaper over the top of each result (TODO)

        var shaperBody = (EntityShaperExpression)shapedQueryExpression.ShaperExpression;
        // var shaperParameter = Expression.Parameter(typeof(string), "s");
        // var shaperLambda = Expression.Lambda(shaperBody, QueryCompilationContext.QueryContextParameter, shaperParameter);

        var queryExpression = (MongoQueryExpression)shapedQueryExpression.QueryExpression;

        string collectionName = queryExpression.Collection;

        return Expression.Call(null, __executeQuery.MakeGenericMethod(shaperBody.Type),
            QueryCompilationContext.QueryContextParameter,
            Expression.Constant(collectionName),
            Expression.Constant(queryExpression), Expression.Constant(shapedQueryExpression.ResultCardinality));
    }

    private static IEnumerable<T> ExecuteQuery<T>(
        QueryContext queryContext,
        string collectionName,
        MongoQueryExpression queryExpression,
        ResultCardinality resultCardinality)
    {
        // Console.WriteLine(
        //     $"ExecuteSequence {queryExpression.GetHashCode():x8} {queryContext.GetHashCode():x8}");

        var client = ((MongoQueryContext)queryContext).MongoClient;
        var source = client.Database.GetCollection<T>(collectionName).AsQueryable();
        var provider = source.Provider;

        var query =
            (MethodCallExpression)new MongoToV3TranslatingEvaluatorExpressionVisitor(queryContext, source.Expression).Visit(
                queryExpression
                    .ShuntedExpression)!;

        var mappedCallExpression = Expression.Call(null, query.Method, query.Arguments);

        // EF wants single items returned in an enumerable but LINQ providers do it differently
        return resultCardinality == ResultCardinality.Enumerable
            ? provider.CreateQuery<T>(mappedCallExpression)
            : new[] {provider.Execute<T>(mappedCallExpression)};
    }
}
