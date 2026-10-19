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
using System.Diagnostics;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

// Cross-collection $lookup state: the driver's LINQ provider can't express collection / multi-hop joins.
internal sealed partial class MongoQueryExpression
{
    private readonly List<LookupExpression> _pendingLookups = [];
    private readonly List<MongoCorrelatedReducerLeaf> _correlatedReducerLeaves = [];
    private readonly Dictionary<IEntityType, MongoCollectionExpression> _innerCollections = new();

    // One entry per join (unlike _innerCollections, keyed by entity type, where Employee.Manager.Manager or two
    // sibling joins onto one type collapse): a later hop finds its predecessor by position.
    private readonly List<JoinInfo> _joins = [];

    /// <summary>
    /// Pending $lookup stages for cross-collection collection Include operations, ordered so that a
    /// transitive lookup (whose <see cref="LookupExpression.LocalField"/> matches against an
    /// already-unwound intermediate lookup's <see cref="LookupExpression.As"/> field, e.g.
    /// <c>_lookup_Order.CustomerID</c>) is emitted AFTER the lookup it depends on. Joins can be
    /// registered in an order that doesn't respect this dependency, so we sort here.
    /// </summary>
    public IReadOnlyList<LookupExpression> GetPendingLookups()
        => OrderLookupsByDependency(_pendingLookups);

    private static List<LookupExpression> OrderLookupsByDependency(List<LookupExpression> lookups)
    {
        var ordered = new List<LookupExpression>();
        var remaining = new List<LookupExpression>(lookups);

        while (remaining.Count > 0)
        {
            var emittedThisPass = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                var dependsOnPending = remaining.Any(other =>
                    !ReferenceEquals(other, candidate)
                    && candidate.ReadsOutputOf(other));

                if (!dependsOnPending)
                {
                    ordered.Add(candidate);
                    remaining.RemoveAt(i);
                    emittedThisPass = true;
                    break;
                }
            }

            if (!emittedThisPass)
            {
                // Cyclic dependency: keep registration order rather than hang.
                ordered.AddRange(remaining);
                break;
            }
        }

