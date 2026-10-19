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
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.NativeTranslation;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Folds a CLOSED <c>Enumerable.Where(localCollection, e =&gt; …)</c> that is the collection argument of an
/// <c>Enumerable.Contains</c> — e.g. <c>new List&lt;string&gt; { "ABCDE", id }.Where(e =&gt; e != null).Contains(c.CustomerID)</c>
/// (EF's <c>Contains_with_local_enumerable_inline</c>/<c>_closure_mix</c>) — into a plain local collection, so the
/// Contains translates to an ordinary indexable <c>$in</c> on both the native and driver-LINQ paths.
/// </summary>
/// <remarks>
/// A constant source is filtered once, here, at compile time. A query-parameter source cannot be (its value is
/// per-execution), so it is replaced by an EF runtime parameter (<see cref="QueryCompilationContext.RegisterRuntimeParameter"/>)
/// whose extractor re-applies the filter to the parameter's value on every execution. A predicate reading the clock
/// always takes that per-execution path (a compile-time fold would bake the first reading into the cached plan), and
/// one making a non-deterministic call is not folded at all. Only a predicate that
/// references nothing but its own lambda parameter is folded; anything else is left untouched.
/// </remarks>
internal sealed class LocalCollectionFilterFoldingVisitor(QueryCompilationContext queryCompilationContext) : ExpressionVisitor
{
    private int _foldedParameterCount;

    /// <inheritdoc />
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (node is { Method: { IsStatic: true, Name: nameof(Enumerable.Contains) }, Arguments.Count: 2 }
            && node.Method.DeclaringType == typeof(Enumerable)
            && TryFold(node.Arguments[0], out var folded))
        {
            return node.Update(null, [folded, Visit(node.Arguments[1])]);
        }

        return base.VisitMethodCall(node);
    }

    private bool TryFold(Expression collection, out Expression folded)
    {
        folded = collection;

        if (collection is not MethodCallExpression { Method.IsGenericMethod: true } whereCall
            || whereCall.Method.GetGenericMethodDefinition() != EnumerableMethods.Where
            || whereCall.Arguments[1] is not LambdaExpression predicate
            || !FreeVariableFinder.IsClosed(predicate))
        {
            return false;
        }

        // A non-deterministic call is never folded (main throws for it; the unfolded query reaches the same guard).
        if (NonDeterministicCalls.ContainsNonDeterministicCall(predicate))
        {
            return false;
        }

        var source = whereCall.Arguments[0];
        var toArray = EnumerableMethods.ToArray.MakeGenericMethod(whereCall.Method.GetGenericArguments()[0]);

        // A constant source is filtered once, at compile time, unless the predicate reads the clock: that is a
        // per-execution value, so it takes the runtime-parameter path below (the cached plan would otherwise keep the
        // first execution's reading).
        if (source is ConstantExpression && !RuntimeClock.ContainsClock(predicate))
        {
            var evaluate = Expression.Lambda<Func<object?>>(
                Expression.Convert(Expression.Call(toArray, whereCall), typeof(object)));
            folded = Expression.Constant(evaluate.Compile()(), toArray.ReturnType);
            return true;
        }

        Expression? sourceValue = null;
        if (source is ConstantExpression)
        {
            sourceValue = source;
        }
        else if (NativeQueryParameter.TryGetQueryParameterName(source, out var parameterName))
        {
            sourceValue = Expression.Convert(
                Expression.Property(ParameterValues(QueryCompilationContext.QueryContextParameter), "Item", Expression.Constant(parameterName)),
                source.Type);
        }

        if (sourceValue is null)
        {
            return false;
        }

        var extractor = Expression.Lambda(
            Expression.Call(toArray, Expression.Call(whereCall.Method, sourceValue, predicate)),
            QueryCompilationContext.QueryContextParameter);

        // "__" prefix: on EF8/EF9 a query parameter is a ParameterExpression recognized by that prefix
        // (NativeQueryParameter.TryGetQueryParameterName); harmless on EF10.
        folded = queryCompilationContext.RegisterRuntimeParameter(
            $"__mongo_filtered_{_foldedParameterCount++}", extractor);
        return true;
    }

    private static Expression ParameterValues(ParameterExpression queryContext)
#if EF8 || EF9
        => Expression.Property(queryContext, nameof(QueryContext.ParameterValues));
#else
        => Expression.Property(queryContext, nameof(QueryContext.Parameters));
#endif

    private sealed class FreeVariableFinder(IReadOnlyCollection<ParameterExpression> bound) : ExpressionVisitor
    {
        private bool _found;

        public static bool IsClosed(LambdaExpression lambda)
        {
            var finder = new FreeVariableFinder(lambda.Parameters);
            finder.Visit(lambda.Body);
            return !finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= !bound.Contains(node);
            return node;
        }

        // A QueryParameterExpression (EF10) or any other provider/EF extension node: not closed.
        protected override Expression VisitExtension(Expression node)
        {
            _found = true;
            return node;
        }

        // Nested lambdas: out of scope, keep the fold narrow.
        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            _found = true;
            return node;
        }
    }
}
