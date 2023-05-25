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
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Visits the tree resolving any query context parameter bindings and EF references so the query can be used with the MongoDB V3 LINQ provider.
/// </summary>
internal class MongoToLinqTranslatingExpressionVisitor : ExpressionVisitor
{
    private readonly QueryContext _queryContext;
    private readonly Expression _source;

    internal MongoToLinqTranslatingExpressionVisitor(QueryContext queryContext, Expression source)
    {
        _queryContext = queryContext;
        _source = source;
    }

    public override Expression? Visit(Expression? expression)
    {
        switch (expression)
        {
            // Replace the QueryContext parameter values with constant values.
            case ParameterExpression parameterExpression:

                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    return Expression.Constant(_queryContext.ParameterValues[parameterExpression.Name]);
                }

                break;

            // Replace the root with the supplied MongoDB LINQ V3 provider source.
            case EntityQueryRootExpression:
                return _source;
        }

        return base.Visit(expression);
    }
}
