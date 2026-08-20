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
    private readonly Dictionary<IEntityType, bool> _innerCollectionIsLeftOuter = new();
    private bool _hasInterleavingOperatorSinceLastJoin;
    private int _joinRegistrationCount;

    // Every join registered so far, in registration order. Unlike _innerCollections (keyed by
    // IEntityType, so a self-referencing navigation chain like Employee.Manager.Manager collapses both
    // hops into a single dictionary entry), this records one entry per join, letting a later hop find
    // the immediately-preceding hop even when it targets the same entity type.
    private readonly List<JoinRegistration> _joinRegistrations = [];
    private readonly Dictionary<IEntityType, NavigationlessJoinKey> _navigationlessJoinKeys = new();

    /// <summary>
    /// The resolved <c>$lookup</c> key info for a Join hop with no corresponding model navigation —
    /// captured so a later dependent hop can scope its <see cref="LookupExpression.LocalField"/>, and
    /// so this hop's own <c>$lookup</c> can be emitted retroactively if flat mode is forced.
    /// </summary>
    /// <param name="CollectionName">The target collection to look up from.</param>
    /// <param name="LocalField">The local (outer) field path used for the equality match.</param>
    /// <param name="ForeignField">The foreign (inner) field path used for the equality match.</param>
    /// <param name="Alias">The stable <c>_lookup_&lt;ShortName&gt;</c> alias this hop's projection was
    /// registered under.</param>
    internal readonly record struct NavigationlessJoinKey(
        string CollectionName, string LocalField, string ForeignField, string Alias);

    /// <summary>
    /// Register the raw join-key info for a Join hop that has no corresponding model navigation.
    /// </summary>
    public void RegisterNavigationlessJoinKey(IEntityType entityType, NavigationlessJoinKey key)
        => _navigationlessJoinKeys[entityType] = key;

    /// <summary>
    /// Attempt to retrieve the raw join-key info previously registered for a navigation-less Join hop
    /// whose inner entity is <paramref name="entityType"/>.
    /// </summary>
    public bool TryGetNavigationlessJoinKey(IEntityType entityType, out NavigationlessJoinKey key)
        => _navigationlessJoinKeys.TryGetValue(entityType, out key);

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
    /// Records that a <c>Skip</c>/<c>Take</c>/<c>Distinct</c> was visited while at least one join was
    /// already registered (see <see cref="Visitors.MongoQueryableMethodTranslatingExpressionVisitor"/>).
    /// Operators visited before the first join don't count - they precede every join, not sit between two of
    /// them.
    /// </summary>
    public void MarkPotentialJoinInterleavingOperator()
    {
        if (_innerCollections.Count > 0)
        {
            _hasInterleavingOperatorSinceLastJoin = true;
        }
    }

    /// <summary>
    /// Whether a <c>Skip</c>/<c>Take</c>/<c>Distinct</c> has been visited between two joins - see
    /// <see cref="MarkPotentialJoinInterleavingOperator"/>.
    /// </summary>
    public bool HasInterleavingOperatorSinceLastJoin => _hasInterleavingOperatorSinceLastJoin;

    /// <summary>
    /// Register that a join was processed and report whether this is the second (or later) one.
    /// Deliberately counts every join call, NOT distinct target entity types: <see cref="InnerCollections"/>
    /// is keyed by <see cref="IEntityType"/> and dedups two navigations that target the same collection
    /// (e.g. a self-join), so it under-counts joins for that shape - see EF-373.
    /// </summary>
    public bool RegisterJoinAndReportSecondOrLater()
        => ++_joinRegistrationCount > 1;

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
    /// Every join registered so far, in registration order. See <see cref="RegisterJoin"/>.
    /// </summary>
    public IReadOnlyList<JoinRegistration> JoinRegistrations => _joinRegistrations;

    /// <summary>
    /// Records that a join against <paramref name="targetEntityType"/> was resolved to
    /// <paramref name="navigation"/> and surfaced under <paramref name="alias"/>, so a subsequent chained
    /// hop can find the immediately-preceding hop by position rather than by <see cref="IEntityType"/>,
    /// which can't distinguish repeat hops against the same entity type.
    /// </summary>
    public void RegisterJoin(IEntityType targetEntityType, string alias, INavigation? navigation)
        => _joinRegistrations.Add(new JoinRegistration(targetEntityType, alias, navigation));

    /// <summary>
    /// Register an inner collection for a join, recording the LINQ operator's own left-outer/inner
    /// semantics the first time this entity type is joined (see <see cref="TryGetJoinIsLeftOuter"/>) so a
    /// later retroactive flattening never has to re-derive it from model metadata, which can disagree
    /// with the operator actually used.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> of the inner collection.</param>
    /// <param name="isLeftOuter">Whether the join that introduced this entity type is left-outer.</param>
    /// <returns>The <see cref="MongoCollectionExpression"/> for the inner collection.</returns>
    public MongoCollectionExpression AddInnerCollection(IEntityType entityType, bool isLeftOuter)
    {
        if (!_innerCollections.TryGetValue(entityType, out var collection))
        {
            collection = new MongoCollectionExpression(entityType);
            _innerCollections[entityType] = collection;
            _innerCollectionIsLeftOuter[entityType] = isLeftOuter;
        }

        return collection;
    }

    /// <summary>
    /// The left-outer/inner semantics recorded by <see cref="AddInnerCollection"/> when <paramref name="entityType"/>
    /// was first joined. Returns <see langword="false"/> for a <paramref name="isLeftOuter"/> lookup miss —
    /// callers must treat a missed lookup as "unknown", not as "inner".
    /// </summary>
    public bool TryGetJoinIsLeftOuter(IEntityType entityType, out bool isLeftOuter)
        => _innerCollectionIsLeftOuter.TryGetValue(entityType, out isLeftOuter);
}

/// <summary>
/// One entry in <see cref="MongoQueryExpression.JoinRegistrations"/> — see
/// <see cref="MongoQueryExpression.RegisterJoin"/>.
/// </summary>
internal readonly record struct JoinRegistration(IEntityType TargetEntityType, string Alias, INavigation? Navigation);
