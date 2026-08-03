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
/// Populates the native-translation ops (<see cref="Expressions.MongoSelectDefinition.PipelineOps"/> —
/// match / sort / skip / limit, recorded in arrival order, EF-347) on a
/// <see cref="Expressions.MongoQueryExpression"/> for the seven slot-bearing LINQ operators, and owns the
/// whitelist that suppresses the non-native catch-all. Extracted from the QMTEV (EF-332) so
/// native-translation logic no longer lives inside the EF query dispatcher.
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

        // Post-group slot-operator guard. Once a GroupBy has been seen on this query (IsGroupBy), a slot
        // operator applied AFTER it — a Where (HAVING) / OrderBy / ThenBy / Skip / Take — operates over the
        // grouped result, NOT the entity. But every arm below resolves its member accesses against the ENTITY
        // type (the translator is built from CollectionExpression.EntityType), so a post-group predicate/sort
        // whose member name COLLIDES with a real entity property (e.g. an aggregate alias "Amount" shadowing
        // Entity.Amount) would resolve and emit a PRE-$group $match/$sort — the operator would run BEFORE
        // aggregation, silently returning wrong data. The native $group path does not support post-group
        // operators, so mark the query non-native to force a clean driver-LINQ fallback (throws only under
        // NativeOnly). Keyed on IsGroupBy (set unconditionally by TranslateGroupBy) rather than the finalized
        // Grouping so it also covers a post-group operator over a bare/unsupported grouping (which is already
        // Fallback anyway — no behavior change). Scoped to the seven slot operators only: the grouped
        // Select/OfType and the reducer/aggregate arms are excluded, so the SUPPORTED
        // GroupBy(key).Select(aggregate) (whose Select is dispatched here with IsGroupBy already true) still
        // goes native.
        // IsDistinct rides the same guard: a projected Distinct binds the same degenerate-$group machinery, so
        // a slot operator applied after it must fall back cleanly for the identical reason (it would otherwise
        // resolve against the entity type and emit a pre-$group $match/$sort). Only the Join-family decline
        // differs between GroupBy and Distinct (see MongoSelectDefinition.IsDistinct); this slot guard is shared.
        // (Centralized as HasTerminalOperator, EF-347 review follow-up — see MongoSelectDefinition.)
        //
        // EF-347 slice B: a set-op-only terminal is EXEMPT — the seven slot operators composed after a set op
        // fall through to their arms below and record into TrailingOps (MongoSelectDefinition.ActiveOps flips
        // once SetOperation is attached), so they filter/sort/page the COMBINED result and emit after the
        // set-op stage. Only a set-op-ONLY terminal is exempt: a GroupBy/Distinct/SelectMany terminal (or a
        // mixed one) still trips this guard and falls back. The deferred own-override operators (Select/
        // Distinct/GroupBy/SelectMany/OfType, chained set ops) each keep their own untouched HasTerminalOperator
        // guard, so they stay terminal after a set op.
        if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
            && IsPostGroupSlotOperator(methodDefinition))
        {
            // TODO(CSHARP-6017): delete this MarkSawUnrecordedPaging call with the rest of the paging guard.
            // This return happens BEFORE the AppendSkip/AppendLimit arms below, so a Skip/Take reaching here is
            // never recorded as an op and MongoSelectDefinition.HasPagingAnywhere would not see it — yet the
            // Skip/Take IS still in the captured method chain the driver-LINQ fallback executes, so CSHARP-6017
            // still folds it into the correlated $lookup sub-pipeline if this sequence is used as a join inner.
            // MEASURED (EF-366): Orders.Join(Regions.Select(r => new {r.Country}).Distinct().Take(1), ...) —
            // TryBindDistinctFromProjection sets IsDistinct, so HasTerminalOperator is true here and the Take(1)
            // was swallowed — returned all 5 orders where at most 2 is correct, silently, under DEFAULT Native
            // as well as explicit DriverLinq, with the inner's $group/$replaceRoot/$limit:1 visibly folded into
            // the $lookup's own pipeline. Recording the fact here is exact: it says only "a Skip/Take was seen
            // and not lowered", which is precisely the condition under which the fold applies.
            if (methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take)
            {
                mongoQ.Select.MarkSawUnrecordedPaging();
            }

            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        if (methodDefinition == QueryableMethods.Where)
        {
            // PipelineOps are emitted verbatim in arrival order (EF-347 Task 2): a Where (→ $match)
            // applied AFTER paging is recorded AFTER it too, and the lowerer emits ops in that same
            // order — correct by MongoDB's sequential pipeline semantics. No canonical-order guard.
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            // Same as Where above (EF-347 Task 2): a $sort recorded after paging is emitted after it,
            // verbatim — correct by sequential pipeline semantics. No canonical-order guard.
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.OrderBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.StartOrReplaceSort(new MongoOrdering(keyNode, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending)
        {
            // Same as OrderBy above (EF-347 Task 2): no canonical-order guard.
            var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.ThenBy;
            if (translator.TryTranslateField(keySelector.Body, out var keyNode))
                mongoQ.Select.AppendThenBy(new MongoOrdering(keyNode, ascending));
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.Skip)
        {
            // Repeated / non-canonical-order paging is natively representable (EF-347 Task 2): each
            // Skip appends a $skip op at its arrival position, and the lowerer emits ops verbatim.
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
            {
                // TODO(CSHARP-6017): delete MarkSawUnrecordedPaging with the rest of the paging guard. Same
                // reasoning as the post-terminal early return above — the Skip is declined rather than recorded,
                // but it stays in the captured chain the fallback executes. Unlike that path this one has not
                // been shown reachable from ordinary LINQ (EF parameterizes a captured/computed count, so
                // TranslateCountExpression essentially always succeeds), so it is defence-in-depth against a
                // silent-wrong-data hole, not a measured bug — see the design spec §2.9.
                mongoQ.Select.MarkSawUnrecordedPaging();
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
                mongoQ.Select.AppendSkip(count);
        }
        else if (methodDefinition == QueryableMethods.Take)
        {
            // Same as Skip above (EF-347 Task 2): repeated / non-canonical-order Take is representable.
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
            {
                // TODO(CSHARP-6017): same as the Skip arm immediately above — declined, not recorded, but still
                // in the captured chain. Defence-in-depth; not shown reachable from ordinary LINQ.
                mongoQ.Select.MarkSawUnrecordedPaging();
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
            else
                mongoQ.Select.AppendLimit(count);
        }
        else if (TryGetReducerKind(methodDefinition, out var reducerKind))
        {
            // First/FirstOrDefault/Single/SingleOrDefault (no predicate — EF normalizes the predicate
            // overloads to Where(pred) followed by the no-arg terminal, so only the no-arg forms reach
            // here). Synthesize a $limit (1 for First*, 2 for Single*) and record the reducer kind; EF
            // Core's base cardinality reduction runs over the returned IEnumerable<T> to apply the actual
            // First/Single semantics (empty => throw/null, >1 => throw for Single*).
            if (!NativeCardinalityBinder.TryBindReducer(mongoQ, reducerKind, call.Method.ReturnType))
                mongoQ.Select.MarkNotNativelyRepresentable();
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

    // The seven slot operators whose native lowering (a $match / $sort / $skip / $limit) would be emitted
    // BEFORE a $group when applied after a GroupBy — so they must force fallback on a grouped query (see the
    // post-group guard in PopulateNativeSlots). Deliberately excludes Select / OfType / GroupBy and the
    // reducer / scalar-aggregate operators so the supported grouped Select is not marked non-native.
    private static bool IsPostGroupSlotOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Where
           || methodDefinition == QueryableMethods.OrderBy
           || methodDefinition == QueryableMethods.OrderByDescending
           || methodDefinition == QueryableMethods.ThenBy
           || methodDefinition == QueryableMethods.ThenByDescending
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.Take;

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
           || methodDefinition == QueryableMethods.OfType
           || methodDefinition == QueryableMethods.Distinct
           || methodDefinition == QueryableMethods.Union
           || methodDefinition == QueryableMethods.Concat
           || methodDefinition == QueryableMethods.Intersect
           || methodDefinition == QueryableMethods.Except
           || methodDefinition == QueryableMethods.SelectManyWithCollectionSelector
           || methodDefinition == QueryableMethods.GroupByWithKeySelector
           || methodDefinition == QueryableMethods.GroupByWithKeyElementSelector
           || methodDefinition == QueryableMethods.FirstWithoutPredicate
           || methodDefinition == QueryableMethods.FirstOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.SingleWithoutPredicate
           || methodDefinition == QueryableMethods.SingleOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.CountWithoutPredicate
           || methodDefinition == QueryableMethods.LongCountWithoutPredicate
           || methodDefinition == QueryableMethods.AnyWithoutPredicate
           || methodDefinition == QueryableMethods.All
           || QueryableMethods.IsSumWithoutSelector(methodDefinition)
           || QueryableMethods.IsSumWithSelector(methodDefinition)
           || methodDefinition == QueryableMethods.MinWithoutSelector
           || methodDefinition == QueryableMethods.MinWithSelector
           || methodDefinition == QueryableMethods.MaxWithoutSelector
           || methodDefinition == QueryableMethods.MaxWithSelector
           || QueryableMethods.IsAverageWithoutSelector(methodDefinition)
           || QueryableMethods.IsAverageWithSelector(methodDefinition);

    // Maps the four no-predicate cardinality-reducer QueryableMethods to their MongoReducerKind. The
    // predicate-taking overloads are normalized by EF to Where(pred).First()/... before reaching here, so
    // they are intentionally not matched — leaving them off means the catch-all in PopulateNativeSlots
    // marks them non-native if one somehow arrives unnormalized.
    private static bool TryGetReducerKind(MethodInfo methodDefinition, out MongoReducerKind kind)
    {
        if (methodDefinition == QueryableMethods.FirstWithoutPredicate)
        {
            kind = MongoReducerKind.First;
            return true;
        }

        if (methodDefinition == QueryableMethods.FirstOrDefaultWithoutPredicate)
        {
            kind = MongoReducerKind.FirstOrDefault;
            return true;
        }

        if (methodDefinition == QueryableMethods.SingleWithoutPredicate)
        {
            kind = MongoReducerKind.Single;
            return true;
        }

        if (methodDefinition == QueryableMethods.SingleOrDefaultWithoutPredicate)
        {
            kind = MongoReducerKind.SingleOrDefault;
            return true;
        }

        kind = default;
        return false;
    }

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
