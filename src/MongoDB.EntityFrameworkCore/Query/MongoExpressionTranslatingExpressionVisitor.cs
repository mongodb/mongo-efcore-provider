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
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query;

internal class MongoExpressionTranslatingExpressionVisitor : ExpressionVisitor
{
    private readonly QueryCompilationContext _queryCompilationContext;
    private readonly QueryableMethodTranslatingExpressionVisitor _queryableMethodTranslatingExpressionVisitor;
    private readonly IModel _model;

    public MongoExpressionTranslatingExpressionVisitor(
        QueryCompilationContext queryCompilationContext,
        QueryableMethodTranslatingExpressionVisitor queryableMethodTranslatingExpressionVisitor)
    {
        _queryCompilationContext = queryCompilationContext;
        _queryableMethodTranslatingExpressionVisitor = queryableMethodTranslatingExpressionVisitor;
        _model = queryCompilationContext.Model;
    }

    public virtual Expression? Translate(Expression expression)
    {
        return Visit(expression);
    }

    protected override Expression VisitParameter(ParameterExpression parameterExpression)
    {
        if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal) == true)
        {
            return Expression.Call(
                __getParameterValueMethodInfo.MakeGenericMethod(parameterExpression.Type),
                QueryCompilationContext.QueryContextParameter,
                Expression.Constant(parameterExpression.Name));
        }

        throw new InvalidOperationException($"The LINQ expression '{parameterExpression.Print()}' could not be translated.");
    }

    private static readonly MethodInfo __getParameterValueMethodInfo =
        typeof(MongoExpressionTranslatingExpressionVisitor).GetTypeInfo().GetDeclaredMethod(nameof(GetParameterValue))!;

    private static T GetParameterValue<T>(QueryContext queryContext, string parameterName)
        => (T)queryContext.ParameterValues[parameterName]!;
}
