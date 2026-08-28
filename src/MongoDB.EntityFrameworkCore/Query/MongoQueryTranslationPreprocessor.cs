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

#if EF8 || EF9
        // EF-436: record whether the ORIGINAL (pre-nav-expansion) query contains any raw
        // GroupJoin(...).SelectMany(...) pair anywhere in it, before base.Process runs
        // nav-expansion/normalization - see MongoQueryCompilationContext.HasGroupJoinFlattenPair and
        // MongoQueryableMethodTranslatingExpressionVisitor.IsRecognizedGroupJoinFlattenLeftJoin for how this
        // per-query boolean is later used to admit a flattened LeftJoin call for this compilation, as opposed
        // to EF's unrelated internal optional-reference-navigation lowering.
        ((MongoQueryCompilationContext)QueryCompilationContext).HasGroupJoinFlattenPair =
            GroupJoinFlattenPairDetector.Detect(query);
#endif

        // Nav expansion throws for IQueryable methods that it is not aware of, so we remove
        // any VectorSearch call from the root and then put it back after. This only works because
        // nav-expansion has nothing to do for this call.
        query = VectorSearchExtractor.RemoveVectorSearchCalls(query, out var removed);
        query = base.Process(query);
        query = VectorSearchReplacer.ReplaceVectorSearchCalls(query, removed);

        return query;
    }

#if !EF8

    /// <inheritdoc />
    protected override bool IsEfConstantSupported => true;

#endif

#if EF8 || EF9
    /// <summary>
    /// Detects whether the given expression tree contains at least one raw <c>GroupJoin(...).SelectMany(...)</c>
    /// pair - see <see cref="MongoQueryCompilationContext.HasGroupJoinFlattenPair"/>. Matches by canonical
    /// <c>MethodInfo</c> (<see cref="QueryableMethods.GroupJoin"/> and the two <c>SelectMany</c> definitions),
    /// per this codebase's own reference-equality-on-<c>MethodInfo</c> convention (see this area's
    /// <c>AGENTS.md</c> "Reference-equality on MethodInfo" pitfall) rather than a looser name-only match.
    /// Deliberately loose in one respect only: it does not replicate EF Core's own
    /// <c>QueryableMethodNormalizingExpressionVisitor.TryFlattenGroupJoinSelectMany</c> collection-selector
    /// shape check (e.g. that the <c>SelectMany</c> actually reads the <c>GroupJoin</c>'s own group member).
    /// A false positive here (a pair that does not end up flattened, or is not one this provider ends up
    /// handling) is harmless - see <see cref="MongoQueryCompilationContext.HasGroupJoinFlattenPair"/>'s
    /// remarks for why the failure mode this guards against is the opposite one.
    /// </summary>
    private sealed class GroupJoinFlattenPairDetector : ExpressionVisitor
    {
        private bool _found;

        public static bool Detect(Expression expression)
        {
            var visitor = new GroupJoinFlattenPairDetector();
            visitor.Visit(expression);
            return visitor._found;
        }

        public override Expression? Visit(Expression? node)
            // Short-circuit once found - no need to keep walking.
            => _found ? node : base.Visit(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod
                && (node.Method.GetGenericMethodDefinition() == QueryableMethods.SelectManyWithCollectionSelector
                    || node.Method.GetGenericMethodDefinition() == QueryableMethods.SelectManyWithoutCollectionSelector)
                && node.Arguments[0] is MethodCallExpression { Method.IsGenericMethod: true } groupJoinCall
                && groupJoinCall.Method.GetGenericMethodDefinition() == QueryableMethods.GroupJoin)
            {
                _found = true;
                return node;
            }

            return base.VisitMethodCall(node);
        }
    }
#endif

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

