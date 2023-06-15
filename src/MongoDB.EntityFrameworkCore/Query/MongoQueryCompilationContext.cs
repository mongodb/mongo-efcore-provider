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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query;

/// <inheritdoc />
public class MongoQueryCompilationContext : QueryCompilationContext
{
    private readonly ExpressionPrinter _expressionPrinter;

    /// <summary>
    /// Create a new <see cref="MongoQueryCompilationContext"/>.
    /// </summary>
    /// <param name="dependencies">The <see cref="QueryContextDependencies"/> required by this service.</param>
    /// <param name="async">Whether this is an asynchronous query or not.</param>
    public MongoQueryCompilationContext(
        QueryCompilationContextDependencies dependencies,
        bool async)
        : base(dependencies, async)
    {
        _expressionPrinter = new ExpressionPrinter();
    }

    /// <inheritdoc />
    public override Func<QueryContext, TResult> CreateQueryExecutor<TResult>(Expression query)
    {
        query = Dependencies.QueryTranslationPreprocessorFactory.Create(this).Process(query);
        query = Dependencies.QueryableMethodTranslatingExpressionVisitorFactory.Create(this).Visit(query);
        query = Dependencies.QueryTranslationPostprocessorFactory.Create(this).Process(query);

        query = Dependencies.ShapedQueryCompilingExpressionVisitorFactory.Create(this).Visit(query);

        var queryExecutorExpression = Expression.Lambda<Func<QueryContext, TResult>>(
            query,
            QueryContextParameter);

        try
        {
            return queryExecutorExpression.Compile();
        }
        finally
        {
            Logger.QueryExecutionPlanned(Dependencies.Context, _expressionPrinter, queryExecutorExpression);
        }
    }
}