        return ordered;
    }

    /// <summary>
    /// The single-level reference <c>$lookup</c>s the native streaming path emits. A lone reference Include registers
    /// no pending <see cref="LookupExpression"/> (driver-LINQ uses a native LeftJoin; see
    /// <see cref="UsesDriverJoinFields"/>), so lookups are synthesized from <see cref="InnerCollections"/> without
    /// changing the DOM fallback's join shape.
    /// </summary>
    public IReadOnlyList<LookupExpression> GetStreamingReferenceLookups()
    {
        var pending = GetPendingLookups();
        if (pending.Count > 0)
        {
            return pending;
        }

        if (!UsesDriverJoinFields)
        {
            return pending;
        }

        var rootEntityType = CollectionExpression.EntityType;
        var synthesized = new List<LookupExpression>();
        foreach (var innerEntityType in _innerCollections.Keys)
        {
            var matches = rootEntityType.GetNavigations()
                .Where(n => !n.IsCollection
                            && !n.TargetEntityType.IsOwned()
                            && n.TargetEntityType == innerEntityType)
                .ToList();

            // Matching is by target type, so two navigations to one collection (Doc.Author and Doc.Editor -> Person)
            // are ambiguous; fall back rather than risk the wrong navigation's alias.
            if (matches.Count != 1)
            {
                return Array.Empty<LookupExpression>();
            }

            synthesized.Add(new LookupExpression(matches[0]));
        }

        return synthesized;
    }

    /// <summary>
    /// Register a $lookup stage for a cross-collection collection Include.
    /// </summary>
    /// <remarks>
    /// Lookups are deduplicated by <see cref="LookupExpression.As"/>: a second registration under an existing alias is dropped, except that a
    /// pipelined lookup is folded into an existing bare one (<see cref="LookupExpression.PipelineStages"/> and
    /// <see cref="LookupExpression.PipelineKind"/> only). That merge exists for navigation lookups (an Include meeting a join or a
    /// filtered Include on one navigation). The other lookup state (<see cref="LookupExpression.InnerPredicate"/>, element filters, sort,
    /// limit, skip, outer conditions, <see cref="LookupExpression.ExcludesNullLocalKey"/>) is only set on correlated-array lookups, whose alias
    /// encodes target, key paths, null exclusion and an <c>_f&lt;N&gt;</c> filter suffix, so two with one alias are the same lookup and
    /// dropping the second is correct; re-registering the same instance (a join's lookup that an Include path registered) is a no-op. A caller that could produce a different lookup under an existing alias must pick a free alias with
    /// <see cref="GetUnusedLookupAlias"/> first.
    /// </remarks>
    public void AddLookup(LookupExpression lookup)
    {
        var existingIndex = _pendingLookups.FindIndex(l => l.As == lookup.As);
        if (existingIndex == -1)
        {
            _pendingLookups.Add(lookup);
            return;
        }

        var existing = _pendingLookups[existingIndex];

        // Re-registering the very same instance (a join's lookup that an Include path already registered) is a no-op.
        if (ReferenceEquals(existing, lookup))
        {
            return;
        }

        Debug.Assert(
            existing.IsCorrelatedArraySource == lookup.IsCorrelatedArraySource
            && (lookup.IsCorrelatedArraySource
                || (lookup.InnerPredicate is null
                    && lookup.ElementFilters.Count == 0
                    && !lookup.HasElementStages
                    && !lookup.HasOuterConditions
                    && !lookup.ExcludesNullLocalKey)),
            $"AddLookup would drop state of '{lookup.As}': only navigation lookups merge, and a correlated-array lookup must get a unique alias.");
        if (!existing.HasPipeline && lookup.HasPipeline)
        {
            existing.PipelineStages.AddRange(lookup.PipelineStages);
            if (existing.PipelineKind == LookupPipelineKind.None)
            {
                existing.PipelineKind = lookup.PipelineKind;
            }
        }
    }

    /// <summary>
    /// <paramref name="candidate"/>, suffixed <c>_1</c>, <c>_2</c>, ... until no registered <c>$lookup</c> or join uses it,
    /// so a renamed lookup can't land on another's alias (where <see cref="AddLookup"/> would collapse the two). A
    /// lookup satisfying <paramref name="isSameLookup"/> doesn't count: it is an earlier registration of the same
    /// lookup, which must keep sharing the alias.
    /// </summary>
    internal string GetUnusedLookupAlias(string candidate, Func<LookupExpression, bool>? isSameLookup = null)
    {
        var alias = candidate;
        for (var suffix = 1;
             _pendingLookups.Any(l => l.As == alias && isSameLookup?.Invoke(l) != true) || _joins.Any(j => j.Alias == alias);
             suffix++)
        {
            alias = $"{candidate}_{suffix}";
        }

        return alias;
    }

    /// <summary>The correlated-reducer projection leaves registered on this query, in projection order.</summary>
    public IReadOnlyList<MongoCorrelatedReducerLeaf> CorrelatedReducerLeaves => _correlatedReducerLeaves;

    // No dedup, unlike AddLookup: the recognizer declines two leaves on one navigation (they'd collide on As).
    public void AddCorrelatedReducerLeaf(MongoCorrelatedReducerLeaf leaf)
        => _correlatedReducerLeaves.Add(leaf);

    /// <summary>
    /// Inner collections involved in join operations, deduplicated by entity type — one entry per
    /// joined collection, which is what serializer registration needs. Use <see cref="Joins"/> for
    /// anything that has to reason about the number of joins, positional ordering, or which navigation
    /// each one came from, since two joins can share a target entity type.
    /// </summary>
    public IReadOnlyDictionary<IEntityType, MongoCollectionExpression> InnerCollections
        => _innerCollections;

    /// <summary>
    /// The cross-collection joins on this query, in registration order, one entry per join.
    /// </summary>
    public IReadOnlyList<JoinInfo> Joins => _joins;

    /// <summary>
    /// Whether every join is a left-outer join over a reference navigation, which neither drops nor multiplies
    /// rows, so a <c>$skip</c>/<c>$limit</c> before the join equals the same stage after it.
    /// </summary>
    internal bool AreAllJoinsRowCountPreserving()
    {
        foreach (var join in _joins)
        {
            if (!join.IsRowCountPreserving)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Register a cross-collection join. The returned <see cref="JoinInfo"/> is filled in with the
    /// join's navigation and <c>$lookup</c> once they have been resolved from the join's key selector.
    /// </summary>
    /// <param name="innerEntityType">The <see cref="IEntityType"/> of the joined (inner) collection.</param>
    /// <param name="isLeftOuter">Whether the LINQ operator that introduced this join is left-outer.</param>
    /// <returns>The <see cref="JoinInfo"/> recording this join.</returns>
    public JoinInfo AddJoin(IEntityType innerEntityType, bool isLeftOuter)
    {
        var join = new JoinInfo(innerEntityType, isLeftOuter);
        _joins.Add(join);
        return join;
    }

    /// <summary>
    /// Whether this query involves join operations across multiple collections.
    /// </summary>
    public bool IsJoinQuery => _innerCollections.Count > 0;

    /// <summary>
    /// Whether this query is materialized from the driver's native LeftJoin output, which nests the
    /// root entity under <c>_outer</c> and the single joined reference under <c>_inner</c>.
    /// <para>
    /// This is the single source of truth for the shaper's document shape and is computed directly
    /// from the emission decision rather than tracked as mutable state: the driver's native LeftJoin
    /// is only used when there is at least one inner collection AND no <c>$lookup</c>+<c>$unwind</c>
    /// stage was registered (any forced-unwind lookup flattens the document to root-level
    /// <c>_lookup_*</c> fields instead — see <see cref="Visitors.MongoEFToLinqTranslatingExpressionVisitor"/>'s
    /// <c>StripJoinForLookup</c> path). When this is <see langword="false"/>, every cross-collection
    /// projection reads its own root-level <c>_lookup_&lt;NavigationName&gt;</c> field.
    /// </para>
    /// </summary>
    public bool UsesDriverJoinFields
        => _innerCollections.Count > 0 && !_pendingLookups.Any(l => l.ForceUnwind);

    /// <summary>
    /// Register an inner collection for a join operation.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> of the inner collection.</param>
    /// <returns>The <see cref="MongoCollectionExpression"/> for the inner collection.</returns>
    public MongoCollectionExpression AddInnerCollection(IEntityType entityType)
    {
        if (!_innerCollections.TryGetValue(entityType, out var collection))
        {
            collection = new MongoCollectionExpression(entityType);
            _innerCollections[entityType] = collection;
        }

        return collection;
    }

    /// <summary>See <see cref="GetStreamingReferenceLookups"/>. Recomputed on each access, not a cheap field read.</summary>
    public IReadOnlyList<LookupExpression> Lookups => GetStreamingReferenceLookups();
}
