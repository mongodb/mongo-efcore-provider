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
        //
        // This runs unconditionally on every query. A reliable pre-check (does the tree
        // contain the Join+Select-over-IncludeExpression shape?) is itself a full tree
        // walk, so it would not save any work over just running the unwrapper — whose
        // VisitMethodCall match is narrow and a no-op when the shape is absent. We
        // therefore accept the single always-on walk rather than adding a redundant one.
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
            // result selector whose parameter `o` is the join's transparent
            // identifier (carrier of Outer and Inner). Nav-expansion emits
            // LeftJoin (not Join) when the FK is nullable, e.g. for an optional
            // reference navigation like Item.Product where ProductId is string?.
            //
            // Method-call matching uses canonical QueryableMethods constants
            // (reference-equality on the generic-method-definition) where they
            // exist; LeftJoin has no EF8/EF9 constant so it falls back to a
            // string-name check guarded by a #if.
            if (node.Method.IsGenericMethod
                && node.Method.GetGenericMethodDefinition() == QueryableMethods.Select
                && node.Arguments.Count == 2
                && node.Arguments[0] is MethodCallExpression joinCall
                && IsJoinOrLeftJoin(joinCall.Method)
                && joinCall.Arguments.Count == 5
                && Unquote(node.Arguments[1]) is LambdaExpression selectorLambda
                && selectorLambda.Body is Microsoft.EntityFrameworkCore.Query.IncludeExpression includeExpr
                && IsFieldAccessOf(includeExpr.EntityExpression, selectorLambda.Parameters[0], "Outer")
                && IsFieldAccessOf(includeExpr.NavigationExpression, selectorLambda.Parameters[0], "Inner"))
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

        private static bool IsJoinOrLeftJoin(System.Reflection.MethodInfo method)
        {
            if (!method.IsGenericMethod)
            {
                return false;
            }

            var definition = method.GetGenericMethodDefinition();
            if (definition == QueryableMethods.Join)
            {
                return true;
            }

#if !EF8 && !EF9
            if (definition == QueryableMethods.LeftJoin)
            {
                return true;
            }
#else
            // EF8/EF9 don't expose a canonical QueryableMethods.LeftJoin constant;
            // fall back to a name match for the method nav-expansion emits.
            if (method.Name == "LeftJoin")
            {
                return true;
            }
#endif
            return false;
        }

        private static Expression Unquote(Expression e)
            => e is UnaryExpression { NodeType: ExpressionType.Quote, Operand: var inner } ? inner : e;

        // Structural check: is `e` a `<expectedParam>.<memberName>` access on the
        // join's transparent-identifier parameter? Replaces an earlier brittle
        // check on the compiler-generated `TransparentIdentifier...` type name.
        private static bool IsFieldAccessOf(Expression e, ParameterExpression expectedParam, string memberName)
            => e is MemberExpression me
               && me.Member.Name == memberName
               && ReferenceEquals(me.Expression, expectedParam);
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

