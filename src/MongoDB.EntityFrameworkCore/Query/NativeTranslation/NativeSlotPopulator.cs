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
/// Populates the native ops (<see cref="Expressions.MongoSelectDefinition.PipelineOps"/>: match/sort/skip/limit in arrival order) for the slot-bearing
/// LINQ operators, and owns the whitelist that suppresses the non-native catch-all.
/// </summary>
internal static class NativeSlotPopulator
{
    /// <summary>
    /// Populates the slots for Where, OrderBy/ThenBy[Descending], Skip and Take, and records reducers, candidate joins and vector search. Called from
    /// <see cref="Visitors.MongoQueryableMethodTranslatingExpressionVisitor"/>'s VisitMethodCall.
    /// </summary>
    internal static void PopulateNativeSlots(
        ShapedQueryExpression shapedQuery,
        MethodInfo methodDefinition,
        MethodCallExpression call)
    {
        var mongoQ = (MongoQueryExpression)shapedQuery.QueryExpression;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType);

        // A Where directly on a bare GroupBy(key) is EF's normalization of Any/Count/LongCount(pred); its parameter is the IGrouping, not the entity, so the
        // general Where arm must not attempt it. Stash the group predicate (TryBindGroupWherePredicate) for the terminal; must run before the post-terminal
        // guard below, which would always decline it.
        if (methodDefinition == QueryableMethods.Where
            && mongoQ.Select.PendingGroupKey != null && mongoQ.Select.Grouping == null)
        {
            // Paging already on the group (GroupBy(key).Skip(1).Where(...)) must decline: GroupHavingPredicate is emitted before GroupPagingOps.
            if (mongoQ.Select.PendingGroupPaging != null)
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            // Only one group predicate can be stashed; overwriting would silently drop the first Where.
            var wherePredicate = call.Arguments[1].UnwrapLambdaFromQuote();
            if (mongoQ.Select.PendingGroupPredicate != null
                || !NativeGroupByBinder.TryBindGroupWherePredicate(mongoQ, wherePredicate))
                mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        // OrderBy/ThenBy directly on the ungrouped GroupBy(key): an aggregate key (g.Count()) needs a $group accumulator that doesn't exist until the terminal
        // Select, so defer the raw selector onto PendingGroupOrderings (TryBindGroupProjection resolves or declines). Grouping == null excludes OrderBy
        // composed after the Select. A GroupBy nested on a prior grouping is admitted (SnapshotPriorGroupingForNestedGroupBy clears Grouping).
        if (mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping == null && mongoQ.Select.PendingGroupKey != null
            && (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending
                || methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending))
        {
            // Same hazard as the Where arm: GroupOrderOp's sort is emitted before GroupPagingOps.
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

        // Skip/Take directly on the ungrouped GroupBy(key): the count translates immediately. Grouping == null as above.
        if (mongoQ.Select.IsGroupBy && mongoQ.Select.Grouping == null && mongoQ.Select.PendingGroupKey != null
            && (methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take))
        {
            var count = NativeQueryParameter.TranslateConstantOrParameter(call.Arguments[1], forSerialization: null);
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

        // Post-group guard. After a GroupBy or projected Distinct a slot operator runs over the grouped result, but the arms below resolve members against
        // the entity: an aggregate alias shadowing an entity property ("Amount") would emit a pre-$group $match/$sort and silently return wrong data.
        // Grouped Select/OfType and reducer/aggregate arms aren't slot operators, so GroupBy(key).Select(aggregate) still goes native.
        //
        // Exempt: a set-op-only terminal (slot ops record into TrailingOps), and a slot op directly after a projected Distinct, whose arms resolve against the
        // Distinct's own output schema (TryResolveDistinctOrderingKey / DistinctAliasScope) or decline; Skip/Take have no field reference.
        var isPostDistinctSlot = mongoQ.Select.IsProjectedDistinctOutput && IsSevenSlotOperator(methodDefinition);

        // Also exempt: a Where directly after a finalized keyed GroupBy(key).Select(...), resolving against the Select's output aliases (ProjectedAliasScope)
        // or declining, landing in PostGroupOps after the flatten $project. Where only: ordering/paging/Distinct over grouped output aren't supported yet.
        var isPostGroupWhere = methodDefinition == QueryableMethods.Where && mongoQ.Select.IsFinalizedKeyedGroupOutput;

        // Likewise OrderBy/ThenBy/Skip/Take after a finalized keyed GroupBy(key).Select(...), recorded into PostGroupOps with the sort key resolving against
        // the output aliases. Not over a join (paging there would need the join-deferral bookkeeping).
        var isPostGroupOrdering = mongoQ.Select.IsFinalizedKeyedGroupOutput && mongoQ.Joins.Count == 0
            && (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending
                || methodDefinition == QueryableMethods.ThenBy || methodDefinition == QueryableMethods.ThenByDescending
                || methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take);

        if (mongoQ.Select.HasTerminalOperator && !mongoQ.Select.IsSetOpTerminalOnly
            && IsSevenSlotOperator(methodDefinition) && !isPostDistinctSlot && !isPostGroupWhere && !isPostGroupOrdering)
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        // Post-confirmed-join guard. Once a Select has confirmed a join and registered its $lookup, a later slot operator would record into PipelineOps, which
        // lower before the $lookup/$unwind: over a 1:N $unwind it pages/filters the un-joined outer rows against the stale outer entity type. Decline. Not
        // folded into HasTerminalOperator, which is also evaluated at join-recording time and would break reference-Include confirmation.
        //
        // Defence in depth: nav-expansion normally visits slot operators before the confirming Select (closed by TryAdmitSingleEligibleNativeJoinScope's
        // HasPaging/Cardinality conjuncts); kept in case that changes. Reverse needs no arm (a sort before a confirmed join translates against the root scope
        // only, so it commutes). Reducers are gated in TryBindReducer; scalar aggregates follow the lookup block.
        //
        // A post-group Where is exempt: a finalized grouping takes precedence in ActiveOps, recording into PostGroupOps after the $lookup/$unwind and $group.
        if (mongoQ.Select.HasConfirmedJoinLookup && IsSevenSlotOperator(methodDefinition) && !isPostGroupWhere)
        {
            mongoQ.Select.MarkNotNativelyRepresentable();
            return;
        }

        if (isPostGroupOrdering)
        {
            if (methodDefinition == QueryableMethods.Skip || methodDefinition == QueryableMethods.Take)
            {
                PopulatePagingSlot(
                    mongoQ, call,
                    methodDefinition == QueryableMethods.Skip ? mongoQ.Select.AppendSkip : mongoQ.Select.AppendLimit);
                return;
            }

            // Only an output alias is a sort key (never an entity member an alias shadows); declines otherwise.
            var postGroupKeySelector = call.Arguments[1].UnwrapLambdaFromQuote();
            translator.SelfParam = postGroupKeySelector.Parameters[0];
            translator.ProjectedAliasScope = new MongoProjectedAliasScope(
                postGroupKeySelector.Parameters[0], mongoQ.Select.Projection);
            var postGroupAscending = methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.ThenBy;
            if (translator.TryTranslateValue(postGroupKeySelector.Body, out var postGroupKey)
                && postGroupKey is MongoElementRefExpression)
            {
                var postGroupOrdering = new MongoOrdering(postGroupKey, postGroupAscending);
                if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
                    mongoQ.Select.StartOrReplaceSort(postGroupOrdering);
                else
                    mongoQ.Select.AppendThenBy(postGroupOrdering);
            }
            else
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }

            return;
        }

        if (isPostGroupWhere)
        {
            // Resolve against the grouped Select's output aliases only. On a decline, mark rather than try the arms below, which resolve members against the entity
            // or a join scope by name, which an alias can shadow.
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
            var predicate = call.Arguments[1].UnwrapLambdaFromQuote();
            // Lets a nested correlated element predicate over this root parameter build a two-scope translator instead of declining (MongoExpressionTranslator.SelfParam).
            translator.SelfParam = predicate.Parameters[0];
            // After a projected Distinct, resolve against its output alias, never the entity (a renamed member can collide with an unrelated entity property).
            if (mongoQ.Select.IsProjectedDistinctOutput)
                translator.DistinctAliasScope = mongoQ.Select.Grouping;
            if (translator.TryTranslate(predicate.Body, out var predicateNode))
                mongoQ.Select.AddPredicateConjunct(predicateNode);
            // `customers.Contains(ti.Inner)` references Inner but needs no $lookup (the FK lives on the outer document): it stays an outer-side $match and
            // deliberately doesn't call MarkJoinInnerAccessConfirmed (TryMatchInnerListContains).
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 }
                     && mongoQ.Joins.Count == 1
                     && mongoQ.Joins[0].Navigation is { } containsNavigation
                     && NativeJoinScopeTranslator.TryMatchInnerListContains(
                         predicate.Parameters[0], predicate.Body, containsNavigation, out var innerListContainsNode))
                mongoQ.Select.AddPredicateConjunct(innerListContainsNode);
            // Outer-side only: $match ops lower before the $lookup, so a Where reaching Inner is left to the arms below (deferring into PostJoinOps). Deliberately
            // shorter than TryAdmitSingleEligibleNativeJoinScope: those conjuncts protect registering a $lookup, and this arm registers nothing (an unconfirmed join
            // routes to Fallback). Don't copy this shorter set to a registering call site.
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelScope
                     && !NativeJoinScopeTranslator.ReferencesInnerScope(predicate.Parameters[0], predicate.Body)
                     && NativeJoinScopeTranslator.TryTranslatePredicate(
                         singleLevelScope, predicate.Parameters[0], predicate.Body, out var joinPredicateNode))
                mongoQ.Select.AddPredicateConjunct(joinPredicateNode);
            // Chained join scope: only the root scope is resolvable here (TryTranslateRootScopeOnly enforces it).
            else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedScope
                     && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
                         chainedScope, predicate.Parameters[0], predicate.Body, valueMode: false, out var chainedPredicateNode))
                mongoQ.Select.AddPredicateConjunct(chainedPredicateNode);
            // Chained join scope reading a non-root level (`od.Order.Customer.City == "Seattle"`): each conjunct resolves to one scope and defers into PostJoinOps,
            // after every level's $lookup/$unwind. Tried after the root-only arm so a root-only body still records ahead of the $lookup. Join confirmation is
            // left to the trailing Select.
            else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedInnerScope
                     && TryTranslateChainedScopeConjunction(
                         mongoQ, chainedInnerScope, predicate.Parameters[0], predicate.Body, out var chainedConjuncts))
            {
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                foreach (var chainedConjunct in chainedConjuncts)
                {
                    mongoQ.Select.AddPredicateConjunct(chainedConjunct);
                }
            }
            // A reference-Include null check (`Include(e => e.Manager).First(e => e.Manager == null)` -> `ti.Inner == null`). Doesn't register the lookup: the
            // Select(ti => ti.Outer) EF synthesizes next confirms it, and registering here too would double-count MarkReferenceIncludeConfirmed and trip
            // HasUnconfirmedCandidateJoin. Flips ActiveOps to PostJoinOps so the check (and First()'s $limit) run after the $lookup/$unwind. Left-outer only:
            // an inner join's $unwind drops unmatched rows, making the check vacuous. Not restricted to reference navigations: every JoinInfo.Lookup is ForceUnwind.
            else if (mongoQ.Select.JoinScope != null
                     && mongoQ.Joins.Count == 1
                     && mongoQ.Joins[0] is { IsLeftOuter: true, Lookup: { ForceUnwind: true } lookup }
                     && NativeJoinScopeTranslator.TryMatchInnerNullCheck(
                         predicate.Parameters[0], predicate.Body, out var isNotNull))
            {
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                mongoQ.Select.AddPredicateConjunct(new MongoLookupNullCheckExpression(lookup.As, isNotNull));
            }
            // A general predicate reaching a single-level join's Inner side (`o.Customer.City != "London"`), resolved via JoinScope.Levels[0].InnerPrefix; the narrower
            // null-check arm is tried first. Not restricted to reference navigations: every JoinInfo.Lookup is ForceUnwind, so a collection-nav join is already one
            // (Outer, Inner) row per pair when PostJoinOps lower, and a $match over them is exactly Join(...).Where(...). No left-outer requirement: dropping
            // unmatched rows is correct for a general comparison. Join confirmation is left to the trailing Select(ti => ti.Outer).
            else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } innerScope
                     && mongoQ.Joins.Count == 1
                     && mongoQ.Joins[0].Lookup is { ForceUnwind: true }
                     && NativeJoinScopeTranslator.TryTranslatePredicate(
                         innerScope, predicate.Parameters[0], predicate.Body, out var innerPredicateNode))
            {
                mongoQ.Select.MarkJoinInnerAccessConfirmed();
                mongoQ.Select.AddPredicateConjunct(innerPredicateNode);
            }
            // A top-level && over a single-level join scope that neither arm above takes whole, typically an Inner null check plus an Inner predicate
            // (`o != null && o.CustomerID == "ALFKI"`). Each conjunct is translated on its own into PostJoinOps (exact: a $match has no short-circuit). All-or-nothing.
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
            // An unfiltered reference-collection-nav count compared to a value (`c.Orders.Count > 2`), which translator.TryTranslate always declines (embedded only).
            else if (NativeReferenceCollectionCountPredicateBinder.TryTranslate(
                         mongoQ, predicate.Parameters[0], predicate.Body, out var countPredicateNode))
            {
                mongoQ.Select.AddPredicateConjunct(countPredicateNode);
            }
            // Any other aggregate or quantifier over a reference-collection navigation (`c.Orders.Any(o => ...)`): translates over the staged `$lookup` array;
            // commits the lookup and flips to PostJoinOps before the conjunct is recorded, as the count arm does.
            else if (NativeReferencedCollectionBinder.TryTranslatePredicate(
                         mongoQ, predicate.Parameters[0], predicate.Body, out var referencedPredicateNode))
            {
                mongoQ.Select.AddPredicateConjunct(referencedPredicateNode);
            }
            else
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.OrderBy || methodDefinition == QueryableMethods.OrderByDescending)
        {
            // OrderBy starts (replaces) a sort; ThenBy appends to it.
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
            PopulatePagingSlot(mongoQ, call, mongoQ.Select.AppendSkip);
        }
        else if (methodDefinition == QueryableMethods.Take)
        {
            PopulatePagingSlot(mongoQ, call, mongoQ.Select.AppendLimit);
        }
        else if (methodDefinition == QueryableMethods.Reverse)
        {
            // MQL has no "reverse row order" stage. The only sound native form is inverting an explicit trailing sort (TryFlipTrailingSortDirection); Reverse() over an
            // unordered source has undefined order, so decline rather than invent an unreliable $natural sort.
            if (!mongoQ.Select.TryFlipTrailingSortDirection())
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (TryGetReducerKind(methodDefinition, out var reducerKind))
        {
            // No-predicate First/Single only (EF normalizes predicate overloads to Where(pred) + the no-arg terminal). Synthesizes a $limit and records the reducer
            // kind; EF's base cardinality reduction applies the actual First/Single throws.
            if (!NativeCardinalityBinder.TryBindReducer(mongoQ, reducerKind, call.Method.ReturnType))
                mongoQ.Select.MarkNotNativelyRepresentable();
        }
        else if (methodDefinition == QueryableMethods.Join
                 || methodDefinition == QueryableMethods.GroupJoin
#if !EF8 && !EF9
                 || methodDefinition == QueryableMethods.LeftJoin
#else
                 // EF8/EF9 lower GroupJoin+DefaultIfEmpty (incl. an optional reference Include) onto this shim instead of a public LeftJoin (Ef8Ef9LeftJoinMethod);
                 // without this every EF8/EF9 optional reference Include would be non-native.
                 || MongoQueryableMethodTranslatingExpressionVisitor.IsEf8Ef9LeftJoinShim(call.Method)
#endif
                )
        {
            // Might be nav-expansion of a single-level reference Include: record a candidate rather than marking non-native; TranslateSelect confirms it when the
            // trailing IncludeExpression matches. Unconfirmed candidates route to Fallback (default-deny), so a user join is unaffected (MongoSelectDefinition
            // §Reference-Include candidate join).
            mongoQ.Select.MarkSawCandidateReferenceIncludeJoin();
        }
        else if (call.IsVectorSearch())
        {
            // Binding the slot is what makes the route native, so a native route without a $vectorSearch stage (rows in insertion order) is unreachable: bind or decline
            // are the only exits. Handled here (not IsNativeRepresentableSlotOperator) because VectorSearch has no QueryableMethods constant.
            if (!NativeVectorSearchBinder.TryBind(mongoQ, call))
            {
                mongoQ.Select.MarkNotNativelyRepresentable();
            }
        }
        else if (!IsNativeRepresentableSlotOperator(methodDefinition))
        {
            // Any other operator isn't lowered into a slot; staying native would silently drop it (e.g. Distinct run as a bare scan). Select/OfType set the flag in their own overrides.
            mongoQ.Select.MarkNotNativelyRepresentable();
        }
    }

    /// <summary>Backs the Where arm for a top-level <c>&amp;&amp;</c> over a single-level join scope: each conjunct is an Inner null check (left-outer only) or a join-scope predicate. Pure: every node or none.</summary>
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
        foreach (var node in body.FlattenAndAlso())
        {
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
    /// Translates a <c>Where</c> body over a CHAINED join scope that reads a non-root level (<c>od.Order.Customer.City == "Seattle"</c>). Each top-level
    /// <c>&amp;&amp;</c> conjunct must be a bare-level null check (<see cref="NativeJoinScopeTranslator.TryMatchScopeNullCheck"/>) on a left-outer level, or a
    /// predicate resolving to exactly one scope. All-or-nothing and pure.
    /// </summary>
    /// <remarks>
    /// Declines when no conjunct reads a non-root level: a root-only body belongs to the root-scope arm (PipelineOps, ahead of the $lookup). A non-root
    /// conjunct needs its level's Lookup to be ForceUnwind so there is one row per (Outer, Inner) pair when PostJoinOps lower.
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
        foreach (var node in body.FlattenAndAlso())
        {
            if (NativeJoinScopeTranslator.TryMatchScopeNullCheck(scope, rootParam, node, out _, out _))
            {
                // An inner join drops unmatched rows, so the check would be constant: the shared gate (TryTranslateScopeNullCheck) admits only a left-outer level with
                // a ForceUnwind lookup, whose missing field $ifNull reads as null. A matched but rejected check declines.
                if (!NativeJoinScopeProjectionBinder.TryTranslateScopeNullCheck(mongoQ, scope, rootParam, node, out var nullCheck))
                {
                    return false;
                }

                translated.Add(nullCheck);
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

    /// <summary>Records an OrderBy/ThenBy key via <paramref name="record"/>, trying each supported key shape in turn and marking the query non-native if none translates.</summary>
    private static void PopulateSortSlot(
        MongoQueryExpression mongoQ,
        MongoExpressionTranslator translator,
        MethodCallExpression call,
        bool ascending,
        Action<MongoOrdering> record)
    {
        var keySelector = call.Arguments[1].UnwrapLambdaFromQuote();
        translator.SelfParam = keySelector.Parameters[0];

        // Post-Distinct: resolve against the Distinct's own output via TryResolveDistinctOrderingKey; otherwise fall through with DistinctAliasScope set so a
        // computed key over the output translates. A name outside the key parts never resolves against the entity.
        if (mongoQ.Select.IsProjectedDistinctOutput)
        {
            var distinctGrouping = mongoQ.Select.Grouping;
            if (NativeGroupByBinder.TryResolveDistinctOrderingKey(
                    distinctGrouping, keySelector.Parameters[0], keySelector.Body, out var distinctKey))
            {
                // Same StoredOrdering rule as every arm below (EF-337): a converted or represented Distinct key would $sort by its stored form.
                if (StoredOrdering.SortKeyPreservesClrOrdering(distinctKey))
                    record(new MongoOrdering(distinctKey, ascending));
                else
                    mongoQ.Select.MarkNotNativelyRepresentable();
                return;
            }

            translator.DistinctAliasScope = distinctGrouping;
        }

        // Every arm's key must sort like the CLR value (StoredOrdering, EF-337), checked before any arm mutates state; a failing arm falls through and the final else
        // declines. A computed key is already default-serialized.
        if (translator.TryTranslateField(keySelector.Body, out var keyNode)
            && StoredOrdering.SortKeyPreservesClrOrdering(keyNode))
            record(new MongoOrdering(keyNode, ascending));
        else if (TryTranslateComputedSortKey(translator, keySelector.Body, out var computedKey))
            record(new MongoOrdering(computedKey, ascending));
        else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } singleLevelScope
                 && !NativeJoinScopeTranslator.ReferencesInnerScope(keySelector.Parameters[0], keySelector.Body)
                 && NativeJoinScopeTranslator.TryTranslateValue(
                     singleLevelScope, keySelector.Parameters[0], keySelector.Body, out var joinSortKey)
                 && StoredOrdering.SortKeyPreservesClrOrdering(joinSortKey))
            record(new MongoOrdering(joinSortKey, ascending));
        // Sort key reaching a single-level join's Inner side, deferred into PostJoinOps so it lowers after the $lookup/$unwind. Collection navigations are allowed
        // (as in the Where Inner arm): a $sort changes neither row count nor identity.
        else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } innerSortScope
                 && mongoQ.Joins.Count == 1
                 && NativeJoinScopeTranslator.TryTranslateValue(
                     innerSortScope, keySelector.Parameters[0], keySelector.Body, out var innerSortKey)
                 && StoredOrdering.SortKeyPreservesClrOrdering(innerSortKey))
        {
            // Relocate first: an earlier outer-only key in the same chain is still a MongoSortOp in PipelineOps and must share this $sort stage (DeferTrailingSortPastConfirmedJoin).
            mongoQ.Select.DeferTrailingSortPastConfirmedJoin();
            mongoQ.Select.MarkJoinInnerAccessConfirmed();
            record(new MongoOrdering(innerSortKey, ascending));
        }
        // A conditional Inner sort key (`o.Customer != null ? o.Customer.City : ""`); TryTranslateValue has no ternary handling.
        else if (mongoQ.Select.JoinScope is { Levels.Count: 1 } conditionalSortScope
                 && mongoQ.Joins.Count == 1
                 && TryTranslateConditionalSortKey(
                     mongoQ, conditionalSortScope, keySelector.Parameters[0], keySelector.Body,
                     out var conditionalSortKey)
                 && StoredOrdering.SortKeyPreservesClrOrdering(conditionalSortKey))
        {
            mongoQ.Select.DeferTrailingSortPastConfirmedJoin();
            mongoQ.Select.MarkJoinInnerAccessConfirmed();
            record(new MongoOrdering(conditionalSortKey, ascending));
        }
        else if (mongoQ.Select.JoinScope is { Levels.Count: > 1 } chainedScope
                 && NativeJoinScopeTranslator.TryTranslateRootScopeOnly(
                     chainedScope, keySelector.Parameters[0], keySelector.Body, valueMode: true, out var chainedSortKey)
                 && StoredOrdering.SortKeyPreservesClrOrdering(chainedSortKey))
            record(new MongoOrdering(chainedSortKey, ascending));
        // Sort key through a positional-ctor DTO Select: nav-expansion composes `x => new CustomerListItem(x.CustomerID, x.City).City` before the Select; EF folds
        // `new T(...).Prop` only when NewExpression.Members is set. Translate the matching ctor argument directly (parameter name = property name, ignoring case).
        else if (keySelector.Body is MemberExpression { Expression: NewExpression { Members: null } ctorExpr, Member: PropertyInfo prop }
                 && ctorExpr.Constructor is { } ctor
                 && Array.FindIndex(
                     ctor.GetParameters(), p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase)) is var argIndex
                 && argIndex >= 0
                 && argIndex < ctorExpr.Arguments.Count
                 && TryTranslateSortKeyExpression(translator, ctorExpr.Arguments[argIndex], out var ctorSortKey)
                 && StoredOrdering.SortKeyPreservesClrOrdering(ctorSortKey))
            record(new MongoOrdering(ctorSortKey, ascending));
        // A key reading an aggregate over a reference-collection navigation (`OrderBy(c => c.Orders.Count)`): a computed key over the staged `$lookup` array, so the
        // sort runs after the lookup (the chain's trailing sort is relocated with it).
        else if (NativeReferencedCollectionBinder.TryTranslateSortKey(
                     mongoQ, keySelector.Parameters[0], keySelector.Body, out var referencedSortKey))
            record(new MongoOrdering(referencedSortKey, ascending));
        else
            mongoQ.Select.MarkNotNativelyRepresentable();
    }

    /// <summary>
    /// Translates a nav-null-check ternary sort key over a single-level join's Inner side (<c>o.Customer != null ? o.Customer.City : ""</c>), mirroring
    /// <see cref="NativeJoinScopeProjectionBinder.TryBindConditionalProjection"/> but not shared with it because the two confirm the join differently (the
    /// caller owns the commit). Declines an inner join's degenerate check and a collection navigation.
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

        // scopeIndex is 1-based over Levels (0 is the root, never returned); mongoQ.Joins is the parallel 0-based list.
        var checkedJoin = mongoQ.Joins[scopeIndex - 1];
        var level = scope.Levels[scopeIndex - 1];

        // An inner join drops unmatched rows (constant check); a collection navigation has no single "is null" answer. Same guard as TryBindConditionalProjection.
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

    /// <summary>Translates a value to a sort key as a plain field, else a computed key.</summary>
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

    /// <summary>Records a <c>Skip</c>/<c>Take</c> count via <paramref name="record"/>, marking the query non-native if the count doesn't translate.</summary>
    private static void PopulatePagingSlot(
        MongoQueryExpression mongoQ, MethodCallExpression call, Action<MongoExpression> record)
    {
        // With no join recorded yet, this paging precedes any join and must not be deferred past a later one (HasPagingRecordedBeforeAnyJoin); the else arm
        // records the opposite so the join gate can decline a snapshot holding paging from both sides of a join.
        if (mongoQ.Joins.Count == 0)
            mongoQ.Select.MarkPagingRecordedBeforeAnyJoin();
        else
            mongoQ.Select.MarkPagingRecordedAfterAJoin(mongoQ.Joins.Count);

        var count = NativeQueryParameter.TranslateConstantOrParameter(call.Arguments[1], forSerialization: null);
        if (count is null)
            mongoQ.Select.MarkNotNativelyRepresentable();
        else
            record(count);
    }

    // The slot operators whose native stage would be emitted before a $group or the $lookup/$unwind; both post-terminal guards in PopulateNativeSlots key off this.
    private static bool IsSevenSlotOperator(MethodInfo methodDefinition)
        => methodDefinition == QueryableMethods.Where
           || methodDefinition == QueryableMethods.OrderBy
           || methodDefinition == QueryableMethods.OrderByDescending
           || methodDefinition == QueryableMethods.ThenBy
           || methodDefinition == QueryableMethods.ThenByDescending
           || methodDefinition == QueryableMethods.Skip
           || methodDefinition == QueryableMethods.Take;

    // The operators PopulateNativeSlots lowers natively; everything else sets the flag in its own Translate override (Select/OfType) or hits the catch-all.
    // VectorSearch is recognized separately. Built on IsSevenSlotOperator so the two lists can't disagree.
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

    // Only the no-predicate reducers: EF normalizes predicate overloads to Where(pred).First(); an unnormalized one falls to the catch-all.
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
    /// Attempts to translate a computed (non-field) sort key. <c>$sort</c> accepts field paths only, so <see cref="MongoSelectLowerer"/> materializes it into
    /// a synthetic field with <c>$set</c>/<c>$unset</c>.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="MongoAggregationExpressionRenderer.CanRender"/> (a <c>$set</c> body is an aggregation expression), turning a query-dialect-only
    /// node into a translate-time decline; a new node kind reachable as a sort key needs arms in both <c>Render</c> and <c>CanRender</c>.
    /// <see cref="MongoExpressionTranslator.TryTranslateValue"/> rejects operands lacking default serialization, so a value-converted field can't be sorted
    /// by raw stored order via a computed key. A bare constant/parameter whose CLR type <see cref="MongoDB.Bson.BsonValue.Create(object)"/> rejects would throw
    /// at pipeline-build time, invisible to <c>CanRender</c>; <see cref="TryProbeBareValueRenders"/> declines it (<c>RenderAddFields</c> separately
    /// <c>$literal</c>-wraps bare values so a <c>"$"</c> string isn't a field path). A filtered owned-collection count key's element predicate goes through
    /// <c>TranslateComparisonCore</c>, which declines a relational comparison over a property not ordering like its CLR value (<see cref="StoredOrdering"/>, EF-337).
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

    /// <summary>Strips top-level boxing <c>Convert</c>-to-<see cref="object"/> layers to recover a bare sort key's declared type, as <c>TranslateOperand</c> does; otherwise <see cref="TryProbeBareValueRenders"/> would reject a boxed value-type parameter under its reference-type allowlist.</summary>
    internal static Type UnwrapBoxingToObjectType(Expression e)
    {
        while (e is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked, Type: var t } u
               && t == typeof(object))
            e = u.Operand;

        return e.Type;
    }

    /// <summary>
    /// <see langword="false"/> only when <paramref name="translated"/> is a bare constant or parameter that would make <see cref="MongoAggregationExpressionRenderer.Render"/>
    /// throw at pipeline-build time. A constant is trial-rendered; a value-type parameter is probed with a default instance (<c>BsonValue.Create</c> admission is
    /// keyed on CLR type, matching <c>MongoPipelineFactory.SerializeParameter</c>). Other nodes return <see langword="true"/>.
    /// </summary>
    internal static bool TryProbeBareValueRenders(MongoExpression translated, Type declaredType)
    {
        switch (translated)
        {
            case MongoConstantExpression constant:
                return TryRender(constant);

            case MongoParameterExpression:
                // A looser declared type (object boxing an int) may over-decline, costing nativeness only. Must track SerializeParameter's use of BsonValue.Create.
                var underlying = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
                if (underlying.IsValueType)
                {
                    var sample = Activator.CreateInstance(underlying);
                    return TryRender(new MongoConstantExpression(sample, forSerialization: null));
                }

                // A null default proxy can't discriminate, and BsonValue.Create maps collections element-wise (int[] renders, Uri[] throws), so allow only reference types that always render.
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
