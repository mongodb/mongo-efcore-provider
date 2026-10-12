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
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Aggregates, quantifiers and counts over a cross-document collection (`c.Orders.Sum(o => o.Total)`, `.Any(pred)`, `.Count(pred)`),
// reached through a collection navigation or a keyed correlation.
internal sealed partial class MongoEFToLinqTranslatingExpressionVisitor
{
    private static readonly HashSet<string> ReferencedCollectionTerminalOperators =
    [
        nameof(Queryable.Count), nameof(Queryable.LongCount), nameof(Queryable.Sum), nameof(Queryable.Min),
        nameof(Queryable.Max), nameof(Queryable.Average), nameof(Queryable.Any), nameof(Queryable.All)
    ];

    // The operators that may sit between the correlation and the terminal operator (`Count(Where(Where(root, corr), pred))`).
    private static readonly HashSet<string> ReferencedCollectionChainOperators =
    [
        nameof(Queryable.Where), nameof(Queryable.Select), nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending),
        nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending)
    ];

    /// <summary>
    /// Rewrites a terminal operator over the foreign-key correlation of a collection navigation
    /// (<c>Sum(Where(DbSet&lt;Target&gt;(), fkPredicate), selector)</c>) to the same <c>Enumerable</c> operator over
    /// <c>Mql.Field(outerDoc, "_lookup_&lt;Nav&gt;", navSerializer)</c>, which the driver renders server-side (<c>$sum</c>,
    /// <c>$filter</c>, <c>$size</c>, ...) over the looked-up array. Generalizes the bare <c>Count</c> rewrite, whose lookup match it reuses.
    /// </summary>
    /// <remarks>
    /// Only the correlation the binder staged the lookup for may read its array (re-verified by
    /// <see cref="TryResolveNavigationLookupArray"/>); any other subquery over that type is left alone and fails as it always did.
    /// </remarks>
    private bool TryRewriteReferencedCollectionOperator(MethodCallExpression node, out Expression result)
    {
        result = null!;

        // A correlated subquery inside the lambda of another one (`c.Orders.Sum(o => o.Details.Sum(d => d.Qty))`) correlates with the
        // element, not the root document the arrays hang off, so it is never resolved here.
        if (_referencedElementLambdaDepth > 0
            || node.Method.DeclaringType != typeof(Queryable)
            || !ReferencedCollectionTerminalOperators.Contains(node.Method.Name)
            || node.Arguments.Count < 1
            || !TryFindCorrelationBase(node.Arguments[0], out var correlationWhere, out var rootExpression)
            || !TryResolveNavigationLookupArray(correlationWhere, rootExpression, out var array))
        {
            return false;
        }

        var source = RewriteChainOverArray(node.Arguments[0], correlationWhere, array);
        if (source is null || FindEnumerableCounterpart(node.Method) is not { } enumerableMethod)
            return false;

        var arguments = new List<Expression> { source };
        for (var i = 1; i < node.Arguments.Count; i++)
            arguments.Add(VisitElementLambda(node.Arguments[i]));

        result = Expression.Call(null, enumerableMethod, arguments);

        // Remembered so a relational comparison of it can exclude the empty group's null (see VisitBinary).
        if (result.Type.IsNullableType())
            _nullableReferencedAggregates.Add(result);

        return true;
    }

    // The rewritten aggregates whose result can be null (an empty group), by reference.
    private readonly HashSet<Expression> _nullableReferencedAggregates = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The correlation <c>Where</c> of a chain of <see cref="ReferencedCollectionChainOperators"/> over a query root: the one
    /// <c>Where</c> whose predicate reads the outer entity. Any other <c>Where</c> is an element filter, whether written before the
    /// correlation (<c>GroupJoin(db.Rates.Where(f), ..)</c>) or after it. Two predicates that read the outer entity are ambiguous.
    /// </summary>
    private static bool TryFindCorrelationBase(
        Expression chain, out MethodCallExpression correlationWhere, out EntityQueryRootExpression root)
    {
        correlationWhere = null!;
        root = null!;

        MethodCallExpression? candidate = null;
        var current = chain;
        while (current is not EntityQueryRootExpression)
        {
            if (current is not MethodCallExpression { Method: { DeclaringType: var declaring } method } call
                || declaring != typeof(Queryable)
                || !ReferencedCollectionChainOperators.Contains(method.Name))
            {
                return false;
            }

            if (method.Name == nameof(Queryable.Where)
                && call.Arguments[1].UnwrapLambdaFromQuote() is { Parameters.Count: 1 } predicate
                && FindOuterReference(predicate.Body, predicate.Parameters[0]) is not null)
            {
                if (candidate is not null)
                    return false;

                candidate = call;
            }

            current = call.Arguments[0];
        }

        if (candidate is null)
            return false;

        correlationWhere = candidate;
        root = (EntityQueryRootExpression)current;
        return true;
    }

    /// <summary>
    /// Resolves the exact foreign-key correlation <paramref name="correlationWhere"/> to the <c>InjectAfterRoot</c> lookup the binder staged
    /// for its navigation, and builds the typed read of that array off the outer document.
    /// </summary>
    private bool TryResolveNavigationLookupArray(
        MethodCallExpression correlationWhere, EntityQueryRootExpression rootExpression, out Expression array)
    {
        array = null!;

        // The outer document reference: the parameter that is NOT the inner Where lambda's parameter.
        var predicate = correlationWhere.Arguments[1].UnwrapLambdaFromQuote();
        var outerReference = FindOuterReference(predicate.Body, predicate.Parameters[0]);
        if (outerReference is null)
            return false;

        var outerParameter = outerReference.RemoveConvert();
        while (outerParameter is MemberExpression { Expression: { } outerReceiver })
            outerParameter = outerReceiver.RemoveConvert();

        if (outerParameter is not ParameterExpression)
            return false;

        // Matched by target entity type; the multi-branch set-operation case is rejected earlier by GuardAgainstMultiBranchNavigationCount.
        var targetEntityType = rootExpression.EntityType;

        // A lookup matched by target type says nothing about THIS predicate: only the exact FK correlation of the lookup's own navigation
        // may read its array. Re-verified here rather than trusted to the binder that staged it.
        var navigationLookup = _pendingLookups.FirstOrDefault(l => l.InjectAfterRoot && l.TargetEntityType == targetEntityType);
        if (navigationLookup?.Navigation is { } navigation
            && NativeTranslation.NativeCorrelationMatcher.TryMatchCorrelatedCollection(
                predicate.Body, navigation.DeclaringEntityType, outerParameter, targetEntityType, requireEmbedded: false,
                out var matchedNavigation)
            && matchedNavigation == navigation)
        {
            array = MqlField(
                Visit(outerReference)!, navigation.ClrType, navigationLookup.As, _bsonSerializerFactory.GetNavigationSerializer(navigation));
            return true;
        }

        // No navigation for this correlation (a GroupJoin on arbitrary keys, an explicit subquery, a model with no collection navigation):
        // the plain keyed array the binder staged for exactly the two properties the predicate compares.
        if (FindEntityType(outerParameter.Type) is { } outerEntityType
            && NativeTranslation.NativeCorrelationMatcher.TryMatchCorrelationKeys(
                predicate.Body, outerEntityType, outerParameter, targetEntityType, out var keyParts))
        {
            var alias = NativeTranslation.NativeCorrelationMatcher.BuildCorrelatedArrayLookup(targetEntityType, keyParts).As;
            if (_pendingLookups.Where(l => l.IsBridgeReadableArray).Concat(_bridgeOwnedLookups).FirstOrDefault(l => l.As == alias) is { } keyedLookup)
            {
                array = MqlField(
                    Visit(outerReference)!, typeof(List<>).MakeGenericType(targetEntityType.ClrType), keyedLookup.As,
                    _bsonSerializerFactory.GetEntityListSerializer(targetEntityType));
                return true;
            }
        }

        return false;
    }

    // Plain keyed arrays this translation stages for itself: the native binder folds an element filter into the lookup it stages, which
    // the bridge cannot use, so it reads an unfiltered array and applies the filter itself. Emitted after the root with the others.
    private readonly List<LookupExpression> _bridgeOwnedLookups = [];

    /// <summary>
    /// Stages a plain keyed array for every correlated subquery of <paramref name="source"/> that matches a strict key correlation
    /// (<see cref="NativeTranslation.NativeCorrelationMatcher.TryMatchRawKeyCorrelation"/>) and for which the binder staged no
    /// bridge-readable array. A TPH-derived target is skipped (the keyed array has no discriminator narrowing), so it fails loudly.
    /// </summary>
    private void StageBridgeOwnedLookups(Expression source)
        => new CorrelatedSubqueryScanner(this).Visit(source);

    private sealed class CorrelatedSubqueryScanner(MongoEFToLinqTranslatingExpressionVisitor owner) : System.Linq.Expressions.ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            // A set operation's other operands are `$unionWith` sub-pipelines, which cannot see an array staged after the root: only its
            // first operand is scanned, so a correlation in another one is left unresolved and fails at translation, as it always did.
            if (SetOperationMethodNames.Contains(node.Method.Name) && IsQueryableOrEnumerable(node.Method) && node.Arguments.Count >= 1)
            {
                Visit(node.Arguments[0]);
                return node;
            }

            // An operator over a correlated subquery: its source chain holds the correlation, its other arguments are lambdas over the
            // subquery's elements, whose own correlated subqueries correlate with the element, not the root document.
            if (node.Method.DeclaringType == typeof(Queryable)
                && node.Arguments.Count >= 1
                && TryFindCorrelationBase(node.Arguments[0], out _, out _))
            {
                Visit(node.Arguments[0]);
                return node;
            }

            if (node.Method is { Name: nameof(Queryable.Where), DeclaringType: var declaring }
                && declaring == typeof(Queryable)
                && TryGetChainRoot(node.Arguments[0], out var root)
                && root.EntityType.BaseType is null
                && node.Arguments[1].UnwrapLambdaFromQuote() is { Parameters.Count: 1 } predicate
                && FindOuterReference(predicate.Body, predicate.Parameters[0]) is { } outerReference)
            {
                var outerParameter = outerReference.RemoveConvert();
                while (outerParameter is MemberExpression { Expression: { } receiver })
                    outerParameter = receiver.RemoveConvert();

                if (outerParameter is ParameterExpression
                    && owner.FindEntityType(outerParameter.Type) is { } outerEntityType
                    && NativeTranslation.NativeCorrelationMatcher.TryMatchCorrelationKeys(
                        predicate.Body, outerEntityType, outerParameter, root.EntityType, out var keyParts))
                {
                    var plain = NativeTranslation.NativeCorrelationMatcher.BuildCorrelatedArrayLookup(root.EntityType, keyParts);

                    if (!owner._pendingLookups.Any(l => l.IsBridgeReadableArray && l.As == plain.As)
                        && !owner._bridgeOwnedLookups.Any(l => l.As == plain.As))
                    {
                        owner._bridgeOwnedLookups.Add(plain);
                    }
                }

                // The correlation's own lambda is not scanned for nested subqueries.
                return node;
            }

            return base.VisitMethodCall(node);
        }

        /// <summary>The query root at the bottom of a chain of <see cref="ReferencedCollectionChainOperators"/>.</summary>
        private static bool TryGetChainRoot(Expression chain, out EntityQueryRootExpression root)
        {
            root = null!;

            var current = chain;
            while (current is MethodCallExpression { Method: { DeclaringType: var declaring } method } call
                   && declaring == typeof(Queryable)
                   && ReferencedCollectionChainOperators.Contains(method.Name))
            {
                current = call.Arguments[0];
            }

            if (current is not EntityQueryRootExpression rootExpression)
                return false;

            root = rootExpression;
            return true;
        }
    }

    /// <summary>
    /// Rebuilds <paramref name="chain"/> with the correlation replaced by <paramref name="array"/> and every
    /// <c>Queryable</c> operator above it by its <c>Enumerable</c> counterpart.
    /// </summary>
    private Expression? RewriteChainOverArray(Expression chain, MethodCallExpression correlationWhere, Expression array)
    {
        // The query root is the array. The correlation itself is consumed by the array, which already holds only the correlated elements;
        // every other Where is an element filter that stays.
        if (chain is EntityQueryRootExpression)
            return array;

        if (ReferenceEquals(chain, correlationWhere))
            return RewriteChainOverArray(((MethodCallExpression)chain).Arguments[0], correlationWhere, array);

        if (chain is not MethodCallExpression call
            || RewriteChainOverArray(call.Arguments[0], correlationWhere, array) is not { } source
            || FindEnumerableCounterpart(call.Method) is not { } enumerableMethod)
        {
            return null;
        }

        var arguments = new List<Expression> { source };
        for (var i = 1; i < call.Arguments.Count; i++)
            arguments.Add(VisitElementLambda(call.Arguments[i]));

        return Expression.Call(null, enumerableMethod, arguments);
    }

    private int _referencedElementLambdaDepth;

    private Expression VisitElementLambda(Expression argument)
    {
        _referencedElementLambdaDepth++;
        try
        {
            return Visit(argument.UnwrapLambdaFromQuote())!;
        }
        finally
        {
            _referencedElementLambdaDepth--;
        }
    }

    /// <summary>
    /// The <c>Enumerable</c> operator with the same name, type arguments and parameters as <paramref name="queryableMethod"/>, a
    /// <c>Func</c> where it takes an <c>Expression&lt;Func&gt;</c> and an <c>IEnumerable</c> where it takes an <c>IQueryable</c>.
    /// </summary>
    private static MethodInfo? FindEnumerableCounterpart(MethodInfo queryableMethod)
    {
        var queryableParameters = queryableMethod.GetParameters();

        foreach (var candidate in typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (candidate.Name != queryableMethod.Name
                || candidate.IsGenericMethodDefinition != queryableMethod.IsGenericMethod
                || candidate.GetParameters().Length != queryableParameters.Length)
            {
                continue;
            }

            var closed = candidate;
            if (candidate.IsGenericMethodDefinition)
            {
                var typeArguments = queryableMethod.GetGenericArguments();
                if (candidate.GetGenericArguments().Length != typeArguments.Length)
                    continue;

                try
                {
                    closed = candidate.MakeGenericMethod(typeArguments);
                }
                catch (ArgumentException)
                {
                    continue;
                }
            }

            var closedParameters = closed.GetParameters();
            if (queryableParameters.Select((p, i) => ParameterMatches(p.ParameterType, closedParameters[i].ParameterType)).All(m => m))
                return closed;
        }

        return null;

        static bool ParameterMatches(Type queryable, Type enumerable)
            => queryable == enumerable
               || (queryable.IsGenericType
                   && queryable.GetGenericTypeDefinition() == typeof(IQueryable<>)
                   && enumerable == typeof(IEnumerable<>).MakeGenericType(queryable.GetGenericArguments()))
               || (queryable.IsGenericType
                   && queryable.GetGenericTypeDefinition() == typeof(Expression<>)
                   && queryable.GetGenericArguments()[0] == enumerable);
    }
}
