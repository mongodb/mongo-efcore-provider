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
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a top-level MongoDB-specific collection for querying server-side.
/// </summary>
internal sealed partial class MongoQueryExpression : Expression
{
    private Dictionary<ProjectionMember, Expression> _projectionMapping = new();
    private readonly List<ProjectionExpression> _projection = [];

    /// <summary>
    /// Create a <see cref="MongoQueryExpression"/> for the given entity type.
    /// </summary>
    /// <param name="entityType">The <see cref="IEntityType"/> this collection relates to.</param>
    public MongoQueryExpression(IEntityType entityType)
    {
        CollectionExpression = new MongoCollectionExpression(entityType);
        _projectionMapping[new ProjectionMember()] =
            new EntityProjectionExpression(entityType, new RootReferenceExpression(entityType));
    }

    /// <summary>
    /// Represents the Mongo collection this query is bound to.
    /// </summary>
    public MongoCollectionExpression CollectionExpression { get; private set; }

    /// <summary>
    /// The native-translation logical query IR (filter / sort / paging / projection) for this collection.
    /// <c>NativeSlotPopulator</c> / <c>NativeProjectionBinder</c> populate its slots; the gate and lowerer
    /// read them. Get-only — callers mutate the <see cref="MongoSelectDefinition"/>'s members, never reassign it.
    /// </summary>
    public MongoSelectDefinition Select { get; } = new();

    /// <summary>
    /// The <see cref="Expression"/> captured from the original EF-bound LINQ query.
    /// </summary>
    public Expression? CapturedExpression { get; set; }

    /// <inheritdoc />
    public override Type Type
        => typeof(object);

    /// <inheritdoc />
    public override ExpressionType NodeType
        => ExpressionType.Extension;

    public int AddToProjection(Expression expression, string? alias = null)
    {
        var existingIndex = _projection.FindIndex(pe => pe.Expression.Equals(expression));
        if (existingIndex != -1)
        {
            return existingIndex;
        }

        var baseAlias = alias ?? (expression as IAccessExpression)?.Name;

        var currentAlias = baseAlias;
        var counter = 0;
        while (_projection.Any(pe => string.Equals(pe.Alias, currentAlias, StringComparison.OrdinalIgnoreCase)))
        {
            currentAlias = $"{baseAlias}{counter++}";
        }

        _projection.Add(new ProjectionExpression(expression, currentAlias, false));

        return _projection.Count - 1;
    }

    public Expression GetMappedProjection(ProjectionMember projectionMember)
        => _projectionMapping[projectionMember];

    /// <summary>
    /// Re-points the query's ROOT <see cref="ProjectionMember"/> at a fresh
    /// <see cref="EntityProjectionExpression"/>/<see cref="RootReferenceExpression"/> pair for
    /// <paramref name="entityType"/> — built exactly the way the constructor builds the query root's own pair,
    /// but for a different entity type.
    /// </summary>
    /// <remarks>
    /// Used by the bare whole-inner-element <c>SelectMany</c> (<c>MongoUnwindSource.WholeElement</c>): after the
    /// <c>$unwind</c> + <c>$replaceRoot</c> the unwound ELEMENT *is* the root document, and the element's own
    /// shaper is the only shaper that survives (the trailing <c>ti =&gt; ti.Inner</c> selector drops the outer
    /// one). Leaving the root member mapped to the OUTER entity's projection makes every member binding — most
    /// visibly a nested owned navigation reached through EF's auto-<c>IncludeExpression</c> machinery — resolve
    /// against the wrong entity type (<c>EntityProjectionExpression.BindNavigation</c> throws
    /// "Unable to bind 'navigation' … to an entity projection of &lt;owner&gt;").
    /// Must be called BEFORE <c>MongoProjectionBindingExpressionVisitor.Translate</c> runs for the trailing
    /// selector, which is the only consumer of this mapping and which replaces it wholesale afterwards.
    /// </remarks>
    public void ReRootProjectionAt(IEntityType entityType)
        => _projectionMapping[new ProjectionMember()] =
            new EntityProjectionExpression(entityType, new RootReferenceExpression(entityType));

    public IReadOnlyList<ProjectionExpression> Projection
        => _projection;

