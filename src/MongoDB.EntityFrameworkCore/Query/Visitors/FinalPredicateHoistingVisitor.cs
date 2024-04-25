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
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visit the tree and move any end-of-evaluation predicates we can further up the tree to ensure
/// that they are translated correctly as we have to remove and post-replay the final predicate
/// in order to utilize the LINQ V3 provider.
/// </summary>
internal class FinalPredicateHoistingVisitor : ExpressionVisitor
{
    private static readonly FinalPredicateHoistingVisitor Instance = new();

    private FinalPredicateHoistingVisitor()
    {
    }

    /// <summary>
    /// Hoist a predicate from a final query-executing method call up one level so
    /// it can be translated before being passed to the LINQ v3 provider.
    /// </summary>
    /// <param name="expression">The full LINQ query expression.</param>
    /// <returns>A hoisted version of the LINQ query expression if required, otherwise the original expression.</returns>
    public static Expression Hoist(Expression expression)
    {
        return Instance.Visit(expression);
    }

    /// <inheritdoc/>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        if (method.DeclaringType == typeof(Queryable) && method.IsGenericMethod)
        {
            var genericMethod = method.GetGenericMethodDefinition();
            var genericType = method.GetGenericArguments()[0];

            switch (method.Name)
            {
                case nameof(Queryable.Count) when genericMethod == QueryableMethods.CountWithPredicate:
                    return Expression.Call(null,
                        QueryableMethods.CountWithoutPredicate.MakeGenericMethod(genericType),
                        Expression.Call(null,
                            QueryableMethods.Where.MakeGenericMethod(genericType),
                            methodCallExpression.Arguments));

                case nameof(Queryable.LongCount) when genericMethod == QueryableMethods.LongCountWithPredicate:
                    return Expression.Call(null,
                        QueryableMethods.LongCountWithoutPredicate.MakeGenericMethod(genericType),
                        Expression.Call(null,
                            QueryableMethods.Where.MakeGenericMethod(genericType),
                            methodCallExpression.Arguments));

                case nameof(Queryable.Any) when genericMethod == QueryableMethods.AnyWithPredicate:
                    return Expression.Call(null,
                        QueryableMethods.AnyWithoutPredicate.MakeGenericMethod(genericType),
                        Expression.Call(null,
                            QueryableMethods.Where.MakeGenericMethod(genericType),
                            methodCallExpression.Arguments));

                // We do not support All at this time as there is no predicate-less version we can use
                case nameof(Queryable.All) when genericMethod == QueryableMethods.All:
                    throw new NotImplementedException("All() is not supported at this time.");
            }
        }

        return methodCallExpression;
    }
}
