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
        if (Projection.Any())
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