    public void ApplyProjection()
    {
        // Deliberately NOT "if (Projection.Any()) return;" (the guard this replaced). That version assumed
        // a non-empty Projection always means every _projectionMapping entry was ALREADY resolved by-index by
        // some other mechanism (a join's RebindInnerShaperToOuterQuery, GroupBy's flatten shaper, a native
        // SelectMany result shaper) — true for those paths (confirmed empirically: _projectionMapping is
        // always EMPTY by this point when Projection was populated by one of them). A projected
        // reference-collection-nav list leaf (`Orders = c.Orders.ToList()`, EF-449/Task 1) breaks that
        // assumption: it calls MongoQueryExpression.AddToProjection directly (mirroring the cross-collection
        // Include path) for its OWN array shaper, independently of the generic
        // _projectionBindingExpressionVisitor fold — so Projection is non-empty by the time this runs whenever
        // such a leaf sits in the SAME projection as an ordinary scalar sibling (e.g. `new { c.CustomerID,
        // Orders = c.Orders.ToList() }`). The old guard then skipped flattening _projectionMapping entirely,
        // leaving the scalar sibling's ProjectionMember mapped to its raw (non-constant) expression forever;
        // GetProjectionIndex expects a ConstantExpression for any ProjectionMember-keyed binding and throws
        // ("Operation is not valid due to the current state of the object") at compile time.
        //
        // The fix is narrowly scoped to `Route == NativeRoute.Projection` — the exact condition
        // NativeProjectionBinder.TryPopulateNativeProjection sets on SUCCESS, which is the only route the
        // reference-collection-list leaf reaches. Widening unconditionally (dropping the guard whenever
        // _projectionMapping has entries, regardless of Route) regressed a genuinely DIFFERENT case, measured:
        // `Custom_projection_reference_navigation_PK_to_FK_optimization` (a MemberInit constructing a nested
        // Customer sub-object through a reference navigation, combined with a join) is a shape the native
        // projection binder correctly DECLINES (Route stays Fallback, _hasUnsupportedOperator true) — but its
        // generic shaper fold still leaves non-constant entries in _projectionMapping, and unconditionally
        // flattening those let the query silently succeed via the mixed/fallback shaper instead of the
        // `NotSupportedException` it must throw (`AssertTranslationFailed` in that test asserts exactly that
        // decline). Gating on Route == Projection keeps that decline intact while still covering every shape
        // this fix targets, since Route is Fallback whenever _hasUnsupportedOperator is true.
        //
        // CONFIRMED (review finding I2), not just reasoned: this guard is still wider than just the
        // reference-collection-list feature -- it also newly applies to a native WRAPPED-join projection
        // (EF-444, NativeJoinScopeProjectionBinder.TryBindProjection), which ALSO sets Route == Projection.
        // That path never calls _projectionBindingExpressionVisitor.Translate at all (TranslateSelect returns
        // early via BuildSelectManyResultShaper/BindResultMember once TryBindProjection succeeds), so
        // _projectionMapping still holds only the ONE entry the constructor seeds
        // (EmptyProjectionMember -> the root entity's own EntityProjectionExpression) -- never cleared, since
        // ReplaceProjectionMapping is never reached for this route. Two sub-cases, both verified by reading
        // the actual call graph rather than assumed:
        //   (a) the projection ALSO includes a whole-entity Outer leaf (`new { o, ... }`): BindResultMember
        //       (MongoQueryableMethodTranslatingExpressionVisitor.cs) resolves that SAME root
        //       EntityProjectionExpression via GetMappedProjection(EmptyProjectionMember) and calls
        //       AddToProjection on it FIRST, at translate time. AddToProjection dedupes by Expression.Equals --
        //       EntityProjectionExpression DOES override Equals (structurally, on EntityType + Name +
        //       ParentAccessExpression, not just reference identity) -- so this method's later AddToProjection
        //       call on the SAME object/EmptyProjectionMember entry resolves to the SAME existing index either
        //       way: reference equality already holds for this same-object case, and the structural override is
        //       a superset that would dedupe even two distinct-but-equivalent instances, so if anything it
        //       strengthens rather than weakens this argument; no new entry, no behavior change.
        //   (b) no whole-entity Outer leaf is projected: nothing else ever touches that constructor-seeded
        //       entry, so this method adds it to Projection for the first time here -- an extra, otherwise
        //       unused entry. Confirmed inert: the join-scope shaper is built ENTIRELY by INDEX (every
        //       ProjectionBindingExpression BindResultMember embeds already carries an Index, never a
        //       ProjectionMember), so nothing ever reads back the flattened EmptyProjectionMember constant
        //       this method produces; and nothing iterates the WHOLE Projection list at a point in the
        //       pipeline where this extra entry could matter (NativeJoinScopeProjectionBinder's own
        //       Projection-iterating collision check runs at TRANSLATE time, strictly before this
        //       POSTPROCESS-time method ever runs, so it never sees the extra entry either).
        // Spec baselines for every EF-444 join-projection test are unchanged, corroborating this.
        if (Projection.Any() && (_projectionMapping.Count == 0 || Select.Route != NativeRoute.Projection))
        {
            return;
        }

        Dictionary<ProjectionMember, Expression> result = new();
        foreach (var (projectionMember, expression) in _projectionMapping)
        {
            // The alias is normally the projection member's own name, but the emit side may have registered
            // an override (see MongoSelectDefinition.AddProjectionAliasOverride) — notably for a bare
            // selector body, whose ProjectionMember has no last member and would otherwise get a null alias.
            // Reading the override keeps the emitted $project key and the name the DOM shaper reads in sync.
            //
            // EF-395: also consult the override when Select.IsDistinct is set, even though that flips Route
            // to NativeRoute.GroupBy (NativeGroupByBinder.TryBindDistinctFromProjection's degenerate $group
            // over a projection). IsDistinct is set nowhere else, and only after re-adding each original
            // projection's alias unchanged via the flatten $project — so the override this select's ORIGINAL
            // (pre-Distinct) projection registered is still exactly what the flattened output emits, and
            // omitting it here would revert a bare body's alias to null (memberName), crashing the shaper.
            // An ordinary GroupBy(key).Select(aggregate) never sets IsDistinct, so it is unaffected.
            var memberName = projectionMember.Last?.Name;
            var alias = (Select.Route == NativeRoute.Projection || Select.IsDistinct)
                        && Select.TryGetProjectionAlias(memberName, out var overriddenAlias)
                ? overriddenAlias
                : memberName;

            result[projectionMember] = Constant(AddToProjection(expression, alias));
        }

        _projectionMapping = result;
    }

    public void ReplaceProjectionMapping(IDictionary<ProjectionMember, Expression> projectionMapping)
    {
        _projectionMapping.Clear();
        foreach (var (projectionMember, expression) in projectionMapping)
        {
            _projectionMapping[projectionMember] = expression;
        }
    }
}
