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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.Query;

/// <inheritdoc />
public class MongoQueryTranslationPreprocessor : QueryTranslationPreprocessor
{
    /// <inheritdoc />
    public MongoQueryTranslationPreprocessor(
        QueryTranslationPreprocessorDependencies dependencies,
        QueryCompilationContext queryCompilationContext)
        : base(dependencies, queryCompilationContext)
    {
    }

    /// <inheritdoc />
    public override Expression Process(Expression query)
    {
        query = FinalPredicateHoistingVisitor.Hoist(query);
        query = new EntityFrameworkDetourExpressionVisitor(QueryCompilationContext).Visit(query);

        // Nav expansion throws for IQueryable methods that it is not aware of, so we remove
        // any VectorSearch call from the root and then put it back after. This only works because
        // nav-expansion has nothing to do for this call.
        query = VectorSearchExtractor.RemoveVectorSearchCalls(query, out var removed);
        query = base.Process(query);
        query = VectorSearchReplacer.ReplaceVectorSearchCalls(query, removed);

        // EF Core's nav-expansion rewrites cross-collection dependent-to-principal
        // reference Includes as a synthetic Queryable.Join + Select wrapping an
        // IncludeExpression. Lift those back to a plain Select(p => IncludeExpression(p, ..., nav))
        // so the rest of the provider sees a uniform Include shape (the loader path
        // built in EF-117 Stage 1 then picks up the reference case in Stage 2).
        query = IncludeJoinUnwrapper.Unwrap(query);

        return query;
    }

#if !EF8

    /// <inheritdoc />
    protected override bool IsEfConstantSupported => true;

#endif

    /// <summary>
    /// Rewrites the synthetic <c>Queryable.Join(...).Select(o =&gt; IncludeExpression(o.Outer, o.Inner, nav))</c>
    /// shape that EF Core's nav-expansion produces for dependent-to-principal reference
    /// Include into <c>&lt;outerSource&gt;.Select(p =&gt; IncludeExpression(p, default(TInner), nav))</c>.
    /// The provider then sees the same Include shape as for collection navigations and
    /// the cross-collection loader path (EF-117) materializes the related entity via a
    /// per-principal sub-query.
    /// </summary>
    private sealed class IncludeJoinUnwrapper : ExpressionVisitor
    {
        public static Expression Unwrap(Expression expression)
            => new IncludeJoinUnwrapper().Visit(expression);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // Match: <something>.Select(o => IncludeExpression(o.Outer, o.Inner, nav))
            // where <something> is a Queryable.Join or Queryable.LeftJoin with a
            // TransparentIdentifier result selector. Nav-expansion emits LeftJoin
            // (not Join) when the FK is nullable, e.g. for an optional reference
            // navigation like Item.Product where ProductId is string?.
            if (node.Method.Name == nameof(Queryable.Select)
                && node.Arguments.Count == 2
                && node.Arguments[0] is MethodCallExpression joinCall
                && (joinCall.Method.Name == nameof(Queryable.Join)
                    || joinCall.Method.Name == nameof(Queryable.LeftJoin))
                && joinCall.Arguments.Count == 5
                && Unquote(node.Arguments[1]) is LambdaExpression selectorLambda
                && selectorLambda.Body is Microsoft.EntityFrameworkCore.Query.IncludeExpression includeExpr
                && IsTransparentIdentifierFieldAccess(includeExpr.EntityExpression, "Outer", out var outerParam)
                && IsTransparentIdentifierFieldAccess(includeExpr.NavigationExpression, "Inner", out var innerParam)
                && outerParam == innerParam
                && outerParam == selectorLambda.Parameters[0])
            {
                var outerSource = joinCall.Arguments[0];
                var outerType = outerSource.Type.GetGenericArguments()[0];

                // Build new selector: p => IncludeExpression(p, default(TInner), nav)
                var newParam = Expression.Parameter(outerType, "p");
                var newInclude = includeExpr.Update(
                    newParam,
                    Expression.Default(includeExpr.NavigationExpression.Type));
                var newSelector = Expression.Lambda(newInclude, newParam);

                var selectMethod = node.Method.GetGenericMethodDefinition()
                    .MakeGenericMethod(outerType, newInclude.Type);
                return Expression.Call(selectMethod, Visit(outerSource), Expression.Quote(newSelector));
            }

            return base.VisitMethodCall(node);
        }

        private static Expression Unquote(Expression e)
            => e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: var inner } ? inner : e;

        private static bool IsTransparentIdentifierFieldAccess(Expression e, string memberName, out ParameterExpression? param)
        {
            if (e is MemberExpression me
                && me.Member.Name == memberName
                && me.Expression is ParameterExpression p
                && p.Type.IsGenericType
                && p.Type.Name.StartsWith("TransparentIdentifier", StringComparison.Ordinal))
            {
                param = p;
                return true;
            }
            param = null;
            return false;
        }
    }

    private sealed class VectorSearchExtractor : ExpressionVisitor
    {
        private MethodCallExpression? _removed;

        private VectorSearchExtractor()
        {
        }

        public static Expression RemoveVectorSearchCalls(Expression expression, out MethodCallExpression? removed)
        {
            var visitor = new VectorSearchExtractor();
            var processed = visitor.Visit(expression);
            removed = visitor._removed;
            return processed;
        }

        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            if (methodCallExpression.IsVectorSearch()
                && methodCallExpression.Arguments[0] is QueryRootExpression)
            {
                _removed = methodCallExpression;
                return Visit(methodCallExpression.Arguments[0]);
            }

            return base.VisitMethodCall(methodCallExpression);
        }
    }

    private sealed class VectorSearchReplacer : ExpressionVisitor
    {
        private readonly MethodCallExpression _removed;

        private VectorSearchReplacer(MethodCallExpression removed)
        {
            _removed = removed;
        }

        public static Expression ReplaceVectorSearchCalls(Expression expression, MethodCallExpression? removed)
            => removed == null ? expression : new VectorSearchReplacer(removed).Visit(expression)!;

        public override Expression? Visit(Expression? node)
        {
            if (node is EntityQueryRootExpression)
            {
                var arguments = _removed.Arguments.ToList();
                arguments[0] = node;
                return Expression.Call(_removed.Method, arguments);
            }

            return base.Visit(node);
        }
    }
}

