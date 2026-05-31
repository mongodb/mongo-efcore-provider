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
using Microsoft.EntityFrameworkCore.Metadata;
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
        // Reference-ROOTED ThenInclude chains (e.g. Order.Customer.ThenInclude(c => c.Orders))
        // produce a chain of Joins + a Select over a nested IncludeExpression tree. Collapse
        // that whole join chain to <rootSource>.Select(p => <include tree rooted at p>) so the
        // binding visitor sees a uniform reference-include shape it can route to nested $lookups.
        // Runs before the single-level unwrapper; its match is narrow (multi-level reference
        // root) so single-level / reference+collection-on-same-root cases fall through untouched.
        query = ReferenceChainJoinUnwrapper.Unwrap(query);

        query = IncludeJoinUnwrapper.Unwrap(query);

        return query;
    }

#if !EF8

    /// <inheritdoc />
    protected override bool IsEfConstantSupported => true;

#endif

    // Shared between IncludeJoinUnwrapper and ReferenceChainJoinUnwrapper (both match the
    // Queryable.Join / LeftJoin shape nav-expansion emits). Hoisted here so the join-detection
    // logic — including the EF10+ canonical LeftJoin branch — lives in exactly one place.
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

    /// <summary>
    /// Rewrites the synthetic <c>Queryable.Join(...).Select(o =&gt; IncludeExpression(o.Outer, o.Inner, nav))</c>
    /// shape that EF Core's nav-expansion produces for dependent-to-principal reference
    /// Include into <c>&lt;outerSource&gt;.Select(p =&gt; IncludeExpression(p, default(TInner), nav))</c>.
    /// The provider then sees the same Include shape as for collection navigations and
    /// the cross-collection loader path (EF-117) materializes the related entity via a
    /// per-principal sub-query.
    /// </summary>
    /// <remarks>
    /// When the same root carries a reference Include AND one or more collection Includes
    /// (e.g. <c>Order.Include(o =&gt; o.Customer).Include(o =&gt; o.OrderDetails)</c>),
    /// nav-expansion nests the collection <c>IncludeExpression</c>(s) <em>around</em> the
    /// join-based reference include: the Select body is
    /// <c>IncludeExpression(IncludeExpression(o.Outer, o.Inner, refNav), collectionSubquery, collNav)</c>.
    /// The reference include therefore is not the body's root but sits at the innermost
    /// <c>EntityExpression</c>. We locate it, rewrite it to
    /// <c>IncludeExpression(p, default(TInner), refNav)</c>, and replace every other
    /// reference to <c>o.Outer</c> in the body (the collection sub-queries key off it) with
    /// the new parameter <c>p</c>. Each independent top-level Include then classifies on its
    /// own in <c>MongoIncludeCompiler.ChooseStrategy</c>, yielding two independent
    /// <c>$lookup</c>s.
    /// </remarks>
    private sealed class IncludeJoinUnwrapper : ExpressionVisitor
    {
        public static Expression Unwrap(Expression expression)
            => new IncludeJoinUnwrapper().Visit(expression);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // Match: <something>.Select(o => <body>) where <something> is a Queryable.Join
            // or Queryable.LeftJoin with a result selector whose parameter `o` is the join's
            // transparent identifier (carrier of Outer and Inner), and <body> contains the
            // reference IncludeExpression(o.Outer, o.Inner, refNav) — either as the body root
            // (reference-only) or nested at the innermost EntityExpression beneath one or more
            // wrapping collection IncludeExpressions (reference + collection on one root).
            // Nav-expansion emits LeftJoin (not Join) when the FK is nullable, e.g. for an
            // optional reference navigation like Item.Product where ProductId is string?.
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
                && ContainsReferenceJoinInclude(selectorLambda.Body, selectorLambda.Parameters[0]))
            {
                var transparentParam = selectorLambda.Parameters[0];
                var outerSource = joinCall.Arguments[0];
                var outerType = outerSource.Type.GetGenericArguments()[0];

                // Replace the transparent identifier `o` with the outer entity `p`:
                //  - the reference include's join-sourced parts (o.Outer, o.Inner) collapse to
                //    IncludeExpression(p, default(TInner), refNav);
                //  - any other o.Outer reference (e.g. collection-include sub-query FK predicates)
                //    becomes a plain reference to p.
                var newParam = Expression.Parameter(outerType, "p");
                var newBody = new ReferenceJoinIncludeRewriter(transparentParam, newParam).Visit(selectorLambda.Body);
                var newSelector = Expression.Lambda(newBody, newParam);

                var selectMethod = node.Method.GetGenericMethodDefinition()
                    .MakeGenericMethod(outerType, newBody.Type);
                return Expression.Call(selectMethod, Visit(outerSource), Expression.Quote(newSelector));
            }

            return base.VisitMethodCall(node);
        }

        // True when `body` is, or transitively wraps (through collection-include
        // EntityExpressions), the reference IncludeExpression(o.Outer, o.Inner, refNav).
        private static bool ContainsReferenceJoinInclude(Expression body, ParameterExpression transparentParam)
        {
            while (body is Microsoft.EntityFrameworkCore.Query.IncludeExpression include)
            {
                if (IsFieldAccessOf(include.EntityExpression, transparentParam, "Outer")
                    && IsFieldAccessOf(include.NavigationExpression, transparentParam, "Inner"))
                {
                    return true;
                }

                body = include.EntityExpression;
            }

            return false;
        }

        // Structural check: is `e` a `<expectedParam>.<memberName>` access on the
        // join's transparent-identifier parameter? Replaces an earlier brittle
        // check on the compiler-generated `TransparentIdentifier...` type name.
        private static bool IsFieldAccessOf(Expression e, ParameterExpression expectedParam, string memberName)
            => e is MemberExpression me
               && me.Member.Name == memberName
               && ReferenceEquals(me.Expression, expectedParam);

        /// <summary>
        /// Rebinds a Select body that contains the join-based reference include onto the
        /// plain outer-entity parameter <c>p</c>. The reference
        /// <c>IncludeExpression(o.Outer, o.Inner, refNav)</c> collapses to
        /// <c>IncludeExpression(p, default(TInner), refNav)</c> (dropping the join's inner
        /// sub-query, which the reference loader / <c>$lookup</c> rebuilds from metadata), and
        /// every other <c>o.Outer</c> access — e.g. in a sibling collection-include sub-query's
        /// FK predicate — is replaced with <c>p</c>.
        /// </summary>
        private sealed class ReferenceJoinIncludeRewriter(
            ParameterExpression transparentParam,
            ParameterExpression outerParam) : ExpressionVisitor
        {
            protected override Expression VisitExtension(Expression node)
            {
                if (node is Microsoft.EntityFrameworkCore.Query.IncludeExpression include
                    && IsFieldAccessOf(include.EntityExpression, transparentParam, "Outer")
                    && IsFieldAccessOf(include.NavigationExpression, transparentParam, "Inner"))
                {
                    return include.Update(
                        outerParam,
                        Expression.Default(include.NavigationExpression.Type));
                }

                return base.VisitExtension(node);
            }

            protected override Expression VisitMember(MemberExpression node)
                => node.Member.Name == "Outer" && ReferenceEquals(node.Expression, transparentParam)
                    ? outerParam
                    : base.VisitMember(node);
        }
    }

    /// <summary>
    /// Rewrites the join-chain shape that EF Core's nav-expansion produces for a
    /// reference-ROOTED <c>ThenInclude</c> chain — e.g.
    /// <c>Order.Include(o =&gt; o.Customer).ThenInclude(c =&gt; c.Orders)</c> or an
    /// all-reference chain like <c>Item.Order.Customer</c>. Nav-expansion emits a
    /// sequence of <c>Join</c>/<c>LeftJoin</c> calls (one per reference level) terminated
    /// by <c>Select(t =&gt; IncludeExpression(... IncludeExpression(...)))</c> whose body is a
    /// <em>nested</em> include tree keyed off the joins' transparent-identifier parameter.
    /// </summary>
    /// <remarks>
    /// The provider materializes these chains with nested <c>$lookup</c> stages
    /// (see <c>MongoProjectionBindingExpressionVisitor.RewriteNestedChainForLookup</c>, which
    /// builds the per-level dotted-path lookups for the nested chain),
    /// so the joins are not needed on the data path. This visitor collapses the whole join
    /// chain to <c>&lt;rootSource&gt;.Select(p =&gt; &lt;rebuilt include tree&gt;)</c>, where the rebuilt
    /// tree preserves the original <see cref="IncludeExpression"/> nodes (navigations + SetLoaded)
    /// but replaces every join-artifact entity / navigation sub-expression: the OUTERMOST
    /// include's <c>EntityExpression</c> becomes the new root parameter <c>p</c>, and every
    /// nested include's <c>EntityExpression</c> / <c>NavigationExpression</c> becomes a
    /// <c>default(T)</c> placeholder (the binding visitor drives all nested levels from
    /// navigation metadata, never reading these placeholders).
    /// <para>
    /// The match is intentionally narrow — it requires the Select body's outermost
    /// <see cref="IncludeExpression"/> to itself carry a nested <see cref="IncludeExpression"/>
    /// as its <c>NavigationExpression</c>. Single-level reference includes and
    /// reference+collection-on-the-same-root shapes do not have that nesting, so they fall
    /// through to the single-level <c>IncludeJoinUnwrapper</c> untouched.
    /// </para>
    /// </remarks>
    private sealed class ReferenceChainJoinUnwrapper : ExpressionVisitor
    {
        public static Expression Unwrap(Expression expression)
            => new ReferenceChainJoinUnwrapper().Visit(expression);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod
                && node.Method.GetGenericMethodDefinition() == QueryableMethods.Select
                && node.Arguments.Count == 2
                && node.Arguments[0] is MethodCallExpression joinCall
                && IsJoinOrLeftJoin(joinCall.Method)
                && Unquote(node.Arguments[1]) is LambdaExpression selectorLambda
                && selectorLambda.Body is Microsoft.EntityFrameworkCore.Query.IncludeExpression outerInclude
                && outerInclude.Navigation is INavigation rootNav
                && !rootNav.IsCollection
                && outerInclude.NavigationExpression is Microsoft.EntityFrameworkCore.Query.IncludeExpression)
            {
                // Descend the chain of joins to the original root source (the principal query,
                // possibly carrying OrderBy/Where above the first join).
                Expression rootSource = joinCall;
                while (rootSource is MethodCallExpression mc && IsJoinOrLeftJoin(mc.Method))
                {
                    rootSource = mc.Arguments[0];
                }

                var rootType = rootSource.Type.GetGenericArguments()[0];
                var rootParam = Expression.Parameter(rootType, "p");

                // Rebuild the include tree. SIBLING top-level includes on the same root chain via
                // EntityExpression and all resolve to the root entity -> p. ThenInclude levels chain
                // via NavigationExpression and resolve to joined entities -> default placeholders
                // (the binding visitor drives nested levels purely from navigation metadata). The
                // IncludeExpression nodes themselves (navigation + SetLoaded) are preserved via .Update.
                var rebuiltBody = RebuildTopLevel(outerInclude, rootParam);
                var newSelector = Expression.Lambda(rebuiltBody, rootParam);

                var selectMethod = node.Method.GetGenericMethodDefinition()
                    .MakeGenericMethod(rootType, rebuiltBody.Type);
                return Expression.Call(selectMethod, Visit(rootSource), Expression.Quote(newSelector));
            }

            return base.VisitMethodCall(node);
        }

        // Rebuilds a top-level include node (and any sibling top-level includes reached through
        // its EntityExpression). The deepest EntityExpression is a transparent-identifier access
        // onto the root principal — rebind it to `rootParam`. Each top-level include's
        // NavigationExpression that carries a ThenInclude is rebuilt via RebuildNested.
        private static Expression RebuildTopLevel(
            Microsoft.EntityFrameworkCore.Query.IncludeExpression include,
            ParameterExpression rootParam)
        {
            var entity = include.EntityExpression is Microsoft.EntityFrameworkCore.Query.IncludeExpression siblingInclude
                ? RebuildTopLevel(siblingInclude, rootParam) // another top-level include on the same root
                : (Expression)rootParam;                     // leaf: the join-sourced root entity access

            var navExpr = include.NavigationExpression is Microsoft.EntityFrameworkCore.Query.IncludeExpression nestedInclude
                ? RebuildNested(nestedInclude)               // ThenInclude chain on this navigation
                : Expression.Default(include.NavigationExpression.Type);

            return include.Update(entity, navExpr);
        }

        // Rebuilds a nested (ThenInclude) include node. Its entity and any non-include
        // NavigationExpression are join artifacts -> default placeholders; deeper ThenInclude
        // levels recurse. The binding visitor materializes every nested level from metadata.
        private static Expression RebuildNested(Microsoft.EntityFrameworkCore.Query.IncludeExpression include)
        {
            var navExpr = include.NavigationExpression is Microsoft.EntityFrameworkCore.Query.IncludeExpression deeperInclude
                ? RebuildNested(deeperInclude)
                : Expression.Default(include.NavigationExpression.Type);

            return include.Update(Expression.Default(include.EntityExpression.Type), navExpr);
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

