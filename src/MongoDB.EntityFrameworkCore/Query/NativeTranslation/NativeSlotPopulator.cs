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

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates the native-translation slots (<see cref="Expressions.MongoSelectDefinition"/> Predicate /
/// Orderings / Offset / Limit) on a <see cref="Expressions.MongoQueryExpression"/> for the seven
/// slot-bearing LINQ operators, and owns the whitelist that suppresses the non-native catch-all. Extracted
/// from the QMTEV (EF-332) so native-translation logic no longer lives inside the EF query dispatcher.
/// </summary>
internal static class NativeSlotPopulator
{
    /// <summary>
    /// Populates the native-translation slots on the <see cref="MongoQueryExpression"/> for the
    /// seven slot-bearing operators: Where, OrderBy, OrderByDescending, ThenBy, ThenByDescending,
    /// Skip, and Take.  Called from
    /// <see cref="Visitors.MongoQueryableMethodTranslatingExpressionVisitor"/>'s VisitMethodCall
    /// on the already-evaluated source.
    /// </summary>
    internal static void PopulateNativeSlots(
        ShapedQueryExpression shapedQuery,
        MethodInfo methodDefinition,
        MethodCallExpression call)
    {
        var mongoQ = (MongoQueryExpression)shapedQuery.QueryExpression;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        if (methodDefinition == QueryableMethods.Where)
        {
            // Canonical-order guard: the native lowerer emits $match → $sort → $skip → $limit. A Where
            // (→ $match) applied AFTER any paging (Skip → Offset, or Take → Limit) has already been
            // recorded would be hoisted ahead of that paging on the native pipeline, silently returning
            // the wrong rows. Such a query is not natively representable; fall back to driver-LINQ.
            if (PagingAlreadyApplied(mongoQ))
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            // Canonical-order guard: a $sort emitted after paging ($skip/$limit) has been recorded would
            // be hoisted ahead of it on the native pipeline, sorting the full set instead of the page and
            // returning the wrong rows. Not natively representable; fall back to driver-LINQ.
            if (PagingAlreadyApplied(mongoQ))
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.OrderBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.ResetOrderings(new MongoOrdering(keyNode, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending)
        {
            // Same canonical-order guard as OrderBy: a $sort after paging is not natively representable.
            if (PagingAlreadyApplied(mongoQ))
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.ThenBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.AppendOrdering(new MongoOrdering(keyNode, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.Skip)
        {
            // Enforce canonical order: Skip once, before Take.
            if (mongoQ.Select.Offset != null || mongoQ.Select.Limit != null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
            {
                mongoQ.Select.Offset = TranslateCountExpression(call.Arguments[1]);
                if (mongoQ.Select.Offset is null)
                    mongoQ.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (methodDefinition == QueryableMethods.Take)
        {
            if (mongoQ.Select.Limit != null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
            {
                mongoQ.Select.Limit = TranslateCountExpression(call.Arguments[1]);
                if (mongoQ.Select.Limit is null)
                    mongoQ.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (!IsNativeRepresentableSlotOperator(methodDefinition))
        {
            // Any other top-level operator (Distinct, Cast, DefaultIfEmpty, scalar aggregates, cardinality
            // reducers, Any/All, …) is not lowered into a native slot. Leaving the query "native-representable"
            // would silently drop the operator on the native pipeline (e.g. a Distinct executed as the bare
            // collection scan), so it is conservatively marked non-native. Select / OfType set the flag in their
            // own Translate overrides. This is correctness-safe: the worst case is a missed native optimization
            // and a fall back to the driver-LINQ path, never a wrong result.
            mongoQ.Select.MarkNotNativelyRepresentable();
        }
    }

    // Canonical-order guard shared by the Where / OrderBy / OrderByDescending / ThenBy / ThenByDescending
    // arms: once any paging ($skip → Offset, or $take → Limit) has been recorded, a later $match/$sort would
    // be hoisted ahead of it on the canonical native pipeline and silently return the wrong rows, so the
    // query is not natively representable.
    private static bool PagingAlreadyApplied(MongoQueryExpression mongoQ)
        => mongoQ.Select.Offset != null || mongoQ.Select.Limit != null;

    // The operators PopulateNativeSlots lowers into a native slot. Everything else either sets the flag in its
    // own Translate override (Select/OfType) or must drop off the native path (handled by the catch-all above).
    internal static bool IsNativeRepresentableSlotOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Where
           || methodDefinition == QueryableMethods.OrderBy
           || methodDefinition == QueryableMethods.OrderByDescending
           || methodDefinition == QueryableMethods.ThenBy
           || methodDefinition == QueryableMethods.ThenByDescending
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.Take
           || methodDefinition == QueryableMethods.Select
           || methodDefinition == QueryableMethods.OfType;

    /// <summary>
    /// Translates a Skip/Take count expression to a <see cref="MongoExpression"/>
    /// (either a <see cref="MongoConstantExpression"/> or a <see cref="MongoParameterExpression"/>).
    /// Returns <see langword="null"/> if the expression cannot be represented natively.
    /// </summary>
    private static MongoExpression? TranslateCountExpression(Expression count)
    {
        if (count is ConstantExpression constant)
            return new MongoConstantExpression(constant.Value, forSerialization: null);

        if (NativeQueryParameter.TryGetQueryParameterName(count, out var parameterName))
            return new MongoParameterExpression(parameterName, forSerialization: null);

        return null;
    }
}
