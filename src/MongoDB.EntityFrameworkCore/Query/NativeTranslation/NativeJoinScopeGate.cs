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

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Admissibility gates for resolving a <c>Select</c>, aggregate or <c>GroupBy</c> through a join scope (<see cref="MongoJoinScope"/>). Shared by the
/// queryable-method translator's join-scope <c>Select</c> arms, <see cref="NativeCardinalityBinder"/> and the GroupBy binder.
/// </summary>
internal static class NativeJoinScopeGate
{
    /// <summary>
    /// Shared admissibility gate for the join-scope <c>Select</c> arms in <see cref="Visitors.MongoQueryableMethodTranslatingExpressionVisitor"/>.<c>TranslateSelect</c>, also used by
    /// <see cref="NativeTranslation.NativeCardinalityBinder.TryBindAggregate"/> (a bare <c>Any()</c>/<c>Count()</c> after a
    /// join has no trailing <c>Select</c>, so it needs a second confirming site). On success <paramref name="joinInfo"/> is
    /// the last join in the chain. <b>Mutates on success</b> (defers recorded pipeline ops past the join when paging requires it), so call it
    /// after every pure check; <see cref="EvaluateSingleJoinScope"/> is the non-mutating form.
    /// </summary>
    /// <remarks>
    /// <c>scope.Levels.Count == Joins.Count</c> means every join is individually eligible (<c>TranslateJoinCore</c> stops
    /// extending <c>JoinScope</c> at the first ineligible one) and guards against resolving one join's Inner side against
    /// another join with coincident CLR types. A left-outer join over a collection navigation is admitted: the lowerer's
    /// <c>ForceUnwind</c> honors <see cref="LookupExpression.PreserveNullAndEmptyArrays"/>.
    /// <c>HasUnsupportedOperator</c> is defence in depth: confirming registers the <c>$lookup</c>, flipping
    /// <c>UsesDriverJoinFields</c> and changing the fallback's shape; <see cref="MongoSelectDefinition.HasBareJoinInnerEntityLeaf"/>
    /// now closes the wrong-rows case, and no test fails without this conjunct. Deliberately not <c>Route == Fallback</c>,
    /// which is also true merely because this join is unconfirmed. <c>HasTerminalOperator</c>/<c>UnwindSource</c> are
    /// excluded because the <c>$lookup</c> would be emitted at a different point than these arms assume.
    /// </remarks>
    internal static bool TryAdmitSingleEligibleNativeJoinScope(
        MongoQueryExpression mongoQueryExpression, [NotNullWhen(true)] out JoinInfo? joinInfo)
    {
        if (!EvaluateSingleJoinScope(mongoQueryExpression, out joinInfo, out var defersPipelineOps))
        {
            return false;
        }

        if (defersPipelineOps)
        {
            mongoQueryExpression.Select.DeferPipelineOpsPastConfirmedJoin();
        }

        return true;
    }

