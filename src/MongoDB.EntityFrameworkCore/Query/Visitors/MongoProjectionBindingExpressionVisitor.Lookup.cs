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

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

// Cross-collection $lookup Include machinery. The driver's LINQ provider has no native LeftJoin and cannot express
// collection or multi-hop joins, so collection navigations (Include / projected collections / ThenInclude / filtered
// Include) become manual $lookup + $unwind stages. Entry points: TryBindProjectedCollectionNavigation and
// TryBindProjectedCollectionNavigationCount, called from VisitMethodCall.
internal sealed partial class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    /// <summary>
    /// Binds a projected cross-collection collection navigation (<c>Orders = c.Orders.ToList()</c>), which EF lowers to
    /// <c>ToList(Select(Where(DbSet&lt;Target&gt;(), joinPredicate), selector))</c> with no enclosing
    /// <see cref="IncludeExpression"/>, to a <see cref="CollectionShaperExpression"/> over a dedicated
    /// <c>_lookup_&lt;Nav&gt;</c> array, mirroring the Include path (including nested ThenInclude $lookups).
    /// </summary>
    private bool TryBindProjectedCollectionNavigation(
        MethodCallExpression methodCallExpression,
        out Expression result)
    {
        result = null!;

        if (methodCallExpression.Method.DeclaringType != typeof(Enumerable)
            || methodCallExpression.Arguments.Count != 1)
        {
            return false;
        }

        switch (methodCallExpression.Method.Name)
        {
            case nameof(Enumerable.ToList):
            case nameof(Enumerable.ToArray):
            case nameof(Enumerable.ToHashSet):
                break;
            default:
                return false;
        }

        if (methodCallExpression.Arguments[0] is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Select), DeclaringType: var selectDeclaring }
            } selectCall
            || selectDeclaring != typeof(Queryable))
        {
            return false;
        }

        // The selector must materialize the element entity itself (identity, optionally wrapped in ThenInclude
        // IncludeExpressions). Anything else (o => o.OrderID) is an arbitrary correlated subquery, not a navigation
        // materialization, and must not be rerouted through a $lookup.
        var selectorBody = UnwrapLambdaBody(selectCall.Arguments[1], out var selectorParameter);
        if (selectorParameter == null || !IsEntityMaterializingSelector(selectorBody, selectorParameter))
        {
            return false;
        }

        // The source must be exactly the single navigation-join Where; extra operators indicate an arbitrary subquery.
        if (selectCall.Arguments[0] is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, _]
            } whereCall
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var targetEntityType = rootExpression.EntityType;

        var outerShaper = FindOuterShaper(whereCall.Arguments[1]);
        if (outerShaper?.StructuralType is not IEntityType outerEntityType)
        {
            return false;
        }

        var navigation = ResolveCollectionNavigation(outerShaper, outerEntityType, targetEntityType, whereCall.Arguments[1].UnwrapLambdaFromQuote());
        if (navigation == null)
        {
            return false;
        }

        var lookup = new LookupExpression(navigation);
        ExtractFilteredIncludePipeline(selectCall.Arguments[0], lookup, targetEntityType);
        ExtractThenIncludesFromSubquery(selectCall, lookup);
        _queryExpression.AddLookup(lookup);

        // On the native route, Visit(outerShaper) would hit VisitExtension's whole-root-entity leaf arm and, inside
        // VisitNew's per-argument loop, replace this member's array-leaf mapping with the whole entity: resolve the
        // EntityProjectionExpression directly. Other routes never reach that arm.
        _includedNavigations.Push(navigation);
        EntityProjectionExpression outerEntityProjection;
        if (_queryExpression.Select.Route == NativeRoute.Projection)
        {
            // FindOuterShaper only returns shapers bound by a ProjectionBindingExpression.
            var outerProjectionBinding = (ProjectionBindingExpression)outerShaper.ValueBufferExpression;
            if (outerProjectionBinding.Index is int existingOuterIndex
                && outerProjectionBinding.QueryExpression == _queryExpression)
            {
                outerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[existingOuterIndex].Expression;
            }
            else if (outerProjectionBinding.ProjectionMember is not null)
            {
                outerEntityProjection = (EntityProjectionExpression)_queryExpression.GetMappedProjection(
                    outerProjectionBinding.ProjectionMember);
            }
            else
            {
                // Index-bound to a different query expression: practically unreachable; GetMappedProjection(null) would throw.
                _includedNavigations.Pop();
                return false;
            }
        }
        else
        {
            var visitedOuter = Visit(outerShaper);
            if (visitedOuter is not StructuralTypeShaperExpression
                {
                    ValueBufferExpression: ProjectionBindingExpression { Index: int outerIndex }
                })
            {
                _includedNavigations.Pop();
                return false;
            }

            outerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[outerIndex].Expression;
        }

        var lookupAlias = LookupExpression.GetLookupAlias(navigation);
        var objectArrayProjection = new ObjectArrayProjectionExpression(
            navigation, outerEntityProjection.ParentAccessExpression, lookupAlias);

        Expression innerShaperExpression = CreateNullableEntityShaper(
            navigation.TargetEntityType, objectArrayProjection.InnerProjection);

        innerShaperExpression = WrapSelectorWithNestedCollectionIncludes(
            selectorBody, innerShaperExpression, objectArrayProjection);

        var collectionShaper = new CollectionShaperExpression(
            objectArrayProjection,
            innerShaperExpression,
            navigation,
            navigation.TargetEntityType.ClrType);

        _queryExpression.AddToProjection(objectArrayProjection);
        _includedNavigations.Pop();

        result = collectionShaper;
        return true;
    }

    /// <summary>
    /// Binds a projected cross-collection collection-navigation Count (<c>c.Orders.Count</c>), which EF lowers to
    /// <c>Count(Where(DbSet&lt;Target&gt;(), joinPredicate))</c>. Registers a dedicated <c>_lookup_&lt;Nav&gt;</c> $lookup
    /// (injected right after the root source) and binds the count as a scalar projection; the EF-to-driver translator
    /// rewrites the subtree to <c>{ $size: "$_lookup_&lt;Nav&gt;" }</c>.
    /// </summary>
    private bool TryBindProjectedCollectionNavigationCount(
        MethodCallExpression methodCallExpression,
        out Expression result)
    {
        result = null!;

        if (methodCallExpression.Method.DeclaringType != typeof(Queryable)
            || methodCallExpression.Arguments.Count != 1)
        {
            return false;
        }

        switch (methodCallExpression.Method.Name)
        {
            case nameof(Queryable.Count):
            case nameof(Queryable.LongCount):
                break;
            default:
                return false;
        }

        if (methodCallExpression.Arguments[0] is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
                Arguments: [EntityQueryRootExpression rootExpression, _]
            } whereCall
            || whereDeclaring != typeof(Queryable))
        {
            return false;
        }

        var targetEntityType = rootExpression.EntityType;

        var outerShaper = FindOuterShaper(whereCall.Arguments[1]);
        if (outerShaper?.StructuralType is not IEntityType outerEntityType)
        {
            return false;
        }

        var navigation = ResolveCollectionNavigation(outerShaper, outerEntityType, targetEntityType, whereCall.Arguments[1].UnwrapLambdaFromQuote());
        if (navigation == null)
        {
            return false;
        }

        // Flagged so the EF-to-driver translator injects it right after the root source, before downstream stages that
        // read the _lookup_<Nav> array.
        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        _queryExpression.AddLookup(lookup);

        // Keeping a plain MethodCallExpression in the mapping leaves the projection push-down-able
        // (ProjectionAnalyzer.CanPushDown stays true); the EF-to-driver translator rewrites it to $size.
        result = BindWholeLeaf(methodCallExpression);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="call"/> is a quantifier or aggregate (<c>Any</c>/<c>All</c>/<c>Count</c>/<c>LongCount</c>/
    /// <c>Sum</c>/<c>Min</c>/<c>Max</c>/<c>Average</c>) over <c>Where(DbSet&lt;Target&gt;(), predicate)</c>. Structural
    /// only: whether the correlation is the navigation's foreign key is decided by the native binder
    /// (<c>ResolveReferencedCollection</c>), which must have accepted the projection before this is consulted.
    /// </summary>
    private static bool IsReferencedCollectionAggregateLeaf(MethodCallExpression call)
        => call.Method.DeclaringType == typeof(Queryable)
           && call.Method.Name is nameof(Queryable.Any) or nameof(Queryable.All) or nameof(Queryable.Count)
               or nameof(Queryable.LongCount) or nameof(Queryable.Sum) or nameof(Queryable.Min) or nameof(Queryable.Max)
               or nameof(Queryable.Average)
           && call.Arguments is [MethodCallExpression
           {
               Method: { Name: nameof(Queryable.Where), DeclaringType: var whereDeclaring },
               Arguments: [EntityQueryRootExpression, _]
           }, ..]
           && whereDeclaring == typeof(Queryable);

    private static Expression UnwrapLambdaBody(Expression selector, out ParameterExpression parameter)
    {
        var unwrapped = selector.UnwrapQuote();
        if (unwrapped is LambdaExpression { Parameters: [var p] } lambda)
        {
            parameter = p;
            return lambda.Body;
        }

        parameter = null;
        return unwrapped;
    }

    /// <summary>
    /// Whether the selector body materializes the element entity itself: the identity parameter or a ThenInclude chain
    /// whose innermost entity is that parameter.
    /// </summary>
    private static bool IsEntityMaterializingSelector(Expression body, ParameterExpression parameter)
        => body.UnwrapIncludes() == parameter;

    /// <summary>
    /// Locates the outer (principal-side) <see cref="StructuralTypeShaperExpression"/> referenced inside a projected
    /// collection navigation's FK-correlation predicate (the <c>c</c> in <c>o =&gt; o.CustomerId == c.Id</c>). Keeps the
    /// first shaper bound through a <see cref="ProjectionBindingExpression"/> (an entity projected by the enclosing
    /// query, not a transient shaper inside the subquery), or <see langword="null"/>.
    /// </summary>
    private static StructuralTypeShaperExpression FindOuterShaper(Expression predicate)
    {
        StructuralTypeShaperExpression found = null;
        var finder = new ShaperFinder(shaper =>
        {
            if (found == null && shaper.ValueBufferExpression is ProjectionBindingExpression)
            {
                found = shaper;
            }
        });
        finder.Visit(predicate);
        return found;
    }

    /// <summary>
    /// Reports every <see cref="StructuralTypeShaperExpression"/> in a subtree through a callback, so the caller applies
    /// its own selection policy (<see cref="FindOuterShaper"/>); a shaper can sit at any depth in a predicate.
    /// </summary>
    private sealed class ShaperFinder(Action<StructuralTypeShaperExpression> onFound) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            if (node is StructuralTypeShaperExpression shaper)
            {
                onFound(shaper);
                return node;
            }

            return base.VisitExtension(node);
        }
    }

    /// <summary>
    /// Resolves the collection navigation off <paramref name="outerEntityType"/> whose target is
    /// <paramref name="targetEntityType"/> AND whose foreign-key correlation <paramref name="correlationPredicate"/> is;
    /// <see langword="null"/> otherwise, in which case the caller declines the <c>$lookup</c> fast-path.
    /// </summary>
    /// <remarks>
    /// The target type alone is not enough: binding emits <c>$lookup {localField: principalKey, foreignField: fk}</c>, so any
    /// other predicate (<c>Orders.Where(o =&gt; o.Total &gt; 5).Count()</c>, a <c>GroupJoin</c> on <c>Name == Tag</c>, a
    /// non-key outer member) would silently answer with the foreign-key rows. Same strict match as the native binders
    /// (<see cref="NativeTranslation.NativeCorrelationMatcher.TryMatchCorrelatedCollection"/>).
    /// </remarks>
    internal static INavigation ResolveCollectionNavigation(
        StructuralTypeShaperExpression outerShaper,
        IEntityType outerEntityType,
        IEntityType targetEntityType,
        LambdaExpression correlationPredicate)
        => NativeTranslation.NativeCorrelationMatcher.TryMatchCorrelatedCollection(
            correlationPredicate.Body, outerEntityType, outerShaper, targetEntityType, requireEmbedded: false, out var navigation)
            ? navigation
            : null;

    /// <summary>
    /// Wraps an element shaper with the nested ThenInclude collection includes carried by the (already unwrapped and
    /// validated) selector body of a projected collection navigation.
    /// </summary>
    private Expression WrapSelectorWithNestedCollectionIncludes(
        Expression selectorBody,
        Expression innerShaper,
        ObjectArrayProjectionExpression parentArrayProjection)
    {
        var includes = new List<IncludeExpression>();
        var body = selectorBody;
        while (body is IncludeExpression include)
        {
            includes.Add(include);
            body = include.EntityExpression;
        }

        if (includes.Count == 0)
        {
            return innerShaper;
        }

        var result = innerShaper;
        for (var i = includes.Count - 1; i >= 0; i--)
        {
            var include = includes[i];
            if (include.Navigation is INavigation nav && !nav.IsEmbedded() && nav.IsCollection)
            {
                var nestedLookupAlias = LookupExpression.GetLookupAlias(nav);
                var nestedArrayProjection = new ObjectArrayProjectionExpression(
                    nav, parentArrayProjection.InnerProjection.ParentAccessExpression, nestedLookupAlias, null);

                var nestedInnerShaper = CreateNullableEntityShaper(
                    nav.TargetEntityType, nestedArrayProjection.InnerProjection);

                var nestedCollectionShaper = new CollectionShaperExpression(
                    nestedArrayProjection,
                    nestedInnerShaper,
                    nav,
                    nav.TargetEntityType.ClrType);

                result = new IncludeExpression(result, nestedCollectionShaper, nav, include.SetLoaded);
            }
        }

        return result;
    }

    /// <summary>
    /// For a collection Include whose declaring entity is a joined entity read from a root-level cross-collection
    /// <see cref="ObjectAccessExpression"/> (the Inner side of <c>Owners.LeftJoin(Orders.Include(r =&gt; r.OrderLines), ...)</c>),
    /// returns the pending forced-unwind <c>$lookup</c> (matched by alias) so the Include's <c>$lookup</c> can nest in its
    /// sub-pipeline; <see langword="null"/> leaves the caller's type-based matching in charge.
    /// </summary>
    private LookupExpression TryGetDeclaringJoinLookup(IncludeExpression includeExpression)
    {
        var entityExpression = includeExpression.EntityExpression.UnwrapIncludes();

        if (entityExpression is not StructuralTypeShaperExpression
            {
                ValueBufferExpression: ProjectionBindingExpression { Index: int index } binding
            }
            || binding.QueryExpression != _queryExpression
            || _queryExpression.Projection[index].Expression is not EntityProjectionExpression
            {
                ParentAccessExpression: ObjectAccessExpression { AccessExpression: RootReferenceExpression } access
            }
            || access.Navigation is { } accessNavigation && accessNavigation.IsEmbedded())
        {
            return null;
        }

        return _queryExpression.GetPendingLookups().FirstOrDefault(l => l.As == access.Name && l.ShouldUnwind);
    }

    /// <summary>
    /// Builds the IncludeExpression with a <see cref="CollectionShaperExpression"/> reading the $lookup array of a
    /// cross-collection collection Include.
    /// </summary>
    private Expression RewriteCollectionIncludeForLookup(
        IncludeExpression includeExpression,
        INavigation navigation,
        string lookupAlias)
    {
        _includedNavigations.Push(navigation);
        var visitedEntity = Visit(includeExpression.EntityExpression);

        // A reference Include off the same root may already wrap the entity in IncludeExpressions: unwrap to the shaper
        // carrying the projection index; visitedEntity is passed to Update below to preserve the wrapping.
        var shaperCandidate = visitedEntity.UnwrapIncludes();

        EntityProjectionExpression outerEntityProjection;
        if (shaperCandidate is StructuralTypeShaperExpression shaper
            && shaper.ValueBufferExpression is ProjectionBindingExpression binding
            && binding.Index is int idx)
        {
            outerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[idx].Expression;
        }
        else
        {
            _includedNavigations.Pop();
            return base.VisitExtension(includeExpression);
        }

        var objectArrayProjection = new ObjectArrayProjectionExpression(
            navigation, outerEntityProjection.ParentAccessExpression, lookupAlias);

        Expression innerShaperExpression = CreateNullableEntityShaper(
            navigation.TargetEntityType, objectArrayProjection.InnerProjection);

        // Wrap with IncludeExpressions for nested collections (ThenInclude) so the binding remover processes them per
        // parent document.
        innerShaperExpression = WrapWithNestedCollectionIncludes(
            includeExpression.NavigationExpression, innerShaperExpression, objectArrayProjection);

        var collectionShaper = new CollectionShaperExpression(
            objectArrayProjection,
            innerShaperExpression,
            navigation,
            navigation.TargetEntityType.ClrType);

        _queryExpression.AddToProjection(objectArrayProjection);
        _includedNavigations.Pop();

        return includeExpression.Update(visitedEntity, collectionShaper);
    }

    /// <summary>
    /// Extracts nested ThenInclude $lookups (as pipeline stages on the parent lookup) and filtered-Include operations
    /// (OrderBy, Skip, Take) from an Include's NavigationExpression.
    /// </summary>
    private static void ExtractNestedIncludePipeline(
        Expression navigationExpression,
        LookupExpression parentLookup,
        IEntityType targetEntityType)
    {
        while (navigationExpression is IncludeExpression nestedInclude
               && nestedInclude.Navigation is INavigation nestedNav
               && !nestedNav.IsEmbedded() && nestedNav.IsCollection)
        {
            var nestedLookup = new LookupExpression(nestedNav);
            ExtractNestedIncludePipeline(nestedInclude.NavigationExpression, nestedLookup, nestedNav.TargetEntityType);

            parentLookup.PipelineStages.Add(BuildLookupDocument(nestedLookup));
            // PipelineKind is write-once: don't overwrite DiscriminatorNarrowed or FilteredInclude.
            if (parentLookup.PipelineKind == LookupPipelineKind.None)
            {
                parentLookup.PipelineKind = LookupPipelineKind.NestedInclude;
            }

            navigationExpression = nestedInclude.EntityExpression;
        }

        if (navigationExpression is MaterializeCollectionNavigationExpression materialize)
        {
            var subquery = materialize.Subquery;
            ExtractFilteredIncludePipeline(subquery, parentLookup, targetEntityType);

            ExtractThenIncludesFromSubquery(subquery, parentLookup);
        }
    }

    /// <summary>
    /// Finds IncludeExpressions inside a Select lambda of a collection Include subquery: ThenInclude on
    /// collection-then-collection paths.
    /// </summary>
    private static void ExtractThenIncludesFromSubquery(Expression subquery, LookupExpression parentLookup)
    {
        if (subquery is not MethodCallExpression { Method.Name: "Select" } selectCall)
            return;

        var selectorArg = selectCall.Arguments[1].UnwrapQuote();

        if (selectorArg is not LambdaExpression { Body: IncludeExpression includeExpr })
            return;

        var current = (Expression)includeExpr;
        while (current is IncludeExpression nested
               && nested.Navigation is INavigation nav
               && !nav.IsEmbedded() && nav.IsCollection)
        {
            var nestedLookup = new LookupExpression(nav);

            if (nested.NavigationExpression is MaterializeCollectionNavigationExpression innerMaterialize)
            {
                ExtractThenIncludesFromSubquery(innerMaterialize.Subquery, nestedLookup);
                ExtractFilteredIncludePipeline(innerMaterialize.Subquery, nestedLookup, nav.TargetEntityType);
            }

            parentLookup.PipelineStages.Add(BuildLookupDocument(nestedLookup));
            // Write-once, as in ExtractNestedIncludePipeline.
            if (parentLookup.PipelineKind == LookupPipelineKind.None)
            {
                parentLookup.PipelineKind = LookupPipelineKind.NestedInclude;
            }

            current = nested.EntityExpression;
        }

        // Reference ThenInclude on a collection item (`od => od.Order`): EF lowers it to a Join inside the collection
        // subquery; emit a nested $lookup + $unwind in the parent pipeline so each element carries the document under
        // "_lookup_<RefNav>".
        while (current is IncludeExpression { Navigation: INavigation refNav } nestedRef
               && !refNav.IsEmbedded() && !refNav.IsCollection)
        {
            AddReferenceLookupStages(parentLookup, refNav);
            current = nestedRef.EntityExpression;
        }
    }

    /// <summary>
    /// Adds the $lookup + $unwind stages for a reference ThenInclude nested under a collection Include to the parent
    /// lookup's pipeline.
    /// </summary>
    private static void AddReferenceLookupStages(LookupExpression parentLookup, INavigation referenceNavigation)
    {
        var refLookup = new LookupExpression(referenceNavigation);

        parentLookup.PipelineStages.Add(BuildLookupDocument(refLookup));
        // Always preserving, unlike the flat-lookup path: this $unwind runs inside the parent's sub-pipeline, so a
        // non-preserving one would drop collection elements, and an Include must never change the result set.
        parentLookup.PipelineStages.Add(refLookup.ToUnwindStageDocument(preserveNullAndEmptyArrays: true));
        // Write-once, as in ExtractNestedIncludePipeline.
        if (parentLookup.PipelineKind == LookupPipelineKind.None)
        {
            parentLookup.PipelineKind = LookupPipelineKind.NestedInclude;
        }
    }

    /// <summary>
    /// Builds the <c>$lookup</c> stage for a nested lookup. Every nested-lookup construction site must go through this,
    /// or a lookup carrying <see cref="LookupExpression.PipelineStages"/> is silently flattened and loses them (EF-374).
    /// </summary>
    private static BsonDocument BuildLookupDocument(LookupExpression lookup)
        => lookup.ToLookupStageDocument();

    /// <summary>
    /// Extracts filtered Include operations (OrderBy, Skip, Take) from a subquery into pipeline stages on the lookup.
    /// </summary>
    private static void ExtractFilteredIncludePipeline(
        Expression navigationExpression,
        LookupExpression lookup,
        IEntityType targetEntityType)
    {
        // Walks outermost-first; consecutive OrderBy/ThenBy keys are buffered and flushed as one $sort, since a later
        // $sort would re-sort from scratch and discard earlier keys.
        var stages = new List<BsonDocument>();
        var sortKeys = new List<(string Field, int Direction)>();
        var current = navigationExpression;

        while (current is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;
            switch (methodName)
            {
                case "OrderBy" or "ThenBy":
                    sortKeys.Add((GetSortField(methodCall, targetEntityType), 1));
                    current = methodCall.Arguments[0];
                    break;

                case "OrderByDescending" or "ThenByDescending":
                    sortKeys.Add((GetSortField(methodCall, targetEntityType), -1));
                    current = methodCall.Arguments[0];
                    break;

                case "Skip":
                    FlushSortKeys(sortKeys, stages);
                    if (methodCall.Arguments[1] is ConstantExpression skipConst)
                    {
                        stages.Add(new BsonDocument("$skip", (int)skipConst.Value!));
                    }
                    current = methodCall.Arguments[0];
                    break;

                case "Take":
                    FlushSortKeys(sortKeys, stages);
                    if (methodCall.Arguments[1] is ConstantExpression takeConst)
                    {
                        stages.Add(new BsonDocument("$limit", (int)takeConst.Value!));
                    }
                    current = methodCall.Arguments[0];
                    break;

                case "Where":
                    FlushSortKeys(sortKeys, stages);

                    // The synthetic FK-correlation Where is dropped ($lookup localField/foreignField handles it). A user
                    // filtered-Include predicate or HasQueryFilter also lowers to a Where but is NOT translated into the
                    // sub-pipeline $match; dropping it would return wrong data (bypassing a soft-delete/tenant filter): fail.
                    if (IsFkCorrelationPredicate(methodCall.Arguments[1].UnwrapLambdaFromQuote()))
                    {
                        current = methodCall.Arguments[0];
                        break;
                    }

                    throw new InvalidOperationException(CoreStrings.TranslationFailed(navigationExpression.Print()));

                default:
                    current = null;
                    break;
            }
        }

        FlushSortKeys(sortKeys, stages);

        stages.Reverse();
        if (stages.Count > 0)
        {
            lookup.PipelineStages.AddRange(stages);

            // Don't overwrite DiscriminatorNarrowed (TPH-narrowed target with its own $match): that combination isn't
            // validated for the native FilteredInclude path.
            if (lookup.PipelineKind == LookupPipelineKind.None)
            {
                lookup.PipelineKind = LookupPipelineKind.FilteredInclude;
            }
        }
    }

    /// <summary>Emits one $sort stage from the buffered keys (buffered last-first, so walked in reverse), then clears them.</summary>
    private static void FlushSortKeys(List<(string Field, int Direction)> sortKeys, List<BsonDocument> stages)
    {
        if (sortKeys.Count == 0)
        {
            return;
        }

        var sortDocument = new BsonDocument();
        for (var i = sortKeys.Count - 1; i >= 0; i--)
        {
            sortDocument.Add(sortKeys[i].Field, sortKeys[i].Direction);
        }

        stages.Add(new BsonDocument("$sort", sortDocument));
        sortKeys.Clear();
    }

    /// <summary>
    /// Whether a Where lowered into a collection-Include subquery is the synthetic FK-correlation predicate rather than a
    /// user filtered-Include predicate or a dependent-side query filter.
    /// </summary>
    /// <remarks>
    /// The correlation references the OUTER entity (a <see cref="StructuralTypeShaperExpression"/> and/or a parameter that
    /// is not the lambda's own); a self-contained filter references only its element parameter plus constants/closures.
    /// Conservative: only a predicate referencing the parent is droppable, so a filter is never silently dropped.
    /// </remarks>
    private static bool IsFkCorrelationPredicate(LambdaExpression predicate)
    {
        var detector = new OuterReferenceDetector(predicate.Parameters);
        detector.Visit(predicate.Body);
        return detector.ReferencesOuter;
    }

    /// <summary>
    /// Decides whether a predicate body references the OUTER query: a <see cref="StructuralTypeShaperExpression"/> or a
    /// <see cref="ParameterExpression"/> the lambda did not declare. Short-circuits via <see cref="ReferencesOuter"/>.
    /// </summary>
    private sealed class OuterReferenceDetector(IReadOnlyList<ParameterExpression> ownParameters) : ExpressionVisitor
    {
        public bool ReferencesOuter { get; private set; }

        public override Expression Visit(Expression node)
        {
            if (ReferencesOuter || node == null)
            {
                return node;
            }

            if (node is StructuralTypeShaperExpression)
            {
                ReferencesOuter = true;
                return node;
            }

            return base.Visit(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (!ownParameters.Contains(node))
            {
                ReferencesOuter = true;
            }

            return node;
        }
    }

    /// <summary>Extracts the sort field name from an OrderBy/OrderByDescending call.</summary>
    private static string GetSortField(MethodCallExpression orderByCall, IEntityType entityType)
    {
        var name = orderByCall.Arguments[1].UnwrapLambdaFromQuote().Body.TryGetSimplePropertyName();
        if (name == null)
        {
            // Not a simple property access: fail loudly rather than silently sorting by "_id".
            throw new InvalidOperationException(CoreStrings.TranslationFailed(orderByCall.Print()));
        }

        var property = entityType.FindProperty(name);
        if (property == null)
        {
            // Not a mapped property of the target (`o.Label.Length`, or a member reached through an embedded document where
            // only the outermost name survives): a raw-name $sort would hit a field absent from the documents, which
            // MongoDB treats as all-equal (arbitrary order). Fail loudly.
            throw new InvalidOperationException(CoreStrings.TranslationFailed(orderByCall.Print()));
        }

        return Microsoft.EntityFrameworkCore.MongoPropertyExtensions.GetElementName(property);
    }

    /// <summary>
    /// For ThenInclude on collection-then-collection paths, wraps the nested IncludeExpressions from the subquery's Select
    /// lambda around the inner shaper so it reads the nested $lookup arrays from each parent document.
    /// </summary>
    private Expression WrapWithNestedCollectionIncludes(
        Expression navigationExpression,
        Expression innerShaper,
        ObjectArrayProjectionExpression parentArrayProjection)
    {
        if (navigationExpression is not MaterializeCollectionNavigationExpression materialize)
            return innerShaper;

        if (materialize.Subquery is not MethodCallExpression { Method.Name: "Select" } selectCall)
            return innerShaper;

        var selectorArg = selectCall.Arguments[1].UnwrapQuote();

        if (selectorArg is not LambdaExpression lambda)
            return innerShaper;

        var includes = new List<IncludeExpression>();
        var body = lambda.Body;
        while (body is IncludeExpression include)
        {
            includes.Add(include);
            body = include.EntityExpression;
        }

        if (includes.Count == 0)
            return innerShaper;

        var result = innerShaper;
        for (var i = includes.Count - 1; i >= 0; i--)
        {
            var include = includes[i];
            if (include.Navigation is INavigation nav && !nav.IsEmbedded() && nav.IsCollection)
            {
                var nestedLookupAlias = LookupExpression.GetLookupAlias(nav);
                var nestedArrayProjection = new ObjectArrayProjectionExpression(
                    nav, parentArrayProjection.InnerProjection.ParentAccessExpression, nestedLookupAlias, null);

                var nestedInnerShaper = CreateNullableEntityShaper(
                    nav.TargetEntityType, nestedArrayProjection.InnerProjection);

                // Recurse so deeper ThenIncludes are wrapped onto this element shaper, else the deepest navigation comes
                // back null. Reads "_lookup_<Nav>" sub-documents relative to THIS collection's element, matching the
                // pipeline from ExtractThenIncludesFromSubquery / AddReferenceLookupStages.
                var wrappedNestedInnerShaper = WrapWithNestedCollectionIncludes(
                    include.NavigationExpression, nestedInnerShaper, nestedArrayProjection);

                var nestedCollectionShaper = new CollectionShaperExpression(
                    nestedArrayProjection,
                    wrappedNestedInnerShaper,
                    nav,
                    nav.TargetEntityType.ClrType);

                result = new IncludeExpression(result, nestedCollectionShaper, nav, include.SetLoaded);
            }
            else if (include.Navigation is INavigation refNav && !refNav.IsEmbedded())
            {
                // Reference ThenInclude on a collection item (`od.Order`): the document is unwound under "_lookup_<RefNav>" in
                // each parent element (AddReferenceLookupStages). Visiting the raw EF Join field gives a non-materializable
                // scalar binding, so build the shaper reading it relative to the parent element.
                var refAccess = new NavigationObjectAccessExpression(
                    refNav,
                    parentArrayProjection.InnerProjection.ParentAccessExpression,
                    false,
                    LookupExpression.GetLookupAlias(refNav));
                var refEntityProjection = new EntityProjectionExpression(refNav.TargetEntityType, refAccess);
                var refShaper = CreateNullableEntityShaper(refNav.TargetEntityType, refEntityProjection);

                result = new IncludeExpression(result, refShaper, refNav, include.SetLoaded);
            }
        }

        return result;
    }
}
