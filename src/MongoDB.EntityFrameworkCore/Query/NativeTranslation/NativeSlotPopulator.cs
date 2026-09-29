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
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Query.Visitors;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Populates the native-translation ops (<see cref="Expressions.MongoSelectDefinition.PipelineOps"/> —
/// match/sort/skip/limit, recorded in arrival order) on a <see cref="Expressions.MongoQueryExpression"/> for
/// the seven slot-bearing LINQ operators, and owns the whitelist that suppresses the non-native catch-all.
/// </summary>
internal static class NativeSlotPopulator
{
    /// <summary>
    /// Populates the native slots for the slot-bearing operators (Where, OrderBy/ThenBy[Descending], Skip, Take), and
    /// records reducers, candidate joins and vector search. Called from
    /// <see cref="Visitors.MongoQueryableMethodTranslatingExpressionVisitor"/>'s VisitMethodCall on the evaluated
    /// source.
    /// </summary>
    internal static void PopulateNativeSlots(
        ShapedQueryExpression shapedQuery,
        MethodInfo methodDefinition,
        MethodCallExpression call)
    {
        var mongoQ = (MongoQueryExpression)shapedQuery.QueryExpression;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        // A Where directly on a bare GroupBy(key) is EF's normalization of Any/Count/LongCount(pred) into
        // Where(pred).Any()/Count()/LongCount(); its parameter is the IGrouping, not the entity, so the general Where
        // arm must not attempt it. Stash the group predicate (NativeGroupByBinder.TryBindGroupWherePredicate) for the
        // terminal (TryBindGroupTerminalAggregate). Must run before the post-terminal guard below, which would always
        // decline it.
        if (methodDefinition == QueryableMethods.Where
            && mongoQ.Select.PendingGroupKey != null && mongoQ.Select.Grouping == null)
        {
            // Paging already recorded on the group (GroupBy(key).Skip(1).Where(...)) must decline: the lowerer emits
            // GroupHavingPredicate before GroupPagingOps, so the filter would silently run before the paging.
            if (mongoQ.Select.PendingGroupPaging != null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            // Only one group predicate can be stashed; overwriting it would silently drop the first Where.
            var wherePredicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (mongoQ.Select.PendingGroupPredicate != null
                || !NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePredicate))
                mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        // OrderBy/ThenBy directly on the ungrouped GroupBy(key) result: an aggregate key (g.Count()) needs a $group
        // accumulator that doesn't exist until the terminal Select, so defer the raw selector onto
        // PendingGroupOrderings; NativeGroupByBinder.TryBindGroupProjection resolves it (or declines) then.
        // Grouping == null deliberately excludes OrderBy composed after the Select (must hit the guard below; see
        // GroupBy_post_group_OrderBy_by_aggregate_matches_driver_linq). A GroupBy nested on a prior grouping is
        // admitted: SnapshotPriorGroupingForNestedGroupBy clears Grouping, and the ordering lowers around the nested
        // $group (see Nested_group_by_with_having_ordering_and_paging_on_the_nested_group).
        if (mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping == null && mongoQ.Select.PendingGroupKey != null
            && (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending
                || methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending))
        {
            // Same hazard as the Where arm above: GroupOrderOp's sort is emitted before GroupPagingOps.
            if (mongoQ.Select.PendingGroupPaging != null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            var orderKeySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            var ascending = methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.ThenBy;
            var isThenBy = methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending;

            if (isThenBy && mongoQ.Select.PendingGroupOrderings is { } existingOrderings)
                existingOrderings.Add((ascending, orderKeySelector));
            else
                mongoQ.Select.PendingGroupOrderings = [(ascending, orderKeySelector)];
            return;
        }

        // Skip/Take directly on the ungrouped GroupBy(key) result. Unlike ordering, the count translates immediately.
        // Grouping == null as above: paging after the terminal Select goes to PostGroupOps elsewhere.
        if (mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping == null && mongoQ.Select.PendingGroupKey != null
            && (methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take))
        {
            var count = TranslateCountExpression(call.Arguments[1]);
            if (count is null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            MongoSelectOp op = methodDefinition == QueryableMethods.Skip
                ? new MongoSkipOp(count)
                : new MongoLimitOp(count);
            (mongoQ.Select.PendingGroupPaging ??= []).Add(op);
            return;
        }

        // Post-group guard. After a GroupBy or projected Distinct, a slot operator runs over the grouped result, but
        // the arms below resolve members against the entity: an aggregate alias shadowing an entity property (e.g.
        // "Amount") would emit a pre-$group $match/$sort and silently return wrong data. The grouped Select/OfType and
        // reducer/aggregate arms aren't slot operators, so GroupBy(key).Select(aggregate) still goes native.
        //
        // Exempt: a set-op-only terminal (slot ops record into TrailingOps and run over the combined result), and a
        // slot op directly after a projected Distinct, whose arms resolve against the Distinct's own output schema
        // (NativeGroupByBinder.TryResolveDistinctOrderingKey / MongoExpressionTranslator.DistinctAliasScope) or
        // decline; Skip/Take have no field reference at all.
        var isPostDistinctSlot = mongoQ.Select.IsDistinct && !mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping != null
            && IsSevenSlotOperator(methodDefinition);

        // Also exempt: a Where directly after a finalized keyed GroupBy(key).Select(...), which resolves against the
        // Select's output aliases (MongoExpressionTranslator.ProjectedAliasScope) or declines, and lands in
        // PostGroupOps after the flatten $project. Where only: ordering/paging/Distinct over the grouped output
        // aren't supported yet.
        var isPostGroupWhere = methodDefinition == QueryableMethods.Where && mongoQ.Select.IsFinalizedKeyedGroupOutput;

        if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
            && IsSevenSlotOperator(methodDefinition) && !isPostDistinctSlot && !isPostGroupWhere)
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        // Post-confirmed-join guard. Once a Select has confirmed a join and registered its $lookup, a later slot
        // operator would record into PipelineOps, which lower before the $lookup/$unwind: over a 1:N $unwind it
        // pages/filters the un-joined outer rows, and members resolve against the stale outer entity type. Decline
        // rather than return wrong rows. Not folded into HasTerminalOperator: that is also evaluated at join-recording
        // time and would break reference-Include confirmation.
        //
        // Defence in depth: EF's nav-expansion normally visits slot operators before the confirming Select, an ordering
        // closed by IsSingleEligibleNativeJoinScope's HasPaging/Cardinality conjuncts. Kept in case that ordering
        // changes. Reverse needs no arm: a sort recorded before a confirmed join translates against the root scope
        // only, so it commutes with the join (see JoinScopeWhereSlotPopulationTests). Reducers are gated in
        // NativeCardinalityBinder.TryBindReducer; scalar aggregates need no gate (their stage follows the lookup
        // block).
        //
        // A post-group Where is exempt: a finalized grouping takes precedence in ActiveOps, so it records into
        // PostGroupOps, after the $lookup/$unwind and the $group (a GroupBy over a confirmed join scope).
        if (mongoQ.Select.HasConfirmedJoinLookup && IsSevenSlotOperator(methodDefinition) && !isPostGroupWhere)
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        if (isPostGroupWhere)
        {
            // Resolve against the grouped Select's output aliases only. On a decline, mark rather than try the arms
            // below: they resolve members against the entity or a join scope by name, which an alias can shadow.
            var postGroupPredicate = call.Arguments[1].UnwrapLambdaFromQuote();
            translator.SelfParam = postGroupPredicate.Parameters[0];
            translator.ProjectedAliasScope = new MongoProjectedAliasScope(
                postGroupPredicate.Parameters[0], mongoQ.Select.Projection);
            if (translator.TryTranslate(postGroupPredicate.Body, out var postGroupNode))
                mongoQ.Select.AddPredicateConjunct(postGroupNode);
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.Where)
        {
            // PipelineOps lower in arrival order, so a Where after paging correctly runs after it.
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            // Lets a nested correlated element predicate over this same root parameter build a two-scope translator
            // instead of declining — see MongoExpressionTranslator.SelfParam.
            translator.SelfParam = predicate.Parameters[0];
            // After a projected Distinct, resolve against its output alias, never the entity (a renamed member can
            // collide with an unrelated entity property) — see MongoExpressionTranslator.DistinctAliasScope.
            if (mongoQ.Select.IsDistinct && !mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping is { } distinctScope)
                translator.DistinctAliasScope = distinctScope;
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            // `customers.Contains(ti.Inner)` (Where_navigation_contains): references Inner but needs no $lookup, since
            // the FK lives on the outer document, so it stays an outer-side $match and deliberately doesn't call
            // MarkJoinInnerAccessConfirmed. See NativeJoinScopeTranslator.TryMatchInnerListContains.
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 }
                     && mongoQ.Joins.Count == 1
                     && mongoQ.Joins[0].Navigation is { } containsNavigation
                     && NativeJoinScopeTranslator.TryMatchInnerListContains(
                         predicate.Parameters[0], predicate.Body, containsNavigation, out var innerListContainsNode))
                mongoQ.Select.AddPredicateConjunct(innerListContainsNode);
            // Outer-side only: $match ops lower before the $lookup, so a Where reaching Inner is left to the arms
            // below (which defer into PostJoinOps). This gate is deliberately shorter than
            // IsSingleEligibleNativeJoinScope: those conjuncts protect registering a $lookup, and this arm registers
            // nothing (an unconfirmed join routes to Fallback, discarding the conjunct). Don't copy this shorter set to
            // a registering call site.
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelScope
                     && !NativeJoinScopeTranslator.ReferencesInnerScope(predicate.Parameters[0], predicate.Body)
                     && NativeJoinScopeTranslator.TryTranslatePredicate(
                         singleLevelScope, predicate.Parameters[0], predicate.Body, out var joinPredicateNode))
                mongoQ.Select.AddPredicateConjunct(joinPredicateNode);
            // Chained join scope: only the root scope is resolvable here; TryTranslateRootScopeOnly enforces it.
            else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedScope
                     && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
                         chainedScope, predicate.Parameters[0], predicate.Body, valueMode: false, out var chainedPredicateNode))
                mongoQ.Select.AddPredicateConjunct(chainedPredicateNode);
            // Chained join scope reading a non-root level (`od.Order.Customer.City == "Seattle"` after nav-expansion):
            // each conjunct resolves to one scope and defers into PostJoinOps, after every level's $lookup/$unwind.
            // Tried after the root-only arm, so a root-only body still records ahead of the $lookup. Join
            // confirmation is left to the trailing Select, as in the depth-1 Inner arms.
            else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedInnerScope
                     && TryTranslateChainedScopeConjunction(
                         mongoQ, chainedInnerScope, predicate.Parameters[0], predicate.Body, out var chainedConjuncts))
            {
                // Flip before AddPredicateConjunct so the conjuncts land in PostJoinOps.
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                foreach (var chainedConjunct in chainedConjuncts)
                {
                    mongoQ.Select.AddPredicateConjunct(chainedConjunct);
                }
            }
            // A reference-Include null check (`Include(e => e.Manager).First(e => e.Manager == null)` ->
            // `ti.Inner == null`). Doesn't register the lookup: the Select(ti => ti.Outer) EF always synthesizes next
            // confirms it, and registering here too would double-count MarkReferenceIncludeConfirmed and trip
            // HasUnconfirmedCandidateJoin. It only translates the predicate and flips ActiveOps to PostJoinOps, so the
            // check (and First()'s $limit) run after the $lookup/$unwind. Left-outer only: an inner join's $unwind
            // drops unmatched rows, making the check vacuous. Not restricted to reference navigations: every
            // JoinInfo.Lookup is ForceUnwind, so a left-outer collection join also yields one (Outer, Inner-or-missing)
            // row per pair.
            else if (mongoQ.Select.JoinScope != null
                     && mongoQ.Joins.Count == 1
                     && mongoQ.Joins[0] is { IsLeftOuter: true, Lookup: { ForceUnwind: true } lookup }
                     && NativeJoinScopeTranslator.TryMatchInnerNullCheck(
                         predicate.Parameters[0], predicate.Body, out var isNotNull))
            {
                // Flip before AddPredicateConjunct so the conjunct lands in PostJoinOps.
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                mongoQ.Select.AddPredicateConjunct(new MongoLookupNullCheckExpression(lookup.As, isNotNull));
            }
            // A general predicate reaching a single-level join's Inner side (`o.Customer.City != "London"`);
            // TryTranslatePredicate resolves Inner members via JoinScope.Levels[0].InnerPrefix. The narrower null-check
            // arm is tried first. Not restricted to reference navigations: every JoinInfo.Lookup is ForceUnwind, so by
            // the time PostJoinOps lower a collection-nav join is already one (Outer, Inner) row per pair, and a $match
            // over them is exactly Join(...).Where(...) (EF Core's GroupJoin_Where resolves to the collection nav
            // Customer.Orders). No left-outer requirement, since dropping unmatched rows is correct for a general
            // comparison. Join confirmation is left to the trailing Select(ti => ti.Outer), as in the null-check arm.
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } innerScope
                     && mongoQ.Joins.Count == 1
                     && mongoQ.Joins[0].Lookup is { ForceUnwind: true }
                     && NativeJoinScopeTranslator.TryTranslatePredicate(
                         innerScope, predicate.Parameters[0], predicate.Body, out var innerPredicateNode))
            {
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                mongoQ.Select.AddPredicateConjunct(innerPredicateNode);
            }
            // A top-level && over a single-level join scope that neither arm above takes whole — typically an Inner
            // null check plus an Inner predicate (EF Core's GroupJoin_DefaultIfEmpty_Where,
            // `o != null && o.CustomerID == "ALFKI"`). Each conjunct is translated on its own into PostJoinOps;
            // splitting is exact since a $match has no short-circuit to preserve. All-or-nothing.
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } conjunctionScope
                     && mongoQ.Joins.Count == 1
                     && predicate.Body is BinaryExpression { NodeType: ExpressionType.AndAlso }
                     && TryTranslateJoinScopeConjunction(
                         conjunctionScope, mongoQ.Joins[0], predicate.Parameters[0], predicate.Body, out var conjunctNodes))
            {
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                foreach (var conjunctNode in conjunctNodes)
                {
                    mongoQ.Select.AddPredicateConjunct(conjunctNode);
                }
            }
            // An unfiltered reference-collection-nav count compared to a value (`c.Orders.Count > 2`), which
            // translator.TryTranslate always declines (it handles only embedded collections).
            else if (NativeReferenceCollectionCountPredicateBinder.TryTranslate(
                         mongoQ, predicate.Parameters[0], predicate.Body, out var countPredicateNode))
            {
                mongoQ.Select.AddPredicateConjunct(countPredicateNode);
            }
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            // OrderBy starts (replaces) a sort; ThenBy appends to it. Otherwise identical.
            PopulateSortSlot(
                mongoQ, translator, call,
                ascending: methodDefinition == QueryableMethods.OrderBy,
                record: mongoQ.Select.StartOrReplaceSort);
        }
        else if (methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending)
        {
            PopulateSortSlot(
                mongoQ, translator, call,
                ascending: methodDefinition == QueryableMethods.ThenBy,
                record: mongoQ.Select.AppendThenBy);
        }
        else if (methodDefinition == QueryableMethods.Skip)
        {
            // Each Skip appends a $skip at its arrival position. With no join recorded yet, this paging precedes any
            // join and must not be deferred past a later one — see
            // MongoSelectDefinition.HasPagingRecordedBeforeAnyJoin. The else arm records the opposite, so the join
            // gate can decline a snapshot holding paging from both sides of a join.
            if (mongoQ.Joins.Count == 0)
                mongoQ.Select.MarkPagingRecordedBeforeAnyJoin();
            else
                mongoQ.Select.MarkPagingRecordedAfterAJoin(mongoQ.Joins.Count);
            PopulatePagingSlot(mongoQ, call, mongoQ.Select.AppendSkip);
        }
        else if (methodDefinition == QueryableMethods.Take)
        {
            if (mongoQ.Joins.Count == 0)
                mongoQ.Select.MarkPagingRecordedBeforeAnyJoin();
            else
                mongoQ.Select.MarkPagingRecordedAfterAJoin(mongoQ.Joins.Count);
            PopulatePagingSlot(mongoQ, call, mongoQ.Select.AppendLimit);
        }
        else if (methodDefinition == QueryableMethods.Reverse)
        {
            // MQL has no "reverse row order" stage. The only sound native form is inverting an explicit
            // trailing sort (the exact complement of the original order — see
            // MongoSelectDefinition.TryFlipTrailingSortDirection). Reverse() over an otherwise-unordered
            // source has undefined result order in LINQ generally, so decline rather than invent an
            // unreliable $natural sort.
            if (!mongoQ.Select.TryFlipTrailingSortDirection())
                mongoQ.Select.MarkNotNativelyRepresentable();
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
        else if (methodDefinition == QueryableMethods.Join
                 || methodDefinition == QueryableMethods.GroupJoin
#if !EF8 && !EF9
                 || methodDefinition == QueryableMethods.LeftJoin
#else
                 // EF8/EF9 lower GroupJoin+DefaultIfEmpty (including an optional reference Include) onto this shim
                 // instead of a public LeftJoin (see Ef8Ef9LeftJoinMethod); without this, every EF8/EF9 optional
                 // reference Include would be marked non-native.
                 || MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(call.Method)
#endif
                )
        {
            // Might be EF's nav-expansion of a single-level reference Include. Record a candidate rather
            // than marking non-native; TranslateSelect confirms it when the trailing IncludeExpression
            // matches the recognizer. Unconfirmed candidates route to Fallback, so this is default-deny and
            // a user join is unaffected. See MongoSelectDefinition §Reference-Include candidate join.
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
        else if (call.IsVectorSearch())
        {
            // Binding the slot is what makes the route native, so a native route without a $vectorSearch stage (rows in
            // insertion order, not score order) is unreachable: bind or decline are the only exits. Handled here rather
            // than in IsNativeRepresentableSlotOperator because VectorSearch has no QueryableMethods constant.
            if (!NativeVectorSearchBinder.TryBind(mongoQ, call))
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (!IsNativeRepresentableSlotOperator(methodDefinition))
        {
            // Any other operator isn't lowered into a slot; leaving the query native would silently drop it (e.g. a
            // Distinct run as a bare collection scan). Select/OfType set the flag in their own Translate overrides.
            mongoQ.Select.MarkNotNativelyRepresentable();
        }
    }

    /// <summary>
    /// Backs the Where arm for a top-level <c>&amp;&amp;</c> over a single-level join scope: flattens the conjunction
    /// and translates each conjunct as an Inner null check (left-outer joins only) or a join-scope predicate via
    /// <see cref="NativeJoinScopeTranslator.TryTranslatePredicate"/>. Pure: returns every node or none.
    /// </summary>
    private static bool TryTranslateJoinScopeConjunction(
        MongoJoinScope scope, JoinInfo join, ParameterExpression rootParam, Expression body,
        [NotNullWhen(true)] out List<MongoExpression>? conjuncts)
    {
        conjuncts = null;
        if (join.Lookup is not { ForceUnwind: true } lookup)
        {
            return false;
        }

        var translated = new List<MongoExpression>();
        var pending = new Stack<Expression>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
            {
                // Right pushed first so conjuncts are emitted in source order.
                pending.Push(andAlso.Right);
                pending.Push(andAlso.Left);
                continue;
            }

            if (NativeJoinScopeTranslator.TryMatchInnerNullCheck(rootParam, node, out var isNotNull))
            {
                if (!join.IsLeftOuter)
                {
                    return false;
                }

                translated.Add(new MongoLookupNullCheckExpression(lookup.As, isNotNull));
            }
            else if (NativeJoinScopeTranslator.TryTranslatePredicate(scope, rootParam, node, out var conjunct))
            {
                translated.Add(conjunct);
            }
            else
            {
                return false;
            }
        }

        conjuncts = translated;
        return true;
    }

    /// <summary>
    /// Translates a <c>Where</c> body over a CHAINED join scope (<c>Levels.Count &gt; 1</c>) that reads a non-root
    /// level — nav-expansion's shape for a multi-hop reference-navigation filter (<c>od.Order.Customer.City ==
    /// "Seattle"</c>). Each top-level <c>&amp;&amp;</c> conjunct must be either a bare-level null check
    /// (<see cref="NativeJoinScopeTranslator.TryMatchScopeNullCheck"/>) on a left-outer level, or a predicate resolving to
    /// exactly one scope. Splitting is exact since a <c>$match</c> has no short-circuit to preserve. All-or-nothing
    /// and pure: nothing is recorded on decline.
    /// </summary>
    /// <remarks>
    /// Declines when no conjunct reads a non-root level: a root-only body belongs to the root-scope arm, which records
    /// into PipelineOps ahead of the $lookup. A non-root conjunct needs its level's Lookup to be ForceUnwind, like
    /// the depth-1 Inner arm, so by the time PostJoinOps lower there is one row per (Outer, Inner) pair.
    /// </remarks>
    private static bool TryTranslateChainedScopeConjunction(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, Expression body,
        [NotNullWhen(true)] out List<MongoExpression>? conjuncts)
    {
        conjuncts = null;
        if (mongoQ.Joins.Count != scope.Levels.Count)
        {
            return false;
        }

        var translated = new List<MongoExpression>();
        var readsNonRootScope = false;
        var pending = new Stack<Expression>();
        pending.Push(body);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is BinaryExpression { NodeType: ExpressionType.AndAlso } andAlso)
            {
                // Right pushed first so conjuncts are emitted in source order.
                pending.Push(andAlso.Right);
                pending.Push(andAlso.Left);
                continue;
            }

            if (NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, rootParam, node, out var nullCheckIndex, out var isNotNull))
            {
                // An inner join drops unmatched rows, so the check would be constant. Same guard as
                // NativeJoinScopeProjectionBinder.TryTranslateScopeNullCheckConditional (left-outer level plus a
                // ForceUnwind lookup, whose missing field $ifNull reads as null).
                var level = scope.Levels[nullCheckIndex - 1];
                if (!level.IsLeftOuter || mongoQ.Joins[nullCheckIndex - 1].Lookup is not { ForceUnwind: true })
                {
                    return false;
                }

                translated.Add(new MongoLookupNullCheckExpression(level.InnerPrefix, isNotNull));
                readsNonRootScope = true;
            }
            else if (NativeJoinScopeTranslator.TryTranslateSingleScopePredicate(
                         scope, rootParam, node, out var scopeIndex, out var conjunct))
            {
                if (scopeIndex > 0)
                {
                    if (mongoQ.Joins[scopeIndex - 1].Lookup is not { ForceUnwind: true })
                    {
                        return false;
                    }

                    readsNonRootScope = true;
                }

                translated.Add(conjunct);
            }
            else
            {
                return false;
            }
        }

        if (!readsNonRootScope)
        {
            return false;
        }

        conjuncts = translated;
        return true;
    }

    /// <summary>
    /// Records an OrderBy/ThenBy[Descending] key via <paramref name="record"/>, trying each supported key shape in turn
    /// and marking the query non-native if none translates.
    /// </summary>
    private static void PopulateSortSlot(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        MethodCallExpression call,
        bool ascending,
        Action<MongoOrdering> record)
    {
        var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
        translator.SelfParam = keySelector.Parameters[0];

        // Post-Distinct: resolve against the Distinct's own output (identity key or a named, possibly computed, key
        // part) via TryResolveDistinctOrderingKey; otherwise fall through with DistinctAliasScope set so a computed key
        // over the Distinct's output translates. A name outside the Distinct's key parts never resolves against the
        // entity.
        if (mongoQ.Select.IsDistinct && !mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping is { } distinctGrouping)
        {
            if (NativeGroupByBinder.TryResolveDistinctOrderingKey(
                    distinctGrouping, keySelector.Parameters[0], keySelector.Body, out var distinctKey))
            {
                record(new MongoOrdering(distinctKey, ascending));
                return;
            }

            translator.DistinctAliasScope = distinctGrouping;
        }

        if (translator.TryTranslateField(keySelector.Body, out var keyNode))
            record(new MongoOrdering(keyNode, ascending));
        else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
            record(new MongoOrdering(computedKey, ascending));
        else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelScope
                 && !NativeJoinScopeTranslator.ReferencesInnerScope(keySelector.Parameters[0], keySelector.Body)
                 && NativeJoinScopeTranslator.TryTranslateValue(
                     singleLevelScope, keySelector.Parameters[0], keySelector.Body, out var joinSortKey))
            record(new MongoOrdering(joinSortKey, ascending));
        // Sort key reaching a single-level join's Inner side (`Join(...).OrderBy(x => x.Inner.OrderID)`), deferred into
        // PostJoinOps so it lowers after the $lookup/$unwind. Collection navigations are allowed (as in the Where
        // Inner arm): a $sort changes neither row count nor identity, so it's correct for any join cardinality (and
        // Customers.Join(Orders, ...) resolves to the Customer.Orders collection navigation).
        else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } innerSortScope
                 && mongoQ.Joins.Count == 1
                 && NativeJoinScopeTranslator.TryTranslateValue(
                     innerSortScope, keySelector.Parameters[0], keySelector.Body, out var innerSortKey))
        {
            // Relocate first: an earlier outer-only key in the same chain is still a MongoSortOp in PipelineOps and
            // must share this $sort stage, or the order is silently wrong. See DeferTrailingSortPastConfirmedJoin.
            mongoQ.Select.DeferTrailingSortPastConfirmedJoin();
            mongoQ.Select.MarkJoinInnerAccessConfirmed();
            record(new MongoOrdering(innerSortKey, ascending));
        }
        // A conditional Inner sort key (`o.Customer != null ? o.Customer.City : ""`); TryTranslateValue has no ternary
        // handling, so the arm above never matches it.
        else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } conditionalSortScope
                 && mongoQ.Joins.Count == 1
                 && TryTranslateConditionalSortKey(
                     mongoQ, conditionalSortScope, keySelector.Parameters[0], keySelector.Body,
                     out var conditionalSortKey))
        {
            // Same relocate-then-confirm sequence as above.
            mongoQ.Select.DeferTrailingSortPastConfirmedJoin();
            mongoQ.Select.MarkJoinInnerAccessConfirmed();
            record(new MongoOrdering(conditionalSortKey, ascending));
        }
        else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedScope
                 && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
                     chainedScope, keySelector.Parameters[0], keySelector.Body, valueMode: true, out var chainedSortKey))
            record(new MongoOrdering(chainedSortKey, ascending));
        // Sort key through a positional-ctor DTO Select (Member_binding_after_ctor_arguments_fails_with_client_eval).
        // Nav-expansion composes `x => new CustomerListItem(x.CustomerID, x.City).City` and visits it before the Select
        // (no projection aliases yet); EF folds `new T(...).Prop` only when NewExpression.Members is set. Translate the
        // matching ctor argument directly (same root parameter), matched by parameter name = property name, ignoring
        // case.
        else if (keySelector.Body is MemberExpression { Expression: NewExpression { Members: null } ctorExpr, Member: PropertyInfo prop }
                 && ctorExpr.Constructor is { } ctor
                 && Array.FindIndex(
                     ctor.GetParameters(), p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase)) is var argIndex
                 && argIndex >= 0
                 && argIndex < ctorExpr.Arguments.Count
                 && TryTranslateSortKeyExpression(translator, ctorExpr.Arguments[argIndex], out var ctorSortKey))
            record(new MongoOrdering(ctorSortKey, ascending));
        else
            mongoQ.Select.MarkNotNativelyRepresentable();
    }

    /// <summary>
    /// Translates a nav-null-check ternary sort key over a single-level join's Inner side
    /// (<c>o =&gt; o.Customer != null ? o.Customer.City : ""</c>), mirroring
    /// <see cref="NativeJoinScopeProjectionBinder.TryBindConditionalProjection"/>. Not shared with it because the two
    /// confirm the join differently, so the caller owns the commit step. Declines an inner join's degenerate check and
    /// a collection navigation (see <c>JoinScopeOrderBySlotPopulationTests</c>).
    /// </summary>
    private static bool TryTranslateConditionalSortKey(
        MongoQueryExpression mongoQ, MongoJoinScope scope, ParameterExpression rootParam, Expression body,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (body is not ConditionalExpression conditional
            || !NativeJoinScopeTranslator.TryMatchScopeNullCheck(
                scope, rootParam, conditional.Test, out var scopeIndex, out var isNotNull))
        {
            return false;
        }

        // scopeIndex is 1-based over Levels (see TryMatchScopeNullCheck's own contract: 0 is the root, never
        // returned); mongoQ.Joins is the parallel 0-based list TranslateJoinCore built the scope from.
        var checkedJoin = mongoQ.Joins[scopeIndex - 1];
        var level = scope.Levels[scopeIndex - 1];

        // An inner join drops unmatched rows, so the check is constant; a collection navigation has no single
        // "is null" answer. Same guard as TryBindConditionalProjection.
        if (!level.IsLeftOuter || checkedJoin.Navigation is { IsCollection: true })
        {
            return false;
        }

        if (!NativeJoinScopeProjectionBinder.TryTranslateConditionalBranch(scope, rootParam, conditional.IfTrue, out var ifTrue)
            || !NativeJoinScopeProjectionBinder.TryTranslateConditionalBranch(scope, rootParam, conditional.IfFalse, out var ifFalse))
        {
            return false;
        }

        result = new MongoConditionalExpression(
            new MongoLookupNullCheckExpression(level.InnerPrefix, isNotNull), ifTrue, ifFalse);
        return true;
    }

    /// <summary>
    /// Translates a value to a sort key as a plain field, else a computed key.
    /// </summary>
    private static bool TryTranslateSortKeyExpression(
        MongoExpressionTranslator translator, Expression valueExpression, [NotNullWhen(true)] out MongoExpression? sortKey)
    {
        if (translator.TryTranslateField(valueExpression, out var fieldKey))
        {
            sortKey = fieldKey;
            return true;
        }

        if (TryTranslateComputedSortKey(translator, valueExpression, out var computedKey))
        {
            sortKey = computedKey;
            return true;
        }

        sortKey = null;
        return false;
    }

    /// <summary>
    /// Records a <c>Skip</c>/<c>Take</c> count via <paramref name="record"/>, marking the query non-native if
    /// the count expression does not translate. The two operators differ only in which op they append.
    /// </summary>
    private static void PopulatePagingSlot(
        MongoQueryExpression mongoQ, MethodCallExpression call, Action<MongoExpression> record)
    {
        var count = TranslateCountExpression(call.Arguments[1]);
        if (count is null)
            mongoQ.Select.MarkNotNativelyRepresentable();
        else
            record(count);
    }

    // The slot operators whose native stage would be emitted before a $group (after GroupBy) or the $lookup/$unwind
    // (after a confirmed join); both post-terminal guards in PopulateNativeSlots key off this list.
    private static bool IsSevenSlotOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Where
           || methodDefinition == QueryableMethods.OrderBy
           || methodDefinition == QueryableMethods.OrderByDescending
           || methodDefinition == QueryableMethods.ThenBy
           || methodDefinition == QueryableMethods.ThenByDescending
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.Take;

    // The operators PopulateNativeSlots lowers natively; everything else sets the flag in its own Translate override
    // (Select/OfType) or hits the catch-all. VectorSearch is recognized separately (no QueryableMethods constant).
    // Built on IsSevenSlotOperator so the two lists can't disagree.
    internal static bool IsNativeRepresentableSlotOperator(MethodInfo methodDefinition)
        => IsSevenSlotOperator(methodDefinition)
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
           || methodDefinition == QueryableMethods.LastWithoutPredicate
           || methodDefinition == QueryableMethods.LastOrDefaultWithoutPredicate
           || methodDefinition == QueryableMethods.Reverse
           || methodDefinition == QueryableMethods.CountWithoutPredicate
           || methodDefinition == QueryableMethods.LongCountWithoutPredicate
           || methodDefinition == QueryableMethods.AnyWithoutPredicate
           || methodDefinition == QueryableMethods.Contains
           || methodDefinition == QueryableMethods.All
           || QueryableMethods.IsSumWithoutSelector(methodDefinition)
           || QueryableMethods.IsSumWithSelector(methodDefinition)
           || methodDefinition == QueryableMethods.MinWithoutSelector
           || methodDefinition == QueryableMethods.MinWithSelector
           || methodDefinition == QueryableMethods.MaxWithoutSelector
           || methodDefinition == QueryableMethods.MaxWithSelector
           || QueryableMethods.IsAverageWithoutSelector(methodDefinition)
           || QueryableMethods.IsAverageWithSelector(methodDefinition);

    // Only the no-predicate reducers: EF normalizes predicate overloads to Where(pred).First() etc., and an
    // unnormalized one falls to the catch-all.
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

        if (methodDefinition == QueryableMethods.LastWithoutPredicate)
        {
            kind = MongoReducerKind.Last;
            return true;
        }

        if (methodDefinition == QueryableMethods.LastOrDefaultWithoutPredicate)
        {
            kind = MongoReducerKind.LastOrDefault;
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

    /// <summary>
    /// Attempts to translate a computed (non-field) sort key. <c>$sort</c> accepts field paths only, so
    /// <see cref="MongoSelectLowerer"/> materializes it into a synthetic field with <c>$set</c>/<c>$unset</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <see cref="MongoAggregationExpressionRenderer.CanRender"/> (a <c>$set</c> body is an aggregation
    /// expression), turning a query-dialect-only node into a translate-time decline rather than a render-time throw. A
    /// new node kind reachable as a sort key needs arms in both <c>Render</c> and <c>CanRender</c>.
    /// </para>
    /// <para>
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> rejects operands lacking default serialization, so a
    /// value-converted field can't be sorted by raw stored order via a computed key (a plain field key has no such
    /// guard).
    /// </para>
    /// <para>
    /// A bare constant/parameter whose CLR type <see cref="MongoDB.Bson.BsonValue.Create(object)"/> rejects would throw
    /// at pipeline-build time, which <c>CanRender</c> can't see; <see cref="TryProbeBareValueRenders"/> declines it.
    /// (<c>RenderAddFields</c> separately <c>$literal</c>-wraps bare values so a <c>"$"</c> string isn't a field path.)
    /// </para>
    /// <para>
    /// A filtered owned-collection count key skips the operand-serialization guard inside its element predicate; over a
    /// non-default <c>BsonRepresentation</c> it compares the stored representation, but driver-LINQ does the same.
    /// </para>
    /// </remarks>
    private static bool TryTranslateComputedSortKey(
        MongoExpressionTranslator translator,
        Expression keySelectorBody,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;

        if (!translator.TryTranslateValue(keySelectorBody, out var translated))
            return false;

        if (!MongoAggregationExpressionRenderer.CanRender(translated))
            return false;

        if (!TryProbeBareValueRenders(translated, UnwrapBoxingToObjectType(keySelectorBody)))
            return false;

        result = translated;
        return true;
    }

    /// <summary>
    /// Strips top-level boxing <c>Convert</c>-to-<see cref="object"/> layers to recover a bare sort key's declared
    /// type, as <c>MongoExpressionTranslator.TranslateOperand</c> does; otherwise
    /// <see cref="TryProbeBareValueRenders"/> would reject a boxed value-type parameter under its reference-type
    /// allowlist.
    /// </summary>
    internal static Type UnwrapBoxingToObjectType(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Type: var t } u
               && t == typeof(object))
            e = u.Operand;

        return e.Type;
    }

    /// <summary>
    /// Returns <see langword="false"/> only when <paramref name="translated"/> is a bare constant or parameter whose
    /// value would make <see cref="MongoAggregationExpressionRenderer.Render"/> throw at pipeline-build time. A
    /// constant is trial-rendered; a value-type parameter is probed with a default instance (<c>BsonValue.Create</c>
    /// admission is keyed on CLR type, matching <c>MongoPipelineFactory.SerializeParameter</c>). Other nodes return
    /// <see langword="true"/>.
    /// </summary>
    internal static bool TryProbeBareValueRenders(MongoExpression translated, Type declaredType)
    {
        switch (translated)
        {
            case MongoConstantExpression constant:
                return TryRender(constant);

            case MongoParameterExpression:
                // A looser declared type (object boxing an int) may over-decline, costing nativeness only. Must track
                // SerializeParameter's use of BsonValue.Create.
                var underlying = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
                if (underlying.IsValueType)
                {
                    var sample = Activator.CreateInstance(underlying);
                    return TryRender(new MongoConstantExpression(sample, forSerialization: null));
                }

                // A null default proxy can't discriminate, and BsonValue.Create maps collections element-wise (int[]
                // renders, Uri[] throws), so allow only reference types that always render.
                return underlying == typeof(string) || typeof(BsonValue).IsAssignableFrom(underlying);

            default:
                return true;
        }

        static bool TryRender(MongoExpression node)
        {
            try
            {
                MongoAggregationExpressionRenderer.Render(node, new PlaceholderTable());
                return true;
            }
            catch (Exception)
            {
                // Any throw means decline (fallback, or a clean NativeOnly throw) rather than crash at build time.
                return false;
            }
        }
    }
}
