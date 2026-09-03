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

// Cross-collection $lookup workaround state. The C# driver's LINQ provider has no native LeftJoin translator
// and cannot express collection / multi-hop joins, so the provider registers manual $lookup + $unwind stages
// and tracks the inner collections itself.
internal sealed partial class MongoQueryExpression
{
    private readonly List<LookupExpression> _pendingLookups = [];
    private readonly List<MongoCorrelatedReducerLeaf> _correlatedReducerLeaves = [];
    private readonly Dictionary<IEntityType, MongoCollectionExpression> _innerCollections = new();

    // IEntityType, so a self-referencing navigation chain like Employee.Manager.Manager, or two sibling
    // joins onto the same target type, collapse into a single dictionary entry), this records one entry
    // per join, letting a later hop find the immediately-preceding hop even when it targets the same
    // entity type, and letting the flatten decision trigger on join COUNT rather than distinct target
    // entity types. A navigation-less Join hop (EF-377) has no INavigation to key a lookup dictionary
    // by anyway, so its raw join-key info lives on its own JoinInfo.Lookup instead of a side table.
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

        // Repeatedly emit any lookup whose localField does not depend on a still-unemitted lookup's
        // output field. A lookup depends on another when its localField is prefixed with "<other.As>.".
        while (remaining.Count > 0)
        {
            var emittedThisPass = false;
            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                var dependsOnPending = remaining.Any(other =>
                    !ReferenceEquals(other, candidate)
                    && candidate.LocalField.StartsWith(other.As + ".", StringComparison.Ordinal));

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
                // Cyclic / unresolvable dependency — fall back to registration order to avoid a hang.
                ordered.AddRange(remaining);
                break;
            }
        }

        return ordered;
    }

    /// <summary>
    /// The single-level reference <c>$lookup</c>s the native streaming path must emit as
    /// <c>$lookup</c> + <c>$unwind</c> stages (to a root-level <c>_lookup_&lt;Nav&gt;</c> field) and read back
    /// in the forward-only materializer.
    /// <para>
    /// A lone reference Include is translated by the driver-LINQ path as a driver-native LeftJoin
    /// (<c>_outer</c>/<c>_inner</c>) and registers NO pending <see cref="LookupExpression"/> — see
    /// <see cref="UsesDriverJoinFields"/>. The native pipeline cannot produce the driver's LeftJoin shape, so
    /// for that case the reference lookups are synthesized here from <see cref="InnerCollections"/> (each
    /// inner collection reached by a direct single-reference navigation off the root). This keeps the
    /// DOM/driver-LINQ join-shape decision untouched (no pending lookup is registered, so the DOM fallback
    /// still uses the driver-native LeftJoin) while giving the native streaming path the flat
    /// <c>_lookup_&lt;Nav&gt;</c> shape its materializer reads.
    /// </para>
    /// <para>
    /// When pending reference lookups ARE registered (multi-join flat mode), those are returned directly.
    /// </para>
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

        // Driver-native LeftJoin case: synthesize a reference lookup per inner collection that is the target
        // of a direct single-reference navigation off the root entity.
        var rootEntityType = CollectionExpression.EntityType;
        var synthesized = new List<LookupExpression>();
        foreach (var innerEntityType in _innerCollections.Keys)
        {
            var matches = rootEntityType.GetNavigations()
                .Where(n => !n.IsCollection
                            && !n.TargetEntityType.IsOwned()
                            && n.TargetEntityType == innerEntityType)
                .ToList();

            // Synthesis matches by target type. If more than one single-reference navigation off the root
            // targets the same inner collection (e.g. Doc.Author and Doc.Editor both -> Person), we cannot
            // tell which one this lookup is for by type alone — bail to the driver/DOM fallback rather than
            // risk resolving to the wrong navigation's element alias.
            if (matches.Count != 1)
            {
                // Zero: not a direct single-reference navigation off the root (e.g. transitive / collection).
                // More than one: ambiguous by target type. Either way, not streamable here -> fall back.
                return Array.Empty<LookupExpression>();
            }

            synthesized.Add(new LookupExpression(matches[0]));
        }

        return synthesized;
    }

    /// <summary>
    /// Register a $lookup stage for a cross-collection collection Include.
    /// </summary>
    public void AddLookup(LookupExpression lookup)
    {
        if (!_pendingLookups.Any(l => l.As == lookup.As))
        {
            _pendingLookups.Add(lookup);
        }
    }

    /// <summary>
    /// The reference-collection-nav <c>First</c>/<c>FirstOrDefault</c> projection leaves (EF-449) registered on
    /// this query, in projection order. See <see cref="MongoCorrelatedReducerLeaf"/>.
    /// </summary>
    public IReadOnlyList<MongoCorrelatedReducerLeaf> CorrelatedReducerLeaves => _correlatedReducerLeaves;

    /// <summary>
    /// Register a reference-collection-nav <c>First</c>/<c>FirstOrDefault</c> projection leaf (EF-449).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="AddLookup"/> this does NOT deduplicate: two distinct projection members can legitimately
    /// reduce the same navigation only if they also share one <see cref="LookupExpression"/>, which the
    /// recognizer declines outright (a second lookup on the same navigation would collide on
    /// <see cref="LookupExpression.As"/> while carrying a different sub-pipeline). So each registered leaf here
    /// names a distinct <see cref="MongoCorrelatedReducerLeaf.Alias"/> by construction.
    /// </remarks>
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

    /// <summary>
    /// The ordered list of <c>$lookup</c> stages the native pipeline must emit for cross-collection
    /// Include operations. Surfaces the reference-lookup reconstruction from
    /// <see cref="GetStreamingReferenceLookups"/>: when no pending lookups are registered (the driver's
    /// native LeftJoin path), the single-level reference lookups are synthesized from
    /// <see cref="InnerCollections"/>; otherwise the already-registered pending lookups are returned
    /// directly. Consumed by the native lowerer to emit <c>$lookup</c> + <c>$unwind</c> stages.
    /// </summary>
    /// <remarks>
    /// This is NOT a stored slot: each access <b>recomputes</b> <see cref="GetStreamingReferenceLookups"/>,
    /// an O(navigations) reconstruction off <see cref="InnerCollections"/>. Callers should not treat it as a
    /// cheap field read. It is slated for structural replacement (a populated <c>Lookups</c> slot) in the
    /// Collection Includes sub-project. It lives here (rather than on <see cref="MongoSelectDefinition"/>)
    /// because it recomputes from the group-3 lookup state that stays on this type.
    /// </remarks>
    public IReadOnlyList<LookupExpression> Lookups => GetStreamingReferenceLookups();
}
