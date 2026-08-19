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

// TODO(EF-317): Cross-collection $lookup workaround state. The C# driver's LINQ provider has no native
// LeftJoin translator and cannot express collection / multi-hop joins, so the provider registers manual
// $lookup + $unwind stages and tracks the inner collections itself. When the driver ships native LeftJoin
// support, the $lookup-emission members here (the pending-lookup list and its dependency ordering) are
// expected to be removed; the inner-collection tracking and UsesDriverJoinFields decision are the
// driver-native seam and will likely shrink rather than disappear.
internal sealed partial class MongoQueryExpression
{
    private readonly List<LookupExpression> _pendingLookups = [];
    private readonly Dictionary<IEntityType, MongoCollectionExpression> _innerCollections = new();
    private readonly List<(LookupExpression? Lookup, string Alias)> _joinHistory = [];
    private readonly Dictionary<string, string> _claimedTransitiveAliases = new();

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
    /// Record the (possibly not-yet-flattened) $lookup built for a <c>Join</c>/<c>LeftJoin</c> call, in
    /// call order, with the alias its result is read back under. Resolving the "through" join
    /// positionally (via <see cref="GetJoinAliasFromEnd"/>) rather than by target entity type is required
    /// because two sibling navigations can share a target type and navigation name (EF-376). A
    /// <see langword="null"/> <paramref name="lookup"/> means the navigation couldn't be resolved, so it
    /// can never be a later transitive join's "through" leg.
    /// </summary>
    public void RecordJoin(LookupExpression? lookup, string alias) => _joinHistory.Add((lookup, alias));

    /// <summary>
    /// Resolve the alias of the join recorded <paramref name="hopsFromEnd"/> calls before the one
    /// currently being processed (0 = the immediately preceding join). Returns <see langword="null"/>
    /// when there is no such prior join, or when that join's <see cref="LookupExpression"/> is
    /// <see langword="null"/> (per <see cref="RecordJoin"/>, an unresolved navigation whose alias is
    /// never emitted into the document, so it can never validly be a later join's "through" leg).
    /// </summary>
    public string? GetJoinAliasFromEnd(int hopsFromEnd)
    {
        var index = _joinHistory.Count - 1 - hopsFromEnd;
        return index >= 0 && index < _joinHistory.Count && _joinHistory[index].Lookup != null
            ? _joinHistory[index].Alias
            : null;
    }

    /// <summary>
    /// Claim the plain <c>_lookup_&lt;Navigation&gt;</c> alias for a transitive join reached through
    /// <paramref name="throughAlias"/>. Returns <see langword="false"/> only when a DIFFERENT intermediate
    /// already claimed the same alias (EF-376: sibling navigations sharing a target type/name) — the
    /// caller then falls back to a path-qualified alias instead.
    /// </summary>
    public bool TryClaimTransitiveAlias(string flatAlias, string throughAlias)
    {
        if (!_claimedTransitiveAliases.TryGetValue(flatAlias, out var claimedThroughAlias))
        {
            _claimedTransitiveAliases[flatAlias] = throughAlias;
            return true;
        }

        return claimedThroughAlias == throughAlias;
    }

    /// <summary>
    /// Flatten every join recorded so far (including ones still driver-native, e.g. the very first join)
    /// into root-level $lookup+$unwind stages, once a second cross-collection join makes the driver-native
    /// <c>_outer</c>/<c>_inner</c> shape unusable. <paramref name="currentLookup"/> — the join that
    /// triggered flattening — is added first (a pre-existing, baseline-locked ordering contract); the rest
    /// follow in recording order. <see cref="AddLookup"/> dedups by alias, so re-flattening an
    /// already-flattened join is a no-op.
    /// </summary>
    public void FlattenRecordedJoins(LookupExpression? currentLookup)
    {
        if (currentLookup != null)
        {
            AddLookup(currentLookup);
        }

        foreach (var (lookup, _) in _joinHistory)
        {
            if (lookup != null && !ReferenceEquals(lookup, currentLookup))
            {
                AddLookup(lookup);
            }
        }
    }

    /// <summary>
    /// Inner collections involved in join operations.
    /// </summary>
    public IReadOnlyDictionary<IEntityType, MongoCollectionExpression> InnerCollections
        => _innerCollections;

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
}
