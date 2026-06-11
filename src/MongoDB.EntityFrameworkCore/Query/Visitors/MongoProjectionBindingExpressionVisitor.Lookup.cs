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

// TODO(EF-317): Cross-collection $lookup Include machinery. EF Core lowers cross-collection collection
// navigations (Include / projected collections / nested ThenInclude / filtered Include) onto manual
// $lookup + $unwind pipeline stages because the C# driver's LINQ provider has no native LeftJoin and
// cannot express collection or multi-hop joins. When the driver ships native LeftJoin support, the
// members in this file are expected to be removed; the only entry points from the rest of the visitor
// are the TryBindProjectedCollectionNavigation / TryBindProjectedCollectionNavigationCount dispatch
// calls in VisitMethodCall.
internal sealed partial class MongoProjectionBindingExpressionVisitor : ExpressionVisitor
{
    /// <summary>
    /// Detects and binds a projected cross-collection collection navigation such as
    /// <c>select new { ..., Orders = c.Orders.ToList() }</c>. EF Core lowers the projected collection to
    /// <c>Enumerable.ToList(Queryable.Select(Queryable.Where(DbSet&lt;Target&gt;(), joinPredicate), selector))</c>
    /// where there is no enclosing <see cref="IncludeExpression"/> to register the $lookup. This binds the
    /// projection to a <see cref="CollectionShaperExpression"/> over a dedicated <c>_lookup_&lt;Nav&gt;</c>
    /// array (added as a $lookup on the query), mirroring the cross-collection Include path including any
    /// nested ThenInclude $lookups carried by the selector.
    /// </summary>
    private bool TryBindProjectedCollectionNavigation(
        MethodCallExpression methodCallExpression,
        out Expression result)
    {
        result = null!;

        // Unwrap the materializing terminal operator (ToList / ToArray / ToHashSet).
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

        // Expect Queryable.Select(source, selector).
        if (methodCallExpression.Arguments[0] is not MethodCallExpression
            {
                Method: { Name: nameof(Queryable.Select), DeclaringType: var selectDeclaring }
            } selectCall
            || selectDeclaring != typeof(Queryable))
        {
            return false;
        }

        // The selector must materialize the navigation's element entity itself: an identity projection
        // (o => o) optionally wrapped in ThenInclude IncludeExpressions (o => Include(o, ...)). Anything
        // else (e.g. o => o.OrderID, o => o.OrderDate) is an arbitrary correlated subquery projection, not
        // a navigation materialization, and must not be rerouted through a $lookup here.
        var selectorBody = UnwrapLambdaBody(selectCall.Arguments[1], out var selectorParameter);
        if (selectorParameter == null || !IsEntityMaterializingSelector(selectorBody, selectorParameter))
        {
            return false;
        }

        // The source between the Select and the root must be exactly the single navigation-join Where
        // (DbSet.Where(fkEquality)). Additional operators (extra Where/OrderBy/Skip/Take with custom
        // keys) indicate an arbitrary subquery rather than a plain projected collection navigation.
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

        // Find the outer entity shaper referenced by the join predicate and the collection navigation
        // (off the outer entity) whose target is this DbSet.
        var outerShaper = FindOuterShaper(whereCall.Arguments[1]);
        if (outerShaper?.StructuralType is not IEntityType outerEntityType)
        {
            return false;
        }

        var navigation = ResolveCollectionNavigation(outerEntityType, targetEntityType, whereCall);
        if (navigation == null)
        {
            return false;
        }

        // Register the $lookup for the projected collection, carrying any filtered-Include pipeline stages
        // and nested ThenInclude $lookups extracted from the selector.
        var lookup = new LookupExpression(navigation);
        ExtractFilteredIncludePipeline(selectCall.Arguments[0], lookup, targetEntityType);
        ExtractThenIncludesFromSubquery(selectCall, lookup);
        _queryExpression.AddLookup(lookup);

        // Bind the outer entity so we can reach its EntityProjectionExpression / ParentAccessExpression.
        _includedNavigations.Push(navigation);
        var visitedOuter = Visit(outerShaper);
        if (visitedOuter is not StructuralTypeShaperExpression
            {
                ValueBufferExpression: ProjectionBindingExpression { Index: int outerIndex }
            })
        {
            _includedNavigations.Pop();
            return false;
        }

        var outerEntityProjection = (EntityProjectionExpression)_queryExpression.Projection[outerIndex].Expression;

        var lookupAlias = LookupExpression.GetLookupAlias(navigation);
        var objectArrayProjection = new ObjectArrayProjectionExpression(
            navigation, outerEntityProjection.ParentAccessExpression, lookupAlias);

        Expression innerShaperExpression = new StructuralTypeShaperExpression(
            navigation.TargetEntityType,
            Expression.Convert(
                Expression.Convert(objectArrayProjection.InnerProjection, typeof(object)),
                typeof(ValueBuffer)),
            nullable: true);

        // Wrap the inner shaper with nested ThenInclude collection includes so the shaper reads the
        // nested $lookup arrays from each element document.
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
    /// Detects and binds a projected cross-collection collection-navigation Count such as
    /// <c>select new { ..., TotalOrders = c.Orders.Count }</c>. EF Core lowers the count to
    /// <c>Queryable.Count(Queryable.Where(DbSet&lt;Target&gt;(), joinPredicate))</c> (no predicate on the
    /// Count). This registers a dedicated <c>_lookup_&lt;Nav&gt;</c> $lookup (flagged to be injected right
    /// after the root collection source) and binds the count as a scalar projection. The EF-to-driver
    /// translator rewrites the same subtree into a server-side <c>{ $size: "$_lookup_&lt;Nav&gt;" }</c>.
    /// </summary>
    private bool TryBindProjectedCollectionNavigationCount(
        MethodCallExpression methodCallExpression,
        out Expression result)
    {
        result = null!;

        // Expect Queryable.Count(source) / Queryable.LongCount(source) with NO predicate.
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

        // The source must be exactly the single navigation-join Where (DbSet.Where(fkEquality)).
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

        // Find the outer entity shaper referenced by the join predicate and the collection navigation
        // (off the outer entity) whose target is this DbSet.
        var outerShaper = FindOuterShaper(whereCall.Arguments[1]);
        if (outerShaper?.StructuralType is not IEntityType outerEntityType)
        {
            return false;
        }

        var navigation = ResolveCollectionNavigation(outerEntityType, targetEntityType, whereCall);
        if (navigation == null)
        {
            return false;
        }

        // Register the $lookup, flagged so the EF-to-driver translator injects it right after the root
        // source (before the user's downstream $match/$sort/$project that read the _lookup_<Nav> array).
        var lookup = new LookupExpression(navigation) { InjectAfterRoot = true };
        _queryExpression.AddLookup(lookup);

        // Bind the count as a scalar projection mapped to the original Count(Where(...)) subtree. Keeping
        // it a plain MethodCallExpression in the projection mapping leaves the projection push-down-able
        // (ProjectionAnalyzer.CanPushDown stays true); the EF-to-driver translator rewrites it to $size.
        var projectionMember = GetCurrentProjectionMember();
        _projectionMapping[projectionMember] = methodCallExpression;

        result = new ProjectionBindingExpression(_queryExpression, projectionMember, methodCallExpression.Type);
        return true;
    }

    /// <summary>
    /// Unwrap a (possibly quoted) lambda selector, returning its body and single parameter.
    /// </summary>
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
    /// Whether the selector body materializes the element entity itself — either the identity parameter
    /// or an <see cref="IncludeExpression"/> chain (ThenInclude) whose innermost entity is that parameter.
    /// </summary>
    private static bool IsEntityMaterializingSelector(Expression body, ParameterExpression parameter)
    {
        while (body is IncludeExpression include)
        {
            body = include.EntityExpression;
        }

        return body == parameter;
    }

    /// <summary>
    /// Locate the outer entity <see cref="StructuralTypeShaperExpression"/> — the principal side of the join —
    /// referenced inside a projected collection navigation's FK-correlation predicate (e.g. the <c>c</c> in
    /// <c>o =&gt; o.CustomerId == c.Id</c>). The shaper can sit at any depth within the predicate tree, so we
    /// walk the tree with <see cref="ShaperFinder"/> and keep the first shaper that is bound through a
    /// <see cref="ProjectionBindingExpression"/> — that one is an entity projected by the enclosing query (the
    /// outer entity we want), as opposed to any transient shaper introduced inside the subquery itself.
    /// Returns <see langword="null"/> when no such shaper is present.
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
    /// A minimal <see cref="ExpressionVisitor"/> that surfaces every <see cref="StructuralTypeShaperExpression"/>
    /// in a subtree by invoking the supplied callback for each one. A visitor (rather than a positional
    /// pattern-match) is required because a shaper node — EF Core's representation of a materialized entity — is
    /// an <see cref="ExpressionType.Extension"/> node that can be nested at arbitrary depth inside a predicate
    /// (under <c>==</c>, <c>&amp;&amp;</c>, conversions, <c>EF.Property</c> calls, …), with no fixed position to
    /// match against, so the whole tree must be traversed. Reporting every match through a callback (instead of
    /// returning one) lets the caller apply its own selection policy — see <see cref="FindOuterShaper"/>.
    /// </summary>
    private sealed class ShaperFinder(Action<StructuralTypeShaperExpression> onFound) : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node)
        {
            // StructuralTypeShaperExpression is an Extension node, so VisitExtension is where it surfaces.
            if (node is StructuralTypeShaperExpression shaper)
            {
                onFound(shaper);
                return node; // A shaper is a leaf for our purposes — don't descend into its own subtree.
            }

