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

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.NativeTranslation;

/// <summary>
/// Translates a <c>Where</c> predicate or <c>OrderBy</c>/<c>ThenBy</c> key reading an aggregate or quantifier over a
/// cross-document collection (<c>c.Orders.Any(o =&gt; o.Total &gt; 100)</c>) into a native <c>$lookup</c> plus an operator over
/// the looked-up array. The projection counterpart is the resolver wired up in <see cref="NativeProjectionBinder"/>; design:
/// <c>docs/superpowers/specs/2026-10-08-native-referenced-collection-aggregates-design.md</c>.
/// </summary>
/// <remarks>
/// The lookup is staged, never applied, until the whole predicate or key translated. A <c>$match</c>/<c>$sort</c> reading the
/// array must run after the <c>$lookup</c> but <see cref="MongoSelectDefinition.PipelineOps"/> are emitted before it, so the
/// commit flips to <see cref="MongoSelectDefinition.PostJoinOps"/> exactly as the count-comparison binder does
/// (<see cref="MongoSelectDefinition.MarkReferenceCollectionCountPredicateConfirmed"/>), which also makes
/// <see cref="MongoSelectDefinition.Route"/> decline a later set op, projected Distinct, keyed GroupBy, join or unwind.
/// </remarks>
internal static class NativeReferencedCollectionBinder
{
    /// <summary>Translates <paramref name="body"/> as a <c>Where</c> predicate; on success commits the lookup and flips to <c>PostJoinOps</c>.</summary>
    internal static bool TryTranslatePredicate(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression body,
        [NotNullWhen(true)] out MongoExpression? result)
    {
        result = null;
        if (!TryTranslate(mongoQ, outerParameter, body, valueMode: false, out var node, out var lookups, out var stamps))
            return false;

        Commit(mongoQ, lookups, stamps);
        mongoQ.Select.MarkReferenceCollectionCountPredicateConfirmed();
        result = node;
        return true;
    }

    /// <summary>
    /// Translates <paramref name="body"/> as a sort key; on success commits the lookup, relocates a trailing sort of the same chain
    /// next to the key (one logical ordering must be one <c>$sort</c>) and flips to <c>PostJoinOps</c>.
    /// </summary>
    internal static bool TryTranslateSortKey(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression body,
        [NotNullWhen(true)] out MongoExpression? key)
    {
        key = null;
        if (!TryTranslate(mongoQ, outerParameter, body, valueMode: true, out var node, out var lookups, out var stamps)
            || !StoredOrdering.SortKeyPreservesClrOrdering(node))
        {
            return false;
        }

        Commit(mongoQ, lookups, stamps);
        mongoQ.Select.DeferTrailingSortPastConfirmedJoin();
        mongoQ.Select.MarkReferenceCollectionCountPredicateConfirmed();
        key = node;
        return true;
    }

    /// <summary>
    /// Translates <paramref name="body"/> as the operand of a scalar <c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c> over the query root
    /// (<c>Customers.Sum(c =&gt; c.Orders.Sum(o =&gt; o.Id))</c>): the per-row value is the aggregate over the looked-up array, reduced again by the
    /// <c>$group</c>. Stages nothing: the caller commits <paramref name="lookups"/> with <see cref="CommitStaged"/> once its own checks have
    /// passed, so a later decline leaves no lookup behind.
    /// </summary>
    internal static bool TryTranslateAggregateOperand(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression body,
        [NotNullWhen(true)] out MongoExpression? operand,
        out List<LookupExpression> lookups,
        out List<LookupExpression> stamps)
        => TryTranslate(mongoQ, outerParameter, body, valueMode: true, out operand, out lookups, out stamps);

    /// <summary>Commits the lookups a successful <see cref="TryTranslateAggregateOperand"/> staged.</summary>
    internal static void CommitStaged(MongoQueryExpression mongoQ, List<LookupExpression> lookups, List<LookupExpression> stamps)
        => Commit(mongoQ, lookups, stamps);

    private static bool TryTranslate(
        MongoQueryExpression mongoQ,
        ParameterExpression outerParameter,
        Expression body,
        bool valueMode,
        [NotNullWhen(true)] out MongoExpression? node,
        out List<LookupExpression> lookups,
        out List<LookupExpression> stamps)
    {
        node = null;
        var staging = new PendingProjectionStaging();
        lookups = staging.Lookups;
        stamps = staging.BareCountStamps;

        // Only the plain root scan: after a terminal operator or over a join scope the lowering has no place for the flip.
        if (mongoQ.Select.HasTerminalOperator || mongoQ.Select.JoinScope is not null)
            return false;

        var resolvedAny = false;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, outerParameter)
        {
            ReferencedCollectionResolver = source =>
            {
                var resolved = NativeProjectionBinder.ResolveReferencedCollection(
                    mongoQ, outerParameter, source, staging);
                resolvedAny |= resolved is not null;
                return resolved;
            }
        };

        var translated = valueMode
            ? translator.TryTranslateValue(body, out node)
            : translator.TryTranslate(body, out node);

        // No referenced collection resolved: an ordinary predicate or key the earlier arms already had their chance at. Keyed on
        // resolution, not newly staged lookups: an earlier Where/OrderBy may have registered the same `$lookup`.
        return translated && node is not null && resolvedAny;
    }

    private static void Commit(MongoQueryExpression mongoQ, List<LookupExpression> lookups, List<LookupExpression> stamps)
    {
        // Whole-array sources: a later filtered or paged Include of the same navigation is rerouted instead of merged into them.
        foreach (var stamp in stamps)
            stamp.IsBareCountSizeSource = true;

        foreach (var lookup in lookups)
            mongoQ.AddLookup(lookup);
    }
}
