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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Clears the read-side projection list (<see cref="AddToProjection"/>'s store) when a GroupBy composes on an
    /// already-finalized prior grouping (called alongside <c>SnapshotPriorGroupingForNestedGroupBy</c>). The
    /// prior stage's entries are dead, and left in place they collide with the second stage's identically-named
    /// aliases ("Key"), so the de-dup renames them to "Key0" — a field the pipeline never emits.
    /// </summary>
    internal void ClearReadProjectionForNestedGroupBy()
        => _projection.Clear();

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
    /// Used by the bare whole-inner-element <c>SelectMany</c> (<c>MongoUnwindSource.WholeElement</c>): after
    /// <c>$unwind</c> + <c>$replaceRoot</c> the element is the root document. Left mapped to the outer entity,
    /// member bindings (e.g. an auto-included owned navigation) resolve against the wrong type and
    /// <c>BindNavigation</c> throws. Must be called before <c>MongoProjectionBindingExpressionVisitor.Translate</c>
    /// runs for the trailing selector.
    /// </remarks>
    public void ReRootProjectionAt(IEntityType entityType)
        => _projectionMapping[new ProjectionMember()] =
            new EntityProjectionExpression(entityType, new RootReferenceExpression(entityType));

    public IReadOnlyList<ProjectionExpression> Projection
        => _projection;

    public void ApplyProjection()
    {
        // Not a plain "if (Projection.Any()) return;": some native routes call AddToProjection directly at
        // translate time yet leave ordinary _projectionMapping entries unflattened, and GetProjectionIndex then
        // throws on the non-constant entry. Those routes:
        //  - Route == Projection: a reference-collection-nav list leaf (`new { c.CustomerID, Orders =
        //    c.Orders.ToList() }`) registers its own array shaper. Native wrapped-join projections also take this
        //    route; their shaper is built entirely by index, so flattening the constructor-seeded root entry
        //    is inert (AddToProjection dedupes it if already present).
        //  - HasClientWrappedWholeEntityShaper: a client method's whole-entity operand is registered directly,
        //    but a scalar sibling (e.g. `e.Manager != null`) still needs flattening.
        // Don't widen to other routes: under Fallback (e.g. Custom_projection_reference_navigation_PK_to_FK_
        // optimization) flattening lets a query that must fail translation silently succeed.
        if (Projection.Any()
            && (_projectionMapping.Count == 0
                || (Select.Route != NativeRoute.Projection && !Select.HasClientWrappedWholeEntityShaper)))
        {
            return;
        }

        Dictionary<ProjectionMember, Expression> result = new();
        foreach (var (projectionMember, expression) in _projectionMapping)
        {
            // Honor an alias override (MongoSelectDefinition.AddProjectionAliasOverride) so the emitted $project
            // key matches what the shaper reads — notably for a bare selector body, whose ProjectionMember has no
            // name. Also under IsDistinct (Route == GroupBy via TryBindDistinctFromProjection): the flatten
            // $project re-emits the original aliases unchanged, so skipping the override would null a bare
            // body's alias and crash the shaper.
            var memberName = projectionMember.Last?.Name;
            var alias = (Select.Route == NativeRoute.Projection || Select.IsDistinct)
                        && Select.TryGetProjectionAlias(memberName, out var overriddenAlias)
                ? overriddenAlias
                : memberName ?? TryGetNaturalMemberAlias(expression);

            result[projectionMember] = Constant(AddToProjection(expression, alias));
        }

        _projectionMapping = result;
    }

    /// <summary>
    /// Alias for a <c>_projectionMapping</c> entry with no member name (e.g. EmptyProjectionMember), such as a
    /// plain member read inside a client-only conditional that EF's generic fold left behind. A null alias means
    /// "read the whole document" and would collide with the query's whole-entity operand, mis-aliasing the field.
    /// </summary>
    private static string? TryGetNaturalMemberAlias(Expression expression)
    {
        if (expression is not MemberExpression { Member: PropertyInfo property } memberExpression)
        {
            return null;
        }

        var current = memberExpression.Expression;
        while (current is IncludeExpression include)
        {
            current = include.EntityExpression;
        }

        return current is StructuralTypeShaperExpression { StructuralType: IReadOnlyEntityType entityType }
            ? entityType.FindProperty(property)?.GetElementName() ?? property.Name
            : property.Name;
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
