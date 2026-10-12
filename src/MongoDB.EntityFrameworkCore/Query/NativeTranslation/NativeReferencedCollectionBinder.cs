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
/// Translates a <c>Where</c> predicate or an <c>OrderBy</c>/<c>ThenBy</c> key that reads an aggregate or quantifier over a
/// cross-document collection (<c>c.Orders.Any(o =&gt; o.Total &gt; 100)</c>, <c>c.Orders.Sum(o =&gt; o.Total)</c>) into a native
/// <c>$lookup</c> plus an operator over the looked-up array. The projection counterpart is the resolver wired up in
/// <see cref="NativeProjectionBinder"/>; the design is <c>docs/superpowers/specs/2026-10-08-native-referenced-collection-aggregates-design.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// A translator whose <see cref="MongoExpressionTranslator.ReferencedCollectionResolver"/> maps the strict foreign-key
/// correlation (<see cref="NativeCorrelationMatcher"/>) to the staged lookup array serves the same arms that already serve owned
/// collections (<c>$elemMatch</c> quantifiers, filtered sizes, array reductions). The lookup is staged, never applied, until the whole
/// predicate or key translated; a translation that used no lookup is not this binder's to accept.
/// </para>
/// <para>
/// A <c>$match</c> or <c>$sort</c> reading the array must run after the <c>$lookup</c>, but the lowerer emits <see cref="MongoSelectDefinition.PipelineOps"/>
/// before it, so the commit flips the select's active op list to <see cref="MongoSelectDefinition.PostJoinOps"/>
/// (<see cref="MongoSelectDefinition.MarkReferenceCollectionCountPredicateConfirmed"/>), exactly as the existing count-comparison binder does:
/// ops recorded earlier stay ahead of the lookup, ops recorded after follow it. That flag also makes <see cref="MongoSelectDefinition.Route"/>
/// decline a later set op, projected Distinct, keyed GroupBy, join or unwind, whose lowering would not flush <c>PostJoinOps</c>.
/// </para>
/// </remarks>
internal static class NativeReferencedCollectionBinder
{
    /// <summary>
    /// Translates <paramref name="body"/> as a <c>Where</c> predicate. On success the lookup is committed, the select is flipped to
    /// <see cref="MongoSelectDefinition.PostJoinOps"/>, and the caller records the returned node as a conjunct.
    /// </summary>
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
    /// Translates <paramref name="body"/> as an <c>OrderBy</c>/<c>ThenBy</c> key. On success the lookup is committed, a trailing
    /// sort of the same chain is relocated next to the key (one logical ordering must be one <c>$sort</c>), the select is flipped,
    /// and the caller records the returned key as an ordering.
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
        lookups = [];
        stamps = [];

        // Only the plain root scan: after a terminal operator or over a join scope the lowering has no place for the flip.
        if (mongoQ.Select.HasTerminalOperator || mongoQ.Select.JoinScope is not null)
            return false;

        var pendingLookups = lookups;
        var pendingStamps = stamps;
        var resolvedAny = false;
        var translator = new MongoExpressionTranslator(mongoQ.CollectionExpression.EntityType, outerParameter)
        {
            ReferencedCollectionResolver = source =>
            {
                var resolved = NativeProjectionBinder.ResolveReferencedCollection(
                    mongoQ, outerParameter, source, pendingLookups, pendingStamps);
                resolvedAny |= resolved is not null;
                return resolved;
            }
        };

        var translated = valueMode
            ? translator.TryTranslateValue(body, out node)
            : translator.TryTranslate(body, out node);

        // A translation that resolved no referenced collection read nothing from one: an ordinary predicate or key, which the arms
        // before this one already had their chance at. Keyed on resolution, not on newly staged lookups: an earlier Where or OrderBy
        // may already have registered the same `$lookup`, in which case nothing new is staged.
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