    /// <summary>
    /// The pure half of <see cref="TryAdmitSingleEligibleNativeJoinScope"/>: whether the query is a single eligible join scope, and whether admitting it
    /// requires deferring the recorded pipeline ops past the join. Mutates nothing, so a caller can ask without committing to the deferral.
    /// </summary>
    internal static bool EvaluateSingleJoinScope(
        MongoQueryExpression mongoQueryExpression, [NotNullWhen(true)] out JoinInfo? joinInfo, out bool defersPipelineOps)
    {
        joinInfo = null;
        defersPipelineOps = false;

        if (!HasCompleteJoinScope(mongoQueryExpression, out _))
        {
            return false;
        }

        // IsNativelyEligible already checked each join before JoinScope was built to Joins.Count levels.
        var candidate = mongoQueryExpression.Joins[^1];

        // Paging or a reducer recorded before this arm confirms is emitted ahead of the $lookup/$unwind, sound only when every
        // level's $unwind is 1:1; otherwise it pages the un-joined outer rows (`Owners.Join(Orders, ...).Take(2)` returned
        // three). This is the common ordering since nav-expansion applies a join's result selector last; the mirror ordering
        // is closed by HasConfirmedJoinLookup. $match/$sort commute with the join.
        //
        // Only a left-outer reference navigation is 1:1 (preserveNullAndEmptyArrays: true; pinned by
        // Projection_take_projection and friends). Collection navigations multiply rows; inner reference joins drop
        // unmatched rows. Every level must be checked, since the ops precede the whole $lookup block. Reducers decline
        // unless every join is 1:1; paging instead defers the recorded PipelineOps past the $lookup/$unwind block.
        if (mongoQueryExpression.Select.Cardinality?.Reducer != null)
        {
            // Reducer case (First/FirstOrDefault/Single/...).
            if (!mongoQueryExpression.AreAllJoinsRowCountPreserving())
            {
                return false;
            }
        }
        else if (mongoQueryExpression.Select.HasPaging)
        {
            // PostJoinOps and PostLookupPagingOps aren't mutually exclusive: if an inner-predicate Where over a required
            // reference nav already populated PostJoinOps, deferring earlier paging would emit filter-then-page for
            // page-then-filter (`Skip(1).Take(2).Where(od => od.Order.CustomerID != "ALFKI")`). Decline.
            if (mongoQueryExpression.Select.JoinInnerAccessConfirmed)
            {
                return false;
            }

            var everyJoinPreLookupSafe = mongoQueryExpression.AreAllJoinsRowCountPreserving();

            // Keep paging before the $lookup when every join is 1:1-safe; otherwise defer it past the $lookup/$unwind. Paging
            // before a required reference dereference then pages the joined result, which differs only for dangling references
            // (pinned by Paging_after_a_dangling_required_reference_dereference_pages_the_joined_result_under_NativeOnly).
            if (!everyJoinPreLookupSafe)
            {
                // Paging recorded before any join existed (`Customers.Take(1).GroupJoin(Orders, ...).SelectMany(g => g.DefaultIfEmpty())`)
                // pages the outer sequence and must not be deferred past a row-multiplying $unwind. HasPagingRecordedBeforeAnyJoin
                // alone can't distinguish this from a reference-nav dereference in a projection (whose join is synthesized just as
                // late, and is at most 0:1). "May multiply" is any join not provably a reference navigation: a collection OR a
                // navigation-less join (NativeJoinTests.Outer_paging_before_a_navigation_less_one_to_many_join_pages_the_outer_rows).
                // Such paging is already ahead of the $lookup in PipelineOps and stays there, unless paging was also recorded after a
                // join (no correct single placement: decline).
                //
                // It also covers an inner join onto an optional or principal-side reference, which DROPS rows:
                // `OrderByDescending(Id).Take(2).Join(Customers, ...)` pages the outer rows first, so deferring would page the joined
                // rows instead (JoinInfo.PagingBeforeItMustStayAhead).
                if (mongoQueryExpression.Select.HasPagingRecordedBeforeAnyJoin
                    && mongoQueryExpression.Joins.Any(j => j.PagingBeforeItMustStayAhead))
                {
                    if (mongoQueryExpression.Select.HasPagingRecordedAfterAJoin)
                    {
                        return false;
                    }
                }
                // Paging BETWEEN two joins of a chain (`Join(a, ...).Skip(1).Take(2).Join(b, ...)`) pages the earlier join's rows, but
                // the deferred snapshot is emitted after EVERY join's $lookup/$unwind. The lowerer has no per-join boundary to place
                // it at, so decline; the driver-LINQ fallback emits each $lookup at its own boundary. Checked before deferring, so a
                // decline mutates nothing. Pinned by Ef373InterleavedPagingTests.
                else if (mongoQueryExpression.Select.HasPagingRecordedBetweenJoins(mongoQueryExpression.Joins.Count))
                {
                    return false;
                }
                else
                {
                    defersPipelineOps = true;
                }
            }
        }

        joinInfo = candidate;
        return true;
    }

    /// <summary>
    /// The structural core shared by <see cref="TryAdmitSingleEligibleNativeJoinScope"/> and <see cref="TryGetGroupByJoinScope"/>:
    /// one level per join, no unsupported or terminal operator, no unwind source, a resolved <c>Lookup</c> on every join. Pure.
    /// </summary>
    internal static bool HasCompleteJoinScope(
        MongoQueryExpression mongoQueryExpression, [NotNullWhen(true)] out MongoJoinScope? scope)
    {
        var select = mongoQueryExpression.Select;
        scope = select.JoinScope;
        if (scope == null
            || scope.Levels.Count != mongoQueryExpression.Joins.Count
            || select.HasUnsupportedOperator
            || select.HasTerminalOperator
            || select.UnwindSource != null)
        {
            scope = null;
            return false;
        }

        // Every join needs a resolved Lookup, not just the last: ConfirmEntireChain confirms every level, so a null earlier
        // Lookup would drop that $lookup while the projection still reads its alias.
        foreach (var chainJoin in mongoQueryExpression.Joins)
        {
            if (chainJoin.Lookup is null)
            {
                scope = null;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The join scope a <c>GroupBy</c> may resolve through, or <see langword="null"/> to keep root-entity resolution.
    /// Pure: unlike <see cref="TryAdmitSingleEligibleNativeJoinScope"/> it never defers recorded ops. Called before <c>IsGroupBy</c> is set.
    /// </summary>
    /// <remarks>
    /// Paging recorded ahead of the grouped join is kept only when every join is 1:1; any other paging declines rather than
    /// being deferred, since deferral would silently change group counts (acceptable only for a projection). Chains decline
    /// on any paging. The key selector's parameter must be the join's <c>TransparentIdentifier</c>: an entity-typed one
    /// can't be routed by <see cref="MongoGroupElementTranslator"/>. Defence in depth: no shape is known to reach a
    /// non-<c>TransparentIdentifier</c> parameter over a join scope.
    /// </remarks>
    internal static MongoJoinScope? TryGetGroupByJoinScope(MongoQueryExpression mongoQueryExpression, LambdaExpression keySelector)
    {
        var select = mongoQueryExpression.Select;
        if (!HasCompleteJoinScope(mongoQueryExpression, out var scope)
            || !keySelector.Parameters[0].Type.IsTransparentIdentifierType()
            || select.Cardinality != null
            // ConfirmEntireChain isn't idempotent (it counts confirmations), so a second confirm must be unreachable.
            || select.HasConfirmedJoinLookup)
        {
            return null;
        }

        if (select.HasPaging
            && (scope.Levels.Count > 1
                || select.JoinInnerAccessConfirmed
                || !mongoQueryExpression.AreAllJoinsRowCountPreserving()))
        {
            return null;
        }

        return scope;
    }
}
