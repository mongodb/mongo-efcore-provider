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
using Microsoft.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Query;

/// <summary>
/// Removes <c>WithQueryProvider</c> markers from a query and reports the mode they asked for.
/// </summary>
/// <remarks>
/// Runs on the raw query before it is captured as <see cref="MongoQueryCompilationContext.OriginalExpression"/>, because the
/// driver-LINQ fallback executes that captured chain and the driver does not know the marker.
/// </remarks>
internal sealed class WithQueryProviderExtractor : ExpressionVisitor
{
    private MongoQueryMode? _mode;

    private WithQueryProviderExtractor()
    {
    }

    public static Expression Extract(Expression query, out MongoQueryMode? mode)
    {
        var visitor = new WithQueryProviderExtractor();
        var processed = visitor.Visit(query);
        mode = visitor._mode;
        return processed;
    }

    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        if (!methodCallExpression.TryGetWithQueryProvider(out var mode))
        {
            return base.VisitMethodCall(methodCallExpression);
        }

        if (_mode != null && _mode != mode)
        {
            throw new InvalidOperationException(
                $"{nameof(MongoQueryableExtensions.WithQueryProvider)} was applied to more than one query root with conflicting modes ('{_mode}' and '{mode}'). "
                + "A query can only run in one query mode.");
        }

        _mode = mode;
        return Visit(methodCallExpression.Arguments[0]);
    }
}
