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
    /// Pending $lookup stages, ordered so a transitive lookup (whose <see cref="LookupExpression.LocalField"/> reads an
    /// earlier lookup's <see cref="LookupExpression.As"/>, e.g. <c>_lookup_Order.CustomerID</c>) is emitted AFTER the
    /// lookup it depends on; joins can be registered in any order.
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

    /// <summary>Registers a <c>$lookup</c> stage for a cross-collection collection Include.</summary>
    /// <remarks>
    /// The same navigation can be registered twice: first as a bare placeholder by the emit side, then with its
    /// <c>ThenInclude</c> sub-pipeline by the bind side. First-wins dedup would drop the real pipeline (a
    /// <c>ThenInclude(OrderDetails)</c> returned zero rows), so the incoming pipeline is merged into the existing
    /// object; replacing it would lose <c>ForceUnwind</c>/<c>PreserveNullAndEmptyArrays</c>/<c>InjectAfterRoot</c>
    /// and orphan <see cref="JoinInfo.Lookup"/>. The <see cref="LookupPipelineKind"/> stamp is write-once.
    /// The merge is one-directional (<c>!existing.HasPipeline &amp;&amp; lookup.HasPipeline</c>): an incoming pipeline
    /// over an existing one (e.g. a TPH discriminator <c>$match</c>) is not merged. Unreachable today because the
    /// emit-side recognizers decline those shapes; a latent gap if those guards are relaxed.
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
    /// Inner collections involved in joins, deduplicated by entity type (what serializer registration needs). Use
    /// <see cref="Joins"/> to reason about join count, order or navigation, since two joins can share a target type.
    /// </summary>
    public IReadOnlyDictionary<IEntityType, MongoCollectionExpression> InnerCollections
        => _innerCollections;

    /// <summary>The cross-collection joins on this query, in registration order.</summary>
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
    /// Registers a cross-collection join; the returned <see cref="JoinInfo"/> is filled in with the navigation and
    /// <c>$lookup</c> once resolved from the key selector.
    /// </summary>
    public JoinInfo AddJoin(IEntityType innerEntityType, bool isLeftOuter)
    {
        var join = new JoinInfo(innerEntityType, isLeftOuter);
        _joins.Add(join);
        return join;
    }

    /// <summary>Whether this query joins across collections.</summary>
    public bool IsJoinQuery => _innerCollections.Count > 0;

    /// <summary>
    /// Whether this query is materialized from the driver's native LeftJoin output (root under <c>_outer</c>, the
    /// joined reference under <c>_inner</c>). Computed rather than tracked, as the shaper's single source of truth:
    /// used only with an inner collection AND no forced-unwind lookup (which flattens the document to root-level
    /// <c>_lookup_*</c> fields; see <see cref="Visitors.MongoEFToLinqTranslatingExpressionVisitor"/>'s
    /// <c>StripJoinForLookup</c> path).
    /// </summary>
    public bool UsesDriverJoinFields
        => _innerCollections.Count > 0 && !_pendingLookups.Any(l => l.ForceUnwind);

    /// <summary>Registers an inner collection for a join.</summary>
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