            return base.VisitExtension(node);
        }
    }

    /// <summary>
    /// Resolve the collection navigation off <paramref name="outerEntityType"/> whose target is
    /// <paramref name="targetEntityType"/>. The target entity type alone is ambiguous when more than one
    /// collection navigation points at it — two foreign keys between the same pair of types, or a
    /// self-reference. In that case we disambiguate using the dependent-side foreign-key properties the
    /// correlation predicate (<paramref name="whereCall"/>'s FK-equality lambda) actually compares against,
    /// matching the navigation whose <see cref="IForeignKey.Properties"/> are exactly those. Returns
    /// <see langword="null"/> when nothing matches, or when the match cannot be resolved unambiguously, in
    /// which case the caller declines the $lookup fast-path rather than guessing.
    /// </summary>
    private static INavigation ResolveCollectionNavigation(
        IEntityType outerEntityType,
        IEntityType targetEntityType,
        MethodCallExpression whereCall)
    {
        var candidates = outerEntityType.GetNavigations()
            .Where(n => n.IsCollection && !n.IsEmbedded() && n.TargetEntityType == targetEntityType)
            .ToList();

        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        // Ambiguous by target type: select the navigation whose foreign-key properties are exactly the
        // dependent-side properties the correlation predicate compares against.
        var dependentKeyNames = CollectDependentPropertyNames(whereCall.Arguments[1].UnwrapLambdaFromQuote());
        if (dependentKeyNames.Count == 0)
        {
            return null;
        }

        var byForeignKey = candidates
            .Where(n => n.ForeignKey.Properties.All(p => dependentKeyNames.Contains(p.Name)))
            .ToList();

        return byForeignKey.Count == 1 ? byForeignKey[0] : null;
    }

    /// <summary>
    /// Collect the names of properties accessed on the predicate's own lambda parameter(s) — the dependent
    /// side of an FK-correlation predicate. Member access (<c>o.CustomerId</c>) and
    /// <c>EF.Property(o, "CustomerId")</c> forms are both recognised; outer-shaper references (the principal
    /// key side) are ignored because they are not rooted at a lambda parameter.
    /// </summary>
    private static HashSet<string> CollectDependentPropertyNames(LambdaExpression predicate)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        new DependentPropertyNameCollector(predicate.Parameters, names).Visit(predicate.Body);
        return names;
    }

    private sealed class DependentPropertyNameCollector(
        IReadOnlyList<ParameterExpression> parameters,
        HashSet<string> names) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (RootsAtParameter(node.Expression))
            {
                names.Add(node.Member.Name);
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsEFPropertyMethod()
                && node.Arguments.Count == 2
                && node.Arguments[1] is ConstantExpression { Value: string propertyName }
                && RootsAtParameter(node.Arguments[0]))
            {
                names.Add(propertyName);
            }

            return base.VisitMethodCall(node);
        }

        private bool RootsAtParameter(Expression expression)
            => expression.RemoveConvert() is ParameterExpression p && parameters.Contains(p);
    }

    /// <summary>
    /// Wrap an element shaper with nested ThenInclude collection includes extracted from the selector lambda
    /// body of a projected collection navigation (the <c>o =&gt; Include(o, nav, ...)</c> body of the
    /// <c>Select(source, …)</c> call). <paramref name="selectorBody"/> is the lambda body the caller has
    /// already unwrapped and validated as an entity-materializing selector.
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

                var nestedInnerShaper = new StructuralTypeShaperExpression(
                    nav.TargetEntityType,
                    Expression.Convert(
                        Expression.Convert(nestedArrayProjection.InnerProjection, typeof(object)),
                        typeof(ValueBuffer)),
                    nullable: true);

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
    /// For cross-collection collection Include (e.g., Customer.Include(c => c.Orders)),
    /// build an IncludeExpression with a CollectionShaperExpression that reads from the $lookup array.
    /// The $lookup stage is appended via AppendLookupStages in the LINQ translator.
    /// </summary>
    private Expression RewriteCollectionIncludeForLookup(
        IncludeExpression includeExpression,
        INavigation navigation)
    {
        _includedNavigations.Push(navigation);
        var visitedEntity = Visit(includeExpression.EntityExpression);

        // The entity may already be wrapped in one or more IncludeExpressions when a reference
        // Include precedes this collection Include off the same root (e.g.
        // Include(o => o.Customer).Include(o => o.OrderDetails)). Unwrap the IncludeExpression
        // chain to reach the underlying StructuralTypeShaperExpression that carries the projection
        // index; the wrapping is preserved by passing visitedEntity through to Update below.
        var shaperCandidate = visitedEntity;
        while (shaperCandidate is IncludeExpression wrappingInclude)
        {
            shaperCandidate = wrappingInclude.EntityExpression;
        }

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

        var lookupAlias = LookupExpression.GetLookupAlias(navigation);
        var objectArrayProjection = new ObjectArrayProjectionExpression(
            navigation, outerEntityProjection.ParentAccessExpression, lookupAlias);

        Expression innerShaperExpression = new StructuralTypeShaperExpression(
            navigation.TargetEntityType,
            Expression.Convert(
                Expression.Convert(objectArrayProjection.InnerProjection, typeof(object)),
                typeof(ValueBuffer)),
            nullable: true);

        // For ThenInclude on collection-then-collection paths, wrap the inner shaper
        // with IncludeExpressions for nested collections so the binding remover
        // processes them when iterating each parent document.
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
    /// Process the NavigationExpression of an Include, extracting:
    /// - Nested ThenInclude $lookups (added as pipeline stages on the parent lookup)
    /// - Filtered Include operations (OrderBy, Skip, Take)
    /// </summary>
    private static void ExtractNestedIncludePipeline(
        Expression navigationExpression,
        LookupExpression parentLookup,
        IEntityType targetEntityType)
    {
        // Unwrap nested IncludeExpressions (ThenInclude on collections)
        while (navigationExpression is IncludeExpression nestedInclude
               && nestedInclude.Navigation is INavigation nestedNav
               && !nestedNav.IsEmbedded() && nestedNav.IsCollection)
        {
            var nestedLookup = new LookupExpression(nestedNav);
            // Recurse for deeper nesting
            ExtractNestedIncludePipeline(nestedInclude.NavigationExpression, nestedLookup, nestedNav.TargetEntityType);

            // Bubble deferred filter renders discovered in deeper nesting up toward the root lookup.
            parentLookup.PendingNestedFilterRenders.AddRange(nestedLookup.PendingNestedFilterRenders);

            // This nested $lookup is emitted in localField/foreignField form (no sub-pipeline), so a user filter
            // predicate on it cannot be carried as a $match here and there is no access to the EF→driver-LINQ
            // visitor / serializer to render one. Fail loudly rather than silently dropping it. Filtered nested
            // ThenIncludes that DO use the pipeline form are handled in ExtractThenIncludesFromSubquery. EF-X021.
            if (nestedLookup.FilterPredicates.Count > 0)
            {
                throw new InvalidOperationException(CoreStrings.TranslationFailed(navigationExpression.Print()));
            }

            parentLookup.PipelineStages.Add(new BsonDocument("$lookup", new BsonDocument
            {
                { "from", nestedLookup.From },
                { "localField", nestedLookup.LocalField },
                { "foreignField", nestedLookup.ForeignField },
                { "as", nestedLookup.As }
            }));

            // Continue with the entity expression (which may have more wrapping)
            navigationExpression = nestedInclude.EntityExpression;
        }

        // Extract filtered Include operations and nested ThenIncludes from MaterializeCollectionNavigation subquery
        if (navigationExpression is MaterializeCollectionNavigationExpression materialize)
        {
            var subquery = materialize.Subquery;
            ExtractFilteredIncludePipeline(subquery, parentLookup, targetEntityType);

            // Check for ThenInclude inside Select(source, o => Include(o, nestedNav, ...))
            ExtractThenIncludesFromSubquery(subquery, parentLookup);
        }
    }

    /// <summary>
    /// Look for IncludeExpressions inside a Select lambda of a collection Include subquery.
    /// These represent ThenInclude on collection-then-collection paths.
    /// </summary>
    private static void ExtractThenIncludesFromSubquery(Expression subquery, LookupExpression parentLookup)
    {
        // The subquery is Select(Where(source, pred), o => Include(o, nav, navExpr))
        if (subquery is not MethodCallExpression { Method.Name: "Select" } selectCall)
            return;

        var selectorArg = selectCall.Arguments[1].UnwrapQuote();

        if (selectorArg is not LambdaExpression { Body: IncludeExpression includeExpr })
            return;

        // Walk every nested IncludeExpression, dispatching each by navigation kind. A single walk (rather than
        // a collection loop followed by a separate reference loop) is required because merging two Include
        // chains on the same collection navigation — e.g. .Include(c.Orders).ThenInclude(o.Lines) and
        // .Include(c.Orders).ThenInclude(o.Customer) — lowers to INTERLEAVED collection and reference
        // ThenIncludes (the reference often outermost). Type-gated sequential loops would drop whichever kind
        // is nested under the other. EF-X025.
        var current = (Expression)includeExpr;
        while (current is IncludeExpression nested
               && nested.Navigation is INavigation nav
               && !nav.IsEmbedded())
        {
            if (nav.IsCollection)
            {
                AddCollectionLookupStages(parentLookup, nav, nested);
            }
            else
            {
                // Reference ThenInclude on a collection item (e.g. Product.OrderDetails.ThenInclude(od => od.Order)).
                // EF lowers the reference to a Join inside the collection subquery; emit it as a nested
                // $lookup + $unwind inside the parent lookup's pipeline so each collection element carries
                // its single referenced document under "_lookup_<RefNav>".
                AddReferenceLookupStages(parentLookup, nav);
            }

            current = nested.EntityExpression;
        }
    }

    /// <summary>
    /// Add the nested $lookup stages for a collection ThenInclude (with any filtered-Include pipeline and
    /// deeper nested ThenIncludes) into the parent lookup's pipeline.
    /// </summary>
    private static void AddCollectionLookupStages(
        LookupExpression parentLookup, INavigation nav, IncludeExpression nested)
    {
        var nestedLookup = new LookupExpression(nav);

        // Check for deeper nesting inside this ThenInclude's NavigationExpression
        if (nested.NavigationExpression is MaterializeCollectionNavigationExpression innerMaterialize)
        {
            ExtractThenIncludesFromSubquery(innerMaterialize.Subquery, nestedLookup);
            ExtractFilteredIncludePipeline(innerMaterialize.Subquery, nestedLookup, nav.TargetEntityType);
        }

        var nestedLookupDoc = new BsonDocument("$lookup", new BsonDocument
        {
            { "from", nestedLookup.From },
            { "localField", nestedLookup.LocalField },
            { "foreignField", nestedLookup.ForeignField },
            { "as", nestedLookup.As }
        });

        if (nestedLookup.HasPipeline)
        {
            // Use pipeline form for nested lookups with their own stages
            var pipeline = new BsonArray
            {
                new BsonDocument("$match",
                    new BsonDocument("$expr",
                        new BsonDocument("$eq", new BsonArray { $"${nestedLookup.ForeignField}", "$$localField" })))
            };

            // A user filter predicate on this nested ThenInclude target cannot be rendered here (no access
            // to the EF→driver-LINQ visitor / serializer). Defer it: EmitLookupStages mutates this pipeline
            // in place, inserting the rendered $match after the FK-correlation $match (index 1) and before
            // the paging stages added below. Never silently dropped. EF-X021.
            if (nestedLookup.FilterPredicates.Count > 0)
            {
                parentLookup.PendingNestedFilterRenders.Add(
                    (pipeline, 1, nestedLookup.FilterPredicates, nestedLookup.Navigation.TargetEntityType));
            }

            foreach (var stage in nestedLookup.PipelineStages)
                pipeline.Add(stage);

            nestedLookupDoc = new BsonDocument("$lookup", new BsonDocument
            {
                { "from", nestedLookup.From },
                { "let", new BsonDocument("localField", $"${nestedLookup.LocalField}") },
                { "pipeline", pipeline },
                { "as", nestedLookup.As }
            });
        }

        // Bubble deferred filter renders discovered in deeper nesting up toward the root lookup.
        parentLookup.PendingNestedFilterRenders.AddRange(nestedLookup.PendingNestedFilterRenders);

        parentLookup.PipelineStages.Add(nestedLookupDoc);
    }

    /// <summary>
    /// Add the $lookup + $unwind stages for a cross-collection reference ThenInclude nested under a
    /// collection Include into the parent lookup's pipeline. The referenced document is unwound
    /// (preserving null) so each parent element carries a single "_lookup_&lt;RefNav&gt;" sub-document.
    /// </summary>
    private static void AddReferenceLookupStages(LookupExpression parentLookup, INavigation referenceNavigation)
    {
        var refLookup = new LookupExpression(referenceNavigation);

        parentLookup.PipelineStages.Add(new BsonDocument("$lookup", new BsonDocument
        {
            { "from", refLookup.From },
            { "localField", refLookup.LocalField },
            { "foreignField", refLookup.ForeignField },
            { "as", refLookup.As }
        }));
        parentLookup.PipelineStages.Add(new BsonDocument("$unwind", new BsonDocument
        {
            { "path", $"${refLookup.As}" },
            { "preserveNullAndEmptyArrays", true }
        }));
    }

    /// <summary>
    /// Extract filtered Include operations (OrderBy, Skip, Take) from a subquery expression
    /// and add them as pipeline stages to the LookupExpression.
    /// </summary>
    private static void ExtractFilteredIncludePipeline(
        Expression navigationExpression,
        LookupExpression lookup,
        IEntityType targetEntityType)
    {
        // Walk from the outermost call inward, collecting stages in reverse order.
        var stages = new List<BsonDocument>();
        var current = navigationExpression;

        // Index of the first filter predicate this call appends (the lookup may already carry some from an
        // earlier extraction pass); only this call's slice is reversed to execution order at the end.
        var predicatesStart = lookup.FilterPredicates.Count;

        // Whether the walk has descended through a ThenInclude's Select/Join. A user filtered-Include predicate
        // sits ABOVE that descent (captured as a must-render predicate); a Where reached AFTER descending is the
        // source-side query filter (e.g. a dependent HasQueryFilter), which is frequently redundant and may
        // reference a navigation that cannot be expressed in a $lookup sub-pipeline — captured best-effort
        // (rendered if possible, otherwise dropped, matching the pre-EF-X021 behavior for those shapes).
        var descended = false;

        while (current is MethodCallExpression methodCall)
        {
            var methodName = methodCall.Method.Name;
            switch (methodName)
            {
                case "OrderBy" or "ThenBy":
                    stages.Add(new BsonDocument("$sort", new BsonDocument(GetSortField(methodCall, targetEntityType), 1)));
                    current = methodCall.Arguments[0];
                    break;

                case "OrderByDescending" or "ThenByDescending":
                    stages.Add(new BsonDocument("$sort", new BsonDocument(GetSortField(methodCall, targetEntityType), -1)));
                    current = methodCall.Arguments[0];
                    break;

                case "Skip":
                    if (methodCall.Arguments[1] is ConstantExpression skipConst)
                    {
                        stages.Add(new BsonDocument("$skip", (int)skipConst.Value!));
                    }
                    current = methodCall.Arguments[0];
                    break;

                case "Take":
                    if (methodCall.Arguments[1] is ConstantExpression takeConst)
                    {
                        stages.Add(new BsonDocument("$limit", (int)takeConst.Value!));
                    }
                    current = methodCall.Arguments[0];
                    break;

                case "Where":
                    // The synthetic FK-correlation predicate (the join condition) is handled by the $lookup
                    // localField/foreignField, so it is dropped.
                    if (IsFkCorrelationPredicate(methodCall.Arguments[1].UnwrapLambdaFromQuote()))
                    {
                        current = methodCall.Arguments[0];
                        break;
                    }

                    if (descended)
                    {
                        // A Where below a ThenInclude's Select/Join: the source-side query filter. Capture it
                        // best-effort (rendered into the sub-pipeline $match if the driver can express it,
                        // otherwise dropped) and stop — matching the pre-EF-X021 behavior so a redundant query
                        // filter that references a navigation does not break translation. EF-X021.
                        lookup.BestEffortFilterPredicates.Add(methodCall.Arguments[1].UnwrapLambdaFromQuote());
                        current = null;
                        break;
                    }

                    // A user filtered-Include predicate (.Include(c => c.Orders.Where(...))). Capture it for the
                    // driver to render into a sub-pipeline $match when the $lookup is emitted (see
                    // MongoEFToLinqTranslatingExpressionVisitor.EmitLookupStages) — never silently dropped; an
                    // unrenderable predicate here fails loudly. Collected outermost-first. EF-X021.
                    lookup.FilterPredicates.Add(methodCall.Arguments[1].UnwrapLambdaFromQuote());
                    current = methodCall.Arguments[0];
                    break;

                case "Select":
                case "Join":
                case "LeftJoin":
                    // A filtered Include followed by ThenInclude lowers the collection subquery to
                    // Select(<filtered source>.LeftJoin/Join(<thenInclude target>, ...), e => Include(e, ...)):
                    // the outermost call is the entity-materialization Select, below it a Join/LeftJoin for the
                    // reference ThenInclude, and the filtered OrderBy/Skip/Take live in the join's OUTER source
                    // (Arguments[0]). These structural operators emit no filtered-pipeline stage of their own
                    // (the ThenInclude $lookups are appended separately by ExtractThenIncludesFromSubquery);
                    // descend into the source so the filtered operators/predicates underneath are collected.
                    descended = true;
                    current = methodCall.Arguments[0];
                    break;

                default:
                    // Hit the base (EntityQueryRootExpression or similar) — stop.
                    current = null;
                    break;
            }
        }

        // Stages were collected outermost-first; reverse so they execute in the right order.
        stages.Reverse();
        lookup.PipelineStages.AddRange(stages);

        // Filter predicates were likewise collected outermost-first; reverse for a stable, source-order
        // sequence of $match stages (their relative order is semantically irrelevant — all are ANDed).
        if (predicatesStart < lookup.FilterPredicates.Count)
        {
            lookup.FilterPredicates.Reverse(predicatesStart, lookup.FilterPredicates.Count - predicatesStart);
        }
    }

    /// <summary>
    /// Whether a reference navigation can be absent after a left-join — i.e. its left-joined document may be
    /// missing. A reference on the dependent side is optional when its foreign key is not required; a
    /// reference on the principal side (1:1 inverse) is optional when the dependent is not required.
    /// </summary>
    private static bool IsOptionalReferenceNavigation(INavigation navigation)
        => navigation.IsOnDependent
            ? !navigation.ForeignKey.IsRequired
            : !navigation.ForeignKey.IsRequiredDependent;

    /// <summary>
    /// Determines whether a Where predicate lowered into a collection-Include subquery is the synthetic
    /// FK-correlation predicate (the join condition between the parent and the dependent) rather than a
    /// user-supplied filtered-Include predicate or a dependent-side query filter.
    /// </summary>
    /// <remarks>
    /// The FK-correlation predicate references the OUTER (parent) entity — EF lowers that reference as a
    /// <see cref="StructuralTypeShaperExpression"/> (and/or a <see cref="ParameterExpression"/> that is not
    /// the predicate lambda's own parameter). A self-contained user filter / query filter references only
    /// the lambda's own element parameter plus constants / captured closure values. Classifying
    /// conservatively: only a predicate that references the parent is treated as the (droppable) correlation;
    /// anything self-contained is reported as an (untranslated) filter so it is never silently dropped.
    /// </remarks>
    private static bool IsFkCorrelationPredicate(LambdaExpression predicate)
    {
        var detector = new OuterReferenceDetector(predicate.Parameters);
        detector.Visit(predicate.Body);
        return detector.ReferencesOuter;
    }

    /// <summary>
    /// Walks a predicate body to decide whether it references the OUTER (parent) query — the hallmark of an
    /// FK-correlation/join predicate, as opposed to a self-contained filter on the dependent element. Two
    /// things count as an outer reference: a <see cref="StructuralTypeShaperExpression"/> (a materialized
    /// parent entity), or a <see cref="ParameterExpression"/> the predicate lambda did not itself declare
    /// (a parameter captured from an enclosing scope). A visitor is needed because either can appear at any
    /// depth in the tree; the walk short-circuits via <see cref="ReferencesOuter"/> as soon as one is found,
    /// so the rest of the tree is skipped.
    /// </summary>
    private sealed class OuterReferenceDetector(IReadOnlyList<ParameterExpression> ownParameters) : ExpressionVisitor
    {
        public bool ReferencesOuter { get; private set; }

        public override Expression Visit(Expression node)
        {
            // Short-circuit: once an outer reference is found there's nothing left to learn, so stop walking.
            if (ReferencesOuter || node == null)
            {
                return node;
            }

            // A materialized parent entity appearing in the predicate is itself an outer reference.
            if (node is StructuralTypeShaperExpression)
            {
                ReferencesOuter = true;
                return node;
            }

            return base.Visit(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            // A parameter the predicate lambda didn't declare must come from an enclosing (outer) scope.
            if (!ownParameters.Contains(node))
            {
                ReferencesOuter = true;
            }

            return node;
        }
    }

    /// <summary>
    /// Extract the sort field name from an OrderBy/OrderByDescending method call.
    /// </summary>
    private static string GetSortField(MethodCallExpression orderByCall, IEntityType entityType)
    {
        // The key selector is the second argument: e.g., o => o.OrderID
        var name = orderByCall.Arguments[1].UnwrapLambdaFromQuote().Body.TryGetSimplePropertyName();
        if (name == null)
        {
            return "_id";
        }

        var property = entityType.FindProperty(name);
        return property != null ? Microsoft.EntityFrameworkCore.MongoPropertyExtensions.GetElementName(property) : name;
    }

    /// <summary>
    /// For ThenInclude on collection-then-collection paths, extract the nested IncludeExpressions
    /// from the subquery's Select lambda and wrap them around the inner shaper so the shaper
    /// reads the nested $lookup arrays from each parent document.
    /// </summary>
    private Expression WrapWithNestedCollectionIncludes(
        Expression navigationExpression,
        Expression innerShaper,
        ObjectArrayProjectionExpression parentArrayProjection)
    {
        if (navigationExpression is not MaterializeCollectionNavigationExpression materialize)
            return innerShaper;

        // Look for Select(source, o => Include(o, nav, navExpr))
        if (materialize.Subquery is not MethodCallExpression { Method.Name: "Select" } selectCall)
            return innerShaper;

        var selectorArg = selectCall.Arguments[1].UnwrapQuote();

        if (selectorArg is not LambdaExpression lambda)
            return innerShaper;

        // Collect nested IncludeExpressions from the lambda body
        var includes = new List<IncludeExpression>();
        var body = lambda.Body;
        while (body is IncludeExpression include)
        {
            includes.Add(include);
            body = include.EntityExpression;
        }

        if (includes.Count == 0)
            return innerShaper;

        // Wrap the inner shaper with Include expressions (innermost first)
        var result = innerShaper;
        for (var i = includes.Count - 1; i >= 0; i--)
        {
            var include = includes[i];
            if (include.Navigation is INavigation nav && !nav.IsEmbedded() && nav.IsCollection)
            {
                // Build a CollectionShaperExpression for this nested collection Include
                var nestedLookupAlias = LookupExpression.GetLookupAlias(nav);
                var nestedArrayProjection = new ObjectArrayProjectionExpression(
                    nav, parentArrayProjection.InnerProjection.ParentAccessExpression, nestedLookupAlias, null);

                var nestedInnerShaper = new StructuralTypeShaperExpression(
                    nav.TargetEntityType,
                    Expression.Convert(
                        Expression.Convert(nestedArrayProjection.InnerProjection, typeof(object)),
                        typeof(ValueBuffer)),
                    nullable: true);

                // Recurse so this nested collection's own deeper ThenIncludes (e.g. a reference Include
                // such as OrderDetail.Product on a Customer.Orders.OrderDetails.Product path) are wrapped
                // onto its element shaper. Without this the deepest navigation is never materialized and
                // comes back null. The recursion reads the deeper "_lookup_<Nav>" sub-documents relative
                // to THIS collection's element (nestedArrayProjection), matching the nested $lookup pipeline
                // emitted by ExtractThenIncludesFromSubquery / AddReferenceLookupStages.
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
                // Reference ThenInclude on a collection item (e.g. od.Order). The referenced document is
                // unwound under "_lookup_<RefNav>" in each parent element by the nested $lookup + $unwind
                // registered in the parent lookup pipeline (see AddReferenceLookupStages). Build an entity
                // shaper that reads that sub-document by name relative to the parent element, rather than
                // visiting the raw EF Join field (which produces a non-materializable scalar binding).
                var refAccess = new NavigationObjectAccessExpression(
                    refNav,
                    parentArrayProjection.InnerProjection.ParentAccessExpression,
                    false,
                    LookupExpression.GetLookupAlias(refNav));
                var refEntityProjection = new EntityProjectionExpression(refNav.TargetEntityType, refAccess);
                var refShaper = new StructuralTypeShaperExpression(
                    refNav.TargetEntityType,
                    Expression.Convert(Expression.Convert(refEntityProjection, typeof(object)), typeof(ValueBuffer)),
                    nullable: true);

                result = new IncludeExpression(result, refShaper, refNav, include.SetLoaded);
            }
        }

        return result;
    }
}
